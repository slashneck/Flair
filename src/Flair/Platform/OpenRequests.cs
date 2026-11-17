using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using Flair.Core;

namespace Flair.Platform;

/// <summary>
/// Opening a .flair file while Flair already runs hands the path to the running copy over a pipe only this Windows
/// user can reach, instead of starting a second Flair.
/// </summary>
public sealed class OpenRequests : IDisposable
{
    private static string PipeName =>
        "Flair-Open-" + (WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName) + AppPaths.InstanceSuffix;

    private readonly CancellationTokenSource _stop = new();

    /// <summary>A path to open, or null when Flair was only asked to show its window. Raised on a pool thread.</summary>
    public event Action<string?>? Received;

    public OpenRequests() => _ = Task.Run(ListenAsync);

    private async Task ListenAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_stop.Token);
                using var reader = new StreamReader(server, Encoding.UTF8);
                string text = (await reader.ReadToEndAsync(_stop.Token)).Trim();
                Received?.Invoke(text.Length == 0 ? null : text);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Log.Warn($"Open request failed: {ex.Message}");
                await Task.Delay(500);
            }
        }
    }

    /// <summary>Sends a path (or nothing, to just show the window) to the running Flair. False when none answers.</summary>
    public static bool SendToRunning(string? path)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(3000);
            byte[] bytes = Encoding.UTF8.GetBytes(path ?? "");
            client.Write(bytes);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose() => _stop.Cancel();
}
