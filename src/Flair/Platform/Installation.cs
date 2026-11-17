using System.Diagnostics;
using Microsoft.Win32;
using Flair.Core;
using Flair.UI;

namespace Flair.Platform;

/// <summary>
/// The per-user install the setup creates: the "Installed apps" entry and uninstalling.
/// Uninstalling removes the program only. Your presets and settings stay on the PC.
/// </summary>
public static class Installation
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Flair";

    public static void RefreshUninstallEntry(string installDir)
    {
        if (AppPaths.IsIsolatedTestRun) return; // test runs never touch the real "Installed apps" list
        try
        {
            string exe = Path.Combine(installDir, "Flair.exe");
            long kb = new DirectoryInfo(installDir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) / 1024;
            using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
            key.SetValue("DisplayName", "Flair");
            key.SetValue("DisplayVersion", Updater.Current.ToString());
            key.SetValue("Publisher", "Flair");
            key.SetValue("DisplayIcon", exe);
            key.SetValue("InstallLocation", installDir);
            key.SetValue("UninstallString", $"\"{exe}\" --uninstall");
            key.SetValue("URLInfoAbout", Updater.RepoUrl);
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            key.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, kb), RegistryValueKind.DWord);
        }
        catch (Exception ex)
        {
            Log.Warn($"Couldn't update the uninstall entry: {ex.Message}");
        }
    }

    public static int Uninstall()
    {
        string installDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        if (!File.Exists(Path.Combine(installDir, Updater.ManifestName)))
        {
            DarkDialog.Ask("Nothing to uninstall", "This copy of Flair wasn't installed by the setup, so you can simply delete its folder.", "OK", null);
            return 1;
        }
        if (!DarkDialog.Ask("Uninstall Flair?",
                "Flair is removed from this PC and your Discord status is cleared. Your presets stay in %AppData%\\Flair.",
                "Uninstall"))
            return 1;

        StopOtherInstances(installDir);
        try { Autostart.Apply(false); } catch { }
        FileAssociation.Remove();
        foreach (string shortcut in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Flair.lnk"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Flair.lnk"),
                 })
        {
            try { File.Delete(shortcut); } catch { }
        }
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false); } catch { }

        string self = Environment.ProcessPath ?? Path.Combine(installDir, "Flair.exe");
        foreach (string rel in Updater.ReadManifest(installDir))
        {
            string path = Path.Combine(installDir, rel);
            if (!path.Equals(self, StringComparison.OrdinalIgnoreCase)) Updater.DeleteWithRetry(path);
        }
        Updater.DeleteWithRetry(Path.Combine(installDir, Updater.ManifestName));
        Updater.RemoveEmptyFolders(installDir);

        // Caches only (the WebView profile). Settings live in the roaming folder and stay.
        try { Directory.Delete(AppPaths.LocalDataDir, recursive: true); } catch { }

        DarkDialog.Ask("Flair is uninstalled", "Your presets are still in %AppData%\\Flair, in case you come back.", "OK", null);

        // The running exe can't delete itself, so a short-lived cmd removes it and the then empty folder.
        Process.Start(new ProcessStartInfo("cmd.exe",
            $"/c ping 127.0.0.1 -n 3 >nul & del /f /q \"{self}\" & rd \"{installDir}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        return 0;
    }

    private static void StopOtherInstances(string installDir)
    {
        foreach (var proc in Process.GetProcessesByName("Flair"))
        {
            using (proc)
            {
                try
                {
                    if (proc.Id == Environment.ProcessId) continue;
                    string? path = proc.MainModule?.FileName;
                    if (path == null || !path.StartsWith(installDir, StringComparison.OrdinalIgnoreCase)) continue;
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(5000);
                }
                catch { }
            }
        }
    }
}
