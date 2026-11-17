using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Flair.Core;
using Flair.Discord;
using Flair.Platform;

namespace Flair.UI;

/// <summary>
/// The editor window: a WebView2 page (wwwroot) talking to C# over a tiny JSON-RPC bridge. Created on demand and fully
/// disposed on close, so the Edge engine only uses memory while you're looking at it. Only Flair's own pages load in
/// it; links open in your browser, and only the ones listed here or on your own presets' buttons.
/// </summary>
public sealed class MainWindow : Form
{
    private const string AppHost = "app.flair";

    private static readonly string[] AllowedLinks =
    {
        "https://discord.com/developers/applications", "https://github.com/slashneck/Flair", "https://www.gnu.org/licenses/",
        "https://developer.microsoft.com/microsoft-edge/webview2", "https://github.com/dotnet/runtime",
    };
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly StatusEngine _engine;
    private readonly UpdateService _updates;
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(10, 10, 10) };
    private readonly List<string> _pendingImports = new();
    private bool _ready, _pageLoaded;

    public MainWindow(StatusEngine engine, UpdateService updates)
    {
        _engine = engine;
        _updates = updates;
        Text = "Flair";
        Icon = AppAssets.AppIcon();
        BackColor = Color.FromArgb(10, 10, 10);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1000, 660);
        Size = new Size(1240, 800);
        Controls.Add(_web);

        _engine.Changed += OnEngineChanged;
        _updates.Changed += OnUpdateChanged;
        Load += async (_, _) => await InitAsync();
        FormClosed += (_, _) =>
        {
            _engine.Changed -= OnEngineChanged;
            _updates.Changed -= OnUpdateChanged;
            _engine.PauseHotkeys(false);
        };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int on = 1;
        DwmSetWindowAttribute(Handle, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref on, sizeof(int));
        int caption = 0x000A0A0A; // COLORREF (BGR) of the app background
        DwmSetWindowAttribute(Handle, 35 /* DWMWA_CAPTION_COLOR */, ref caption, sizeof(int));
    }

    private async Task InitAsync()
    {
        try
        {
            string args = "--disable-background-networking --disable-component-update --disable-domain-reliability " +
                          "--disable-sync --no-pings --disable-features=msSmartScreenProtection,Translate,SpareRendererForSitePerProcess";
#if DEBUG
            // Development only: lets automated UI tests drive the page. Never compiled into Release builds.
            if (Environment.GetEnvironmentVariable("FLAIR_CDP_PORT") is { Length: > 0 } cdpPort)
                args += $" --remote-debugging-address=127.0.0.1 --remote-debugging-port={cdpPort}";
#endif
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(AppPaths.LocalDataDir, "webview"),
                new CoreWebView2EnvironmentOptions(args));
            await _web.EnsureCoreWebView2Async(env);
            var core = _web.CoreWebView2;

            var s = core.Settings;
            s.AreDefaultContextMenusEnabled = false;
            s.AreBrowserAcceleratorKeysEnabled = false;
            s.IsZoomControlEnabled = false;
            s.IsStatusBarEnabled = false;
            s.IsPasswordAutosaveEnabled = false;
            s.IsGeneralAutofillEnabled = false;
            s.IsSwipeNavigationEnabled = false;
            s.IsReputationCheckingRequired = false;
#if DEBUG
            s.AreDevToolsEnabled = true;
#else
            s.AreDevToolsEnabled = false;
#endif

            core.SetVirtualHostNameToFolderMapping(AppHost, AppPaths.WebRoot, CoreWebView2HostResourceAccessKind.DenyCors);
            core.NavigationStarting += (_, e) =>
            {
                if (!e.Uri.StartsWith($"https://{AppHost}/", StringComparison.OrdinalIgnoreCase)) e.Cancel = true;
            };
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.WebMessageReceived += OnWebMessage;
            core.Navigate($"https://{AppHost}/index.html");
            _ready = true;
        }
        catch (Exception ex)
        {
            Log.Error("Window failed to start", ex);
            MessageBox.Show(this, $"Flair's window couldn't start:\n{ex.Message}\n\nYour status keeps running from the tray.",
                "Flair", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Close();
        }
    }

    /// <summary>Imports a .flair file, once the page is there to show what came in.</summary>
    public void ImportFile(string path)
    {
        if (!_pageLoaded)
        {
            _pendingImports.Add(path);
            return;
        }
        try
        {
            var added = _engine.Import(path);
            PostEvent("imported", new { names = added.Select(p => p.Name), first = added.FirstOrDefault()?.Id });
        }
        catch (Exception ex)
        {
            PostEvent("toast", new { text = ex.Message, kind = "error" });
        }
    }

    private void OnEngineChanged() => PostEvent("state", Snapshot());

    private void OnUpdateChanged() => PostEvent("updateChanged", _updates.Status);

    private object Snapshot() => new
    {
        state = _engine.State,
        user = _engine.Presence.LastUser,
        presets = _engine.Presets,
        settings = _engine.Settings,
        defaultAppId = AppSettings.DefaultAppId,
    };

    private void PostEvent(string name, object? data)
    {
        if (!_ready || IsDisposed) return;
        string payload = JsonSerializer.Serialize(new { @event = name, data }, Json);
        try
        {
            if (InvokeRequired) BeginInvoke(() => { if (!IsDisposed) _web.CoreWebView2?.PostWebMessageAsJson(payload); });
            else _web.CoreWebView2?.PostWebMessageAsJson(payload);
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    private async void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonNode? msg;
        try { msg = JsonNode.Parse(e.WebMessageAsJson); }
        catch { return; }
        int id = msg?["id"]?.GetValue<int>() ?? 0;
        string method = msg?["method"]?.GetValue<string>() ?? "";
        var p = msg?["params"] as JsonObject ?? new JsonObject();

        object? result = null;
        string? error = null;
        try
        {
            result = await Dispatch(method, p);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Log.Warn($"UI call {method} failed: {ex.Message}");
        }
        if (IsDisposed) return;
        _web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new { id, result, error }, Json));
    }

    private async Task<object?> Dispatch(string method, JsonObject p)
    {
        string Str(string key) => p[key]?.GetValue<string>() ?? "";
        bool Bool(string key) => p[key]?.GetValue<bool>() ?? false;
        T Obj<T>(string key) => p[key].Deserialize<T>(Json) ?? throw new ArgumentException($"{key} is missing");

        switch (method)
        {
            case "app.init":
                _pageLoaded = true;
                if (_pendingImports.Count > 0)
                {
                    var paths = _pendingImports.ToList();
                    _pendingImports.Clear();
                    BeginInvoke(() => paths.ForEach(ImportFile));
                }
                return new
                {
                    snapshot = Snapshot(),
                    version = Application.ProductVersion.Split('+')[0],
                    update = _updates.Status,
#if DEBUG
                    dev = true,
#else
                    dev = false,
#endif
                };

            case "preset.create":
                return _engine.Create();
            case "preset.update":
                return _engine.Update(Obj<Preset>("preset"));
            case "preset.duplicate":
                return _engine.Duplicate(Str("id"));
            case "preset.delete":
                _engine.Delete(Str("id"));
                return null;
            case "preset.reorder":
                _engine.Reorder(Obj<List<string>>("ids"));
                return null;
            case "preset.goLive":
                _engine.GoLive(Str("id"));
                return null;

            case "status.setLive":
                _engine.SetLive(Bool("live"));
                return null;
            case "status.next":
                _engine.Next();
                return null;
            case "rotation.set":
                _engine.SetRotation(Bool("enabled"), Obj<List<string>>("ids"), p["seconds"]?.GetValue<int>() ?? 60, Bool("shuffle"));
                return null;

            case "settings.save":
                _engine.ApplySettings(Obj<AppSettings>("settings"));
                return _engine.Settings;
            case "hotkeys.pause":
                _engine.PauseHotkeys(Bool("paused"));
                return null;

            case "app.lookup":
                return await DiscordApps.LookupAsync(Str("appId"));

            case "presets.export":
                return Export(Obj<List<string>>("ids"));
            case "presets.import":
                using (var dialog = new OpenFileDialog { Filter = "Flair presets (*.flair)|*.flair", Title = "Import presets" })
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return null;
                    var added = _engine.Import(dialog.FileName);
                    return new { names = added.Select(x => x.Name), first = added.FirstOrDefault()?.Id };
                }
            case "presets.importText":
                {
                    // A file dropped on the window: the page reads it and passes the text, it goes through the same checks.
                    string tmp = Path.Combine(Path.GetTempPath(), $"flair-drop-{Guid.NewGuid():N}.flair");
                    try
                    {
                        string text = Str("text");
                        if (text.Length > 512 * 1024) throw new InvalidDataException("That file is too big to be a Flair preset");
                        await File.WriteAllTextAsync(tmp, text);
                        var added = _engine.Import(tmp);
                        return new { names = added.Select(x => x.Name), first = added.FirstOrDefault()?.Id };
                    }
                    finally
                    {
                        try { File.Delete(tmp); } catch { }
                    }
                }

            case "link.open":
                OpenLink(Str("url"));
                return null;
            case "log.open":
                Process.Start(new ProcessStartInfo(Log.FilePath) { UseShellExecute = true });
                return null;
            case "update.check":
                await _updates.CheckAsync(download: true);
                return _updates.Status;
            case "update.install":
                _updates.Install();
                return null;

            default:
                throw new InvalidOperationException($"Unknown call {method}");
        }
    }

    private string? Export(List<string> ids)
    {
        var first = ids.Select(_engine.Find).FirstOrDefault(x => x != null);
        if (first == null) return null;
        string name = ids.Count == 1 ? first.Name : "Flair presets";
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '-');
        using var dialog = new SaveFileDialog
        {
            Filter = "Flair presets (*.flair)|*.flair",
            FileName = name + PresetFile.Extension,
            Title = ids.Count == 1 ? "Export preset" : "Export presets",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return null;
        _engine.Export(ids, dialog.FileName);
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{dialog.FileName}\"") { UseShellExecute = false });
        return dialog.FileName;
    }

    private void OpenLink(string url)
    {
        bool allowed = AllowedLinks.Any(a => url.StartsWith(a, StringComparison.OrdinalIgnoreCase))
                       || _engine.Presets.Any(x => x.Buttons.Any(b => b.Url.Trim() == url));
        if (!allowed || !Preset.IsWebLink(url)) throw new InvalidOperationException("That link can't be opened from here");
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
