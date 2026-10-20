using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Pocketbay;

public sealed class MainWindow : Window
{
    const double SidebarWidth = 52;
    static readonly Brush Chrome = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1C));

    readonly EmulatorManager _manager = new();
    EmulatorClient? _client;
    readonly KeymapEngine _engine = new(KeymapStore.Active());
    readonly ScreenView _screen = new();
    readonly KeymapOverlay _overlay = new();
    readonly Grid _root = new();
    readonly Grid _screenHost = new();
    readonly StackPanel _sidebar = new() { Background = Chrome };
    readonly Border _sidebarHost;
    readonly Dictionary<string, Button> _sidebarButtons = [];
    readonly Border _status;
    readonly TextBlock _statusText = new() { Foreground = new SolidColorBrush(Color.FromRgb(0xD8, 0xD8, 0xD8)), FontSize = 15, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 560 };
    readonly ProgressBar _statusProgress = new() { Height = 6, Width = 320, Margin = new Thickness(0, 14, 0, 0) };
    readonly StackPanel _statusButtons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 16, 0, 0) };
    readonly Border _toast;
    readonly TextBlock _toastText = new() { Foreground = Brushes.White, FontSize = 13 };
    readonly DispatcherTimer _toastTimer = new();
    readonly ComboBox _layoutPicker = new() { MinWidth = 150, Margin = new Thickness(0, 0, 6, 0), VerticalContentAlignment = VerticalAlignment.Center };
    AudioPlayer? _audio;

    CancellationTokenSource? _session;
    bool _quitting, _closeReady, _fullScreen, _sidebarRevealed, _updatingPicker;
    string? _foregroundPackage, _lastClipboard;
    Rect _windowedBounds;
    Version? _pendingUpdate;
    bool _restartAfterClose;

    string? _pendingPack;

    public MainWindow(string? openFile = null)
    {
        if (openFile != null && GamePack.LooksLikePack(openFile)) _pendingPack = openFile;
        WatchInbox();
        Title = "Pocketbay";
        Background = Chrome;
        Icon = AppIcon;
        UseLayoutRounding = true;
        SetInitialBounds();

        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SidebarWidth) });
        Content = _root;

        _screen.Engine = _engine;
        _engine.ToNative = _screen.ToNative;
        _screenHost.Children.Add(_screen);
        _overlay.ScreenRect = () => _screen.ScreenRect;
        _overlay.Keymap = _engine.Keymap;
        _overlay.ToolbarAccessory = _layoutPicker;
        _screenHost.Children.Add(_overlay);

        _statusProgress.IsIndeterminate = true;
        _status = new Border
        {
            Background = Brushes.Black,
            Child = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
                Children =
                {
                    new Image { Source = AppIcon, Width = 96, Height = 96, Margin = new Thickness(0, 0, 0, 16) },
                    _statusText, _statusProgress, _statusButtons,
                },
            },
        };
        _screenHost.Children.Add(_status);

        _toast = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x2A, 0x2A, 0x2A)), CornerRadius = new CornerRadius(16),
            Padding = new Thickness(16, 7, 16, 7), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 24), Child = _toastText, Opacity = 0, IsHitTestVisible = false,
        };
        _screenHost.Children.Add(_toast);
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); _toast.Opacity = 0; };
        _root.Children.Add(_screenHost);

        BuildSidebar();
        _sidebarHost = new Border { Child = _sidebar, Width = SidebarWidth };
        Grid.SetColumn(_sidebarHost, 1);
        _root.Children.Add(_sidebarHost);

        _screen.FirstFrame += () => { _gotFrame = true; _status.Visibility = Visibility.Collapsed; };
        _screen.DeviceSizeChanged += _ => _overlay.Refresh();
        _screen.SizeChanged += (_, _) => _overlay.Refresh();
        _screen.FilesDropped += files =>
        {
            var packs = files.Where(GamePack.LooksLikePack).ToArray();
            if (packs.Length > 0) InstallPack(packs[0]); else Install(files);
        };
        _engine.AimingChanged += on =>
        {
            _screen.SetCursorCaptured(on);
            _overlay.IsAiming = on;
            if (on) { SetSidebarRevealed(false); Toast($"Mouse aim on — press {AimKeyName} or Esc to get the cursor back"); }
        };
        _overlay.Changed += map => { _engine.Keymap = map; KeymapStore.Save(map); };
        _overlay.Done += () => SetEditing(false);
        _layoutPicker.SelectionChanged += (_, _) =>
        {
            if (_updatingPicker || _layoutPicker.SelectedItem is not ComboBoxItem { Tag: Keymap map }) return;
            Activate(map);
        };
        RefreshLayoutPicker();

        PreviewKeyDown += OnPreviewKeyDown;
        PreviewKeyUp += OnPreviewKeyUp;
        PreviewMouseMove += (_, e) => RevealSidebarIfNeeded(e.GetPosition(_root));
        Deactivated += (_, _) => _screen.ReleaseAllInput();
        Activated += (_, _) => { if (_overlay.Mode != OverlayMode.Edit) _screen.Focus(); };
        Loaded += async (_, _) => await StartupAsync();
        Loaded += async (_, _) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(20));
            while (true)
            {
                if (await Updater.DownloadUpdateAsync() is { } v)
                {
                    _pendingUpdate = v;
                    Toast($"Pocketbay {v} downloaded — it installs next time you open Pocketbay (or … > Restart to update)", 8);
                }
                await Task.Delay(TimeSpan.FromHours(6));
            }
        };
        Closing += OnClosing;
        UpdateKeymapState();
    }

    static readonly BitmapSource AppIcon = BitmapDecoder.Create(new Uri("pack://application:,,,/Pocketbay.ico"),
        BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames.OrderByDescending(f => f.PixelWidth).First();

    string AimKeyName => _engine.Keymap.Controls.FirstOrDefault(c => c.Type == ControlType.Aim)?.Key?.CapLabel ?? "the aim key";

    // ---- Window placement ----

    void SetInitialBounds()
    {
        var work = SystemParameters.WorkArea;
        if (Settings.Current.WindowRect is { Length: 4 } r && r[2] > 300 && r[3] > 200 &&
            r[0] >= work.Left - 20 && r[1] >= work.Top - 20 && r[0] + r[2] <= work.Right + 20 && r[1] + r[3] <= work.Bottom + 20)
        {
            Left = r[0]; Top = r[1]; Width = r[2]; Height = r[3];
            return;
        }
        // Largest 16:9 picture (plus sidebar and title bar) that fits the work area.
        var chrome = SystemParameters.WindowCaptionHeight + 2 * SystemParameters.ResizeFrameHorizontalBorderHeight + 8;
        var h = Math.Min(work.Height * 0.9 - chrome, (work.Width * 0.9 - SidebarWidth) * 9 / 16);
        Width = h * 16 / 9 + SidebarWidth + 16;
        Height = h + chrome;
        Left = work.Left + (work.Width - Width) / 2;
        Top = work.Top + (work.Height - Height) / 2;
    }

    // ---- Startup / setup ----

    async Task StartupAsync()
    {
        if (!SdkInstaller.IsInstalled) { await ShowSetupAsync(); return; }
        await RunSessionAsync();
    }

    void ShowStatus(string text, bool spinning, double? progress = null, params (string Label, Action Click)[] buttons)
    {
        _status.Visibility = Visibility.Visible;
        _statusText.Text = text;
        _statusProgress.Visibility = spinning || progress != null ? Visibility.Visible : Visibility.Collapsed;
        _statusProgress.IsIndeterminate = progress == null;
        if (progress is { } p) _statusProgress.Value = p * 100;
        _statusButtons.Children.Clear();
        foreach (var (label, click) in buttons)
        {
            var b = KeymapOverlay.ToolButton(label);
            b.Padding = new Thickness(14, 6, 14, 6);
            b.Click += (_, _) => click();
            _statusButtons.Children.Add(b);
        }
    }

    async Task ShowSetupAsync()
    {
        ShowStatus("Getting Google's Android SDK license…", true);
        string license;
        try { license = await SdkInstaller.FetchLicenseAsync(); }
        catch (Exception e)
        {
            ShowStatus("Couldn't reach Google to download Android.\n" + e.Message, false, null,
                ("Try again", () => _ = ShowSetupAsync()), ("Quit", Close));
            return;
        }

        var free = SdkInstaller.FreeBytes() / (1024.0 * 1024 * 1024);
        var box = new TextBox
        {
            Text = license, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Height = 240, Width = 620, FontSize = 11, Margin = new Thickness(0, 12, 0, 12),
        };
        var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        panel.Children.Add(new TextBlock { Text = "Set up Android", Foreground = Brushes.White, FontSize = 22, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock
        {
            Text = "Pocketbay needs Google's Android emulator and Android 15 with the Play Store (about 3 GB download, ~25 GB on disk). " +
                   "They come straight from Google, under this license:",
            Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap, Width = 620, Margin = new Thickness(0, 8, 0, 0),
        });
        panel.Children.Add(box);
        if (free < 25)
            panel.Children.Add(new TextBlock { Text = $"Only {free:0} GB free on this drive — free up space first or setup may fail.", Foreground = Brushes.Orange, Margin = new Thickness(0, 0, 0, 10) });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var accept = KeymapOverlay.ToolButton("I accept — download and install");
        accept.Background = Glyph.Accent;
        accept.Padding = new Thickness(14, 6, 14, 6);
        var quit = KeymapOverlay.ToolButton("Quit");
        quit.Padding = new Thickness(14, 6, 14, 6);
        buttons.Children.Add(accept);
        buttons.Children.Add(quit);
        panel.Children.Add(buttons);
        var setup = new Border { Background = Chrome, Child = panel };
        _screenHost.Children.Add(setup);
        _status.Visibility = Visibility.Collapsed;

        quit.Click += (_, _) => Close();
        accept.Click += async (_, _) =>
        {
            Settings.Current.LicenseAccepted = true;
            Settings.Save();
            _screenHost.Children.Remove(setup);
            await InstallSdkAsync();
        };
    }

    async Task InstallSdkAsync()
    {
        var progress = new Progress<(string Message, double Fraction)>(p => ShowStatus(p.Message + $"\n{p.Fraction * 100:0}%", false, p.Fraction));
        try
        {
            await SdkInstaller.InstallAsync(progress, CancellationToken.None);
        }
        catch (Exception e)
        {
            Log.Write("install failed: " + e);
            ShowStatus("Setup didn't finish:\n" + e.Message + "\n\nDownloads resume where they stopped.", false, null,
                ("Try again", () => _ = InstallSdkAsync()), ("Report a problem", ReportProblem), ("Quit", Close));
            return;
        }
        await CheckAccelerationAndStartAsync();
    }

    async Task CheckAccelerationAndStartAsync()
    {
        ShowStatus("Checking hardware virtualization…", true);
        var (ok, detail) = await SdkInstaller.CheckAccelerationAsync();
        if (!ok)
        {
            Log.Write("accel-check: " + detail);
            ShowStatus(
                "Android needs hardware virtualization, which is turned off on this PC.\n\n" +
                "1. Press Start, type \"Turn Windows features on or off\", and tick \"Windows Hypervisor Platform\". Restart when asked.\n" +
                "2. If it still fails, turn on virtualization (Intel VT-x or AMD SVM) in your PC's BIOS/UEFI settings.\n\n" +
                "Details: " + detail.Split('\n').LastOrDefault(l => l.Trim().Length > 0)?.Trim(),
                false, null, ("Check again", () => _ = CheckAccelerationAndStartAsync()), ("Report a problem", ReportProblem), ("Quit", Close));
            return;
        }
        await RunSessionAsync();
    }

    // ---- Session ----

    /// Boots (or attaches to) Android, wires screen + input + audio, and restarts
    /// everything if the emulator stops unexpectedly.
    bool _gotFrame;
    static readonly string[] GraphicsFallback = ["standard", "angle", "software"];

    static string GraphicsName(string mode) => mode switch
    {
        "angle" => "compatibility graphics", "software" => "software graphics", _ => "standard graphics",
    };

    /// Android stopped or stalled before showing anything: try the next graphics mode
    /// automatically, and only show an error once every mode has failed.
    bool TryNextGraphicsMode(string why)
    {
        Log.Write($"{why} with {Settings.Current.Graphics} graphics");
        var i = Array.IndexOf(GraphicsFallback, Settings.Current.Graphics);
        if (i < 0 || i + 1 >= GraphicsFallback.Length) return false;
        Settings.Current.Graphics = GraphicsFallback[i + 1];
        Settings.Save();
        return true;
    }

    void ShowStartFailure(string message)
    {
        ShowStatus(message + "\n\n" + _manager.LastLogLines(4), false, null,
            ("Try again", () => { Settings.Current.Graphics = "standard"; Settings.Save(); _ = RunSessionAsync(); }),
            ("Report a problem", ReportProblem));
    }

    async Task RunSessionAsync()
    {
        while (!_quitting)
        {
            ShowStatus($"Starting Android… ({GraphicsName(Settings.Current.Graphics)})\nThe first start can take a few minutes.", true);
            EmulatorManager.Endpoint endpoint;
            try { endpoint = await _manager.StartAsync(); }
            catch (Exception e)
            {
                Log.Write("start failed: " + e);
                ShowStatus(e.Message, false, null,
                    ("Try again", () => _ = RunSessionAsync()),
                    ("Try compatibility graphics", () => { Settings.Current.Graphics = "angle"; Settings.Save(); _ = RunSessionAsync(); }),
                    ("Report a problem", ReportProblem));
                return;
            }

            var client = new EmulatorClient(endpoint.Port, endpoint.Token);
            _client = client;
            _screen.Client = client;
            _engine.Client = client;

            var booted = false;
            var bootStart = DateTime.Now;
            for (int i = 0; i < 1200 && _manager.IsRunning; i++)   // up to 10 minutes on slow PCs
            {
                if (await client.IsBootedAsync()) { booted = true; break; }
                if (i % 10 == 0)
                    ShowStatus($"Starting Android… ({GraphicsName(Settings.Current.Graphics)}) {(int)(DateTime.Now - bootStart).TotalSeconds}s\nThe first start can take a few minutes.", true);
                await Task.Delay(500);
            }
            if (!booted)
            {
                var crashed = !_manager.IsRunning;
                if (!crashed) await _manager.ShutdownAsync();
                client.Dispose();
                _client = null; _screen.Client = null; _engine.Client = null;
                if (TryNextGraphicsMode(crashed ? "Android stopped while booting" : "Android didn't finish booting")) continue;
                ShowStartFailure(crashed ? "Android keeps stopping while it starts." : "Android took too long to start.");
                return;
            }

            _session = new CancellationTokenSource();
            var ct = _session.Token;
            if (booted)
            {
                await _manager.ApplyDeviceDefaultsAsync();
                client.StartInputStream();
                _screen.ResetForNewSession();
                _audio ??= new AudioPlayer();
                Loop(ct, t => client.StreamScreenAsync(_screen.FrameArrived, t), "screen");
                var audio = _audio;
                Loop(ct, t => client.StreamAudioAsync(audio.Enqueue, t), "audio");
                _ = SyncClipboardToPcAsync(client, ct);
                if (_pendingPack is { } packFile) { _pendingPack = null; InstallPack(packFile); }
                _ = PollForegroundAppAsync(ct);

                // Booted but no picture: the graphics mode isn't working on this PC.
                _gotFrame = false;
                for (int i = 0; i < 40 && !_gotFrame && _manager.IsRunning; i++) await Task.Delay(500);
                if (!_gotFrame && _manager.IsRunning)
                {
                    if (TryNextGraphicsMode("no picture after boot"))
                    {
                        ShowStatus($"Switching to {GraphicsName(Settings.Current.Graphics)}…", true);
                        await _manager.ShutdownAsync();   // watchdog below restarts with the new mode
                    }
                    else { ShowStartFailure("Android started but its screen isn't showing."); }
                }
            }

            // Watchdog: if Android stops without us asking, bring it back.
            while (_manager.IsRunning && !_quitting) await Task.Delay(1000);
            _session.Cancel();
            _screen.ReleaseAllInput();
            _screen.Client = null;
            _engine.Client = null;
            _client = null;
            client.Dispose();
        }
    }

    /// Runs a streaming call, reconnecting whenever it ends, until cancelled.
    static void Loop(CancellationToken ct, Func<CancellationToken, Task> body, string name) => _ = Task.Run(async () =>
    {
        while (!ct.IsCancellationRequested)
        {
            try { await body(ct); }
            catch (Exception e) when (!ct.IsCancellationRequested) { Log.Write($"{name} stream ended: {e.Message}"); }
            try { await Task.Delay(1000, ct); } catch { }
        }
    }, ct);

    async Task PollForegroundAppAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var pkg = await _manager.ForegroundPackageAsync();
            if (pkg != _foregroundPackage) { _foregroundPackage = pkg; UpdateKeymapState(); }
            try { await Task.Delay(1500, ct); } catch { return; }
        }
    }

    // ---- Clipboard ----

    /// Android → PC: anything copied in Android lands on the Windows clipboard.
    async Task SyncClipboardToPcAsync(EmulatorClient client, CancellationToken ct)
    {
        _lastClipboard = await client.GetClipboardAsync();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await client.StreamClipboardAsync(text => Dispatcher.BeginInvoke(() =>
                {
                    if (string.IsNullOrEmpty(text) || text == _lastClipboard) return;
                    _lastClipboard = text;
                    try { Clipboard.SetText(text); Toast("Copied to your PC clipboard"); } catch { }
                }), ct);
            }
            catch { }
            try { await Task.Delay(1000, ct); } catch { return; }
        }
    }

    /// PC → Android only on Ctrl+V, so Android apps can't read the PC clipboard in the background.
    async void PasteFromPc()
    {
        if (_client is not { } client) return;
        string text;
        try { text = Clipboard.GetText(); } catch { return; }
        if (string.IsNullOrEmpty(text)) return;
        if (text != _lastClipboard) { _lastClipboard = text; await client.SetClipboardAsync(text); }
        _screen.SendControlShortcut(9);   // Ctrl+V
    }

    // ---- Keymap state & layouts ----

    bool KeymapAppliesToForeground =>
        _engine.Keymap.Packages.Count == 0 || (_foregroundPackage != null && _engine.Keymap.Packages.Contains(_foregroundPackage));

    void UpdateKeymapState()
    {
        var editing = _overlay.Mode == OverlayMode.Edit;
        var wasActive = _engine.IsActive;
        _engine.IsActive = Settings.Current.KeymapEnabled && KeymapAppliesToForeground && !editing;
        if (_engine.IsActive && !wasActive) Toast($"Game controls on — press {AimKeyName} to aim with the mouse, Esc to get the cursor back", 5);
        if (!editing) _overlay.Mode = _engine.IsActive && Settings.Current.ShowKeyHints ? OverlayMode.Play : OverlayMode.Hidden;
        SetOn("keymap", Settings.Current.KeymapEnabled);
        SetOn("edit", editing);
    }

    void SetEditing(bool on)
    {
        if (on)
        {
            _engine.ReleaseAll();
            _overlay.Keymap = _engine.Keymap;
            _overlay.Mode = OverlayMode.Edit;
            Toast("Editing game controls — Android won't respond until you click Done (or press Esc)", 5);
        }
        else
        {
            _overlay.Mode = OverlayMode.Hidden;
            _screen.Focus();
            Toast("Game controls saved");
        }
        UpdateKeymapState();
    }

    void RefreshLayoutPicker()
    {
        _updatingPicker = true;
        _layoutPicker.Items.Clear();
        foreach (var m in KeymapStore.All())
        {
            var item = new ComboBoxItem { Content = m.Name, Tag = m };
            _layoutPicker.Items.Add(item);
            if (m.Id == _engine.Keymap.Id) _layoutPicker.SelectedItem = item;
        }
        _updatingPicker = false;
    }

    void Activate(Keymap map, bool announce = true)
    {
        KeymapStore.SetActive(map.Id);
        _engine.Keymap = map;
        _overlay.Keymap = map;
        RefreshLayoutPicker();
        UpdateKeymapState();
        if (announce) Toast($"Layout: {map.Name}");
    }

    string UniqueName(string b)
    {
        var names = KeymapStore.All().Select(m => m.Name).ToHashSet();
        if (!names.Contains(b)) return b;
        int n = 2;
        while (names.Contains($"{b} {n}")) n++;
        return $"{b} {n}";
    }

    MenuItem LayoutMenu()
    {
        var menu = new MenuItem { Header = "Layout" };
        foreach (var m in KeymapStore.All())
        {
            var map = m;
            var item = new MenuItem { Header = m.Name, IsCheckable = true, IsChecked = m.Id == _engine.Keymap.Id };
            item.Click += (_, _) => Activate(map);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        MenuItem Cmd(string header, Action a) { var i = new MenuItem { Header = header }; i.Click += (_, _) => a(); return i; }
        menu.Items.Add(Cmd("New layout", () => { var m = Keymap.Bgmi(); m.Name = UniqueName("New Layout"); KeymapStore.Save(m); Activate(m); }));
        menu.Items.Add(Cmd("Duplicate layout", () =>
        {
            var m = _engine.Keymap.Clone(); m.Id = Guid.NewGuid().ToString().ToUpperInvariant(); m.Name = UniqueName(_engine.Keymap.Name + " copy");
            KeymapStore.Save(m); Activate(m);
        }));
        menu.Items.Add(Cmd("Rename layout…", () =>
        {
            if (Prompt("Rename layout", _engine.Keymap.Name) is { Length: > 0 } name)
            { var m = _engine.Keymap; m.Name = name.Trim(); KeymapStore.Save(m); Activate(m, false); }
        }));
        menu.Items.Add(Cmd("Delete layout…", () =>
        {
            if (KeymapStore.All().Count <= 1) { Toast("Keep at least one layout"); return; }
            if (MessageBox.Show(this, $"Delete “{_engine.Keymap.Name}”?", "Pocketbay", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
            KeymapStore.Delete(_engine.Keymap.Id);
            Activate(KeymapStore.All()[0]);
        }));
        menu.Items.Add(new Separator());
        menu.Items.Add(Cmd("Import layout…", () =>
        {
            var dlg = new OpenFileDialog { Filter = "Pocketbay layout (*.json)|*.json" };
            if (dlg.ShowDialog(this) != true) return;
            if (KeymapStore.Read(dlg.FileName) is not { } m) { Toast("That file isn't a Pocketbay layout"); return; }
            m.Id = Guid.NewGuid().ToString().ToUpperInvariant();
            m.Name = UniqueName(m.Name);
            KeymapStore.Save(m); Activate(m);
        }));
        menu.Items.Add(Cmd("Export layout…", () =>
        {
            var dlg = new SaveFileDialog { Filter = "Pocketbay layout (*.json)|*.json", FileName = _engine.Keymap.Name + ".json" };
            if (dlg.ShowDialog(this) == true) { KeymapStore.Export(_engine.Keymap, dlg.FileName); Toast("Layout exported"); }
        }));
        return menu;
    }

    string? Prompt(string title, string initial)
    {
        var box = new TextBox { Text = initial, Margin = new Thickness(12), MinWidth = 260 };
        var ok = new Button { Content = "OK", IsDefault = true, Width = 80, Margin = new Thickness(4) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Width = 80, Margin = new Thickness(4) };
        var dlg = new Window
        {
            Title = title, Owner = this, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel { Children = { box, new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8, 0, 8, 8), Children = { ok, cancel } } } },
        };
        ok.Click += (_, _) => dlg.DialogResult = true;
        dlg.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return dlg.ShowDialog() == true ? box.Text : null;
    }

    // ---- Sidebar & actions ----

    void BuildSidebar()
    {
        void Add(string id, string glyph, string tip, Action a)
        {
            var b = new Button
            {
                Content = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 16,
                Width = 40, Height = 36, Margin = new Thickness(6, 2, 6, 2), ToolTip = tip, Cursor = Cursors.Hand,
                Foreground = new SolidColorBrush(Color.FromRgb(0xD9, 0xD9, 0xD9)), Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                Focusable = false,
            };
            System.Windows.Automation.AutomationProperties.SetName(b, tip);
            b.Click += (_, _) => a();
            _sidebarButtons[id] = b;
            _sidebar.Children.Add(b);
        }
        void Divider() => _sidebar.Children.Add(new Border { Height = 1, Margin = new Thickness(12, 6, 12, 6), Background = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)) });

        _sidebar.Margin = new Thickness(0, 8, 0, 0);
        Add("back", "", "Back  (Esc)", () => _client?.Press("GoBack"));
        Add("home", "", "Home  (Ctrl+Shift+H)", () => _client?.Press("GoHome"));
        Add("recents", "", "Recent apps  (Ctrl+Shift+R)", () => _client?.Press("AppSwitch"));
        Divider();
        Add("volup", "", "Volume up", () => _client?.Press("AudioVolumeUp"));
        Add("voldown", "", "Volume down", () => _client?.Press("AudioVolumeDown"));
        Add("rotate", "", "Rotate screen", Rotate);
        Add("shot", "", "Screenshot  (Ctrl+Shift+S)", SaveScreenshot);
        Add("apk", "", "Install an app (.apk) — or drag one onto the window", ChooseApk);
        Divider();
        Add("keymap", "", "Game controls on/off  (Ctrl+K)", ToggleKeymap);
        Add("edit", "", "Edit game controls  (Ctrl+E)", () => SetEditing(_overlay.Mode != OverlayMode.Edit));
        Add("more", "", "More", ShowMoreMenu);
        Add("full", "", "Full screen  (F11)", ToggleFullScreen);
    }

    void SetOn(string id, bool on)
    {
        if (_sidebarButtons.TryGetValue(id, out var b))
            b.Foreground = on ? Glyph.Accent : new SolidColorBrush(Color.FromRgb(0xD9, 0xD9, 0xD9));
    }

    void ShowMoreMenu()
    {
        var menu = new ContextMenu { PlacementTarget = _sidebarButtons["more"], Placement = PlacementMode.Left };
        menu.Items.Add(LayoutMenu());
        var hints = new MenuItem { Header = "Show key hints", IsCheckable = true, IsChecked = Settings.Current.ShowKeyHints, InputGestureText = "Ctrl+/" };
        hints.Click += (_, _) => { Settings.Current.ShowKeyHints = !Settings.Current.ShowKeyHints; Settings.Save(); UpdateKeymapState(); };
        menu.Items.Add(hints);
        var paste = new MenuItem { Header = "Paste into Android", InputGestureText = "Ctrl+V" };
        paste.Click += (_, _) => PasteFromPc();
        menu.Items.Add(paste);
        menu.Items.Add(new Separator());

        var gfx = new MenuItem { Header = "Graphics (restarts Android)" };
        foreach (var (mode, label) in new[] { ("standard", "Standard"), ("angle", "Compatibility (ANGLE / Vulkan)"), ("software", "Software (slow, for broken drivers)") })
        {
            var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = Settings.Current.Graphics == mode };
            item.Click += async (_, _) =>
            {
                Settings.Current.Graphics = mode; Settings.Save();
                ShowStatus("Restarting Android with new graphics settings…", true);
                await _manager.ShutdownAsync();   // the watchdog starts it again with the new flags
            };
            gfx.Items.Add(item);
        }
        menu.Items.Add(gfx);
        var pack = new MenuItem { Header = "Install game pack…" };
        pack.Click += (_, _) =>
        {
            var dlg = new OpenFileDialog { Filter = "Pocketbay game pack (*.pbpack;*.zip)|*.pbpack;*.zip" };
            if (dlg.ShowDialog(this) == true) InstallPack(dlg.FileName);
        };
        menu.Items.Add(pack);
        if (_pendingUpdate is { } upd)
        {
            var restart = new MenuItem { Header = $"Restart to update to {upd}", FontWeight = FontWeights.SemiBold };
            restart.Click += (_, _) => { _restartAfterClose = true; Close(); };
            menu.Items.Add(restart);
        }
        else
        {
            var check = new MenuItem { Header = $"Check for updates (you have {Updater.Current.ToString(3)})" };
            check.Click += async (_, _) =>
            {
                Toast("Checking for updates…");
                if (await Updater.DownloadUpdateAsync() is { } v) { _pendingUpdate = v; Toast($"Pocketbay {v} downloaded — … > Restart to update", 6); }
                else Toast("Pocketbay is up to date");
            };
            menu.Items.Add(check);
        }
        var report = new MenuItem { Header = "Report a problem…" };
        report.Click += (_, _) => ReportProblem();
        menu.Items.Add(report);
        var logs = new MenuItem { Header = "Open logs folder" };
        logs.Click += (_, _) => Process.Start(new ProcessStartInfo(Paths.Logs) { UseShellExecute = true });
        menu.Items.Add(logs);
        var help = new MenuItem { Header = "Keyboard shortcuts" };
        help.Click += (_, _) => MessageBox.Show(this,
            "Esc — Back (or leave mouse aim)\nCtrl+Shift+H — Home\nCtrl+Shift+R — Recent apps\nCtrl+Shift+S — Screenshot\n" +
            "Ctrl+O — Install an app (.apk)\nCtrl+K — Game controls on/off\nCtrl+E — Edit game controls\nCtrl+/ — Show/hide key hints\n" +
            "Ctrl+V — Paste your PC clipboard into Android\nCtrl+C / Ctrl+X / Ctrl+A — Copy / cut / select all in Android\nF11 — Full screen",
            "Pocketbay shortcuts");
        menu.Items.Add(help);
        menu.IsOpen = true;
    }

    void ToggleKeymap()
    {
        Settings.Current.KeymapEnabled = !Settings.Current.KeymapEnabled;
        Settings.Save();
        Toast(Settings.Current.KeymapEnabled ? $"Game controls on (in {_engine.Keymap.Name})" : "Game controls off");
        UpdateKeymapState();
    }

    void Rotate()
    {
        // Always a further quarter turn clockwise: 0° → 90° → 180° → 270° → 0°.
        var next = (_screen.QuarterTurns + 1) % 4;
        _ = _client?.SetRotationAsync(next * 90);
    }

    void SaveScreenshot()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Pocketbay");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"Pocketbay {DateTime.Now:yyyy-MM-dd 'at' HH.mm.ss}.png");
        try { _screen.SaveScreenshot(path); Toast("Screenshot saved to Pictures\\Pocketbay"); } catch (Exception e) { Toast("Screenshot failed: " + e.Message); }
    }

    void ChooseApk()
    {
        var dlg = new OpenFileDialog { Filter = "Android apps (*.apk)|*.apk", Multiselect = true, Title = "Choose Android apps (.apk) to install" };
        if (dlg.ShowDialog(this) == true) Install(dlg.FileNames);
    }

    /// Files handed over by a second launch (double-clicked game pack).
    void WatchInbox()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            foreach (var f in Directory.GetFiles(Paths.Inbox, "*.txt"))
            {
                string path;
                try { path = File.ReadAllText(f).Trim(); File.Delete(f); } catch { continue; }
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                Activate();
                if (!GamePack.LooksLikePack(path)) continue;
                if (_client == null) _pendingPack = path; else InstallPack(path);
            }
        };
        timer.Start();
    }

    async void ReportProblem()
    {
        Toast("Collecting logs…", 30);
        try
        {
            var summary = await Report.SystemSummaryAsync();
            var zip = await Report.CreateAsync();
            Report.ShowAndOpenIssue(zip, summary);
            Toast("Logs saved to your Desktop — attach the zip to the report page that opened", 8);
        }
        catch (Exception e) { Toast("Couldn't collect logs: " + e.Message, 6); }
    }

    async void InstallPack(string path)
    {
        if (_client == null) { Toast("Wait for Android to finish starting"); return; }
        var progress = new Progress<string>(m => ShowStatus(m, true));
        try
        {
            var result = await GamePack.InstallAsync(path, _manager, progress);
            _status.Visibility = Visibility.Collapsed;
            Toast(result, 6);
        }
        catch (Exception e)
        {
            Log.Write("pack install: " + e);
            ShowStatus("The game pack didn't install:\n" + e.Message, false, null,
                ("OK", () => _status.Visibility = Visibility.Collapsed), ("Report a problem", ReportProblem));
        }
    }

    async void Install(string[] apks)
    {
        foreach (var apk in apks)
        {
            Toast($"Installing {Path.GetFileNameWithoutExtension(apk)}…", 60);
            var (_, message) = await _manager.InstallAsync(apk);
            Toast(message);
        }
    }

    void Toast(string text, double seconds = 2.5)
    {
        _toastText.Text = text;
        _toast.Opacity = 1;
        _toastTimer.Stop();
        _toastTimer.Interval = TimeSpan.FromSeconds(seconds);
        _toastTimer.Start();
    }

    // ---- Full screen ----

    void ToggleFullScreen()
    {
        if (!_fullScreen)
        {
            _windowedBounds = new Rect(Left, Top, Width, Height);
            _fullScreen = true;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
            WindowState = WindowState.Maximized;
            _root.ColumnDefinitions[1].Width = new GridLength(0);
            Grid.SetColumn(_sidebarHost, 0);
            _sidebarHost.HorizontalAlignment = HorizontalAlignment.Right;
            Panel.SetZIndex(_sidebarHost, 10);
            _sidebarRevealed = true;
            SetSidebarRevealed(false);
            Toast("Move the pointer to the very right edge to show the sidebar. F11 exits full screen.", 4);
        }
        else
        {
            _fullScreen = false;
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            Left = _windowedBounds.Left; Top = _windowedBounds.Top; Width = _windowedBounds.Width; Height = _windowedBounds.Height;
            _root.ColumnDefinitions[1].Width = new GridLength(SidebarWidth);
            Grid.SetColumn(_sidebarHost, 1);
            _sidebarHost.Visibility = Visibility.Visible;
        }
    }

    /// Full screen: the sidebar appears only when the pointer hits the very right edge and
    /// stays while the pointer is over it, so it never catches clicks meant for the game.
    void RevealSidebarIfNeeded(Point p)
    {
        if (!_fullScreen || _engine.IsAiming) return;
        var w = _root.ActualWidth;
        SetSidebarRevealed(_sidebarRevealed ? p.X >= w - SidebarWidth - 12 : p.X >= w - 2);
    }

    void SetSidebarRevealed(bool show)
    {
        if (!_fullScreen || show == _sidebarRevealed) return;
        _sidebarRevealed = show;
        _sidebarHost.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- Keyboard ----

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (_client == null && key != Key.F11) return;
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

        if (key == Key.F11) { ToggleFullScreen(); e.Handled = true; return; }
        if (_overlay.Mode == OverlayMode.Edit) return;   // the editor handles its own keys

        if (ctrl && key is not (Key.LeftCtrl or Key.RightCtrl))
        {
            Action? shortcut = (key, shift) switch
            {
                (Key.K, false) => ToggleKeymap,
                (Key.E, false) => () => SetEditing(true),
                (Key.OemQuestion, false) => () => { Settings.Current.ShowKeyHints = !Settings.Current.ShowKeyHints; Settings.Save(); UpdateKeymapState(); },
                (Key.V, false) => PasteFromPc,
                (Key.C, false) => () => _screen.SendControlShortcut(8),
                (Key.X, false) => () => _screen.SendControlShortcut(7),
                (Key.A, false) => () => _screen.SendControlShortcut(0),
                (Key.O, false) => ChooseApk,
                (Key.H, true) => () => _client?.Press("GoHome"),
                (Key.R, true) => () => _client?.Press("AppSwitch"),
                (Key.S, true) => SaveScreenshot,
                _ => null,
            };
            if (shortcut != null) { if (!e.IsRepeat) shortcut(); e.Handled = true; return; }
        }
        _screen.HandleKeyDown(key, e.IsRepeat);
        e.Handled = true;   // keep Space/Tab/Alt from driving WPF focus or menus
    }

    void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (_client == null || _overlay.Mode == OverlayMode.Edit) return;
        _screen.HandleKeyUp(e.Key == Key.System ? e.SystemKey : e.Key);
        e.Handled = true;
    }

    // ---- Closing ----

    async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_fullScreen && WindowState == WindowState.Normal)
        {
            Settings.Current.WindowRect = [Left, Top, Width, Height];
            Settings.Save();
        }
        if (_closeReady || _manager.Pid == null)
        {
            _audio?.Dispose();
            if (_restartAfterClose) Updater.RestartNow();
            return;
        }
        e.Cancel = true;
        if (_quitting) return;
        _quitting = true;
        _screen.ReleaseAllInput();
        ShowStatus("Saving and closing Android…", true);
        await _manager.ShutdownAsync();
        _closeReady = true;
        Close();
    }
}
