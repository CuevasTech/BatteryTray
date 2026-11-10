using System.Diagnostics;
using System.Globalization;
using System.Text;
using BatteryTray.Core;
using BatteryTray.Core.Providers;
using BatteryTray.Core.Settings;
using BatteryTray.Services;
using BatteryTray.Tray;
using BatteryTray.Views;
using BatteryTray.Windows.Providers;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Windows.Graphics;

namespace BatteryTray;

/// <summary>
/// Coordina todo: lectores de batería → iconos de la bandeja, panel, avisos y ajustes.
/// Todos los métodos públicos se usan desde el hilo de la interfaz.
/// </summary>
internal sealed class AppController
{
    private enum MenuCommand
    {
        Open = 1,
        Refresh,
        Settings,
        Startup,
        Exit,
    }

    private readonly DispatcherQueue _dispatcher;
    private readonly StartupService _startup = new();
    private readonly NotificationService _notifications = new();
    private readonly LowBatteryAlertTracker _lowAlerts = new();
    private readonly ConnectionChangeTracker _connections = new();
    private TrayIconHost? _host;
    private TrayController? _tray;
    private FlyoutWindow? _flyout;
    private SettingsWindow? _settingsWindow;
    private BatteryMonitor? _monitor;
    private bool _monitorIsDemo;
    private DispatcherQueueTimer? _saveTimer;
    private int _updateQueued;
    private bool _startupEnabled;
    private bool _exiting;

    public AppController(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        Current = this;
    }

    public static AppController? Current { get; private set; }

    public AppSettings Settings { get; private set; } = new();

    public IReadOnlyList<BatteryDevice> Devices => _monitor?.Devices ?? [];

    /// <summary>La lista de dispositivos cambió (en el hilo de la interfaz).</summary>
    public event Action? DevicesChanged;

    public async Task StartAsync()
    {
        var log = new FileLog(AppInfo.LogDirectory);
        AppLog.SetSink(log.Write);
        AppLog.Info($"Inicio BatteryTray {AppInfo.Version} ({(AppInfo.IsPackaged ? "Store" : "portable")}) en Windows {Environment.OSVersion.Version}");

        Settings = JsonStore.LoadSettings(AppInfo.SettingsPath);
        Loc.Initialize(Settings.Language);

        // Las notificaciones se registran antes de mirar cómo se lanzó la app (lo pide Windows App SDK).
        _notifications.Invoked += () => _dispatcher.TryEnqueue(ShowFlyoutDefault);
        _notifications.Initialize();
        bool showFlyout = !LaunchedAtStartup();

        _host = new TrayIconHost();
        _host.Activated += OnTrayActivated;
        _host.ContextMenuRequested += OnTrayContextMenu;
        _host.EnvironmentChanged += () => _tray?.Update(Devices, Settings, force: true);
        _tray = new TrayController(_host);
        _tray.Update([], Settings);

        _flyout = new FlyoutWindow(this);
        _flyout.UpdateDevices([]);
        _flyout.WarmUp();

        _saveTimer = _dispatcher.CreateTimer();
        _saveTimer.Interval = TimeSpan.FromSeconds(30);
        _saveTimer.Tick += (_, _) => SaveCache(onlyIfDirty: true);
        _saveTimer.Start();

        _startupEnabled = await _startup.GetStateAsync() == StartupState.Enabled;
        await StartMonitorAsync();

        if (HasArgument(Environment.GetCommandLineArgs(), SettingsArgument))
        {
            _dispatcher.TryEnqueue(DispatcherQueuePriority.Low, OpenSettings);
        }
        else if (showFlyout)
        {
            // Al abrirla a mano se enseña el panel: así se ve que arrancó aunque el icono esté escondido tras "^".
            _dispatcher.TryEnqueue(DispatcherQueuePriority.Low, ShowFlyoutDefault);
        }
    }

    /// <summary>Argumento para abrir directamente los ajustes.</summary>
    public const string SettingsArgument = "--settings";

    private static bool HasArgument(IEnumerable<string> args, string name) =>
        args.Any(a => string.Equals(a.Trim().Trim('"'), name, StringComparison.OrdinalIgnoreCase));

    private static bool LaunchedAtStartup()
    {
        if (Environment.GetCommandLineArgs().Any(a => string.Equals(a, StartupService.StartupArgument, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        try
        {
            return AppInstance.GetCurrent().GetActivatedEventArgs().Kind == ExtendedActivationKind.StartupTask;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ------------------------------------------------------------ lectores de batería

    private async Task StartMonitorAsync()
    {
        if (_monitor is not null)
        {
            _monitor.DevicesChanged -= OnMonitorChanged;
            SaveCache(onlyIfDirty: true);
            BatteryMonitor old = _monitor;
            _monitor = null;
            await old.DisposeAsync();
        }

        _monitorIsDemo = Settings.DemoMode;
        IBatteryProvider[] providers = _monitorIsDemo
            ? [new DemoBatteryProvider()]
            : [new BluetoothBatteryProvider(), new SystemBatteryProvider(Loc.Get("ThisPc"))];

        Dictionary<string, CachedReading>? cache = _monitorIsDemo ? null : JsonStore.LoadCache(AppInfo.CachePath);
        var monitor = new BatteryMonitor(providers, cache);
        monitor.DevicesChanged += OnMonitorChanged;
        _monitor = monitor;

        // En segundo plano: enumerar Bluetooth puede tardar un poco.
        await Task.Run(() => monitor.StartAsync());
        OnDevicesChanged();
    }

    private void OnMonitorChanged(object? sender, EventArgs e)
    {
        // Llega desde cualquier hilo; se agrupan las actualizaciones seguidas en una sola.
        if (Interlocked.Exchange(ref _updateQueued, 1) == 0)
        {
            _dispatcher.TryEnqueue(() =>
            {
                Interlocked.Exchange(ref _updateQueued, 0);
                OnDevicesChanged();
            });
        }
    }

    private void OnDevicesChanged()
    {
        if (_exiting)
        {
            return;
        }

        IReadOnlyList<BatteryDevice> devices = Devices;
        _tray?.Update(devices, Settings);
        _flyout?.UpdateDevices(devices);
        DevicesChanged?.Invoke();
        EvaluateNotifications(devices);
    }

    private void EvaluateNotifications(IReadOnlyList<BatteryDevice> devices)
    {
        var visible = devices.Where(d => !Settings.IsHidden(d.Id)).ToList();

        // Los rastreadores se evalúan siempre para que su estado esté al día aunque los avisos estén apagados.
        IReadOnlyList<BatteryDevice> low = _lowAlerts.Evaluate(visible, Settings.LowBatteryThreshold);
        var changes = _connections.Evaluate(visible);

        if (Settings.NotifyLowBattery)
        {
            foreach (BatteryDevice d in low)
            {
                _notifications.Show(Loc.Format("NotifyLowTitle", Settings.DisplayName(d)), Loc.Format("NotifyLowBody", d.Level));
            }
        }

        if (Settings.NotifyConnectionChanges)
        {
            foreach ((BatteryDevice d, bool connected) in changes)
            {
                string name = Settings.DisplayName(d);
                if (connected)
                {
                    _notifications.Show(Loc.Format("NotifyConnectedTitle", name), d.Level is int l ? Loc.Format("NotifyBatteryBody", l) : "");
                }
                else
                {
                    _notifications.Show(Loc.Format("NotifyDisconnectedTitle", name), "");
                }
            }
        }
    }

    public async Task RefreshAsync()
    {
        if (_monitor is BatteryMonitor monitor)
        {
            await Task.Run(() => monitor.RefreshAsync());
            OnDevicesChanged();
        }
    }

    public void Forget(string deviceId)
    {
        _monitor?.Forget(deviceId);
        SaveCache(onlyIfDirty: true);
    }

    private void SaveCache(bool onlyIfDirty)
    {
        if (_monitor is null || _monitorIsDemo)
        {
            return;
        }

        IReadOnlyDictionary<string, CachedReading> snapshot = _monitor.GetCacheSnapshot(out bool dirty);
        if (dirty || !onlyIfDirty)
        {
            JsonStore.SaveCache(AppInfo.CachePath, snapshot);
        }
    }

    // ------------------------------------------------------------ ajustes

    public void UpdateSettings(Action<AppSettings> change, bool restartProviders = false)
    {
        change(Settings);
        Settings.Normalize();
        JsonStore.SaveSettings(AppInfo.SettingsPath, Settings);

        if (restartProviders)
        {
            _ = StartMonitorAsync();
        }
        else
        {
            OnDevicesChanged();
        }
    }

    public Task<StartupState> GetStartupStateAsync() => _startup.GetStateAsync();

    public async Task<StartupState> SetStartupAsync(bool enabled)
    {
        StartupState state = await _startup.SetEnabledAsync(enabled);
        _startupEnabled = state == StartupState.Enabled;
        return state;
    }

    public async Task<string> BuildDiagnosticsAsync()
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"# BatteryTray {AppInfo.Version} ({(AppInfo.IsPackaged ? "Store" : "portable")})");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Windows {Environment.OSVersion.Version} | {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture} | idioma {CultureInfo.CurrentUICulture.Name} → {Loc.LanguageTag}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Ajustes: iconos={Settings.TrayMode}, estilo={Settings.IconStyle}, aviso={Settings.NotifyLowBattery} ({Settings.LowBatteryThreshold}%), demo={Settings.DemoMode}, ocultos={Settings.HiddenDevices.Count}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Inicio con Windows: {await _startup.GetStateAsync()}");
        sb.AppendLine();
        if (_monitor is not null)
        {
            sb.AppendLine(await _monitor.GetDiagnosticsAsync());
        }

        return sb.ToString();
    }

    // ------------------------------------------------------------ ventanas

    private void OnTrayActivated(string key, int x, int y, bool fromKeyboard)
    {
        if (_flyout is null)
        {
            return;
        }

        if (_flyout.IsOpen)
        {
            _flyout.Hide();
        }
        else if (!_flyout.RecentlyHidden)
        {
            _flyout.ShowAt(x, y, fromKeyboard);
        }
    }

    private void OnTrayContextMenu(string key, int x, int y)
    {
        if (_host is null)
        {
            return;
        }

        _flyout?.Hide();
        TrayMenuItem[] items =
        [
            new((int)MenuCommand.Open, Loc.Get("MenuOpen"), IsDefault: true),
            new((int)MenuCommand.Refresh, Loc.Get("MenuRefresh")),
            TrayMenuItem.Separator,
            new((int)MenuCommand.Settings, Loc.Get("MenuSettings")),
            new((int)MenuCommand.Startup, Loc.Get("MenuStartup"), IsChecked: _startupEnabled),
            TrayMenuItem.Separator,
            new((int)MenuCommand.Exit, Loc.Get("MenuExit")),
        ];

        switch ((MenuCommand)_host.ShowMenu(items, x, y, ThemeHelper.TaskbarUsesLightTheme()))
        {
            case MenuCommand.Open:
                _flyout?.ShowAt(x, y);
                break;
            case MenuCommand.Refresh:
                _ = RefreshAsync();
                break;
            case MenuCommand.Settings:
                OpenSettings();
                break;
            case MenuCommand.Startup:
                _ = SetStartupAsync(!_startupEnabled);
                break;
            case MenuCommand.Exit:
                _ = ExitAsync();
                break;
        }
    }

    public void ShowFlyoutDefault()
    {
        if (_flyout is null)
        {
            return;
        }

        // Esquina de la pantalla principal junto al reloj.
        RectInt32 work = DisplayArea.Primary.WorkArea;
        _flyout.ShowAt(work.X + work.Width - 1, work.Y + work.Height - 1);
    }

    /// <summary>Otra copia de la app intentó arrancar: se enseña el panel de esta.</summary>
    public void OnSecondInstance(AppActivationArguments activation)
    {
        // Solo se mira si pide los ajustes; cualquier otro argumento se ignora.
        string arguments = activation.Data is global::Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch
            ? launch.Arguments ?? ""
            : "";
        bool settings = HasArgument(arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries), SettingsArgument);
        _dispatcher.TryEnqueue(settings ? OpenSettings : ShowFlyoutDefault);
    }

    public void OpenSettings()
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(this);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }

        _settingsWindow.Activate();
    }

    // ------------------------------------------------------------ salir

    public void Restart()
    {
        if (AppInfo.IsPackaged)
        {
            _ = Cleanup().ContinueWith(_ => AppInstance.Restart(""), TaskScheduler.FromCurrentSynchronizationContext());
            return;
        }

        _ = RestartUnpackagedAsync();
    }

    private async Task RestartUnpackagedAsync()
    {
        await Cleanup();
        try
        {
            // Se libera la "instancia única" antes de lanzar la nueva copia.
            AppInstance.GetCurrent().UnregisterKey();
            if (Environment.ProcessPath is string exe && File.Exists(exe))
            {
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false });
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("No se pudo reiniciar", ex);
        }

        Application.Current.Exit();
    }

    public async Task ExitAsync()
    {
        await Cleanup();
        Application.Current.Exit();
    }

    private async Task Cleanup()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        AppLog.Info("Cerrando");
        _saveTimer?.Stop();
        SaveCache(onlyIfDirty: false);

        if (_monitor is not null)
        {
            _monitor.DevicesChanged -= OnMonitorChanged;
            await _monitor.DisposeAsync();
        }

        _tray?.Dispose();
        _host?.Dispose();
        _notifications.Dispose();
        _settingsWindow?.Close();
    }
}
