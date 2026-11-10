using BatteryTray.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace BatteryTray;

public partial class App : Application
{
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
