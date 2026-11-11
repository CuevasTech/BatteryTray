using System.Runtime.InteropServices;

namespace BatteryTray.Interop;

/// <summary>Llamadas a la API de Windows para la barra dentro de la barra de tareas.</summary>
internal static unsafe partial class NativeMethods
{
    public const uint WM_DESTROY = 0x0002;
    public const uint WM_MOUSEACTIVATE = 0x0021;
    public const uint WM_TIMER = 0x0113;
    public const uint WM_MOUSEMOVE = 0x0200;
    public const uint WM_LBUTTONDOWN = 0x0201;
    public const uint WM_LBUTTONUP = 0x0202;
    public const uint WM_RBUTTONUP = 0x0205;
    public const uint WM_MOUSELEAVE = 0x02A3;
    public const int MA_NOACTIVATE = 3;

    public const uint WS_POPUP = 0x80000000;
    public const uint WS_CHILD = 0x40000000;
    public const uint WS_CLIPSIBLINGS = 0x04000000;
    public const uint WS_EX_TOPMOST = 0x00000008;
    public const uint WS_EX_LAYERED = 0x00080000;
    public const nint HWND_MESSAGE = -3;
    public const nint HWND_TOP = 0;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const int SW_HIDE = 0;
    public const int IDC_ARROW = 32512;
    public const int CW_USEDEFAULT = unchecked((int)0x80000000);

    public const uint ULW_ALPHA = 0x2;
    public const byte AC_SRC_OVER = 0x0;
    public const byte AC_SRC_ALPHA = 0x1;
    public const uint DIB_RGB_COLORS = 0;
    public const uint TME_LEAVE = 0x2;

    public const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0;
    public const uint WINEVENT_SKIPOWNPROCESS = 0x2;
    public const int OBJID_WINDOW = 0;

    public const uint ICC_BAR_CLASSES = 0x4;
    public const uint TTS_ALWAYSTIP = 0x1;
    public const uint TTS_NOPREFIX = 0x2;
    public const uint TTF_SUBCLASS = 0x10;
    public const uint TTM_SETMAXTIPWIDTH = WM_USER + 24;
    public const uint TTM_ADDTOOLW = WM_USER + 50;
    public const uint TTM_DELTOOLW = WM_USER + 51;
    public const uint TTM_POP = WM_USER + 28;
    public const uint TTM_ACTIVATE = WM_USER + 1;
    public const uint WM_NOTIFY = 0x004E;
    public const int TTN_SHOW = -521;
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint MONITOR_DEFAULTTOPRIMARY = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly int Width => Right - Left;

        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SIZE
    {
        public int Width;
        public int Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public POINT pt;
        public uint lPrivate;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TRACKMOUSEEVENT
    {
        public uint cbSize;
        public uint dwFlags;
        public nint hwndTrack;
        public uint dwHoverTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TOOLINFOW
    {
        public uint cbSize;
        public uint uFlags;
        public nint hwnd;
        public nuint uId;
        public RECT rect;
        public nint hinst;
        public char* lpszText;
        public nint lParam;
        public void* lpReserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NMHDR
    {
        public nint hwndFrom;
        public nuint idFrom;
        public int code;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INITCOMMONCONTROLSEX
    {
        public uint dwSize;
        public uint dwICC;
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint FindWindowW(string? className, string? windowName);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint FindWindowExW(nint parent, nint childAfter, string? className, string? windowName);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(nint hwnd, RECT* rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetClientRect(nint hwnd, RECT* rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ScreenToClient(nint hwnd, POINT* point);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ClientToScreen(nint hwnd, POINT* point);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShowWindow(nint hwnd, int command);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(nint hwnd);

    [LibraryImport("user32.dll")]
    public static partial uint GetDpiForWindow(nint hwnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UpdateLayeredWindow(nint hwnd, nint hdcDst, POINT* pptDst, SIZE* psize, nint hdcSrc, POINT* pptSrc, uint crKey, BLENDFUNCTION* pblend, uint flags);

    [LibraryImport("user32.dll")]
    public static partial nint GetDC(nint hwnd);

    [LibraryImport("user32.dll")]
    public static partial int ReleaseDC(nint hwnd, nint hdc);

    [LibraryImport("user32.dll")]
    public static partial nuint SetTimer(nint hwnd, nuint id, uint elapse, nint timerProc);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool KillTimer(nint hwnd, nuint id);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TrackMouseEvent(TRACKMOUSEEVENT* track);

    [LibraryImport("user32.dll")]
    public static partial int GetMessageW(MSG* msg, nint hwnd, uint filterMin, uint filterMax);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TranslateMessage(MSG* msg);

    [LibraryImport("user32.dll")]
    public static partial nint DispatchMessageW(MSG* msg);

    [LibraryImport("user32.dll")]
    public static partial void PostQuitMessage(int exitCode);

    [LibraryImport("user32.dll")]
    public static partial nint SendMessageW(nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    public static partial nint SetWinEventHook(uint eventMin, uint eventMax, nint module, delegate* unmanaged<nint, uint, nint, int, int, uint, uint, void> callback, uint processId, uint threadId, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnhookWinEvent(nint hook);

    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(nint hwnd, uint* processId);

    [LibraryImport("user32.dll")]
    public static partial nint LoadCursorW(nint instance, nint cursorName);

    /// <summary>True si el mensaje actual lo ha enviado otro hilo (u otro proceso).</summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool InSendMessage();

    [LibraryImport("user32.dll")]
    public static partial nint MonitorFromWindow(nint hwnd, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetMonitorInfoW(nint monitor, MONITORINFO* info);

    [LibraryImport("gdi32.dll")]
    public static partial nint CreateCompatibleDC(nint hdc);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteDC(nint hdc);

    [LibraryImport("gdi32.dll")]
    public static partial nint CreateDIBSection(nint hdc, BITMAPINFOHEADER* info, uint usage, void** bits, nint section, uint offset);

    [LibraryImport("gdi32.dll")]
    public static partial nint SelectObject(nint hdc, nint obj);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(nint obj);

    [LibraryImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool InitCommonControlsEx(INITCOMMONCONTROLSEX* init);

    [LibraryImport("uxtheme.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int SetWindowTheme(nint hwnd, string? subAppName, string? subIdList);

    /// <summary>Coordenadas con signo de un mensaje de ratón (lParam).</summary>
    public static (int X, int Y) PointFromLParam(nint lParam) => ((short)LoWord(lParam), (short)HiWord(lParam));
}
