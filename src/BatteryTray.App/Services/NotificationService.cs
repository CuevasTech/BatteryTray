using System.Security;
using BatteryTray.Core;
using Microsoft.Windows.AppNotifications;

namespace BatteryTray.Services;

/// <summary>Notificaciones de Windows (batería baja, conexiones).</summary>
internal sealed class NotificationService : IDisposable
{
    private bool _registered;

    /// <summary>El usuario pulsó una notificación (llega desde otro hilo).</summary>
    public event Action? Invoked;

    public void Initialize()
    {
        try
        {
            AppNotificationManager manager = AppNotificationManager.Default;
            // El manejador se conecta antes de Register para que el clic llegue a este proceso.
            manager.NotificationInvoked += OnInvoked;
            if (AppInfo.IsPackaged)
            {
                manager.Register();
            }
            else
            {
                manager.Register(Loc.Get("AppDisplayName"), new Uri(AppInfo.IconPath));
            }

            _registered = true;
        }
        catch (Exception ex)
        {
            // Sin notificaciones la app sigue funcionando.
            AppLog.Error("No se pudieron activar las notificaciones", ex);
        }
    }

    public void Show(string title, string body)
    {
        if (!_registered)
        {
            return;
        }

        try
        {
            // El XML se construye a mano escapando el texto: los nombres de dispositivo
            // vienen de fuera y no deben poder inyectar etiquetas en la notificación.
            string xml =
                "<toast><visual><binding template=\"ToastGeneric\">" +
                $"<text>{SecurityElement.Escape(title)}</text>" +
                $"<text>{SecurityElement.Escape(body)}</text>" +
                "</binding></visual></toast>";
            AppNotificationManager.Default.Show(new AppNotification(xml));
        }
        catch (Exception ex)
        {
            AppLog.Error("No se pudo mostrar la notificación", ex);
        }
    }

    private void OnInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args) => Invoked?.Invoke();

    public void Dispose()
    {
        if (!_registered)
        {
            return;
        }

        try
        {
            AppNotificationManager.Default.NotificationInvoked -= OnInvoked;
            AppNotificationManager.Default.Unregister();
        }
        catch (Exception ex)
        {
            AppLog.Error("Error al cerrar las notificaciones", ex);
        }

        _registered = false;
    }
}
