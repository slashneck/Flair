using System.Text.Json;
using System.Text.Json.Serialization;

namespace Flair.Core;

/// <summary>Everything Flair remembers besides the presets. Stored in %AppData%\Flair\settings.json.</summary>
public sealed class AppSettings
{
    public const int CurrentVersion = 1;
    public const int MinRotateSeconds = 15; // Discord only takes a few status changes per 20 seconds

    public int SettingsVersion { get; set; }

    /// <summary>Flair's own Discord application, so the status reads "Playing Flair" until someone brings their own.</summary>
    public const string DefaultAppId = "1572290055270310030";

    /// <summary>The Discord application ID used by presets that don't bring their own. Empty uses <see cref="DefaultAppId"/>.</summary>
    public string AppId { get; set; } = "";

    // What's showing
    public bool Live { get; set; }
    public string? ActivePresetId { get; set; }
    /// <summary>When the status last went live, so a restart keeps the elapsed timer running instead of starting at 0:00.</summary>
    public long LiveSinceMs { get; set; }

    // Rotation
    public bool RotateEnabled { get; set; }
    public List<string> RotatePresetIds { get; set; } = new();
    public int RotateSeconds { get; set; } = 60;
    public bool RotateShuffle { get; set; }

    // Hotkeys
    public HotkeySetting ToggleHotkey { get; set; } = new() { Key = Keys.F, Ctrl = true, Alt = true };
    public HotkeySetting NextHotkey { get; set; } = new() { Key = Keys.N, Ctrl = true, Alt = true };

    // App
    public bool StartWithWindows { get; set; } = true;
    public bool CheckForUpdates { get; set; } = true;
    public bool GuideSeen { get; set; }
    public string? LastSeenVersion { get; set; }

    [JsonIgnore]
    public static string FilePath => Path.Combine(AppPaths.DataDir, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions);
                if (loaded != null)
                {
                    loaded.SettingsVersion = CurrentVersion;
                    loaded.Sanitize();
                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("settings.json is unreadable; keeping a backup and using defaults", ex);
            try { File.Copy(FilePath, FilePath + ".broken", overwrite: true); } catch { }
        }
        return new AppSettings { SettingsVersion = CurrentVersion };
    }

    public void Save()
    {
        Sanitize();
        string tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(tmp, FilePath, overwrite: true); // atomic replace: a crash can't leave half a file
    }

    public AppSettings Clone() =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, JsonOptions), JsonOptions)!;

    public void Sanitize()
    {
        AppId = new string((AppId ?? "").Where(char.IsAsciiDigit).Take(24).ToArray());
        RotatePresetIds = (RotatePresetIds ?? new()).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList();
        RotateSeconds = Math.Clamp(RotateSeconds, MinRotateSeconds, 24 * 3600);
        ToggleHotkey ??= new HotkeySetting();
        NextHotkey ??= new HotkeySetting();
    }
}

public sealed class HotkeySetting
{
    public Keys Key { get; set; } = Keys.None;
    public bool Ctrl { get; set; }
    public bool Alt { get; set; }
    public bool Shift { get; set; }
    public bool Win { get; set; }

    [JsonIgnore] public bool IsSet => Key != Keys.None;

    public bool SameAs(HotkeySetting other) =>
        IsSet && Key == other.Key && Ctrl == other.Ctrl && Alt == other.Alt && Shift == other.Shift && Win == other.Win;

    public override string ToString()
    {
        if (!IsSet) return "None";
        var parts = new List<string>();
        if (Ctrl) parts.Add("Ctrl");
        if (Alt) parts.Add("Alt");
        if (Shift) parts.Add("Shift");
        if (Win) parts.Add("Win");
        parts.Add(KeyName(Key));
        return string.Join(" + ", parts);
    }

    private static string KeyName(Keys k) => k switch
    {
        >= Keys.D0 and <= Keys.D9 => ((int)k - (int)Keys.D0).ToString(),
        >= Keys.NumPad0 and <= Keys.NumPad9 => "Num " + ((int)k - (int)Keys.NumPad0),
        Keys.Oemcomma => ",",
        Keys.OemPeriod => ".",
        Keys.OemMinus => "-",
        Keys.Oemplus => "+",
        Keys.Next => "Page Down",
        Keys.Prior => "Page Up",
        _ => k.ToString(),
    };
}
