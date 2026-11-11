using BatteryTray.Windows.Native;

namespace BatteryTray.Windows.Providers.Usb;

/// <summary>
/// Lo que un protocolo necesita para hablar con una colección HID. En la app es
/// <see cref="HidDevice"/>; en las pruebas, un aparato simulado que responde con los
/// bytes documentados de cada fabricante.
/// </summary>
internal interface IHidChannel : IDisposable
{
    HidCollection Collection { get; }

    bool Write(ReadOnlySpan<byte> report, int timeoutMs);

    /// <summary>Bytes leídos, 0 si no llega nada a tiempo, -1 si hay error.</summary>
    int Read(Span<byte> buffer, int timeoutMs);

    bool GetFeature(Span<byte> buffer);

    bool SetFeature(ReadOnlySpan<byte> buffer);

    bool GetInputReport(Span<byte> buffer);

    bool SetOutputReport(ReadOnlySpan<byte> buffer);

    void FlushInput();
}

/// <summary>Abre colecciones HID (en la app, las de verdad).</summary>
internal interface IHidOpener
{
    /// <summary>Para leer y escribir informes.</summary>
    IHidChannel? Open(HidCollection collection);

    /// <summary>Sin permisos: solo informes de característica y de entrada por el canal de control.</summary>
    IHidChannel? OpenForQuery(HidCollection collection);

    /// <summary>Solo lectura de los informes que el aparato envía solo (nunca se escribe).</summary>
    IHidChannel? OpenForRead(HidCollection collection);
}

/// <summary>Abre las colecciones reales.</summary>
internal sealed class RealHidOpener : IHidOpener
{
    public static RealHidOpener Instance { get; } = new();

    public IHidChannel? Open(HidCollection collection) => HidDevice.Open(collection);

    public IHidChannel? OpenForQuery(HidCollection collection) => HidDevice.OpenForQuery(collection);

    public IHidChannel? OpenForRead(HidCollection collection) => HidDevice.OpenForRead(collection);
}
