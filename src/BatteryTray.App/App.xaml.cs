using BatteryTray.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace BatteryTray;

public partial class App : Application
{
    /// <summary>
    /// BATTERYTRAY_SCREENSHOTS=1 es solo para hacer las capturas del README y de la Store:
    /// oculta el aviso del modo demostración y pinta las barras del panel con el azul de
    /// Windows 11 por defecto en vez del color de acento del equipo donde se hacen.
    /// </summary>
    internal static bool ScreenshotMode { get; } = Environment.GetEnvironmentVariable("BATTERYTRAY_SCREENSHOTS") == "1";

    public App()
    {
        InitializeComponent();

        // Es una app de bandeja: no debe cerrarse al ocultar o cerrar sus ventanas.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;

        // Un fallo inesperado se registra y la app sigue viva (si se cerrara, el
        // usuario se quedaría sin indicador sin saber por qué).
        UnhandledException += (_, e) =>
        {
            AppLog.Error("Excepción no controlada", e.Exception);
            e.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Error("Tarea con excepción no observada", e.Exception);
            e.SetObserved();
        };
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            var controller = new AppController(DispatcherQueue.GetForCurrentThread());
            await controller.StartAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("Fallo al iniciar", ex);
        }
    }
}
