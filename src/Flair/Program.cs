using Flair.Core;
using Flair.Platform;
using Flair.UI;

namespace Flair;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length >= 3 && args[0] == "--apply-update")
        {
            ApplicationConfiguration.Initialize();
            return Updater.ApplyUpdate(args[1], int.TryParse(args[2], out int oldPid) ? oldPid : 0);
        }
        if (args.Length > 0 && args[0] == "--uninstall")
        {
            ApplicationConfiguration.Initialize();
            return Installation.Uninstall();
        }

#if DEBUG
        if (args.Length > 1 && args[0] == "--test-presence")
            return SelfTest.Presence(args[1], args.Length > 2 ? int.Parse(args[2]) : 10);
#endif

        // A .flair file that was double-clicked or dropped on the exe
        string? openPath = args.FirstOrDefault(a => !a.StartsWith("--") && a.EndsWith(PresetFile.Extension, StringComparison.OrdinalIgnoreCase));
        if (openPath != null) openPath = Path.GetFullPath(openPath);

        using var mutex = new Mutex(true, @"Local\Flair-SingleInstance" + AppPaths.InstanceSuffix, out bool isFirst);
        if (!isFirst)
        {
            // Already running: hand the file over (or just ask it to show its window).
            OpenRequests.SendToRunning(openPath);
            return 0;
        }

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Error("UI thread exception", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Fatal exception", e.ExceptionObject as Exception);

        ApplicationConfiguration.Initialize();
        Log.Info($"Flair {Updater.Current} starting (args: {string.Join(' ', args)})");
        Updater.CleanUp();
        using var tray = new TrayApp(background: args.Contains("--background") || args.Contains("--updated"), openPath);
        Application.Run(tray);
        Log.Info("Flair exited");
        return 0;
    }
}

#if DEBUG
/// <summary>Headless checks used during development.</summary>
internal static class SelfTest
{
    /// <summary>Shows a sample status with the given application ID for a few seconds and prints what Discord said.</summary>
    public static int Presence(string appId, int seconds)
    {
        using var presence = new Discord.PresenceService();
        presence.Changed += s => Console.WriteLine($"state={s.State} error={s.Error} user={s.User?.UserName}");
        var preset = new Preset
        {
            Name = "Self test", Kind = ActivityKind.Playing, Details = "Testing Flair", State = "In a lobby",
            LargeImage = "https://cdn.discordapp.com/embed/avatars/0.png", LargeText = "Large", PartySize = 2, PartyMax = 5,
            Buttons = { new PresetButton { Label = "GitHub", Url = "https://github.com/slashneck" } },
        };
        preset.Sanitize();
        presence.Show(appId, preset.ToActivity(DateTimeOffset.Now, DateTimeOffset.Now));
        Thread.Sleep(seconds * 1000);
        var final = presence.Status;
        Console.WriteLine($"final={final.State} {final.Error}");
        return final.IsLive ? 0 : 1;
    }
}
#endif
