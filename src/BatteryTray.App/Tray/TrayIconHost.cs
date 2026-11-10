using System.Runtime.InteropServices;
using BatteryTray.Core;
using static BatteryTray.Interop.NativeMethods;

namespace BatteryTray.Tray;

/// <summary>Una opción del menú contextual de la bandeja.</summary>
internal sealed record TrayMenuItem(int Id, string? Text, bool IsChecked = false, bool IsDefault = false)
{
    public static TrayMenuItem Separator { get; } = new(0, null);
}

/// <summary>
/// Gestiona los iconos del área de notificación con la API de Windows (Shell_NotifyIcon).
/// Usa una ventana oculta (no "solo mensajes", para recibir el aviso "TaskbarCreated"
/// cuando el Explorador se reinicia y volver a poner los iconos).
/// Debe crearse y usarse desde el hilo de la interfaz.
/// </summary>
internal sealed unsafe class TrayIconHost : IDisposable
{
    private const uint WM_TRAYICON = WM_APP + 1;
    private const string ClassName = "BatteryTray.TrayHost";
    private static TrayIconHost? s_instance;

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, string> _keysById = [];
    private readonly uint _taskbarCreatedMessage;
    private readonly nint _classNamePtr;
    private nint _hwnd;
    private bool _disposed;

    public TrayIconHost()
    {
        s_instance = this;
        _taskbarCreatedMessage = RegisterWindowMessageW("TaskbarCreated");
        nint instance = GetModuleHandleW(null);
        _classNamePtr = Marshal.StringToHGlobalUni(ClassName);

        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)sizeof(WNDCLASSEXW),
            lpfnWndProc = &WndProc,
            hInstance = instance,
            lpszClassName = (char*)_classNamePtr,
        };
        RegisterClassExW(&wc); // si ya existía la clase, CreateWindowEx la reutiliza

        _hwnd = CreateWindowExW(0, ClassName, "BatteryTray", 0, 0, 0, 0, 0, 0, 0, instance, 0);
        if (_hwnd == 0)
        {
            throw new InvalidOperationException($"No se pudo crear la ventana de la bandeja (error {Marshal.GetLastPInvokeError()}).");
        }
    }

    /// <summary>Clic izquierdo (o Intro) en un icono: clave, punto de anclaje en píxeles y si fue con el teclado.</summary>
    public event Action<string, int, int, bool>? Activated;

    /// <summary>Clic derecho en un icono.</summary>
    public event Action<string, int, int>? ContextMenuRequested;

    /// <summary>Cambió el tema, el DPI o la pantalla: hay que volver a dibujar los iconos.</summary>
    public event Action? EnvironmentChanged;

    public nint Handle => _hwnd;

    /// <summary>Tamaño en píxeles de un icono pequeño para el DPI del sistema.</summary>
    public static int IconSize
    {
        get
        {
            int size = GetSystemMetricsForDpi(SM_CXSMICON, GetDpiForSystem());
            return size > 0 ? size : 16;
        }
    }

    /// <summary>Pone o actualiza un icono. El host pasa a ser dueño del HICON y lo libera.</summary>
    public void SetIcon(string key, nint icon, string tooltip)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_entries.TryGetValue(key, out Entry? entry))
        {
            nint old = entry.Icon;
            entry.Icon = icon;
            entry.Tooltip = tooltip;
            if (!entry.Added)
            {
                Add(entry);
            }
            else if (!Notify(NIM_MODIFY, entry))
            {
                // Si el Explorador perdió el icono, se vuelve a añadir.
                entry.Added = false;
                Add(entry);
            }

            if (old != 0 && old != icon && entry.OwnsIcon)
            {
                DestroyIcon(old);
            }

            return;
        }

        entry = new Entry(key, AllocateId(key)) { Icon = icon, Tooltip = tooltip };
        _entries[key] = entry;
        _keysById[entry.Id] = key;
        Add(entry);
    }

    /// <summary>Quita los iconos cuyas claves no estén en la lista.</summary>
    public void RemoveAllExcept(IReadOnlySet<string> keep)
    {
        foreach (string key in _entries.Keys.Where(k => !keep.Contains(k)).ToList())
        {
            Remove(key);
        }
    }

    public void Remove(string key)
    {
        if (!_entries.Remove(key, out Entry? entry))
        {
            return;
        }

        _keysById.Remove(entry.Id);
        if (entry.Added)
        {
            Notify(NIM_DELETE, entry);
        }

        if (entry.Icon != 0 && entry.OwnsIcon)
        {
            DestroyIcon(entry.Icon);
        }
    }

    /// <summary>Muestra el menú contextual y devuelve el id elegido (0 si se cancela).</summary>
    public int ShowMenu(IReadOnlyList<TrayMenuItem> items, int x, int y, bool lightTheme)
    {
        nint menu = CreatePopupMenu();
        if (menu == 0)
        {
            return 0;
        }

        try
        {
            ApplyMenuTheme(lightTheme);
            uint position = 0;
            foreach (TrayMenuItem item in items)
            {
                if (item.Text is null)
                {
                    AppendMenuW(menu, MF_SEPARATOR, 0, null);
                }
                else
                {
                    AppendMenuW(menu, MF_STRING | (item.IsChecked ? MF_CHECKED : 0), (nuint)item.Id, item.Text);
                    if (item.IsDefault)
                    {
                        SetMenuDefaultItem(menu, position, 1);
                    }
                }

                position++;
            }

            // Necesario para que el menú se cierre al hacer clic fuera (documentado por Microsoft).
            SetForegroundWindow(_hwnd);
            int command = TrackPopupMenuEx(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD | TPM_NONOTIFY | TPM_BOTTOMALIGN | TPM_WORKAREA, x, y, _hwnd, 0);
            PostMessageW(_hwnd, WM_NULL, 0, 0);
            return command;
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private static void ApplyMenuTheme(bool lightTheme)
    {
        try
        {
            SetPreferredAppMode(lightTheme ? 3 : 2); // 2 = forzar oscuro, 3 = forzar claro
            FlushMenuThemes();
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            // Versión de Windows sin esta función: menú con el tema por defecto.
        }
    }

    private uint AllocateId(string key)
    {
        // Id estable por dispositivo (Windows recuerda por id si el usuario quiere ver el icono siempre).
        uint hash = 2166136261;
        foreach (char c in key)
        {
            hash = (hash ^ c) * 16777619;
        }

        uint id = (hash % 0xFFF0) + 0x10;
        while (_keysById.ContainsKey(id))
        {
            id = id >= 0xFFFF ? 0x10 : id + 1;
        }

        return id;
    }

    private void Add(Entry entry)
    {
        if (Notify(NIM_ADD, entry))
        {
            entry.Added = true;
            Notify(NIM_SETVERSION, entry);
        }
        else
        {
            AppLog.Warn($"No se pudo añadir el icono de bandeja ({entry.Key})");
        }
    }

    private bool Notify(uint message, Entry entry)
    {
        var data = new NOTIFYICONDATAW
        {
            cbSize = (uint)sizeof(NOTIFYICONDATAW),
            hWnd = _hwnd,
            uID = entry.Id,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = entry.Icon,
            uVersion = message == NIM_SETVERSION ? NOTIFYICON_VERSION_4 : 0,
        };
        CopyToFixed(entry.Tooltip, data.szTip, 128);
        return Shell_NotifyIconW(message, &data);
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        try
        {
            if (s_instance is { } self && self.HandleMessage(msg, wParam, lParam))
            {
                return 0;
            }
        }
        catch (Exception ex)
        {
            // Una excepción aquí cerraría la app: se registra y se sigue.
            AppLog.Error("Error en la ventana de la bandeja", ex);
        }

        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private bool HandleMessage(uint msg, nint wParam, nint lParam)
    {
        if (msg == WM_TRAYICON)
        {
            uint evt = LoWord(lParam);
            uint id = HiWord(lParam);
            if (!_keysById.TryGetValue(id, out string? key))
            {
                return true;
            }

            int x = (short)LoWord(wParam);
            int y = (short)HiWord(wParam);
            switch (evt)
            {
                case NIN_SELECT:
                case NIN_KEYSELECT:
                    Activated?.Invoke(key, x, y, evt == NIN_KEYSELECT);
                    break;
                case WM_CONTEXTMENU:
                    ContextMenuRequested?.Invoke(key, x, y);
                    break;
            }

            return true;
        }

        if (msg == _taskbarCreatedMessage)
        {
            // El Explorador se reinició: hay que volver a añadir todos los iconos.
            foreach (Entry entry in _entries.Values)
            {
                entry.Added = false;
                Add(entry);
            }

            EnvironmentChanged?.Invoke();
            return true;
        }

        if (msg is WM_SETTINGCHANGE or WM_DISPLAYCHANGE or WM_DPICHANGED)
        {
            EnvironmentChanged?.Invoke();
        }

        return false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        foreach (string key in _entries.Keys.ToList())
        {
            Remove(key);
        }

        if (_hwnd != 0)
        {
            DestroyWindow(_hwnd);
            _hwnd = 0;
        }

        Marshal.FreeHGlobal(_classNamePtr);
        if (ReferenceEquals(s_instance, this))
        {
            s_instance = null;
        }

        _disposed = true;
    }

    private sealed class Entry(string key, uint id)
    {
        public string Key { get; } = key;

        public uint Id { get; } = id;

        public nint Icon { get; set; }

        public string Tooltip { get; set; } = "";

        public bool Added { get; set; }

        /// <summary>El icono de la app (cargado de archivo) también se libera al quitarlo.</summary>
        public bool OwnsIcon => true;
    }
}
