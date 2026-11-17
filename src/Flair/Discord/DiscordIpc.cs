using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Flair.Core;

namespace Flair.Discord;

/// <summary>The Discord account the desktop app is signed in to, as Discord reports it on connect. Kept in memory only.</summary>
public sealed record DiscordUser(string Id, string UserName, string? GlobalName, string? Avatar)
{
    public string DisplayName => string.IsNullOrWhiteSpace(GlobalName) ? UserName : GlobalName!;

    public string AvatarUrl => Avatar is { Length: > 0 }
        ? $"https://cdn.discordapp.com/avatars/{Id}/{Avatar}.{(Avatar.StartsWith("a_") ? "gif" : "png")}?size=128"
        : $"https://cdn.discordapp.com/embed/avatars/{(ulong.TryParse(Id, out ulong id) ? (id >> 22) % 6 : 0)}.png";
}

/// <summary>Discord said no. <see cref="Code"/> is Discord's own error code, like 4000 for an unknown application ID.</summary>
public sealed class DiscordRpcException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

/// <summary>
/// One connection to the Discord desktop app over its local named pipe (\\.\pipe\discord-ipc-N), the same official
/// Rich Presence channel games use. It never sees or sends an account token: Discord itself decides what it accepts.
/// </summary>
public sealed class DiscordIpc : IDisposable
{
    private const int OpHandshake = 0, OpFrame = 1, OpClose = 2, OpPing = 3, OpPong = 4;
    private const int MaxFrame = 64 * 1024;

    private readonly NamedPipeClientStream _pipe;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly Dictionary<string, TaskCompletionSource<JsonNode?>> _waiting = new();
    private readonly CancellationTokenSource _stop = new();
    private Task? _reader;

    /// <summary>Raised once when the pipe closes, from either side.</summary>
    public event Action<string>? Closed;

    public DiscordUser? User { get; private set; }
    public bool IsOpen => _pipe.IsConnected && !_stop.IsCancellationRequested;

    private DiscordIpc(NamedPipeClientStream pipe) => _pipe = pipe;

    /// <summary>True when any Discord desktop app (stable, PTB or Canary) has its pipe open.</summary>
    public static bool DiscordIsRunning()
    {
        for (int i = 0; i < 10; i++)
            if (File.Exists($@"\\.\pipe\discord-ipc-{i}")) return true;
        return false;
    }

    /// <summary>Connects to the first Discord pipe that answers and says hello with the application ID.</summary>
    public static async Task<DiscordIpc> ConnectAsync(string applicationId, CancellationToken ct)
    {
        Exception? last = null;
        for (int i = 0; i < 10; i++)
        {
            string name = $"discord-ipc-{i}";
            if (!File.Exists($@"\\.\pipe\{name}")) continue;
            var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(1500, ct);
                var ipc = new DiscordIpc(pipe);
                await ipc.HandshakeAsync(applicationId, ct);
                return ipc;
            }
            catch (DiscordRpcException)
            {
                await pipe.DisposeAsync();
                throw; // Discord answered and refused, another pipe would say the same
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                last = ex;
                await pipe.DisposeAsync();
            }
        }
        throw new IOException("Discord isn't running", last);
    }

    private async Task HandshakeAsync(string applicationId, CancellationToken ct)
    {
        await WriteAsync(OpHandshake, new JsonObject { ["v"] = 1, ["client_id"] = applicationId }, ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        var (op, body) = await ReadAsync(timeout.Token);
        if (op == OpClose) throw CloseError(body);
        if (op != OpFrame || body?["evt"]?.GetValue<string>() != "READY")
            throw new IOException("Discord answered with something unexpected");
        var u = body?["data"]?["user"];
        if (u != null)
        {
            User = new DiscordUser(u["id"]?.GetValue<string>() ?? "", u["username"]?.GetValue<string>() ?? "",
                u["global_name"]?.GetValue<string>(), u["avatar"]?.GetValue<string>());
        }
        _reader = Task.Run(ReadLoop);
    }

    /// <summary>Shows the activity on the profile, or clears it with null. Returns once Discord accepted it.</summary>
    public async Task SetActivityAsync(JsonObject? activity, CancellationToken ct)
    {
        string nonce = Guid.NewGuid().ToString("N");
        var done = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_waiting) _waiting[nonce] = done;
        try
        {
            await WriteAsync(OpFrame, new JsonObject
            {
                ["cmd"] = "SET_ACTIVITY",
                ["args"] = new JsonObject { ["pid"] = Environment.ProcessId, ["activity"] = activity },
                ["nonce"] = nonce,
            }, ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await using (timeout.Token.Register(() => done.TrySetCanceled()))
            {
                var reply = await done.Task;
                if (reply?["evt"]?.GetValue<string>() == "ERROR")
                    throw new DiscordRpcException(reply["data"]?["code"]?.GetValue<int>() ?? 0,
                        reply["data"]?["message"]?.GetValue<string>() ?? "Discord refused the status");
            }
        }
        finally
        {
            lock (_waiting) _waiting.Remove(nonce);
        }
    }

    private async Task ReadLoop()
    {
        string reason = "Discord closed the connection";
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var (op, body) = await ReadAsync(_stop.Token);
                switch (op)
                {
                    case OpPing:
                        await WriteAsync(OpPong, body as JsonObject ?? new JsonObject(), _stop.Token);
                        break;
                    case OpClose:
                        reason = CloseError(body).Message;
                        return;
                    case OpFrame:
                        string? nonce = body?["nonce"]?.GetValue<string>();
                        if (nonce == null) break;
                        TaskCompletionSource<JsonNode?>? waiter;
                        lock (_waiting) _waiting.TryGetValue(nonce, out waiter);
                        waiter?.TrySetResult(body);
                        break;
                }
            }
        }
        catch (OperationCanceledException) { reason = "Disconnected"; }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or EndOfStreamException) { }
        catch (Exception ex)
        {
            Log.Warn($"Reading from Discord failed: {ex.Message}");
        }
        finally
        {
            lock (_waiting)
                foreach (var w in _waiting.Values) w.TrySetException(new IOException(reason));
            _stop.Cancel();
            Closed?.Invoke(reason);
        }
    }

    private static DiscordRpcException CloseError(JsonNode? body)
    {
        int code = body?["code"]?.GetValue<int>() ?? 0;
        string message = body?["message"]?.GetValue<string>() ?? "Discord closed the connection";
        return new DiscordRpcException(code, message);
    }

    private async Task WriteAsync(int op, JsonNode body, CancellationToken ct)
    {
        byte[] json = Encoding.UTF8.GetBytes(body.ToJsonString());
        byte[] frame = new byte[8 + json.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, op);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), json.Length);
        json.CopyTo(frame, 8);
        await _writeGate.WaitAsync(ct);
        try
        {
            await _pipe.WriteAsync(frame, ct);
            await _pipe.FlushAsync(ct);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task<(int Op, JsonNode? Body)> ReadAsync(CancellationToken ct)
    {
        byte[] header = new byte[8];
        await _pipe.ReadExactlyAsync(header, ct);
        int op = BinaryPrimitives.ReadInt32LittleEndian(header);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        if (length is < 0 or > MaxFrame) throw new IOException($"Discord sent a frame of {length} bytes");
        byte[] payload = new byte[length];
        await _pipe.ReadExactlyAsync(payload, ct);
        return (op, length == 0 ? null : JsonNode.Parse(payload));
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _pipe.Dispose(); } catch { }
        try { _reader?.Wait(1000); } catch { }
        _writeGate.Dispose();
    }
}
