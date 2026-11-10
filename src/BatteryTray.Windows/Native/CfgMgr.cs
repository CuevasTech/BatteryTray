using System.Runtime.InteropServices;

namespace BatteryTray.Windows.Native;

/// <summary>Clave de propiedad de dispositivo (DEVPROPKEY).</summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly struct DevPropKey(Guid fmtid, uint pid)
{
    public readonly Guid FmtId = fmtid;
    public readonly uint Pid = pid;
}

/// <summary>
/// Acceso de solo lectura al árbol de dispositivos de Windows (Configuration Manager, cfgmgr32.dll).
/// Todas las llamadas comprueban el código de retorno y el tipo de dato devuelto,
/// y los búferes se dimensionan con lo que indica la propia API.
/// </summary>
internal static unsafe partial class CfgMgr
{
    /// <summary>DEVPKEY_Bluetooth_Battery: nivel 0-100 (BYTE). Es el dato que enseña Configuración > Bluetooth.</summary>
    public static readonly DevPropKey BluetoothBattery = new(new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5"), 2);

    /// <summary>DEVPKEY_Bluetooth_DeviceAddress: dirección en hex sin separadores (STRING).</summary>
    public static readonly DevPropKey BluetoothAddress = new(new Guid("2BD67D8B-8BEB-48D5-87E0-6CDA3428040A"), 1);

    /// <summary>DEVPKEY_Device_FriendlyName (STRING).</summary>
    public static readonly DevPropKey FriendlyName = new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);

    private const uint CR_SUCCESS = 0x00;
    private const uint CR_BUFFER_SMALL = 0x1A;
    private const uint CM_GETIDLIST_FILTER_ENUMERATOR = 0x00000001;
    private const uint CM_GETIDLIST_FILTER_PRESENT = 0x00000100;
    private const uint CM_LOCATE_DEVNODE_NORMAL = 0x00000000;
    private const uint DEVPROP_TYPE_BYTE = 0x00000003;
    private const uint DEVPROP_TYPE_STRING = 0x00000012;
    private const int MaxStringBytes = 4096;

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_ID_List_SizeW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Get_Device_ID_List_Size(out uint length, string? filter, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_ID_ListW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Get_Device_ID_List(string? filter, char* buffer, uint bufferLength, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Locate_DevNode(out uint devInst, string deviceId, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_PropertyW")]
    private static partial uint CM_Get_DevNode_Property(uint devInst, DevPropKey* key, out uint propertyType, byte* buffer, ref uint bufferSize, uint flags);

    /// <summary>Ids de instancia de los dispositivos presentes de un enumerador (p. ej. "BTHENUM").</summary>
    public static IReadOnlyList<string> GetPresentDeviceIds(string enumerator)
    {
        const uint flags = CM_GETIDLIST_FILTER_ENUMERATOR | CM_GETIDLIST_FILTER_PRESENT;

        // La lista puede crecer entre las dos llamadas: se reintenta unas pocas veces.
        for (int attempt = 0; attempt < 4; attempt++)
        {
            uint cr = CM_Get_Device_ID_List_Size(out uint length, enumerator, flags);
            if (cr != CR_SUCCESS)
            {
                return [];
            }

            if (length <= 1)
            {
                return [];
            }

            // Límite defensivo: una lista real cabe holgadamente en 4 MB.
            if (length > 2 * 1024 * 1024)
            {
                return [];
            }

            char[] buffer = new char[length];
            fixed (char* p = buffer)
            {
                cr = CM_Get_Device_ID_List(enumerator, p, length, flags);
            }

            if (cr == CR_BUFFER_SMALL)
            {
                continue;
            }

            if (cr != CR_SUCCESS)
            {
                return [];
            }

            return ParseMultiSz(buffer);
        }

        return [];
    }

    /// <summary>Lee una propiedad BYTE de un dispositivo; null si no existe o no es BYTE.</summary>
    public static byte? GetByteProperty(string instanceId, DevPropKey key)
    {
        if (!TryLocate(instanceId, out uint devInst))
        {
            return null;
        }

        byte value = 0;
        uint size = 1;
        uint cr = CM_Get_DevNode_Property(devInst, &key, out uint type, &value, ref size, 0);
        return cr == CR_SUCCESS && type == DEVPROP_TYPE_BYTE && size == 1 ? value : null;
    }

    /// <summary>Lee una propiedad de texto de un dispositivo; null si no existe o no es texto.</summary>
    public static string? GetStringProperty(string instanceId, DevPropKey key)
    {
        if (!TryLocate(instanceId, out uint devInst))
        {
            return null;
        }

        uint size = 0;
        uint cr = CM_Get_DevNode_Property(devInst, &key, out uint type, null, ref size, 0);
        if (cr != CR_BUFFER_SMALL || type != DEVPROP_TYPE_STRING || size < 2 || size > MaxStringBytes)
        {
            return null;
        }

        byte[] buffer = new byte[size];
        fixed (byte* p = buffer)
        {
            cr = CM_Get_DevNode_Property(devInst, &key, out type, p, ref size, 0);
        }

        if (cr != CR_SUCCESS || type != DEVPROP_TYPE_STRING)
        {
            return null;
        }

        int chars = (int)Math.Min(size, (uint)buffer.Length) / 2;
        string s = new(MemoryMarshal.Cast<byte, char>(buffer.AsSpan(0, chars * 2)));
        int nul = s.IndexOf('\0');
        return nul >= 0 ? s[..nul] : s;
    }

    private static bool TryLocate(string instanceId, out uint devInst)
    {
        devInst = 0;
        return !string.IsNullOrEmpty(instanceId)
            && CM_Locate_DevNode(out devInst, instanceId, CM_LOCATE_DEVNODE_NORMAL) == CR_SUCCESS;
    }

    internal static IReadOnlyList<string> ParseMultiSz(ReadOnlySpan<char> buffer)
    {
        var result = new List<string>();
        int start = 0;
        for (int i = 0; i < buffer.Length; i++)
        {
            if (buffer[i] != '\0')
            {
                continue;
            }

            if (i == start)
            {
                break; // doble nulo: fin de la lista
            }

            result.Add(new string(buffer[start..i]));
            start = i + 1;
        }

        return result;
    }
}
