using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace Pocketbay;

/// Starts/attaches to the Pocketbay AVD and wraps the adb calls the app needs.
public sealed class EmulatorManager
{
    public const string AvdName = "Pocketbay";
    public const int GrpcPort = 8554;
    public const int ConsolePort = 5556;
    public string Serial => $"emulator-{ConsolePort}";

    public int? Pid { get; private set; }
    Process? _proc;
    string LogFile => Path.Combine(Paths.Logs, "emulator.log");

    public sealed record Endpoint(int Port, string Token);

    public bool IsRunning
    {
        get
        {
            if (Pid is not { } pid) return false;
            try { return !Process.GetProcessById(pid).HasExited; } catch { return false; }
        }
    }

    // ---- Discovery ----

    /// Folders the emulator may advertise itself in (pid_<pid>.ini).
    IEnumerable<string> DiscoveryDirs()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(Path.GetTempPath(), "avd", "running");
        yield return Path.Combine(local, "Temp", "avd", "running");
        // The emulator also prints its discovery path to the log ("Advertising in: …").
        if (File.Exists(LogFile))
        {
            string text;
            try { using var fs = new FileStream(LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); text = new StreamReader(fs).ReadToEnd(); }
            catch { yield break; }
            var m = Regex.Match(text, @"Advertising in:\s*(.+pid_\d+\.ini)");
            if (m.Success) yield return Path.GetDirectoryName(m.Groups[1].Value.Trim())!;
        }
    }

    public (int Pid, Endpoint Endpoint)? FindRunning()
    {
        foreach (var dir in DiscoveryDirs().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.GetFiles(dir, "pid_*.ini"))
            {
                Dictionary<string, string> kv;
                try
                {
                    kv = File.ReadAllLines(file).Select(l => l.Split('=', 2)).Where(p => p.Length == 2)
                        .GroupBy(p => p[0].Trim()).ToDictionary(g => g.Key, g => g.First()[1].Trim());
                }
                catch { continue; }
                if (!int.TryParse(Path.GetFileNameWithoutExtension(file)[4..], out var pid)) continue;
                if (kv.GetValueOrDefault("avd.name") != AvdName) continue;
                if (!int.TryParse(kv.GetValueOrDefault("grpc.port"), out var port) || !kv.TryGetValue("grpc.token", out var token)) continue;
                try { if (Process.GetProcessById(pid).HasExited) continue; } catch { continue; }
                return (pid, new Endpoint(port, token));
            }
        }
        return null;
    }

    // ---- Lifecycle ----

    public static string[] GraphicsArgs(string mode) => mode switch
    {
        "angle" => ["-gpu", "host", "-feature", "Vulkan", "-feature", "GuestAngle"],
        "software" => ["-gpu", "swiftshader"],
        _ => ["-gpu", "host"],
    };

    /// Attaches to a running emulator or launches one, then waits for its gRPC endpoint.
    public async Task<Endpoint> StartAsync()
    {
        if (FindRunning() is { } running) { Pid = running.Pid; return running.Endpoint; }

        ClearStaleLocks();
        var psi = new ProcessStartInfo(Paths.Emulator)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(Paths.Emulator)!,
        };
        foreach (var a in new[] { "-avd", AvdName, "-no-window", "-no-boot-anim", "-no-metrics",
                     "-grpc", GrpcPort.ToString(), "-grpc-use-token", "-port", ConsolePort.ToString(),
                     "-feature", "-WiFiPacketStream",
                     // Pocketbay plays Android's sound itself so it follows the Windows output device.
                     "-audio", "none" })
            psi.ArgumentList.Add(a);
        foreach (var a in GraphicsArgs(Settings.Current.Graphics)) psi.ArgumentList.Add(a);
        psi.Environment["ANDROID_SDK_ROOT"] = Paths.Sdk;
        psi.Environment["ANDROID_HOME"] = Paths.Sdk;
        psi.Environment["ANDROID_AVD_HOME"] = Paths.AvdHome;
        // The emulator writes settings/locks under the user's .android folder and loops
        // forever ("error 3", path not found) if it doesn't exist. Give it one we own.
        var userHome = Directory.CreateDirectory(Path.Combine(Paths.Local, "android-user")).FullName;
        psi.Environment["ANDROID_USER_HOME"] = userHome;
        psi.Environment["ANDROID_EMULATOR_HOME"] = userHome;
        try { Directory.CreateDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".android")); } catch { }

        KillStuckEmulator();
        try { File.WriteAllText(LogFile, ""); } catch (IOException) { }
        _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        void Append(string? line) { if (line == null) return; try { File.AppendAllText(LogFile, line + Environment.NewLine); } catch { } }
        _proc.OutputDataReceived += (_, e) => Append(e.Data);
        _proc.ErrorDataReceived += (_, e) => Append(e.Data);
        _proc.Start();
        _proc.BeginOutputReadLine();
        _proc.BeginErrorReadLine();

        for (int i = 0; i < 240; i++)
        {
            if (FindRunning() is { } r) { Pid = r.Pid; return r.Endpoint; }
            // On Windows emulator.exe can hand off to qemu-system-x86_64.exe and exit; only a
            // failing exit code (or no qemu left running) means Android didn't start.
            if (_proc.HasExited && (_proc.ExitCode != 0 || (i > 20 && !QemuRunning())))
                throw new InvalidOperationException("Android didn't start.\n\n" + LastLogLines(6) + $"\n\nFull log: {LogFile}");
            await Task.Delay(500);
        }
        KillStuckEmulator();
        throw new TimeoutException("Timed out starting Android.");
    }

    /// A previous attempt that never advertised itself (stuck) must not keep running and
    /// holding the log file.
    void KillStuckEmulator()
    {
        try { if (_proc is { HasExited: false }) _proc.Kill(true); } catch { }
        _proc = null;
        if (FindRunning() != null) return;
        foreach (var p in Process.GetProcesses())
        {
            try { if (p.ProcessName.StartsWith("qemu-system", StringComparison.OrdinalIgnoreCase)) p.Kill(true); } catch { }
        }
    }

    public string LastLogLines(int n)
    {
        try
        {
            using var fs = new FileStream(LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var lines = new StreamReader(fs).ReadToEnd().Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).ToList();
            var important = lines.Where(l => l.Contains("FATAL") || l.Contains("ERROR") || l.Contains("PANIC")).TakeLast(n).ToList();
            return string.Join("\n", important.Count > 0 ? important : lines.TakeLast(n));
        }
        catch { return ""; }
    }

    /// A crash or power loss leaves *.lock files behind and the emulator then refuses to
    /// start. Only removed when no emulator process is running at all.
    static bool QemuRunning() => Process.GetProcesses().Any(p =>
    {
        try { return p.ProcessName.StartsWith("qemu-system", StringComparison.OrdinalIgnoreCase); } catch { return false; }
    });

    void ClearStaleLocks()
    {
        if (QemuRunning()) return;
        var avdDir = Path.Combine(Paths.AvdHome, AvdName + ".avd");
        if (!Directory.Exists(avdDir)) return;
        foreach (var f in Directory.GetFiles(avdDir, "*.lock"))
            try { File.Delete(f); } catch { }
        foreach (var d in Directory.GetDirectories(avdDir, "*.lock"))
            try { Directory.Delete(d, true); } catch { }
    }

    /// Saves a quick-boot snapshot and stops the emulator.
    public async Task ShutdownAsync()
    {
        if (Pid is not { } pid) return;
        await RunAdb("emu", "kill");
        for (int i = 0; i < 160 && IsRunning; i++) await Task.Delay(250);
        if (IsRunning) try { Process.GetProcessById(pid).Kill(true); } catch { }
        Pid = null;
    }

    // ---- adb helpers ----

    /// Android settings Pocketbay enforces on every boot.
    public async Task ApplyDeviceDefaultsAsync()
    {
        // Never pop up the on-screen keyboard: the PC keyboard is always attached.
        await RunAdb("shell", "settings put secure show_ime_with_hard_keyboard 0");
    }

    public async Task<(bool Ok, string Message)> InstallAsync(string apk)
    {
        var (code, output) = await RunAdb("install", "-r", apk);
        var ok = code == 0 && output.Contains("Success");
        var name = Path.GetFileNameWithoutExtension(apk);
        var failure = output.Split('\n').LastOrDefault(l => l.Contains("Failure") || l.Contains("error"))?.Trim();
        return (ok, ok ? $"Installed {name}" : failure ?? "Install failed");
    }

    /// Package name of the app in front, e.g. "com.pubg.imobile".
    public async Task<string?> ForegroundPackageAsync()
    {
        var (_, output) = await RunAdb("shell", "dumpsys window | grep -E 'mCurrentFocus|mFocusedApp'");
        var m = Regex.Match(output, @"u0 ([A-Za-z0-9_.]+)/");
        return m.Success ? m.Groups[1].Value : null;
    }

    public Task<(int Code, string Output)> RunAdb(params string[] args) =>
        Run(Paths.Adb, ["-s", Serial, .. args]);

    public Task<(int Code, string Output)> RunAdbLong(string[] args) =>
        Run(Paths.Adb, ["-s", Serial, .. args], 60 * 60_000);

    public static async Task<(int Code, string Output)> Run(string exe, string[] args, int timeoutMs = 120_000)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(timeoutMs);
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch { } return (-1, "timed out"); }
            return (p.ExitCode, await outTask + await errTask);
        }
        catch (Exception e) { return (-1, e.Message); }
    }
}
