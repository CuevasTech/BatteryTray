using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace BatteryTray;

/// <summary>
/// Punto de entrada propio para garantizar una sola copia en marcha:
/// si ya hay una, se le pasa el aviso (para que enseñe su panel) y esta se cierra.
/// </summary>
public static class Program
{
    private const string InstanceKey = "BatteryTray.Main";

    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        AppInstance keyInstance = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (!keyInstance.IsCurrent)
        {
            AppActivationArguments activation = AppInstance.GetCurrent().GetActivatedEventArgs();

            // Esta copia la abrió el usuario, así que puede pasar al primer plano; se le
            // cede ese permiso a la copia que ya estaba abierta para que su panel reciba el foco.
            Interop.NativeMethods.AllowSetForegroundWindow(keyInstance.ProcessId);

            // Se espera fuera del hilo principal; como mucho 5 s.
            Task.Run(() => keyInstance.RedirectActivationToAsync(activation).AsTask()).Wait(TimeSpan.FromSeconds(5));
            return 0;
        }

        keyInstance.Activated += (_, e) => AppController.Current?.OnSecondInstance(e);

        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });

        return 0;
    }
}
