using System.Text;
using BatteryTray.Core;
using BatteryTray.Core.Settings;
using BatteryTray.Interop;
using BatteryTray.Services;
using BatteryTray.Windows.Rendering;

namespace BatteryTray.Tray;

/// <summary>Decide qué iconos mostrar en la bandeja y los dibuja.</summary>
internal sealed class TrayController : IDisposable
{
    public const string MainKey = "main";
    private const string DevicePrefix = "dev:";
    private const int MaxTooltip = 127;

    private readonly TrayIconHost _host;
    private readonly Dictionary<string, string> _lastState = new(StringComparer.Ordinal);

    public TrayController(TrayIconHost host) => _host = host;

    /// <summary>Si la clave es de un dispositivo, devuelve su id.</summary>
    public static string? DeviceIdFromKey(string key) =>
        key.StartsWith(DevicePrefix, StringComparison.Ordinal) ? key[DevicePrefix.Length..] : null;

    public void Update(IReadOnlyList<BatteryDevice> devices, AppSettings settings, bool force = false)
    {
        bool light = ThemeHelper.TaskbarUsesLightTheme();
        int size = TrayIconHost.IconSize;
        if (force)
        {
            _lastState.Clear();
        }

        var visible = devices.Where(d => !settings.IsHidden(d.Id)).ToList();
        var live = visible
            .Where(d => d.IsConnected && !d.IsStale && d.Level is not null && settings.HasTrayIcon(d))
            .ToList();

        var wanted = new List<(string Key, TrayIconSpec? Spec, string Tooltip)>();
        if (settings.TrayMode == TrayMode.PerDevice)
        {
            foreach (BatteryDevice d in live)
            {
                wanted.Add((DevicePrefix + d.Id, Spec(d, settings), Tooltip(d, settings)));
            }
        }
        else if (live.Count > 0)
        {
            BatteryDevice lowest = live.MinBy(d => d.Level ?? 101)!;
            wanted.Add((MainKey, Spec(lowest, settings), SummaryTooltip(visible, settings)));
        }

        if (wanted.Count == 0)
        {
            // Siempre queda al menos un icono para poder abrir la app.
            string tip = visible.Count > 0 ? SummaryTooltip(visible, settings) : Loc.Get("TrayNoDevices");
            wanted.Add((MainKey, null, tip));
        }

        var keep = new HashSet<string>(StringComparer.Ordinal);
        foreach ((string key, TrayIconSpec? spec, string tooltip) in wanted)
        {
            keep.Add(key);
            string tip = DeviceText.Truncate(tooltip, MaxTooltip);
            string state = $"{spec}|{tip}|{light}|{size}";
            if (_lastState.TryGetValue(key, out string? previous) && previous == state)
            {
                continue;
            }

            nint icon = spec is null ? LoadAppIcon(size) : TrayIconRenderer.RenderHicon(spec, size, light);
            if (icon == 0)
            {
                continue;
            }

            _host.SetIcon(key, icon, tip);
            _lastState[key] = state;
        }

        _host.RemoveAllExcept(keep);
        foreach (string key in _lastState.Keys.Where(k => !keep.Contains(k)).ToList())
        {
            _lastState.Remove(key);
        }
    }

    private static TrayIconSpec Spec(BatteryDevice d, AppSettings s) =>
        new(d.Level, d.GetBand(s.LowBatteryThreshold), d.IsCharging, d.Kind, s.IconStyle, s.DeviceColor(d));

    private static string Tooltip(BatteryDevice d, AppSettings s)
    {
        string text = $"{s.DisplayName(d)}: {Loc.Format("Percent", d.Level)}";
        return d.IsCharging ? $"{text} ({Loc.Get("TrayCharging")})" : text;
    }

    private static string SummaryTooltip(IReadOnlyList<BatteryDevice> devices, AppSettings s)
    {
        var sb = new StringBuilder();
        foreach (BatteryDevice d in devices.Where(d => d.IsConnected && d.Level is not null))
        {
            if (sb.Length > 0)
            {
                sb.Append('\n');
            }

            sb.Append(Tooltip(d, s));
        }

        return sb.Length > 0 ? sb.ToString() : Loc.Get("TrayNoDevices");
    }

    private static nint LoadAppIcon(int size) =>
        NativeMethods.LoadImageW(0, AppInfo.IconPath, NativeMethods.IMAGE_ICON, size, size, NativeMethods.LR_LOADFROMFILE);

    public void Dispose() => _lastState.Clear();
}
