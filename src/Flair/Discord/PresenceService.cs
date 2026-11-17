using System.Text.Json.Nodes;
using Flair.Core;

namespace Flair.Discord;

/// <summary>Where the status stands, for the window and the tray icon.</summary>
public sealed record PresenceStatus(string State, string? Error, DiscordUser? User)
{
    // off: nothing to show | noapp: no application ID yet | nodiscord: Discord isn't running
    // connecting | live: on the profile | error: Discord refused, see Error
    public bool IsLive => State == "live";
}

/// <summary>
/// Keeps the wanted status on Discord: connects when Discord shows up, reconnects when it restarts, and spaces updates
/// out so Discord's rate limit is never hit. Turning the status off closes the connection, which clears it right away.
/// </summary>
public sealed class PresenceService : IDisposable
{
    private static readonly TimeSpan MinGap = TimeSpan.FromSeconds(4.2);
    private static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(4);

    private readonly object _gate = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private string _appId = "";
    private JsonObject? _wanted;
    private int _wantedVersion;
    private DiscordIpc? _ipc;
    private string _ipcAppId = "";
    private int _sentVersion = -1;
    private DateTime _lastSend = DateTime.MinValue;
    private (string AppId, int Version)? _refused;
    private PresenceStatus _status = new("off", null, null);
    private DiscordUser? _lastUser;

    public event Action<PresenceStatus>? Changed;

    public PresenceStatus Status
    {
        get { lock (_gate) return _status; }
    }

    /// <summary>The account Discord reported last, also after Discord closed, for the preview.</summary>
    public DiscordUser? LastUser
    {
        get { lock (_gate) return _lastUser; }
    }

    public PresenceService()
    {
        _loop = Task.Run(RunAsync);
    }

    /// <summary>Puts this activity on the profile. Calling it again replaces it; rapid calls are merged.</summary>
    public void Show(string appId, JsonObject activity)
    {
        lock (_gate)
        {
            _appId = appId;
            _wanted = activity;
            _wantedVersion++;
        }
        _wake.Release();
    }

    public void Clear()
    {
        lock (_gate)
        {
            _wanted = null;
            _wantedVersion++;
        }
        _wake.Release();
    }

    private async Task RunAsync()
    {
        var ct = _stop.Token;
        while (!ct.IsCancellationRequested)
        {
            TimeSpan wait = TimeSpan.FromSeconds(30);
            try
            {
                wait = await StepAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Warn($"Presence step failed: {ex.Message}");
                Drop();
                wait = RetryEvery;
            }
            try { await _wake.WaitAsync(wait, ct); }
            catch (OperationCanceledException) { break; }
            while (_wake.CurrentCount > 0) _wake.Wait(0); // several wake-ups in a row count once
        }
        Drop();
    }

    /// <summary>Does one thing towards the wanted state and says how long to wait before looking again.</summary>
    private async Task<TimeSpan> StepAsync(CancellationToken ct)
    {
        string appId;
        JsonObject? wanted;
        int version;
        lock (_gate)
        {
            appId = _appId;
            wanted = _wanted;
            version = _wantedVersion;
        }

        if (wanted == null)
        {
            if (_ipc != null) Log.Info("Status cleared");
            Drop();
            Set("off", null);
            return Timeout.InfiniteTimeSpan;
        }
        if (appId.Length == 0)
        {
            Drop();
            Set("noapp", null);
            return Timeout.InfiniteTimeSpan;
        }
        // Discord turned this exact status down already; wait for a change instead of asking again and again.
        if (_refused is { } r && r.AppId == appId && r.Version == version) return Timeout.InfiniteTimeSpan;

        if (_ipc is { IsOpen: false } || (_ipc != null && _ipcAppId != appId)) Drop();
        if (_ipc == null)
        {
            if (!DiscordIpc.DiscordIsRunning())
            {
                Set("nodiscord", null);
                return RetryEvery;
            }
            Set("connecting", null);
            try
            {
                _ipc = await DiscordIpc.ConnectAsync(appId, ct);
            }
            catch (DiscordRpcException ex)
            {
                _refused = (appId, version);
                Log.Warn($"Discord refused application {appId}: {ex.Code} {ex.Message}");
                Set("error", ex.Code == 4000 ? "Discord doesn't know that application ID. Check it in Settings." : ex.Message);
                return Timeout.InfiniteTimeSpan;
            }
            catch (IOException)
            {
                Set("nodiscord", null);
                return RetryEvery;
            }
            _ipcAppId = appId;
            _sentVersion = -1;
            _ipc.Closed += reason =>
            {
                Log.Info($"Discord connection closed: {reason}");
                _wake.Release();
            };
            lock (_gate) _lastUser = _ipc.User ?? _lastUser;
            Log.Info($"Connected to Discord as {(_ipc.User?.UserName is { } n ? "@" + n : "unknown")} with application {appId}");
        }

        if (_sentVersion == version)
        {
            Set("live", null);
            return Timeout.InfiniteTimeSpan;
        }
        var since = DateTime.UtcNow - _lastSend;
        if (since < MinGap)
        {
            if (_sentVersion >= 0) Set("live", null);
            return MinGap - since;
        }

        _lastSend = DateTime.UtcNow;
        try
        {
            await _ipc.SetActivityAsync(wanted.DeepClone().AsObject(), ct);
        }
        catch (DiscordRpcException ex)
        {
            _refused = (appId, version);
            _sentVersion = version;
            Log.Warn($"Discord refused the status: {ex.Code} {ex.Message}");
            Set("error", Friendly(ex.Message));
            return Timeout.InfiniteTimeSpan;
        }
        _sentVersion = version;
        _refused = null;
        Set("live", null);
        // Look again if something changed while this update was on its way.
        lock (_gate) return _wantedVersion != version ? TimeSpan.Zero : Timeout.InfiniteTimeSpan;
    }

    // Discord's validation messages, said the way people talk.
    private static string Friendly(string message)
    {
        if (message.Contains("buttons", StringComparison.OrdinalIgnoreCase)) return "Discord didn't accept a button. Check its link.";
        if (message.Contains("large_image", StringComparison.OrdinalIgnoreCase) || message.Contains("small_image", StringComparison.OrdinalIgnoreCase))
            return "Discord didn't accept a picture. Use an https link or the name of an uploaded asset.";
        return "Discord didn't accept this status: " + message;
    }

    private void Drop()
    {
        var ipc = _ipc;
        _ipc = null;
        _sentVersion = -1;
        ipc?.Dispose();
    }

    private void Set(string state, string? error)
    {
        PresenceStatus status;
        lock (_gate)
        {
            if (_status.State == state && _status.Error == error && ReferenceEquals(_status.User, _lastUser)) return;
            status = _status = new PresenceStatus(state, error, _lastUser);
        }
        Changed?.Invoke(status);
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _loop.Wait(2000); } catch { }
    }
}
