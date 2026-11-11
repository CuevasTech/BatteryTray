using BatteryTray.Windows.Providers.Usb.Protocols;
using BatteryTray.Windows.Providers.Usb.Protocols.Logitech;

namespace BatteryTray.Windows.Providers.Usb;

/// <summary>
/// Los protocolos, por orden: primero los de cada fabricante (más precisos) y al final el
/// estándar HID. Cada aparato lo atiende el primero que lo reconoce.
/// </summary>
internal static class UsbProtocols
{
    public static IReadOnlyList<IHidBatteryProtocol> All { get; } =
    [
        new Headsets(),
        new LogitechCenturion(),
        new LogitechHidpp(),
        new Razer(),
        new RazerHeadset(),
        new PassiveGamepads(),
        new StandardHidBattery(),
    ];
}
