using BatteryTray.Core;
using BatteryTray.Interop;
using BatteryTray.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.Graphics;
using Launcher = Windows.System.Launcher;
using VirtualKey = Windows.System.VirtualKey;
using WinRT.Interop;

namespace BatteryTray.Views;

/// <summary>
/// Panel que se abre al pulsar el icono de la bandeja, al estilo de los paneles de
/// volumen o Wi-Fi de Windows 11: fondo acrílico, esquinas redondeadas, pegado a la barra.
/// Nunca se cierra de verdad: se oculta, para abrirse al instante la próxima vez.
/// </summary>
public sealed partial class FlyoutWindow : Window
{
    private const double WidthDip = 360;
    private const double MarginDip = 12;
    private static readonly TimeSpan s_reopenGuard = TimeSpan.FromMilliseconds(400);

    private readonly AppController _controller;
    private readonly nint _hwnd;
    private PointInt32 _anchor;
    private DateTimeOffset _hiddenAt = DateTimeOffset.MinValue;
    private bool _warmingUp;
    private bool _taskbarAtTop;

    internal FlyoutWindow(AppController controller)
    {
        _controller = controller;
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);

        Title = Loc.Get("AppDisplayName");
        AppWindow.IsShownInSwitchers = false;
        AppWindow.SetIcon(AppInfo.IconPath);

        OverlappedPresenter presenter = OverlappedPresenter.Create();
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        presenter.SetBorderAndTitleBar(true, false);
        AppWindow.SetPresenter(presenter);
        SetRoundedCorners(_hwnd);

        Activated += OnActivated;
        AppWindow.Closing += (_, e) =>
        {
            e.Cancel = true;
            Hide();
        };

        ApplyTexts();
    }

    public bool IsOpen => AppWindow.IsVisible && !_warmingUp;

    /// <summary>True justo después de ocultarse (el clic en el icono que lo cerró no debe reabrirlo).</summary>
    public bool RecentlyHidden => DateTimeOffset.UtcNow - _hiddenAt < s_reopenGuard;

    private void ApplyTexts()
    {
        TitleText.Text = Loc.Get("FlyoutTitle");
        ToolTipService.SetToolTip(RefreshButton, Loc.Get("RefreshTooltip"));
        AutomationPropertiesHelper.SetName(RefreshButton, Loc.Get("RefreshTooltip"));
        ToolTipService.SetToolTip(SettingsButton, Loc.Get("SettingsTooltip"));
        AutomationPropertiesHelper.SetName(SettingsButton, Loc.Get("SettingsTooltip"));
        EmptyTitle.Text = Loc.Get("EmptyTitle");
        EmptyBody.Text = Loc.Get("EmptyBody");
        BluetoothLink.Content = Loc.Get("BluetoothSettings");
        WelcomeBar.Title = Loc.Get("WelcomeTitle");
        WelcomeBar.Message = Loc.Get("WelcomeBody");
        WelcomeEnableButton.Content = Loc.Get("WelcomeEnable");
        DemoBar.Message = Loc.Get("DemoBanner");
    }

    /// <summary>Rellena la lista. Se llama desde el hilo de la interfaz.</summary>
    internal void UpdateDevices(IReadOnlyList<BatteryDevice> devices)
    {
        var settings = _controller.Settings;
        var items = devices
            .Where(d => !settings.IsHidden(d.Id))
            .Select(d => DeviceItem.From(d, settings))
            .ToList();

        DeviceList.ItemsSource = items;
        EmptyPanel.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdatedText.Text = Loc.Format("UpdatedAt", DateTime.Now.ToString("t", Loc.Culture));
        // BATTERYTRAY_SCREENSHOTS=1 oculta el aviso de demostración (solo para hacer las capturas de la Store).
        DemoBar.IsOpen = settings.DemoMode && Environment.GetEnvironmentVariable("BATTERYTRAY_SCREENSHOTS") != "1";
        WelcomeBar.IsOpen = !settings.WelcomeDismissed;

        if (IsOpen)
        {
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, Reposition);
        }
    }

    /// <summary>
    /// Muestra la ventana fuera de la pantalla una vez al arrancar para que el primer
    /// clic en el icono sea instantáneo (y para poder medir su altura real).
    /// </summary>
    internal void WarmUp()
    {
        _warmingUp = true;
        AppWindow.MoveAndResize(new RectInt32(-32000, -32000, 360, 300));
        ContentPanel.Loaded += OnWarmLoaded;
        AppWindow.Show(false);
    }

    private void OnWarmLoaded(object sender, RoutedEventArgs e)
    {
        ContentPanel.Loaded -= OnWarmLoaded;
        AppWindow.Hide();
        _warmingUp = false;
    }

    internal void ShowAt(int x, int y, bool fromKeyboard = false)
    {
        _anchor = new PointInt32(x, y);
        _warmingUp = false;
        Reposition();
        AppWindow.Show(true);
        Activate();
        NativeMethods.SetForegroundWindow(_hwnd);
        // El foco va dentro del panel (para que funcione Esc), pero el recuadro de foco
        // solo se dibuja si se abrió con el teclado, como en los paneles de Windows.
        RefreshButton.Focus(fromKeyboard ? FocusState.Keyboard : FocusState.Pointer);
        PlayEntrance();

        // Tras el primer pase de diseño se ajusta la altura exacta.
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, Reposition);
    }

    internal void Hide()
    {
        if (AppWindow.IsVisible)
        {
            AppWindow.Hide();
            _hiddenAt = DateTimeOffset.UtcNow;
        }
    }

    private void Reposition()
    {
        DisplayArea area = DisplayArea.GetFromPoint(_anchor, DisplayAreaFallback.Nearest);
        RectInt32 work = area.WorkArea;
        RectInt32 outer = area.OuterBounds;
        double scale = ScaleAt(_anchor);

        // Lo que ocupa el marco de la ventana (diferencia entre tamaño total y área de cliente).
        int frameW = Math.Max(0, AppWindow.Size.Width - AppWindow.ClientSize.Width);
        int frameH = Math.Max(0, AppWindow.Size.Height - AppWindow.ClientSize.Height);

        int w = (int)Math.Round(WidthDip * scale);
        double clientWidthDip = (w - frameW) / scale;
        int h = (int)Math.Ceiling(MeasureHeight(clientWidthDip) * scale) + frameH;
        int m = (int)Math.Round(MarginDip * scale);
        h = Math.Min(h, work.Height - 2 * m);

        // Por defecto, esquina inferior derecha del área de trabajo (encima de la barra).
        int x = work.X + work.Width - w - m;
        int y = work.Y + work.Height - h - m;
        _taskbarAtTop = work.Y > outer.Y;
        if (_taskbarAtTop)
        {
            y = work.Y + m;
        }

        if (work.X > outer.X)
        {
            x = work.X + m; // barra a la izquierda
        }

        AppWindow.MoveAndResize(new RectInt32(x, y, w, h));
    }

    private double MeasureHeight(double widthDip)
    {
        if (ContentPanel.XamlRoot is null)
        {
            // Aún sin diseñar: estimación (se corrige en cuanto se muestra).
            int count = DeviceList.ItemsSource is System.Collections.ICollection c ? c.Count : 0;
            return 52 + Math.Max(1, count) * 72 + 44;
        }

        // Se mide con el ancho real del área de cliente para que los textos que
        // ocupan dos líneas cuenten bien.
        ContentPanel.Measure(new Size(widthDip, double.PositiveInfinity));
        return Math.Ceiling(ContentPanel.DesiredSize.Height);
    }

    private static double ScaleAt(PointInt32 point)
    {
        nint monitor = NativeMethods.MonitorFromPoint(new NativeMethods.POINT { X = point.X, Y = point.Y }, NativeMethods.MONITOR_DEFAULTTONEAREST);
        return monitor != 0 && NativeMethods.GetDpiForMonitor(monitor, 0, out uint dpi, out _) == 0 && dpi > 0
            ? dpi / 96.0
            : NativeMethods.GetDpiForSystem() / 96.0;
    }

    private void PlayEntrance()
    {
        var slide = new DoubleAnimation
        {
            From = _taskbarAtTop ? -24 : 24,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(260),
            EasingFunction = new ExponentialEase { Exponent = 6, EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(slide, SlideTransform);
        Storyboard.SetTargetProperty(slide, "Y");

        var fade = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(140) };
        Storyboard.SetTarget(fade, ContentPanel);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var storyboard = new Storyboard();
        storyboard.Children.Add(slide);
        storyboard.Children.Add(fade);
        storyboard.Begin();
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        // Como los paneles de Windows: al hacer clic fuera, se oculta.
        if (args.WindowActivationState == WindowActivationState.Deactivated && !_warmingUp)
        {
            Hide();
        }
    }

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            Hide();
            e.Handled = true;
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        RefreshButton.IsEnabled = false;
        try
        {
            await _controller.RefreshAsync();
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        _controller.OpenSettings();
    }

    private async void BluetoothLink_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        await Launcher.LaunchUriAsync(new Uri("ms-settings:bluetooth"));
    }

    private async void WelcomeEnable_Click(object sender, RoutedEventArgs e)
    {
        await _controller.SetStartupAsync(true);
        _controller.UpdateSettings(s => s.WelcomeDismissed = true);
    }

    private void WelcomeBar_CloseButtonClick(InfoBar sender, object args) =>
        _controller.UpdateSettings(s => s.WelcomeDismissed = true);

    private static unsafe void SetRoundedCorners(nint hwnd)
    {
        int preference = NativeMethods.DWMWCP_ROUND;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, &preference, sizeof(int));
    }
}

/// <summary>Atajo para el nombre accesible (lector de pantalla) de un control.</summary>
internal static class AutomationPropertiesHelper
{
    public static void SetName(DependencyObject element, string name) =>
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(element, name);
}
