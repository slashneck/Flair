namespace Flair.UI;

/// <summary>Icons, all embedded in Flair.exe.</summary>
internal static class AppAssets
{
    private static readonly Dictionary<string, Icon> Icons = new();

    public static Stream Open(string name) =>
        typeof(AppAssets).Assembly.GetManifestResourceStream("Flair.Assets." + name)
        ?? throw new FileNotFoundException($"Embedded asset missing: {name}");

    /// <summary>Tray icon for a state (live, off, waiting) at the exact size Windows wants for this DPI.</summary>
    public static Icon TrayIcon(string state)
    {
        var size = SystemInformation.SmallIconSize;
        string key = $"tray-{state}-{size.Width}";
        lock (Icons)
        {
            if (!Icons.TryGetValue(key, out var icon))
            {
                using var s = Open($"tray-{state}.ico");
                icon = new Icon(s, size);
                Icons[key] = icon;
            }
            return icon;
        }
    }

    public static Icon AppIcon()
    {
        lock (Icons)
        {
            if (!Icons.TryGetValue("app", out var icon))
            {
                using var s = Open("flair.ico");
                icon = new Icon(s);
                Icons["app"] = icon;
            }
            return icon;
        }
    }
}
