using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using MeetingScribe.Configuration;
using MeetingScribe.Detection;
using MeetingScribe.Infrastructure;
using MeetingScribe.Pipeline;

namespace MeetingScribe;

/// <summary>The whole UI: a tray icon, its menu, and the wiring between monitor and pipeline.</summary>
[SupportedOSPlatform("windows")]
internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly AppConfig _config;
    private readonly CallMonitor _monitor;
    private readonly MeetingPipeline _pipeline;
    private readonly NotifyIcon _notifyIcon;

    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _toggleRecordingItem;
    private readonly ToolStripMenuItem _pauseItem;
    private readonly ToolStripMenuItem _startWithWindowsItem;

    private AppState _state = AppState.Idle;
    private string? _lastNotePath;
    private WebApplication? _localApi;
    private Task? _localApiTask;

    public TrayApplicationContext()
    {
        Paths.EnsureCreated();
        Log.PurgeOldLogs();
        Log.Info("MeetingScribe starting.");

        _config = ConfigStore.Load();

        _statusItem = new ToolStripMenuItem("Starting…") { Enabled = false };
        _toggleRecordingItem = new ToolStripMenuItem("Start recording now", null, (_, _) => ToggleRecording());
        _pauseItem = new ToolStripMenuItem("Pause monitoring", null, (_, _) => TogglePause()) { CheckOnClick = true };
        _startWithWindowsItem = new ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleStartup())
        {
            CheckOnClick = true,
            Checked = StartupRegistration.IsEnabled,
        };

        _notifyIcon = new NotifyIcon
        {
            Icon = TrayIcons.For(AppState.Idle),
            Visible = true,
            Text = "MeetingScribe",
            ContextMenuStrip = BuildMenu(),
        };
        _notifyIcon.DoubleClick += (_, _) => OpenLastNote();
        _notifyIcon.BalloonTipClicked += (_, _) => OpenLastNote();

        _pipeline = new MeetingPipeline(_config);
        _monitor = new CallMonitor(_config.Detection);

        _pipeline.StateChanged += OnStateChanged;
        _pipeline.NoteWritten += OnNoteWritten;
        _pipeline.Failed += OnFailed;
        _pipeline.AutoStopped += () => _monitor.OverrideState(false);

        _monitor.CallStarted += () => _pipeline.StartRecording();
        _monitor.CallEnded += () => _pipeline.StopRecording();
        _monitor.Start();

        StartLocalControlApi();
        SetState(AppState.Idle, ValidateConfiguration() ?? "Watching for meetings");
    }

    private void StartLocalControlApi()
    {
        if (!_config.LocalControl.Enabled)
        {
            Log.Info("Local control API is disabled in the config.");
            return;
        }

        try
        {
            var port = Math.Clamp(_config.LocalControl.Port, 1024, 65535);
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
            var app = builder.Build();

            app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
            app.MapPost("/api/recording/start", () =>
            {
                RequestRecordingStart();
                return Results.Ok(new { status = "started" });
            });
            app.MapPost("/api/recording/stop", () =>
            {
                RequestRecordingStop();
                return Results.Ok(new { status = "stopped" });
            });
            app.MapPost("/api/recording/toggle", () =>
            {
                ToggleRecording();
                return Results.Ok(new { status = _pipeline.IsRecording ? "recording" : "stopped" });
            });

            _localApi = app;
            _localApiTask = app.RunAsync();
            Log.Info($"Local control API listening on http://127.0.0.1:{port}");
        }
        catch (Exception ex)
        {
            Log.Error($"Could not start local control API on port {_config.LocalControl.Port}", ex);
        }
    }

    public void RequestRecordingStart()
    {
        if (_pipeline.IsRecording) return;
        _monitor.OverrideState(true);
        _pipeline.StartRecording();
    }

    public void RequestRecordingStop()
    {
        if (!_pipeline.IsRecording) return;
        _monitor.OverrideState(false);
        _pipeline.StopRecording();
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.AddRange(
        [
            _statusItem,
            new ToolStripSeparator(),
            _toggleRecordingItem,
            _pauseItem,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Transcribe an audio file…", null, (_, _) => ImportAudioFile()),
            new ToolStripSeparator(),
            new ToolStripMenuItem("Open last note", null, (_, _) => OpenLastNote()),
            new ToolStripMenuItem("Open vault folder", null, (_, _) => OpenPath(_config.Vault.Root)),
            new ToolStripMenuItem("Open recordings folder", null, (_, _) => OpenPath(_pipeline.RecordingsDirectory)),
            new ToolStripMenuItem("Open settings file", null, (_, _) => OpenPath(Paths.ConfigFile)),
            new ToolStripMenuItem("Open log", null, (_, _) => OpenPath(Log.CurrentLogFile)),
            new ToolStripSeparator(),
            _startWithWindowsItem,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Exit", null, (_, _) => ExitApplication()),
        ]);

        return menu;
    }

    private string? ValidateConfiguration()
    {
        if (string.IsNullOrWhiteSpace(_config.Vault.Root) || !Directory.Exists(_config.Vault.Root))
        {
            var message = "Set your vault path in the settings file";
            Log.Warn($"Vault root is not configured or does not exist: '{_config.Vault.Root}'");
            ShowBalloon("MeetingScribe needs setup", $"{message} ({Paths.ConfigFile}).", ToolTipIcon.Warning);
            return message;
        }

        return null;
    }

    private void ToggleRecording()
    {
        if (_pipeline.IsRecording)
        {
            _monitor.OverrideState(false);
            _pipeline.StopRecording();
        }
        else
        {
            _monitor.OverrideState(true);
            _pipeline.StartRecording();
        }
    }

    private void TogglePause()
    {
        _monitor.IsPaused = _pauseItem.Checked;

        if (_monitor.IsPaused)
        {
            SetState(AppState.Paused, "Monitoring paused");
        }
        else
        {
            _monitor.OverrideState(_pipeline.IsRecording);
            SetState(_pipeline.IsRecording ? AppState.Recording : AppState.Idle, "Watching for meetings");
        }
    }

    private void ToggleStartup() => StartupRegistration.Set(_startWithWindowsItem.Checked);

    private void ImportAudioFile()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Choose a recording to transcribe",
            Filter = "Audio files|*.wav;*.mp3;*.m4a;*.aiff;*.wma|All files|*.*",
            InitialDirectory = Directory.Exists(_pipeline.RecordingsDirectory)
                ? _pipeline.RecordingsDirectory
                : Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
        };

        if (dialog.ShowDialog() == DialogResult.OK) _pipeline.EnqueueExisting(dialog.FileName);
    }

    private void OnStateChanged(AppState state, string status)
    {
        // Pause is a UI-level state and must not be overwritten by pipeline idle reports.
        if (_monitor.IsPaused && state == AppState.Idle) return;

        SetState(state, status);

        if (state == AppState.Recording) ShowBalloon("Recording started", status, ToolTipIcon.Info);
    }

    private void OnNoteWritten(string path)
    {
        _lastNotePath = path;
        ShowBalloon("Meeting note saved", Path.GetFileName(path), ToolTipIcon.Info);
    }

    private void OnFailed(string message)
    {
        SetState(AppState.Error, message);
        ShowBalloon("MeetingScribe problem", message, ToolTipIcon.Error);
    }

    private void SetState(AppState state, string status)
    {
        _state = state;
        _statusItem.Text = status;
        _toggleRecordingItem.Text = _pipeline.IsRecording ? "Stop recording now" : "Start recording now";
        _notifyIcon.Icon = TrayIcons.For(state);

        var tooltip = $"MeetingScribe — {status}";
        // The shell truncates NotifyIcon tooltips beyond 63 characters.
        _notifyIcon.Text = tooltip.Length > 63 ? tooltip[..60] + "…" : tooltip;
    }

    private void ShowBalloon(string title, string message, ToolTipIcon icon)
    {
        if (!_config.ShowNotifications) return;

        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = message;
        _notifyIcon.BalloonTipIcon = icon;
        _notifyIcon.ShowBalloonTip(5000);
    }

    private void OpenLastNote()
    {
        if (_lastNotePath is not null && File.Exists(_lastNotePath)) OpenPath(_lastNotePath);
        else OpenPath(_config.Vault.Root);
    }

    private static void OpenPath(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || (!File.Exists(path) && !Directory.Exists(path)))
            {
                MessageBox.Show($"Not found:\n{path}", "MeetingScribe", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"Could not open '{path}'", ex);
        }
    }

    private void ExitApplication()
    {
        if (_pipeline.IsRecording)
        {
            var answer = MessageBox.Show(
                "A meeting is being recorded. Stop and transcribe it before exiting?",
                "MeetingScribe",
                MessageBoxButtons.YesNoCancel,
                MessageBoxIcon.Question);

            if (answer == DialogResult.Cancel) return;
            if (answer == DialogResult.Yes) _pipeline.StopRecording();
        }

        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Log.Info($"MeetingScribe shutting down (state: {_state}).");
            StopLocalControlApi();
            _notifyIcon.Visible = false;

            _monitor.Dispose();
            _pipeline.Dispose();
            _notifyIcon.Dispose();
            TrayIcons.DisposeAll();
        }

        base.Dispose(disposing);
    }

    private void StopLocalControlApi()
    {
        if (_localApi is null) return;

        try
        {
            _localApi.StopAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log.Error("Local control API shutdown raised an exception", ex);
        }

        _localApi = null;
    }
}
