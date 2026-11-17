using System.Runtime.InteropServices;
using Microsoft.Win32;
using Flair.Core;

namespace Flair.Platform;

/// <summary>Double-clicking a .flair file someone sent you opens it in Flair. Per user, no admin rights.</summary>
public static class FileAssociation
{
    private const string ProgId = "Flair.Preset";

    public static void Refresh()
    {
        if (AppPaths.IsIsolatedTestRun || !Updater.CanSelfUpdate) return; // only installed copies claim the file type
        try
        {
            string exe = Environment.ProcessPath!;
            string command = $"\"{exe}\" \"%1\"";
            using (var existing = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ProgId}\shell\open\command"))
                if (existing?.GetValue(null) as string == command) return;

            using (var ext = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + PresetFile.Extension))
            {
                ext.SetValue(null, ProgId);
                ext.SetValue("Content Type", "application/json");
            }
            using (var prog = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}"))
            {
                prog.SetValue(null, "Flair status preset");
                using (var icon = prog.CreateSubKey("DefaultIcon")) icon.SetValue(null, $"\"{exe}\",0");
                using (var cmd = prog.CreateSubKey(@"shell\open\command")) cmd.SetValue(null, command);
            }
            SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Log.Warn($"Couldn't register .flair files: {ex.Message}");
        }
    }

    public static void Remove()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + PresetFile.Extension, throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{ProgId}", throwOnMissingSubKey: false);
            SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
        }
        catch { }
    }

    [DllImport("shell32.dll")] private static extern void SHChangeNotify(int eventId, int flags, IntPtr item1, IntPtr item2);
}
