using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using static BatteryTray.Windows.Native.HidNative;

namespace BatteryTray.Windows.Native;

/// <summary>
/// Una colección HID abierta para hablar con el aparato.
///
/// Seguridad: se abre en modo compartido (el programa del fabricante y los juegos siguen
/// funcionando) y toda la E/S tiene un tiempo máximo, así un aparato que no responde
/// nunca bloquea la app. Quien la usa solo envía consultas de lectura de protocolos
/// conocidos, y solo a los aparatos de ese fabricante (ver los proveedores).
/// No es seguro usar una instancia desde varios hilos a la vez.
/// </summary>
internal sealed unsafe class HidDevice : Providers.Usb.IHidChannel
{
    private readonly SafeFileHandle _handle;
    private readonly ManualResetEvent _ioDone = new(false);

    private HidDevice(SafeFileHandle handle, HidCollection collection)
    {
        _handle = handle;
        Collection = collection;
    }

    public HidCollection Collection { get; }

    /// <summary>Abre la colección para leer y escribir informes; null si no se puede (en uso exclusivo, desconectado...).</summary>
    public static HidDevice? Open(HidCollection collection)
    {
        SafeFileHandle handle = CreateFile(
            collection.Path,
            GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            0,
            OPEN_EXISTING,
            FILE_FLAG_OVERLAPPED,
            0);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }

        return new HidDevice(handle, collection);
    }

    /// <summary>
    /// Abre la colección solo para leer los informes que el aparato envía por su cuenta
    /// (p. ej. un mando). Cada lector recibe su propia copia, así que no afecta a los juegos.
    /// </summary>
    public static HidDevice? OpenForRead(HidCollection collection)
    {
        SafeFileHandle handle = CreateFile(collection.Path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, 0, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, 0);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }

        return new HidDevice(handle, collection);
    }

    /// <summary>Abre la colección sin permisos (solo para leer informes de característica, p. ej. en teclados y ratones).</summary>
    public static HidDevice? OpenForQuery(HidCollection collection)
    {
        SafeFileHandle handle = CreateFile(collection.Path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, 0, OPEN_EXISTING, 0, 0);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }

        return new HidDevice(handle, collection);
    }

    /// <summary>Pide un informe de característica (GET_REPORT). buffer[0] es el id del informe.</summary>
    public bool GetFeature(Span<byte> buffer)
    {
        fixed (byte* p = buffer)
        {
            return buffer.Length > 0 && HidD_GetFeature(_handle, p, (uint)buffer.Length);
        }
    }

    /// <summary>Envía un informe de característica (SET_REPORT). Solo para comandos de consulta de protocolos conocidos.</summary>
    public bool SetFeature(ReadOnlySpan<byte> buffer)
    {
        fixed (byte* p = buffer)
        {
            return buffer.Length > 0 && HidD_SetFeature(_handle, p, (uint)buffer.Length);
        }
    }

    /// <summary>Pide el último informe de entrada (GET_REPORT de entrada). buffer[0] es el id del informe.</summary>
    public bool GetInputReport(Span<byte> buffer)
    {
        fixed (byte* p = buffer)
        {
            return buffer.Length > 0 && HidD_GetInputReport(_handle, p, (uint)buffer.Length);
        }
    }

    /// <summary>Envía un informe de salida por el canal de control.</summary>
    public bool SetOutputReport(ReadOnlySpan<byte> buffer)
    {
        fixed (byte* p = buffer)
        {
            return buffer.Length > 0 && HidD_SetOutputReport(_handle, p, (uint)buffer.Length);
        }
    }

    /// <summary>
    /// Extrae un valor de un informe ya leído usando el descriptor del propio aparato
    /// (así se respetan su tamaño y posición sin calcularlos a mano).
    /// </summary>
    public bool TryGetUsageValue(HidpReportType type, ReadOnlySpan<byte> report, ushort usagePage, ushort linkCollection, ushort usage, out uint value)
    {
        value = 0;
        if (!HidD_GetPreparsedData(_handle, out nint preparsed))
        {
            return false;
        }

        try
        {
            fixed (byte* p = report)
            {
                return HidP_GetUsageValue(type, usagePage, linkCollection, usage, out value, preparsed, p, (uint)report.Length) == HIDP_STATUS_SUCCESS;
            }
        }
        finally
        {
            HidD_FreePreparsedData(preparsed);
        }
    }

    /// <summary>Descarta los informes de entrada pendientes.</summary>
    public void FlushInput() => HidD_FlushQueue(_handle);

    /// <summary>Escribe un informe de salida. Devuelve false si falla o tarda más de lo permitido.</summary>
    public bool Write(ReadOnlySpan<byte> report, int timeoutMs)
    {
        // Windows exige escribir exactamente el tamaño de informe de salida de la colección.
        int length = Collection.OutputReportLength > 0 ? Collection.OutputReportLength : report.Length;
        if (report.Length > length || length == 0)
        {
            return false;
        }

        byte[] buffer = new byte[length];
        report.CopyTo(buffer);
        return Transfer(buffer, write: true, timeoutMs) == length;
    }

    /// <summary>
    /// Lee un informe de entrada. Devuelve el número de bytes leídos, 0 si no llega nada a
    /// tiempo y -1 si hay un error (aparato desconectado...).
    /// </summary>
    public int Read(Span<byte> buffer, int timeoutMs)
    {
        if (buffer.Length < Collection.InputReportLength || buffer.Length == 0)
        {
            return -1;
        }

        return Transfer(buffer, write: false, timeoutMs);
    }

    private int Transfer(Span<byte> buffer, bool write, int timeoutMs)
    {
        _ioDone.Reset();
        var overlapped = new NativeOverlapped { EventHandle = _ioDone.SafeWaitHandle.DangerousGetHandle() };
        uint transferred = 0;
        fixed (byte* p = buffer)
        {
            bool ok = write
                ? WriteFile(_handle, p, (uint)buffer.Length, null, &overlapped)
                : ReadFile(_handle, p, (uint)buffer.Length, null, &overlapped);
            if (!ok && Marshal.GetLastPInvokeError() != ERROR_IO_PENDING)
            {
                return -1;
            }

            if (!_ioDone.WaitOne(Math.Max(1, timeoutMs)))
            {
                // Se cancela y se espera a que Windows lo confirme antes de soltar el búfer.
                // Si justo terminó, se aprovecha lo leído.
                CancelIoEx(_handle, &overlapped);
                return GetOverlappedResult(_handle, &overlapped, &transferred, true) ? (int)transferred : 0;
            }

            return GetOverlappedResult(_handle, &overlapped, &transferred, false) ? (int)transferred : -1;
        }
    }

    public void Dispose()
    {
        _handle.Dispose();
        _ioDone.Dispose();
    }
}
