using System.Runtime.InteropServices;

namespace BatteryTray.Windows.Native;

/// <summary>
/// Avisa cuando aparece o desaparece una interfaz de una clase (p. ej. al enchufar un
/// receptor USB). Usa CM_Register_Notification, que no necesita ventana.
/// El aviso llega en un hilo del sistema: el suscriptor solo debe programar trabajo.
/// </summary>
internal sealed unsafe partial class DeviceInterfaceWatcher : IDisposable
{
    private const uint CR_SUCCESS = 0;
    private const uint CM_NOTIFY_FILTER_TYPE_DEVICEINTERFACE = 0;

    private nint _registration;
    private GCHandle _self;

    public DeviceInterfaceWatcher(Guid interfaceClass)
    {
        _self = GCHandle.Alloc(this);
        var filter = new CM_NOTIFY_FILTER
        {
            cbSize = (uint)sizeof(CM_NOTIFY_FILTER),
            FilterType = CM_NOTIFY_FILTER_TYPE_DEVICEINTERFACE,
            ClassGuid = interfaceClass,
        };

        if (CM_Register_Notification(&filter, GCHandle.ToIntPtr(_self), &OnNotification, out _registration) != CR_SUCCESS)
        {
            _registration = 0;
            _self.Free();
        }
    }

    /// <summary>Algo se ha enchufado o desenchufado.</summary>
    public event Action? Changed;

    public bool IsActive => _registration != 0;

    [UnmanagedCallersOnly]
    private static uint OnNotification(nint notify, nint context, uint action, nint eventData, uint eventDataSize)
    {
        try
        {
            if (context != 0 && GCHandle.FromIntPtr(context).Target is DeviceInterfaceWatcher self)
            {
                self.Changed?.Invoke();
            }
        }
        catch (Exception)
        {
            // Nunca se deja escapar una excepción al sistema.
        }

        return 0; // ERROR_SUCCESS
    }

    public void Dispose()
    {
        if (_registration != 0)
        {
            // Espera a que terminen los avisos en curso; no se llama desde el propio aviso.
            CM_Unregister_Notification(_registration);
            _registration = 0;
        }

        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 416)]
    private struct CM_NOTIFY_FILTER
    {
        [FieldOffset(0)]
        public uint cbSize;

        [FieldOffset(4)]
        public uint Flags;

        [FieldOffset(8)]
        public uint FilterType;

        [FieldOffset(12)]
        public uint Reserved;

        [FieldOffset(16)]
        public Guid ClassGuid;
    }

    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Register_Notification(CM_NOTIFY_FILTER* filter, nint context, delegate* unmanaged<nint, nint, uint, nint, uint, uint> callback, out nint notifyContext);

    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Unregister_Notification(nint notifyContext);
}
