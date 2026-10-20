using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace Pocketbay;

/// Self-updates from GitHub Releases. A newer Pocketbay-Windows.zip is downloaded in the
/// background and swapped in the next time Pocketbay starts (a running .exe can be
/// renamed but not overwritten on Windows).
public static class Updater
{
    const string Repo = "deveshpat/pocketbay";
    const string AssetName = "Pocketbay-Windows.zip";

    public static Version Current => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
    static string Dir => Directory.CreateDirectory(Path.Combine(Paths.Local, "update")).FullName;
    static string PendingExe => Path.Combine(Dir, "Pocketbay.exe");
    static string PendingVersion => Path.Combine(Dir, "version.txt");

    static readonly HttpClient Http = CreateHttp();
    static HttpClient CreateHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Pocketbay-Updater/" + Current);
        return h;
    }

    /// Called first thing in Main: installs a downloaded update and relaunches.
    /// Returns true if the process should exit (the new version is starting).
    public static bool ApplyPendingUpdate()
    {
        var exe = Environment.ProcessPath;
        if (exe == null) return false;
        try { if (File.Exists(exe + ".old")) File.Delete(exe + ".old"); } catch { }

        if (!File.Exists(PendingExe) || !File.Exists(PendingVersion)) return false;
        if (!Version.TryParse(File.ReadAllText(PendingVersion).Trim(), out var pending) || pending <= Current)
        {
            Cleanup();
            return false;
        }
        try
        {
            File.Move(exe, exe + ".old");           // allowed while running
            File.Move(PendingExe, exe);
            Cleanup();
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            Log.Write($"updated {Current} -> {pending}");
            return true;
        }
        catch (Exception e)
        {
            Log.Write("update rename failed, using helper: " + e);
            try { if (!File.Exists(exe) && File.Exists(exe + ".old")) File.Move(exe + ".old", exe); } catch { }
            return ApplyWithHelper(exe, pending);
        }
    }

    /// Fallback when the running .exe can't be renamed (folder permissions, antivirus):
    /// a small script waits for this process to exit, copies the new exe over the old
    /// one, and starts it.
    static bool ApplyWithHelper(string exe, Version pending)
    {
        try
        {
            var script = Path.Combine(Dir, "apply-update.cmd");
            var log = Path.Combine(Paths.Logs, "update-helper.log");
            File.WriteAllText(script, string.Join("\r\n", new[]
            {
                "@echo off",
                $"set PID={Environment.ProcessId}",
                ":wait",
                "tasklist /FI \"PID eq %PID%\" | find \"%PID%\" >nul && (timeout /t 1 /nobreak >nul & goto wait)",
                $"copy /y \"{PendingExe}\" \"{exe}\" >> \"{log}\" 2>&1",
                $"if errorlevel 1 (echo copy failed >> \"{log}\" & exit /b 1)",
                $"del \"{PendingExe}\" \"{PendingVersion}\"",
                $"start \"\" \"{exe}\"",
            }));
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"") { UseShellExecute = false, CreateNoWindow = true });
            Log.Write($"update {pending} handed to helper");
            return true;   // exit now; the helper restarts Pocketbay
        }
        catch (Exception e)
        {
            Log.Write("update helper failed: " + e);
            return false;
        }
    }

    static void Cleanup()
    {
        try { File.Delete(PendingExe); } catch { }
        try { File.Delete(PendingVersion); } catch { }
    }

    /// Checks GitHub; if a newer release exists, downloads it. Returns the new version,
    /// or null when up to date (or offline).
    static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task<Version?> DownloadUpdateAsync()
    {
        await Gate.WaitAsync();   // the periodic and manual checks must not download at once
        try
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest"));
            var tag = doc.RootElement.GetProperty("tag_name").GetString()?.TrimStart('v', 'V') ?? "";
            if (!Version.TryParse(tag, out var latest) || latest <= Current) return null;
            if (File.Exists(PendingVersion) && File.ReadAllText(PendingVersion).Trim() == latest.ToString() && File.Exists(PendingExe))
                return latest;   // already downloaded

            var url = doc.RootElement.GetProperty("assets").EnumerateArray()
                .Where(a => a.GetProperty("name").GetString() == AssetName)
                .Select(a => a.GetProperty("browser_download_url").GetString()).FirstOrDefault();
            if (url == null) return null;

            var zip = Path.Combine(Dir, AssetName);
            await using (var input = await Http.GetStreamAsync(url))
            await using (var output = File.Create(zip))
                await input.CopyToAsync(output);

            using (var archive = ZipFile.OpenRead(zip))
            {
                var entry = archive.Entries.First(e => e.Name.Equals("Pocketbay.exe", StringComparison.OrdinalIgnoreCase));
                entry.ExtractToFile(PendingExe, true);
            }
            File.Delete(zip);
            // Sanity check: a Windows executable starts with "MZ".
            var head = new byte[2];
            await using (var fs = File.OpenRead(PendingExe)) await fs.ReadExactlyAsync(head);
            if (head[0] != 'M' || head[1] != 'Z') { Cleanup(); return null; }
            File.WriteAllText(PendingVersion, latest.ToString());
            Log.Write($"update {latest} downloaded");
            return latest;
        }
        catch (Exception e)
        {
            Log.Write("update check: " + e.Message);
            return null;
        }
        finally { Gate.Release(); }
    }

    public static void RestartNow()
    {
        var exe = Environment.ProcessPath;
        if (exe != null) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
    }
}
