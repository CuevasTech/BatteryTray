using System.Runtime.InteropServices;

namespace BatteryTray.Windows.Native;

/// <summary>Interfaces de dispositivo (rutas que se pueden abrir) y propiedades extra.</summary>
internal static unsafe partial class CfgMgr
{
    /// <summary>DEVPKEY_Device_InstanceId (STRING) de una interfaz.</summary>
    public static readonly DevPropKey InstanceId = new(new Guid("78C34FC8-104A-4ACA-9EA4-524D52996E57"), 256);

    /// <summary>DEVPKEY_Device_ContainerId (GUID): igual para todas las partes de un mismo aparato.</summary>
    public static readonly DevPropKey ContainerId = new(new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"), 2);

    /// <summary>DEVPKEY_Device_BusReportedDeviceDesc (STRING): el nombre que da el propio aparato.</summary>
    public static readonly DevPropKey BusReportedDeviceDesc = new(new Guid("540B947E-8B40-45BC-A8A2-6A0B894CBDA2"), 4);

    private const uint CM_GET_DEVICE_INTERFACE_LIST_PRESENT = 0x0;
    private const uint DEVPROP_TYPE_GUID = 0x0000000D;

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_List_SizeW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Get_Device_Interface_List_Size(out uint length, Guid* interfaceClass, string? deviceId, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_ListW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Get_Device_Interface_List(Guid* interfaceClass, string? deviceId, char* buffer, uint bufferLength, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_PropertyW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Get_Device_Interface_Property(string deviceInterface, DevPropKey* key, out uint propertyType, byte* buffer, ref uint bufferSize, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Parent")]
    private static partial uint CM_Get_Parent(out uint parent, uint devInst, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_ID_Size")]
    private static partial uint CM_Get_Device_ID_Size(out uint length, uint devInst, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_IDW")]
    private static partial uint CM_Get_Device_ID(uint devInst, char* buffer, uint bufferLength, uint flags);

    /// <summary>Rutas de las interfaces presentes de una clase (p. ej. todas las colecciones HID).</summary>
    public static IReadOnlyList<string> GetPresentInterfaces(Guid interfaceClass)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            uint cr = CM_Get_Device_Interface_List_Size(out uint length, &interfaceClass, null, CM_GET_DEVICE_INTERFACE_LIST_PRESENT);
            if (cr != CR_SUCCESS || length <= 1 || length > 2 * 1024 * 1024)
            {
                return [];
            }

            char[] buffer = new char[length];
            fixed (char* p = buffer)
            {
                cr = CM_Get_Device_Interface_List(&interfaceClass, null, p, length, CM_GET_DEVICE_INTERFACE_LIST_PRESENT);
            }

            if (cr == CR_BUFFER_SMALL)
            {
                continue; // la lista cambió entre las dos llamadas
            }

            return cr == CR_SUCCESS ? ParseMultiSz(buffer) : [];
        }

        return [];
    }

    /// <summary>Id de instancia del dispositivo al que pertenece una interfaz.</summary>
    public static string? GetInterfaceInstanceId(string interfacePath)
    {
        DevPropKey key = InstanceId;
        uint size = 0;
        uint cr = CM_Get_Device_Interface_Property(interfacePath, &key, out uint type, null, ref size, 0);
        if (cr != CR_BUFFER_SMALL || type != DEVPROP_TYPE_STRING || size < 2 || size > MaxStringBytes)
        {
            return null;
        }

        byte[] buffer = new byte[size];
        fixed (byte* p = buffer)
        {
            cr = CM_Get_Device_Interface_Property(interfacePath, &key, out type, p, ref size, 0);
        }

        if (cr != CR_SUCCESS || type != DEVPROP_TYPE_STRING)
        {
            return null;
        }

        string s = new(MemoryMarshal.Cast<byte, char>(buffer.AsSpan(0, (int)Math.Min(size, (uint)buffer.Length) / 2 * 2)));
        int nul = s.IndexOf('\0');
        return nul >= 0 ? s[..nul] : s;
    }

    /// <summary>Lee una propiedad GUID de un dispositivo; null si no existe.</summary>
    public static Guid? GetGuidProperty(string instanceId, DevPropKey key)
    {
        if (!TryLocate(instanceId, out uint devInst))
        {
            return null;
        }

        Guid value;
        uint size = (uint)sizeof(Guid);
        uint cr = CM_Get_DevNode_Property(devInst, &key, out uint type, (byte*)&value, ref size, 0);
        return cr == CR_SUCCESS && type == DEVPROP_TYPE_GUID && size == sizeof(Guid) ? value : null;
    }

    /// <summary>
    /// True si el dispositivo cuelga de un bus virtual (ViGEm/DS4Windows y similares, que
    /// crean mandos "falsos" a partir de uno real): esos no se cuentan para no duplicarlos.
    /// </summary>
    public static bool IsVirtualDevice(string instanceId)
    {
        string? current = instanceId;
        for (int depth = 0; depth < 6 && current is not null; depth++)
        {
            string upper = current.ToUpperInvariant();
            if (upper.StartsWith(@"ROOT", StringComparison.Ordinal) || upper.Contains("VIGEM", StringComparison.Ordinal) || upper.Contains("NEFARIUS", StringComparison.Ordinal))
            {
                return true;
            }

            current = GetParentInstanceId(current);
        }

        return false;
    }

    /// <summary>Id de instancia del padre de un dispositivo (p. ej. la interfaz USB de una colección HID).</summary>
    public static string? GetParentInstanceId(string instanceId)
    {
        if (!TryLocate(instanceId, out uint devInst) || CM_Get_Parent(out uint parent, devInst, 0) != CR_SUCCESS)
        {
            return null;
        }

        if (CM_Get_Device_ID_Size(out uint length, parent, 0) != CR_SUCCESS || length == 0 || length > 1024)
        {
            return null;
        }

        char[] buffer = new char[length + 1];
        fixed (char* p = buffer)
        {
            if (CM_Get_Device_ID(parent, p, (uint)buffer.Length, 0) != CR_SUCCESS)
            {
                return null;
            }
        }

        return new string(buffer, 0, (int)length);
    }
}
