using System.Security.Cryptography;
using System.Text;
using BatteryTray.Core;
using BatteryTray.Windows.Native;

namespace BatteryTray.Windows.Providers.Usb;

/// <summary>Nombre, fabricante y número de serie que da el aparato (sin limpiar: son datos no fiables).</summary>
internal sealed record HidDeviceGroupNames(string? Product, string? Manufacturer, string? SerialNumber);

/// <summary>Un aparato físico (receptor, ratón por cable, cascos...) con todas sus colecciones HID.</summary>
internal sealed class HidDeviceGroup(string key, IReadOnlyList<HidCollection> collections, HidDeviceGroupNames? names = null)
{
    public string Key { get; } = key;

    public IReadOnlyList<HidCollection> Collections { get; } = collections;

    public HidDeviceGroupNames Names { get; } = names ?? new HidDeviceGroupNames(null, null, null);

    public ushort VendorId => Collections[0].VendorId;

    public ushort ProductId => Collections[0].ProductId;

    public HidTransport Transport => Collections[0].Transport;

    /// <summary>Nombre que da el propio aparato, sin limpiar.</summary>
    public string? Product => Names.Product;

    public string? SerialNumber => Names.SerialNumber;

    /// <summary>La primera colección que cumple la condición.</summary>
    public HidCollection? Find(Func<HidCollection, bool> predicate) => Collections.FirstOrDefault(predicate);

    /// <summary>Copia con nombre y número de serie (se leen solo de los aparatos que se van a consultar).</summary>
    public HidDeviceGroup WithNames() => new(Key, Collections, HidEnumerator.ReadNames(Collections));

    public bool IsUsb => Transport == HidTransport.Usb;

    /// <summary>Agrupa las colecciones por aparato físico.</summary>
    public static IReadOnlyList<HidDeviceGroup> Group(IEnumerable<HidCollection> collections) =>
        collections
            .GroupBy(c => c.DeviceKey, StringComparer.Ordinal)
            .Select(g => new HidDeviceGroup(g.Key, g.OrderBy(c => c.InterfaceNumber ?? -1).ThenBy(c => c.Path, StringComparer.Ordinal).ToList()))
            .ToList();
}

/// <summary>Una batería leída por un protocolo.</summary>
/// <param name="Identity">Algo estable que identifique el aparato (se convierte en un id con hash).</param>
/// <param name="Level">0-100, o null si ahora mismo no se puede leer (apagado, dormido...).</param>
internal sealed record HidBatteryReading(
    string Identity,
    string Name,
    DeviceKind Kind,
    int? Level,
    bool Charging,
    bool Connected = true);

/// <summary>
/// Un protocolo de batería de un fabricante (Logitech, Razer...) o el estándar HID.
///
/// Reglas para todos (seguridad):
/// - <see cref="Supports"/> decide solo con VID/PID y el tipo de colección, sin hablar con el aparato.
///   Así nunca se envía nada a un aparato de otro fabricante.
/// - <see cref="Read"/> solo envía consultas de lectura documentadas por el protocolo y
///   nunca comandos que cambien la configuración del aparato.
/// - Todo lo que devuelve el aparato se trata como no fiable: se comprueban tamaños y rangos.
/// </summary>
internal interface IHidBatteryProtocol
{
    /// <summary>Nombre corto (va en el id y en el diagnóstico); solo letras minúsculas.</summary>
    string Name { get; }

    bool Supports(HidDeviceGroup device);

    /// <summary>Lee las baterías (un receptor puede tener varios aparatos). Se llama en un hilo de fondo.</summary>
    IReadOnlyList<HidBatteryReading> Read(HidDeviceGroup device, CancellationToken cancellationToken);
}

/// <summary>Ids estables y seguros para los aparatos USB.</summary>
internal static class HidIds
{
    /// <summary>
    /// "usb:&lt;protocolo&gt;:&lt;12 hex&gt;". El número de serie y demás datos van resumidos con
    /// SHA-256: el id es estable, corto, con caracteres seguros y no revela el número de serie.
    /// </summary>
    public static string Make(string protocol, string identity)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return $"usb:{protocol}:{Convert.ToHexStringLower(hash.AsSpan(0, 6))}";
    }

    /// <summary>Identidad del aparato a partir de VID/PID y número de serie (o la clave de Windows si no tiene).</summary>
    public static string Identity(HidDeviceGroup device, string? suffix = null) =>
        $"{device.VendorId:X4}:{device.ProductId:X4}:{device.SerialNumber ?? device.Key}{(suffix is null ? "" : ":" + suffix)}";
}
