using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BatteryTray.Windows.Native;

/// <summary>Llamadas a la API HID de Windows (hid.dll) y a la E/S de archivos que usa.</summary>
internal static unsafe partial class HidNative
{
    /// <summary>GUID_DEVINTERFACE_HID: todas las colecciones HID presentes.</summary>
    public static readonly Guid HidInterfaceClass = new("4D1E55B2-F16F-11CF-88CB-001111000030");

    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ = 0x1;
    public const uint FILE_SHARE_WRITE = 0x2;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_FLAG_OVERLAPPED = 0x40000000;
    public const int ERROR_IO_PENDING = 997;
    public const int HIDP_STATUS_SUCCESS = 0x00110000;

    public enum HidpReportType
    {
        Input = 0,
        Output = 1,
        Feature = 2,
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDD_ATTRIBUTES
    {
        public uint Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDP_CAPS
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        public fixed ushort Reserved[17];
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    /// <summary>HIDP_VALUE_CAPS (72 bytes). La unión Range/NotRange se lee como 8 campos.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct HIDP_VALUE_CAPS
    {
        public ushort UsagePage;
        public byte ReportID;
        public byte IsAlias;
        public ushort BitField;
        public ushort LinkCollection;
        public ushort LinkUsage;
        public ushort LinkUsagePage;
        public byte IsRange;
        public byte IsStringRange;
        public byte IsDesignatorRange;
        public byte IsAbsolute;
        public byte HasNull;
        public byte Reserved;
        public ushort BitSize;
        public ushort ReportCount;
        public fixed ushort Reserved2[5];
        public uint UnitsExp;
        public uint Units;
        public int LogicalMin;
        public int LogicalMax;
        public int PhysicalMin;
        public int PhysicalMax;

        // Range.UsageMin / NotRange.Usage
        public ushort UsageMinOrUsage;

        // Range.UsageMax / NotRange.Reserved1
        public ushort UsageMax;
        public ushort StringMin;
        public ushort StringMax;
        public ushort DesignatorMin;
        public ushort DesignatorMax;
        public ushort DataIndexMin;
        public ushort DataIndexMax;
    }

    /// <summary>HIDP_BUTTON_CAPS (72 bytes).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct HIDP_BUTTON_CAPS
    {
        public ushort UsagePage;
        public byte ReportID;
        public byte IsAlias;
        public ushort BitField;
        public ushort LinkCollection;
        public ushort LinkUsage;
        public ushort LinkUsagePage;
        public byte IsRange;
        public byte IsStringRange;
        public byte IsDesignatorRange;
        public byte IsAbsolute;
        public ushort ReportCount;
        public ushort Reserved2;
        public fixed uint Reserved[9];
        public ushort UsageMinOrUsage;
        public ushort UsageMax;
        public ushort StringMin;
        public ushort StringMax;
        public ushort DesignatorMin;
        public ushort DesignatorMax;
        public ushort DataIndexMin;
        public ushort DataIndexMax;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, nint securityAttributes, uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ReadFile(SafeFileHandle file, byte* buffer, uint bytesToRead, uint* bytesRead, NativeOverlapped* overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WriteFile(SafeFileHandle file, byte* buffer, uint bytesToWrite, uint* bytesWritten, NativeOverlapped* overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetOverlappedResult(SafeFileHandle file, NativeOverlapped* overlapped, uint* bytesTransferred, [MarshalAs(UnmanagedType.Bool)] bool wait);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CancelIoEx(SafeFileHandle file, NativeOverlapped* overlapped);

    [LibraryImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool HidD_GetAttributes(SafeFileHandle device, HIDD_ATTRIBUTES* attributes);

    [LibraryImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool HidD_GetPreparsedData(SafeFileHandle device, out nint preparsedData);

    [LibraryImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool HidD_FreePreparsedData(nint preparsedData);

    [LibraryImport("hid.dll")]
    public static partial int HidP_GetCaps(nint preparsedData, HIDP_CAPS* capabilities);

    [LibraryImport("hid.dll")]
    public static partial int HidP_GetValueCaps(HidpReportType reportType, HIDP_VALUE_CAPS* valueCaps, ref ushort valueCapsLength, nint preparsedData);

    [LibraryImport("hid.dll")]
    public static partial int HidP_GetButtonCaps(HidpReportType reportType, HIDP_BUTTON_CAPS* buttonCaps, ref ushort buttonCapsLength, nint preparsedData);

    [LibraryImport("hid.dll")]
    public static partial int HidP_GetUsageValue(HidpReportType reportType, ushort usagePage, ushort linkCollection, ushort usage, out uint usageValue, nint preparsedData, byte* report, uint reportLength);

    [LibraryImport("hid.dll")]
    public static partial int HidP_GetUsages(HidpReportType reportType, ushort usagePage, ushort linkCollection, ushort* usageList, ref uint usageLength, nint preparsedData, byte* report, uint reportLength);

    [LibraryImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool HidD_GetFeature(SafeFileHandle device, byte* reportBuffer, uint reportBufferLength);

    [LibraryImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool HidD_SetFeature(SafeFileHandle device, byte* reportBuffer, uint reportBufferLength);

    [LibraryImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool HidD_GetInputReport(SafeFileHandle device, byte* reportBuffer, uint reportBufferLength);

    [LibraryImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool HidD_SetOutputReport(SafeFileHandle device, byte* reportBuffer, uint reportBufferLength);

    [LibraryImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool HidD_FlushQueue(SafeFileHandle device);

    [LibraryImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool HidD_GetProductString(SafeFileHandle device, char* buffer, uint bufferLength);

    [LibraryImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool HidD_GetManufacturerString(SafeFileHandle device, char* buffer, uint bufferLength);

    [LibraryImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool HidD_GetSerialNumberString(SafeFileHandle device, char* buffer, uint bufferLength);
}
