using System.Diagnostics;
using Flair.Core;
using Flair.Platform;

namespace Flair.UI;

/// <summary>
/// The tray side of Flair: the icon shows whether the status is on Discord, the menu switches presets, and the window
/// opens on a double-click. Flair keeps running here when the window is closed.
/// </summary>
public sealed class TrayApp : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly StatusEngine _engine;
    private readonly SynchronizationContext _ui;
    private readonly OpenRequests _openRequests;
    private readonly UpdateService _updates;
    private MainWindow? _window;
    private string _iconState = "";

    public TrayApp(bool background, string? openPath)
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        bool existed = File.Exists(AppSettings.FilePath);
        var settings = AppSettings.Load();
        string version = Application.ProductVersion.Split('+')[0];
        if (!existed || settings.LastSeenVersion != version)
        {
            settings.LastSeenVersion = version;
            settings.Save();
        }
        // Keeps the startup entry pointing at this exe, e.g. after Flair was moved or updated.
        if (settings.StartWithWindows && !Autostart.IsEnabled()) Autostart.Apply(true);
        FileAssociation.Refresh();

        _tray = new NotifyIcon { Icon = AppAssets.TrayIcon("off"), Text = "Flair", Visible = true };
        ToolStripManager.Renderer = new DarkMenuRenderer();
        _tray.ContextMenuStrip = new ContextMenuStrip { Font = new Font("Segoe UI", 9.5f) };
        _tray.ContextMenuStrip.Opening += (_, _) => BuildMenu();
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowWindow(); };

        _engine = new StatusEngine(settings);
        _engine.Changed += () => Post(RefreshIcon);
        _engine.Announce += text => Post(() =>
        {
            // Inside Flair's own window the change is right there to see.
            if (_window == null || Form.ActiveForm != _window) _tray.ShowBalloonTip(2500, "Flair", text, ToolTipIcon.None);
        });
        _updates = new UpdateService(() => _engine.Settings, () => false, () => Post(ExitThread));
        _openRequests = new OpenRequests();
        _openRequests.Received += path => Post(() =>
        {
            ShowWindow();
            if (path != null) _window?.ImportFile(path);
        });
        BuildMenu();
        RefreshIcon();

        // Started by hand, the window opens. Started with Windows (--background), Flair stays in the tray.
        if (!background || openPath != null || !settings.GuideSeen)
        {
            Post(() =>
            {
                ShowWindow();
                if (openPath != null) _window?.ImportFile(openPath);
            });
        }
    }

    private void Post(Action a) => _ui.Post(_ => a(), null);

    private void ShowWindow()
    {
        if (_window == null || _window.IsDisposed)
        {
            _window = new MainWindow(_engine, _updates);
            _window.FormClosed += (_, _) =>
            {
                _window = null;
                // Hand the Edge engine's memory back right away; the status keeps running from the tray.
                GC.Collect();
                GC.WaitForPendingFinalizers();
            };
            _window.Show();
        }
        else
        {
            if (_window.WindowState == FormWindowState.Minimized) _window.WindowState = FormWindowState.Normal;
            _window.Activate();
        }
    }

    private void RefreshIcon()
    {
        var s = _engine.State;
        string state = !s.Live ? "off" : s.Discord.IsLive ? "live" : "waiting";
        string text = !s.Live ? "Flair · status off"
            : s.Discord.State switch
            {
                "live" => $"Flair · {_engine.Active?.Name}",
                "nodiscord" => "Flair · waiting for Discord",
                "noapp" => "Flair · needs an application ID",
                "error" => "Flair · Discord said no",
                _ => "Flair · connecting",
            };
        _tray.Text = text.Length > 63 ? text[..63] : text;
        if (state == _iconState) return;
        _iconState = state;
        _tray.Icon = AppAssets.TrayIcon(state);
    }

    private void BuildMenu()
    {
        var menu = _tray.ContextMenuStrip!;
        menu.Items.Clear();
        var s = _engine.Settings;
        var state = _engine.State;

        menu.Items.Add(new ToolStripMenuItem("Open Flair", null, (_, _) => ShowWindow()) { Font = new Font(menu.Font, FontStyle.Bold) });
        string line = !state.Live ? "Status is off"
            : state.Discord.State switch
            {
                "live" => "Live on Discord",
                "nodiscord" => "Waiting for Discord to start",
                "noapp" => "Add an application ID in Flair",
                "error" => state.Discord.Error ?? "Discord said no",
                _ => "Connecting to Discord",
            };
        menu.Items.Add(new ToolStripMenuItem(line) { Enabled = false });
        if (_updates.Status is { State: "ready" } update)
            menu.Items.Add(new ToolStripMenuItem($"Restart to update to {update.Available}", null, (_, _) => _updates.Install()));
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(new ToolStripMenuItem(s.Live ? "Turn status off" : "Turn status on", null, (_, _) => _engine.Toggle())
            { ShortcutKeyDisplayString = s.ToggleHotkey.IsSet ? s.ToggleHotkey.ToString() : "", ShowShortcutKeys = true, Enabled = _engine.Presets.Count > 0 });
        foreach (var p in _engine.Presets.Take(15))
        {
            string id = p.Id;
            menu.Items.Add(new ToolStripMenuItem(p.Name, null, (_, _) => _engine.GoLive(id))
            {
                Checked = s.Live && s.ActivePresetId == id,
                ShortcutKeyDisplayString = p.Hotkey.IsSet ? p.Hotkey.ToString() : "",
                ShowShortcutKeys = true,
            });
        }
        if (_engine.Presets.Count > 1)
            menu.Items.Add(new ToolStripMenuItem("Next preset", null, (_, _) => _engine.Next())
                { ShortcutKeyDisplayString = s.NextHotkey.IsSet ? s.NextHotkey.ToString() : "", ShowShortcutKeys = true });
        if (s.RotatePresetIds.Count >= 2)
            menu.Items.Add(new ToolStripMenuItem("Rotate", null, (_, _) =>
                _engine.SetRotation(!s.RotateEnabled, s.RotatePresetIds, s.RotateSeconds, s.RotateShuffle)) { Checked = s.RotateEnabled });

        menu.Items.Add(new ToolStripSeparator());
        bool autostart = Autostart.IsEnabled();
        menu.Items.Add(new ToolStripMenuItem("Start with Windows", null, (_, _) =>
        {
            var updated = _engine.Settings.Clone();
            updated.StartWithWindows = !autostart;
            _engine.ApplySettings(updated);
        }) { Checked = autostart });
        menu.Items.Add("Open log", null, (_, _) => Process.Start(new ProcessStartInfo(Log.FilePath) { UseShellExecute = true }));
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
        DarkMenuRenderer.Style(menu);
    }

    protected override void ExitThreadCore()
    {
        _window?.Close();
        _tray.Visible = false;
        _tray.Dispose();
        _openRequests.Dispose();
        _updates.Dispose();
        _engine.Dispose();
        base.ExitThreadCore();
    }
}
