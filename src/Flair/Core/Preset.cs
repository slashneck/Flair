using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Flair.Core;

/// <summary>Discord's activity types that a Rich Presence app may use. Streaming needs a Twitch or YouTube stream, so it isn't here.</summary>
public enum ActivityKind
{
    Playing = 0,
    Listening = 2,
    Watching = 3,
    Competing = 5,
}

public sealed class PresetButton
{
    public string Label { get; set; } = "";
    public string Url { get; set; } = "";
}

/// <summary>One saved status: everything Discord shows on the profile card.</summary>
public sealed class Preset
{
    public const int MaxText = 128;
    public const int MaxButtonLabel = 32;
    public const int MaxUrl = 512;
    /// <summary>The sparkle uploaded to Flair's own Discord app.</summary>
    public const string FlairPicture = "flair";

    public string Id { get; set; } = NewId();
    public string Name { get; set; } = "New status";
    /// <summary>The Discord application whose name shows after "Playing". Empty uses the one from Settings.</summary>
    public string AppId { get; set; } = "";
    public ActivityKind Kind { get; set; } = ActivityKind.Playing;
    public string Details { get; set; } = "";
    public string State { get; set; } = "";
    public string LargeImage { get; set; } = "";
    public string LargeText { get; set; } = "";
    public string SmallImage { get; set; } = "";
    public string SmallText { get; set; } = "";
    public List<PresetButton> Buttons { get; set; } = new();
    /// <summary>none | elapsed (since the status went live) | countdown (for <see cref="CountdownMinutes"/>) | until (a time of day)</summary>
    public string Timer { get; set; } = "elapsed";
    public int CountdownMinutes { get; set; } = 60;
    public string UntilTime { get; set; } = "20:00";
    public int PartySize { get; set; }
    public int PartyMax { get; set; }
    /// <summary>What the member list shows next to "Playing": name (the app), details or state.</summary>
    public string ListShows { get; set; } = "name";
    public HotkeySetting Hotkey { get; set; } = new();

    public static string NewId() => Guid.NewGuid().ToString("N")[..12];

    public void Sanitize()
    {
        if (string.IsNullOrWhiteSpace(Id)) Id = NewId();
        Name = Clip(Name, 60).Trim();
        if (Name.Length == 0) Name = "Untitled";
        AppId = new string((AppId ?? "").Where(char.IsAsciiDigit).Take(24).ToArray());
        if (!Enum.IsDefined(Kind)) Kind = ActivityKind.Playing;
        Details = Clip(Details, MaxText);
        State = Clip(State, MaxText);
        LargeImage = Clip(LargeImage, 300).Trim();
        LargeText = Clip(LargeText, MaxText);
        SmallImage = Clip(SmallImage, 300).Trim();
        SmallText = Clip(SmallText, MaxText);
        Buttons = (Buttons ?? new()).Where(b => b != null).Take(2).Select(b => new PresetButton
        {
            Label = Clip(b.Label, MaxButtonLabel),
            Url = Clip(b.Url, MaxUrl).Trim(),
        }).ToList();
        Timer = Timer is "none" or "elapsed" or "countdown" or "until" ? Timer : "none";
        CountdownMinutes = Math.Clamp(CountdownMinutes, 1, 60 * 24 * 7);
        if (!TimeOnly.TryParse(UntilTime ?? "", out _)) UntilTime = "20:00";
        PartyMax = Math.Clamp(PartyMax, 0, 999);
        PartySize = Math.Clamp(PartySize, 0, PartyMax);
        ListShows = ListShows is "name" or "details" or "state" ? ListShows : "name";
        Hotkey ??= new HotkeySetting();
    }

    /// <summary>For presets from someone else: a button that isn't a plain web link has no business being kept.</summary>
    public void DropBadButtons() => Buttons.RemoveAll(b => !IsWebLink(b.Url));

    private static string Clip(string? s, int max) => s == null ? "" : s.Length <= max ? s : s[..max];

    public Preset Clone() => JsonSerializer.Deserialize<Preset>(JsonSerializer.Serialize(this, PresetStore.Json), PresetStore.Json)!;

    /// <summary>
    /// The activity as Discord's SET_ACTIVITY wants it. <paramref name="liveSince"/> is when the status went live
    /// (elapsed timers count from there), <paramref name="shownSince"/> when this preset came up (countdowns start there).
    /// </summary>
    public JsonObject ToActivity(DateTimeOffset liveSince, DateTimeOffset shownSince)
    {
        var a = new JsonObject { ["type"] = (int)Kind, ["instance"] = false };
        if (Text(Details) is { } details) a["details"] = details;
        if (Text(State) is { } state) a["state"] = state;
        a["status_display_type"] = ListShows switch { "details" when a["details"] != null => 2, "state" when a["state"] != null => 1, _ => 0 };

        var assets = new JsonObject();
        if (Image(LargeImage) is { } large)
        {
            assets["large_image"] = large;
            if (Text(LargeText) is { } lt) assets["large_text"] = lt;
        }
        if (Image(SmallImage) is { } small)
        {
            assets["small_image"] = small;
            if (Text(SmallText) is { } st) assets["small_text"] = st;
        }
        if (assets.Count > 0) a["assets"] = assets;

        switch (Timer)
        {
            case "elapsed":
                a["timestamps"] = new JsonObject { ["start"] = liveSince.ToUnixTimeMilliseconds() };
                break;
            case "countdown":
                a["timestamps"] = new JsonObject
                {
                    ["start"] = shownSince.ToUnixTimeMilliseconds(),
                    ["end"] = shownSince.AddMinutes(CountdownMinutes).ToUnixTimeMilliseconds(),
                };
                break;
            case "until":
                var end = NextTimeOfDay(UntilTime, DateTimeOffset.Now);
                a["timestamps"] = new JsonObject
                {
                    ["start"] = shownSince.ToUnixTimeMilliseconds(),
                    ["end"] = end.ToUnixTimeMilliseconds(),
                };
                break;
        }

        // Discord only shows a party with a size, and only next to a state line.
        if (PartyMax > 0 && a["state"] != null)
            a["party"] = new JsonObject { ["id"] = "flair-" + Id, ["size"] = new JsonArray(Math.Max(1, PartySize), PartyMax) };

        var buttons = new JsonArray();
        foreach (var b in Buttons)
            if (Text(b.Label) is { } label && IsWebLink(b.Url)) buttons.Add(new JsonObject { ["label"] = label, ["url"] = b.Url.Trim() });
        if (buttons.Count > 0) a["buttons"] = buttons;
        return a;
    }

    public static DateTimeOffset NextTimeOfDay(string hhmm, DateTimeOffset now)
    {
        var time = TimeOnly.TryParse(hhmm, out var t) ? t : new TimeOnly(20, 0);
        var at = new DateTimeOffset(now.Date + time.ToTimeSpan(), now.Offset);
        return at <= now ? at.AddDays(1) : at;
    }

    // Discord rejects text of a single character, so it gets an invisible companion.
    private static string? Text(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        return s.Length == 1 ? s + "⠀" : s;
    }

    /// <summary>An HTTPS picture link, or the key of a picture uploaded to the Discord application.</summary>
    private static string? Image(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        if (s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return Uri.TryCreate(s, UriKind.Absolute, out _) ? s : null;
        if (s.Contains("://")) return null;
        return s.ToLowerInvariant();
    }

    public static bool IsWebLink(string? url) =>
        Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp);
}

/// <summary>All presets, in their order, stored in %AppData%\Flair\presets.json.</summary>
public static class PresetStore
{
    public const int MaxPresets = 200;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string FilePath => Path.Combine(AppPaths.DataDir, "presets.json");

    private sealed class FileShape
    {
        public int Version { get; set; } = 1;
        public List<Preset> Presets { get; set; } = new();
    }

    public static List<Preset> Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var file = JsonSerializer.Deserialize<FileShape>(File.ReadAllText(FilePath), Json);
                if (file?.Presets != null)
                {
                    var list = file.Presets.Where(p => p != null).Take(MaxPresets).ToList();
                    list.ForEach(p => p.Sanitize());
                    return Dedupe(list);
                }
            }
            else
            {
                return Starters();
            }
        }
        catch (Exception ex)
        {
            Log.Error("presets.json is unreadable; keeping a backup", ex);
            try { File.Copy(FilePath, FilePath + ".broken", overwrite: true); } catch { }
        }
        return new List<Preset>();
    }

    public static void Save(IEnumerable<Preset> presets)
    {
        string tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(new FileShape { Presets = presets.ToList() }, Json));
        File.Move(tmp, FilePath, overwrite: true);
    }

    private static List<Preset> Dedupe(List<Preset> list)
    {
        var seen = new HashSet<string>();
        foreach (var p in list)
            if (!seen.Add(p.Id)) { p.Id = Preset.NewId(); seen.Add(p.Id); }
        return list;
    }

    /// <summary>A few statuses to start from on the very first run.</summary>
    private static List<Preset> Starters() => new()
    {
        new Preset
        {
            Name = "Locked in", Kind = ActivityKind.Playing, Details = "Deep in the zone", State = "Do not disturb",
            LargeImage = Preset.FlairPicture, Timer = "elapsed",
        },
        new Preset
        {
            Name = "Lobby open", Kind = ActivityKind.Competing, Details = "Ranked grind", State = "Looking for a duo",
            PartySize = 1, PartyMax = 2, LargeImage = Preset.FlairPicture, Timer = "elapsed",
        },
        new Preset
        {
            Name = "Back at 8", Kind = ActivityKind.Watching, Details = "Away for a bit", State = "Back soon",
            LargeImage = Preset.FlairPicture, Timer = "until", UntilTime = "20:00",
        },
    };
}

/// <summary>The small .flair file for sharing presets: the presets as JSON, without hotkeys.</summary>
public static class PresetFile
{
    public const string Extension = ".flair";
    private const long MaxBytes = 512 * 1024;

    private sealed class Shape
    {
        public string Flair { get; set; } = "presets";
        public int Version { get; set; } = 1;
        public List<Preset> Presets { get; set; } = new();
    }

    public static void Write(string path, IEnumerable<Preset> presets)
    {
        var copies = presets.Select(p =>
        {
            var c = p.Clone();
            c.Hotkey = new HotkeySetting();
            return c;
        }).ToList();
        File.WriteAllText(path, JsonSerializer.Serialize(new Shape { Presets = copies }, PresetStore.Json));
    }

    /// <summary>Reads presets from a file someone shared. Everything is checked and gets fresh IDs.</summary>
    public static List<Preset> Read(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("That file doesn't exist anymore");
        if (info.Length > MaxBytes) throw new InvalidDataException("That file is too big to be a Flair preset");
        Shape? shape;
        try
        {
            shape = JsonSerializer.Deserialize<Shape>(File.ReadAllText(path), PresetStore.Json);
        }
        catch (JsonException)
        {
            throw new InvalidDataException("That isn't a Flair preset file");
        }
        if (shape?.Flair != "presets" || shape.Presets == null || shape.Presets.Count == 0)
            throw new InvalidDataException("That isn't a Flair preset file");
        return shape.Presets.Where(p => p != null).Take(50).Select(p =>
        {
            p.Id = Preset.NewId();
            p.Hotkey = new HotkeySetting();
            p.Sanitize();
            p.DropBadButtons();
            return p;
        }).ToList();
    }
}
