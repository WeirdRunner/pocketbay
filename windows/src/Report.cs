using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using Microsoft.Win32;

namespace Pocketbay;

/// "Report a problem": zips logs + a system summary onto the Desktop and opens a
/// pre-filled GitHub issue. Nothing is uploaded automatically.
public static class Report
{
    const string IssuesUrl = "https://github.com/deveshpat/pocketbay/issues/new";

    public static async Task<string> CreateAsync()
    {
        var summary = await SystemSummaryAsync();
        // Android's own logs (why an app crashed or closed), if Android is running.
        string Adb(params string[] a) => File.Exists(Paths.Adb)
            ? EmulatorManager.Run(Paths.Adb, ["-s", "emulator-5556", .. a], 30_000).GetAwaiter().GetResult().Output : "";
        var crash = await Task.Run(() => Adb("logcat", "-d", "-b", "crash"));
        var main = await Task.Run(() => Adb("logcat", "-d", "-t", "8000", "-b", "main,system,events"));
        var device = await Task.Run(() => Adb("shell", "getprop ro.product.cpu.abilist; getprop ro.build.fingerprint; dumpsys package com.pubg.imobile | grep -E 'versionName|primaryCpuAbi'"));
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var zipPath = Path.Combine(desktop, $"Pocketbay-logs-{DateTime.Now:yyyyMMdd-HHmm}.zip");
        if (File.Exists(zipPath)) File.Delete(zipPath);

        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            void AddText(string name, string text) { using var w = new StreamWriter(zip.CreateEntry(name).Open()); w.Write(Anonymize(text)); }
            AddText("system.txt", summary);
            foreach (var file in Directory.GetFiles(Paths.Logs, "*.log"))
            {
                string text;
                try { using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); text = new StreamReader(fs).ReadToEnd(); }
                catch { continue; }
                // Keep the end of big logs; that's where the failure is.
                if (text.Length > 4_000_000) text = text[^4_000_000..];
                AddText(Path.GetFileName(file), text);
            }
            if (File.Exists(Paths.Settings)) AddText("settings.json", File.ReadAllText(Paths.Settings));
            if (crash.Length > 0) AddText("android-crash.log", crash);
            if (main.Length > 0) AddText("android-logcat.log", main);
            if (device.Length > 0) AddText("android-device.txt", device);
        }
        return zipPath;
    }

    public static void ShowAndOpenIssue(string zipPath, string summary)
    {
        try { Process.Start("explorer.exe", $"/select,\"{zipPath}\""); } catch { }
        var body = "**What happened?**\n\n\n**What did you expect?**\n\n\n" +
                   $"**Logs:** please drag `{Path.GetFileName(zipPath)}` (it's on your Desktop) into this box.\n\n" +
                   "```\n" + Anonymize(summary) + "```\n";
        var url = $"{IssuesUrl}?title={Uri.EscapeDataString("Problem: ")}&body={Uri.EscapeDataString(body)}";
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    /// Replaces the Windows user name (it appears in file paths) with a placeholder.
    static string Anonymize(string text)
    {
        var user = Environment.UserName;
        return string.IsNullOrEmpty(user) || user.Length < 2 ? text : text.Replace(user, "<user>", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<string> SystemSummaryAsync()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Pocketbay {Updater.Current.ToString(3)}");
        sb.AppendLine($"Windows {Environment.OSVersion.Version} ({(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")})");
        sb.AppendLine($"CPU: {Reg(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString")?.Trim() ?? "?"} ({Environment.ProcessorCount} threads)");
        sb.AppendLine($"RAM: {GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024.0 * 1024 * 1024):0.0} GB");
        foreach (var gpu in Gpus()) sb.AppendLine($"GPU: {gpu}");
        sb.AppendLine($"Free disk: {SdkInstaller.FreeBytes() / (1024.0 * 1024 * 1024):0} GB");
        sb.AppendLine($"Graphics mode: {Settings.Current.Graphics}");
        sb.AppendLine($"Android installed: {SdkInstaller.IsInstalled}");
        if (File.Exists(Paths.Emulator))
        {
            var (_, accel) = await EmulatorManager.Run(Paths.Emulator, ["-accel-check"], 30_000);
            sb.AppendLine("Virtualization: " + accel.Trim().Split('\n').LastOrDefault(l => l.Trim().Length > 0)?.Trim());
        }
        return sb.ToString();
    }

    static string? Reg(string key, string value)
    {
        try { using var k = Registry.LocalMachine.OpenSubKey(key); return k?.GetValue(value) as string; } catch { return null; }
    }

    static IEnumerable<string> Gpus()
    {
        const string cls = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
        using var root = Registry.LocalMachine.OpenSubKey(cls);
        if (root == null) yield break;
        foreach (var sub in root.GetSubKeyNames().Where(n => n.All(char.IsDigit)))
        {
            using var k = root.OpenSubKey(sub);
            if (k?.GetValue("DriverDesc") is string desc)
                yield return $"{desc} (driver {k.GetValue("DriverVersion") as string ?? "?"})";
        }
    }
}
