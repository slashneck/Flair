using Flair.Discord;
using Flair.Platform;

namespace Flair.Core;

/// <summary>A snapshot for the window: what's live, Discord's side of it and the rotation.</summary>
public sealed record EngineState(bool Live, string? ActivePresetId, long LiveSinceMs, long ShownSinceMs, PresenceStatus Discord,
    bool Rotating, long NextRotateMs);

/// <summary>
/// The heart of Flair: the presets, which one is live, the rotation timer and the hotkeys. The tray and the window
/// both drive it, and it hands the result to <see cref="PresenceService"/>.
/// All public members are called on the UI thread; <see cref="Changed"/> may fire on any thread.
/// </summary>
public sealed class StatusEngine : IDisposable
{
    private readonly PresenceService _presence = new();
    private readonly HotkeyListener _hotkeys = new();
    private readonly System.Threading.Timer _rotateTimer;
    private readonly SynchronizationContext _ui;
    private List<Preset> _presets;
    private DateTimeOffset _shownSince = DateTimeOffset.Now;
    private DateTimeOffset _nextRotate = DateTimeOffset.MaxValue;
    private int _shuffleBag;
    private readonly List<string> _bag = new();
    private Action[] _hotkeyActions = Array.Empty<Action>();

    public AppSettings Settings { get; private set; }
    public IReadOnlyList<Preset> Presets => _presets;
    public PresenceService Presence => _presence;

    /// <summary>Something changed that the window or tray should show.</summary>
    public event Action? Changed;
    /// <summary>A hotkey switched the status; the tray shows a small note about it.</summary>
    public event Action<string>? Announce;

    public StatusEngine(AppSettings settings)
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        Settings = settings;
        _presets = PresetStore.Load();
        if (!File.Exists(PresetStore.FilePath)) SavePresets();
        _presence.Changed += _ => Changed?.Invoke();
        _hotkeys.Pressed += i => _ui.Post(_ => { if (i < _hotkeyActions.Length) _hotkeyActions[i](); }, null);
        _rotateTimer = new System.Threading.Timer(_ => _ui.Post(_ => RotateTick(), null), null, 1000, 1000);
        BindHotkeys();

        // The last status comes back after a restart, with its elapsed timer still counting from when it went live.
        if (Settings.Live && Find(Settings.ActivePresetId) != null)
        {
            if (Settings.LiveSinceMs <= 0 || DateTimeOffset.Now - DateTimeOffset.FromUnixTimeMilliseconds(Settings.LiveSinceMs) > TimeSpan.FromHours(12))
                Settings.LiveSinceMs = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            ScheduleRotation();
            Push();
        }
        else if (Settings.Live)
        {
            Settings.Live = false;
            Settings.Save();
        }
    }

    public EngineState State => new(Settings.Live, Settings.ActivePresetId, Settings.LiveSinceMs, _shownSince.ToUnixTimeMilliseconds(),
        _presence.Status, Rotating, Rotating ? _nextRotate.ToUnixTimeMilliseconds() : 0);

    public Preset? Active => Find(Settings.ActivePresetId);

    private bool Rotating => Settings.Live && Settings.RotateEnabled && RotationList().Count >= 2;

    public Preset? Find(string? id) => id == null ? null : _presets.FirstOrDefault(p => p.Id == id);

    public string AppIdFor(Preset p) => p.AppId.Length > 0 ? p.AppId : Settings.AppId.Length > 0 ? Settings.AppId : AppSettings.DefaultAppId;

    // ---------- going live ----------

    /// <summary>Shows a preset on the profile right away.</summary>
    public void GoLive(string presetId)
    {
        if (Find(presetId) == null) return;
        bool wasLive = Settings.Live;
        Settings.Live = true;
        if (!wasLive) Settings.LiveSinceMs = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        Settings.ActivePresetId = presetId;
        _shownSince = DateTimeOffset.Now;
        ScheduleRotation();
        Settings.Save();
        Push();
    }

    public void SetLive(bool live)
    {
        if (live == Settings.Live) return;
        if (!live)
        {
            Settings.Live = false;
            Settings.Save();
            Push();
            return;
        }
        string? id = Find(Settings.ActivePresetId)?.Id ?? _presets.FirstOrDefault()?.Id;
        if (id != null) GoLive(id);
    }

    public void Toggle() => SetLive(!Settings.Live);

    /// <summary>The next preset: the next one in the rotation if it runs, otherwise the next in the list.</summary>
    public void Next()
    {
        var list = Settings.RotateEnabled && RotationList().Count >= 2 ? RotationList() : _presets.Select(p => p.Id).ToList();
        if (list.Count == 0) return;
        string? next = PickNext(list);
        if (next != null) GoLive(next);
    }

    private void Push()
    {
        var p = Active;
        if (!Settings.Live || p == null)
        {
            _presence.Clear();
        }
        else
        {
            _presence.Show(AppIdFor(p), p.ToActivity(DateTimeOffset.FromUnixTimeMilliseconds(Settings.LiveSinceMs), _shownSince));
        }
        Changed?.Invoke();
    }

    // ---------- rotation ----------

    private List<string> RotationList() =>
        Settings.RotatePresetIds.Where(id => Find(id) != null).ToList();

    private void ScheduleRotation() =>
        _nextRotate = Rotating ? DateTimeOffset.Now.AddSeconds(Settings.RotateSeconds) : DateTimeOffset.MaxValue;

    private void RotateTick()
    {
        if (!Rotating)
        {
            _nextRotate = DateTimeOffset.MaxValue;
            return;
        }
        if (_nextRotate == DateTimeOffset.MaxValue) ScheduleRotation();
        if (DateTimeOffset.Now < _nextRotate) return;
        string? next = PickNext(RotationList());
        if (next != null) GoLive(next);
    }

    private string? PickNext(List<string> list)
    {
        if (list.Count == 0) return null;
        if (!Settings.RotateShuffle || list.Count < 3)
        {
            int at = list.IndexOf(Settings.ActivePresetId ?? "");
            return list[(at + 1) % list.Count];
        }
        // Shuffle like a playlist: everything once in a random order, then a new order, never the same one twice in a row.
        _bag.RemoveAll(id => !list.Contains(id));
        if (_bag.Count == 0 || _shuffleBag != list.Count)
        {
            _shuffleBag = list.Count;
            _bag.Clear();
            _bag.AddRange(list.OrderBy(_ => Random.Shared.Next()));
            if (_bag[0] == Settings.ActivePresetId) (_bag[0], _bag[^1]) = (_bag[^1], _bag[0]);
        }
        string pick = _bag[0];
        _bag.RemoveAt(0);
        return pick;
    }

    public void SetRotation(bool enabled, List<string> ids, int seconds, bool shuffle)
    {
        Settings.RotateEnabled = enabled;
        Settings.RotatePresetIds = ids;
        Settings.RotateSeconds = seconds;
        Settings.RotateShuffle = shuffle;
        Settings.Sanitize();
        _bag.Clear();
        ScheduleRotation();
        Settings.Save();
        // Starting a rotation while live and on a preset outside it jumps straight into it.
        if (Rotating && !RotationList().Contains(Settings.ActivePresetId ?? "")) GoLive(RotationList()[0]);
        Changed?.Invoke();
    }

    // ---------- presets ----------

    public Preset Create()
    {
        // Flair's own app has the sparkle uploaded; someone else's app might not.
        var p = new Preset { Name = UniqueName("New status"), LargeImage = Settings.AppId.Length == 0 ? Preset.FlairPicture : "" };
        p.Sanitize();
        _presets.Add(p);
        SavePresets();
        return p;
    }

    /// <summary>Saves an edited preset. When it's the live one, Discord gets the new version a moment later.</summary>
    public Preset Update(Preset edited)
    {
        edited.Sanitize();
        int at = _presets.FindIndex(p => p.Id == edited.Id);
        if (at < 0) _presets.Add(edited);
        else _presets[at] = edited;
        SavePresets();
        BindHotkeys();
        if (Settings.Live && Settings.ActivePresetId == edited.Id) Push();
        else Changed?.Invoke();
        return edited;
    }

    public Preset? Duplicate(string id)
    {
        var source = Find(id);
        if (source == null) return null;
        var copy = source.Clone();
        copy.Id = Preset.NewId();
        copy.Name = UniqueName(source.Name + " copy");
        copy.Hotkey = new HotkeySetting();
        _presets.Insert(_presets.IndexOf(source) + 1, copy);
        SavePresets();
        return copy;
    }

    public void Delete(string id)
    {
        var p = Find(id);
        if (p == null) return;
        _presets.Remove(p);
        Settings.RotatePresetIds.Remove(id);
        bool wasActive = Settings.ActivePresetId == id;
        if (wasActive)
        {
            Settings.ActivePresetId = _presets.FirstOrDefault()?.Id;
            if (Settings.ActivePresetId == null) Settings.Live = false;
        }
        Settings.Save();
        SavePresets();
        BindHotkeys();
        if (wasActive) Push();
        else Changed?.Invoke();
    }

    public void Reorder(List<string> ids)
    {
        var order = ids.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
        _presets = _presets.OrderBy(p => order.TryGetValue(p.Id, out int i) ? i : int.MaxValue).ToList();
        SavePresets();
        Changed?.Invoke();
    }

    /// <summary>Adds presets someone shared. Returns the new ones.</summary>
    public List<Preset> Import(string path)
    {
        var incoming = PresetFile.Read(path);
        int room = PresetStore.MaxPresets - _presets.Count;
        if (room <= 0) throw new InvalidOperationException("You have the most presets Flair keeps. Delete a few first.");
        incoming = incoming.Take(room).ToList();
        foreach (var p in incoming) p.Name = UniqueName(p.Name);
        _presets.AddRange(incoming);
        SavePresets();
        Log.Info($"Imported {incoming.Count} preset(s)");
        Changed?.Invoke();
        return incoming;
    }

    public void Export(IEnumerable<string> ids, string path)
    {
        var chosen = ids.Select(Find).Where(p => p != null).Cast<Preset>().ToList();
        if (chosen.Count == 0) throw new InvalidOperationException("Nothing to export");
        PresetFile.Write(path, chosen);
    }

    private string UniqueName(string name)
    {
        if (_presets.All(p => p.Name != name)) return name;
        for (int i = 2; ; i++)
            if (_presets.All(p => p.Name != $"{name} {i}")) return $"{name} {i}";
    }

    private void SavePresets()
    {
        try { PresetStore.Save(_presets); }
        catch (Exception ex) { Log.Error("Saving presets failed", ex); }
    }

    // ---------- settings and hotkeys ----------

    public void ApplySettings(AppSettings updated)
    {
        // The window only edits these, so what's live and the rotation keep their current values.
        updated.Live = Settings.Live;
        updated.ActivePresetId = Settings.ActivePresetId;
        updated.LiveSinceMs = Settings.LiveSinceMs;
        updated.RotateEnabled = Settings.RotateEnabled;
        updated.RotatePresetIds = Settings.RotatePresetIds;
        updated.RotateSeconds = Settings.RotateSeconds;
        updated.RotateShuffle = Settings.RotateShuffle;
        bool appChanged = updated.AppId != Settings.AppId;
        Settings = updated;
        Settings.Save();
        Autostart.Apply(Settings.StartWithWindows);
        BindHotkeys();
        if (appChanged) Push();
        else Changed?.Invoke();
    }

    /// <summary>Hotkeys are off while one is being recorded in the window, so pressing it there doesn't also fire it.</summary>
    public void PauseHotkeys(bool paused)
    {
        if (paused) _hotkeys.SetHotkeys();
        else BindHotkeys();
    }

    private void BindHotkeys()
    {
        var keys = new List<HotkeySetting>();
        var actions = new List<Action>();
        void Add(HotkeySetting hk, Action a)
        {
            if (!hk.IsSet) return;
            keys.Add(hk);
            actions.Add(a);
        }
        Add(Settings.ToggleHotkey, () =>
        {
            Toggle();
            Announce?.Invoke(Settings.Live ? $"Status on: {Active?.Name}" : "Status off");
        });
        Add(Settings.NextHotkey, () =>
        {
            Next();
            Announce?.Invoke($"Now showing {Active?.Name}");
        });
        foreach (var p in _presets)
        {
            string id = p.Id;
            Add(p.Hotkey, () =>
            {
                GoLive(id);
                Announce?.Invoke($"Now showing {Active?.Name}");
            });
        }
        _hotkeyActions = actions.ToArray();
        _hotkeys.SetHotkeys(keys.ToArray());
    }

    public void Dispose()
    {
        _rotateTimer.Dispose();
        _hotkeys.Dispose();
        _presence.Dispose();
    }
}
