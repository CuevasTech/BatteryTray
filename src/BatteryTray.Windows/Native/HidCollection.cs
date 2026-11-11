using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using static BatteryTray.Windows.Native.HidNative;
using Usb = BatteryTray.Windows.Providers.Usb;

namespace BatteryTray.Windows.Native;

/// <summary>Cómo está conectado el aparato.</summary>
internal enum HidTransport
{
    Other,
    Usb,
    Bluetooth,
    BluetoothLe,
}

/// <summary>Un valor estándar de batería declarado en el descriptor HID (p. ej. "Battery Strength").</summary>
internal sealed record HidValueInfo(
    HidpReportType ReportType,
    byte ReportId,
    ushort UsagePage,
    ushort Usage,
    ushort LinkCollection,
    int LogicalMin,
    int LogicalMax,
    ushort BitSize);

/// <summary>
/// Una colección HID tal como la expone Windows: una ruta que se puede abrir, con su
/// fabricante y producto (VID/PID), su uso principal y el tamaño de sus informes.
/// Un mismo aparato (p. ej. un receptor USB) expone varias.
/// </summary>
internal sealed partial record HidCollection
{
    public required string Path { get; init; }

    public required ushort VendorId { get; init; }

    public required ushort ProductId { get; init; }

    public ushort Version { get; init; }

    public required ushort UsagePage { get; init; }

    public required ushort Usage { get; init; }

    public int InputReportLength { get; init; }

    public int OutputReportLength { get; init; }

    public int FeatureReportLength { get; init; }

    /// <summary>Número de interfaz USB (MI_xx), si el aparato tiene varias.</summary>
    public int? InterfaceNumber { get; init; }

    /// <summary>Número de colección dentro de la interfaz (COLxx), si hay varias.</summary>
    public int? CollectionNumber { get; init; }

    public HidTransport Transport { get; init; }

    /// <summary>Agrupa todas las colecciones de un mismo aparato físico.</summary>
    public required string DeviceKey { get; init; }

    /// <summary>Id de instancia de Windows de la colección (para leer propiedades sin hablar con el aparato).</summary>
    public string? InstanceId { get; init; }

    /// <summary>Valores de batería estándar que declara el descriptor (puede estar vacío).</summary>
    public IReadOnlyList<HidValueInfo> BatteryValues { get; init; } = [];

    /// <summary>True si la colección es de un fabricante (página 0xFF00-0xFFFF): ahí van los protocolos propios.</summary>
    public bool IsVendorDefined => UsagePage >= 0xFF00;

    [GeneratedRegex(@"&mi_([0-9a-f]{2})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InterfaceRegex();

    [GeneratedRegex(@"&col([0-9a-f]{2})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CollectionRegex();

    /// <summary>Número de colección que aparece en la ruta ("&amp;col03" → 3).</summary>
    internal static int? ParseCollectionNumber(string path)
    {
        Match m = CollectionRegex().Match(path);
        return m.Success ? int.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture) : null;
    }

    /// <summary>Número de interfaz USB que aparece en la ruta ("&amp;mi_02" → 2).</summary>
    internal static int? ParseInterfaceNumber(string path)
    {
        Match m = InterfaceRegex().Match(path);
        return m.Success ? int.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture) : null;
    }

    /// <summary>Tipo de conexión según la ruta: USB, Bluetooth clásico (HID 0x1124) o BLE (HOGP 0x1812).</summary>
    internal static HidTransport ParseTransport(string path)
    {
        if (path.Contains("{00001812-0000-1000-8000-00805f9b34fb}", StringComparison.OrdinalIgnoreCase))
        {
            return HidTransport.BluetoothLe;
        }

        if (path.Contains("{00001124-0000-1000-8000-00805f9b34fb}", StringComparison.OrdinalIgnoreCase))
        {
            return HidTransport.Bluetooth;
        }

        return path.Contains("#vid_", StringComparison.OrdinalIgnoreCase) ? HidTransport.Usb : HidTransport.Other;
    }
}

/// <summary>Busca las colecciones HID presentes. Solo lee: abre cada una sin permisos de lectura ni escritura.</summary>
internal static unsafe class HidEnumerator
{
    // Los teclados y ratones los abre Windows en exclusiva, pero con acceso 0 se pueden
    // consultar sus atributos y su descriptor sin molestar a nadie.
    private const int MaxReportLength = 4096;
    private const int MaxValueCaps = 512;

    /// <summary>Usos estándar de batería que se buscan en los descriptores.</summary>
    private static readonly (ushort Page, ushort Usage)[] s_batteryUsages =
    [
        (0x06, 0x20), // Generic Device Controls: Battery Strength
        (0x85, 0x65), // Battery System: AbsoluteStateOfCharge (%)
        (0x85, 0x64), // Battery System: RelativeStateOfCharge (%)
        (0x0D, 0x3B), // Digitizers: Battery Strength (lápices)
    ];

    public static IReadOnlyList<HidCollection> GetCollections()
    {
        var result = new List<HidCollection>();
        foreach (string path in CfgMgr.GetPresentInterfaces(HidInterfaceClass))
        {
            if (TryRead(path) is HidCollection c)
            {
                result.Add(c);
            }
        }

        return result;
    }

    internal static HidCollection? TryRead(string path)
    {
        using SafeFileHandle handle = CreateFile(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, 0, OPEN_EXISTING, 0, 0);
        if (handle.IsInvalid)
        {
            return null;
        }

        var attributes = new HIDD_ATTRIBUTES { Size = (uint)sizeof(HIDD_ATTRIBUTES) };
        if (!HidD_GetAttributes(handle, &attributes) || !HidD_GetPreparsedData(handle, out nint preparsed))
        {
            return null;
        }

        try
        {
            HIDP_CAPS caps;
            if (HidP_GetCaps(preparsed, &caps) != HIDP_STATUS_SUCCESS)
            {
                return null;
            }

            string? instanceId = CfgMgr.GetInterfaceInstanceId(path);
            return new HidCollection
            {
                Path = path,
                VendorId = attributes.VendorID,
                ProductId = attributes.ProductID,
                Version = attributes.VersionNumber,
                UsagePage = caps.UsagePage,
                Usage = caps.Usage,
                InputReportLength = Math.Min((int)caps.InputReportByteLength, MaxReportLength),
                OutputReportLength = Math.Min((int)caps.OutputReportByteLength, MaxReportLength),
                FeatureReportLength = Math.Min((int)caps.FeatureReportByteLength, MaxReportLength),
                InterfaceNumber = HidCollection.ParseInterfaceNumber(path),
                CollectionNumber = HidCollection.ParseCollectionNumber(path),
                Transport = HidCollection.ParseTransport(path),
                DeviceKey = DeviceKey(instanceId, path),
                InstanceId = instanceId,
                BatteryValues = FindBatteryValues(preparsed, caps),
            };
        }
        finally
        {
            HidD_FreePreparsedData(preparsed);
        }
    }

    /// <summary>
    /// Añade nombre, fabricante y número de serie a un aparato. Solo se pregunta al aparato
    /// por USB (es inmediato); por Bluetooth podría tardar segundos si está dormido, así que
    /// se usa el nombre que Windows ya tiene guardado.
    /// </summary>
    public static Usb.HidDeviceGroupNames ReadNames(IReadOnlyList<HidCollection> collections)
    {
        foreach (HidCollection c in collections)
        {
            if (c.Transport == HidTransport.Usb)
            {
                using SafeFileHandle handle = CreateFile(c.Path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, 0, OPEN_EXISTING, 0, 0);
                if (handle.IsInvalid)
                {
                    continue;
                }

                string? product = ReadString(handle, &HidD_GetProductStringWrapper);
                if (product is null)
                {
                    continue;
                }

                return new Usb.HidDeviceGroupNames(product, ReadString(handle, &HidD_GetManufacturerStringWrapper), ReadString(handle, &HidD_GetSerialNumberStringWrapper));
            }

            if (c.InstanceId is string id
                && (CfgMgr.GetStringProperty(id, CfgMgr.BusReportedDeviceDesc) ?? CfgMgr.GetStringProperty(id, CfgMgr.FriendlyName)) is string name)
            {
                return new Usb.HidDeviceGroupNames(name, null, null);
            }
        }

        return new Usb.HidDeviceGroupNames(null, null, null);
    }

    /// <summary>
    /// Clave del aparato físico: su ContainerId (igual para todas sus interfaces) y, si no hay,
    /// el dispositivo padre. El ContainerId del propio equipo (componentes internos) no sirve
    /// para agrupar, así que en ese caso se usa el padre.
    /// </summary>
    private static string DeviceKey(string? instanceId, string path)
    {
        if (instanceId is not null)
        {
            if (CfgMgr.GetGuidProperty(instanceId, CfgMgr.ContainerId) is Guid container
                && container != Guid.Empty
                && container != new Guid("00000000-0000-0000-FFFF-FFFFFFFFFFFF"))
            {
                return "container:" + container.ToString("N", CultureInfo.InvariantCulture);
            }

            if (CfgMgr.GetParentInstanceId(instanceId) is string parent)
            {
                return "parent:" + parent.ToUpperInvariant();
            }
        }

        return "path:" + path.ToUpperInvariant();
    }

    private static IReadOnlyList<HidValueInfo> FindBatteryValues(nint preparsed, HIDP_CAPS caps)
    {
        var found = new List<HidValueInfo>();
        Collect(HidpReportType.Input, caps.NumberInputValueCaps);
        Collect(HidpReportType.Feature, caps.NumberFeatureValueCaps);
        return found;

        void Collect(HidpReportType type, ushort count)
        {
            if (count == 0 || count > MaxValueCaps)
            {
                return;
            }

            var buffer = new HIDP_VALUE_CAPS[count];
            ushort length = count;
            fixed (HIDP_VALUE_CAPS* p = buffer)
            {
                if (HidP_GetValueCaps(type, p, ref length, preparsed) != HIDP_STATUS_SUCCESS)
                {
                    return;
                }
            }

            for (int i = 0; i < length && i < buffer.Length; i++)
            {
                HIDP_VALUE_CAPS v = buffer[i];
                if (v.IsRange != 0)
                {
                    continue;
                }

                if (s_batteryUsages.Contains((v.UsagePage, v.UsageMinOrUsage)) && v.LogicalMax != v.LogicalMin)
                {
                    found.Add(new HidValueInfo(type, v.ReportID, v.UsagePage, v.UsageMinOrUsage, v.LinkCollection, v.LogicalMin, v.LogicalMax, v.BitSize));
                }
            }
        }
    }

    // Las cadenas USB miden como mucho 126 caracteres; el búfer es de 127 + nulo.
    private static string? ReadString(SafeFileHandle handle, delegate*<SafeFileHandle, char*, uint, bool> getter)
    {
        char* buffer = stackalloc char[128];
        new Span<char>(buffer, 128).Clear();
        if (!getter(handle, buffer, 128 * sizeof(char)))
        {
            return null;
        }

        string s = new string(buffer, 0, 127);
        int nul = s.IndexOf('\0');
        s = (nul >= 0 ? s[..nul] : s).Trim();
        return s.Length > 0 ? s : null;
    }

    private static bool HidD_GetProductStringWrapper(SafeFileHandle h, char* b, uint l) => HidD_GetProductString(h, b, l);

    private static bool HidD_GetManufacturerStringWrapper(SafeFileHandle h, char* b, uint l) => HidD_GetManufacturerString(h, b, l);

    private static bool HidD_GetSerialNumberStringWrapper(SafeFileHandle h, char* b, uint l) => HidD_GetSerialNumberString(h, b, l);
}
