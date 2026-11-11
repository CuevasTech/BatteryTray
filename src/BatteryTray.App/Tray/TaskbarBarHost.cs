using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using BatteryTray.Core;
using BatteryTray.Services;
using BatteryTray.Windows.Rendering;
using Microsoft.UI.Dispatching;
using static BatteryTray.Interop.NativeMethods;

namespace BatteryTray.Tray;

/// <summary>Lo que tiene que mostrar la barra.</summary>
internal sealed record TaskbarBarState(IReadOnlyList<TaskbarItemSpec> Items, bool LightTaskbar);

/// <summary>
/// La barra de baterías dentro de la barra de tareas, como las antiguas "barras de
/// herramientas" que Windows 11 ya no ofrece. Es una ventana hija de la barra de tareas
/// (Shell_TrayWnd) colocada justo a la izquierda del área de notificación, así que se mueve,
/// se oculta y aparece con ella (ocultación automática, juegos a pantalla completa...).
///
/// Va en un hilo propio: al ser hija de una ventana del Explorador, Windows une su cola de
/// entrada con la de la barra de tareas. Un hilo que solo dibuja responde siempre al
/// momento, así que nunca puede dejar la barra de tareas "colgada" aunque la app esté ocupada.
///
/// Si la ventana no se puede poner (barra vertical, sin sitio, Explorador reiniciándose...),
/// <see cref="IsAvailable"/> pasa a false y la app vuelve a los iconos junto al reloj.
/// </summary>
internal sealed unsafe class TaskbarBarHost : IDisposable
{
    private const string BarClassName = "BatteryTray.TaskbarBar";
    private const string ControlClassName = "BatteryTray.TaskbarBarControl";
    private const string TaskbarClassName = "Shell_TrayWnd";
    private const uint WM_BAR_UPDATE = WM_APP + 20;
    private const uint WM_BAR_REATTACH = WM_APP + 21;
    private const uint WM_BAR_QUIT = WM_APP + 22;
    private const uint WS_EX_NOPARENTNOTIFY = 0x4;
    private const nuint CheckTimerId = 1;
    private const uint CheckIntervalMs = 1000;
    private const int MaxTooltipLength = 512;

    private static TaskbarBarHost? s_instance;

    private readonly DispatcherQueue _dispatcher;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _firstAttach = new(false);
    private readonly Lock _gate = new();
    private TaskbarBarState? _pending;
    private volatile bool _available;
    private volatile bool _stopping;
    private nint _control;
    private bool _disposed;

    // A partir de aquí, solo se usan en el hilo de la barra.
    private nint _bar;
    private nint _tooltip;
    private nint _taskbar;
    private nint _trayWnd;
    private nint _hook;
    private int _toolCount;
    private bool _layeredFailed;
    private TaskbarBarState _state = new([], false);
    private TaskbarBarLayout? _layout;
    private Geometry _geometry;
    private int _hover = -1;
    private bool _pressed;
    private bool _trackingMouse;
    private Bitmap? _appIcon;

    public TaskbarBarHost(DispatcherQueue dispatcher)
    {
        if (s_instance is not null)
        {
            throw new InvalidOperationException("Ya hay una barra en la barra de tareas.");
        }

        s_instance = this;
        _dispatcher = dispatcher;
        _thread = new Thread(Run) { IsBackground = true, Name = "BatteryTray.TaskbarBar" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        // Se espera al primer intento (tarda unos milisegundos) para saber ya si la barra está
        // puesta; así los iconos de la bandeja no aparecen y desaparecen al arrancar.
        _firstAttach.Wait(TimeSpan.FromSeconds(1));
    }

    /// <summary>Clic izquierdo en un elemento: clave y punto de anclaje en píxeles de pantalla.</summary>
    public event Action<string, int, int>? Clicked;

    /// <summary>Clic derecho: clave y posición del ratón.</summary>
    public event Action<string, int, int>? ContextMenuRequested;

    /// <summary>La barra se ha puesto o quitado de la barra de tareas.</summary>
    public event Action? AvailabilityChanged;

    /// <summary>True si la barra está visible dentro de la barra de tareas.</summary>
    public bool IsAvailable => _available;

    /// <summary>Cambia lo que se muestra (desde cualquier hilo).</summary>
    public void Update(TaskbarBarState state)
    {
        lock (_gate)
        {
            _pending = state;
        }

        Post(WM_BAR_UPDATE);
    }

    /// <summary>Vuelve a buscar la barra de tareas (p. ej., tras reiniciarse el Explorador).</summary>
    public void Reattach() => Post(WM_BAR_REATTACH);

    private void Post(uint message)
    {
        nint control = Volatile.Read(ref _control);
        if (control != 0)
        {
            PostMessageW(control, message, 0, 0);
        }
    }

    // ------------------------------------------------------------ hilo de la barra

    private void Run()
    {
        try
        {
            nint instance = GetModuleHandleW(null);
            RegisterClass(BarClassName, instance, LoadCursorW(0, IDC_ARROW));
            RegisterClass(ControlClassName, instance, 0);
            var icc = new INITCOMMONCONTROLSEX { dwSize = (uint)sizeof(INITCOMMONCONTROLSEX), dwICC = ICC_BAR_CLASSES };
            InitCommonControlsEx(&icc);

            // Ventana "solo mensajes" para recibir órdenes y el temporizador aunque la barra no exista.
            nint control = CreateWindowExW(0, ControlClassName, "", 0, 0, 0, 0, 0, HWND_MESSAGE, 0, instance, 0);
            if (control == 0)
            {
                AppLog.Warn($"No se pudo crear la ventana de control de la barra (error {Marshal.GetLastPInvokeError()})");
                return;
            }

            Volatile.Write(ref _control, control);
            SetTimer(control, CheckTimerId, CheckIntervalMs, 0);
            TakePending();
            Attach();
            SignalFirstAttach();

            MSG msg;
            while (GetMessageW(&msg, 0, 0, 0) > 0)
            {
                TranslateMessage(&msg);
                DispatchMessageW(&msg);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Error en la barra de la barra de tareas", ex);
        }
        finally
        {
            SignalFirstAttach();
            DestroyBar();
            nint control = Volatile.Read(ref _control);
            if (control != 0)
            {
                KillTimer(control, CheckTimerId);
                DestroyWindow(control);
                Volatile.Write(ref _control, 0);
            }

            _appIcon?.Dispose();
            _appIcon = null;
            SetAvailable(false);
        }
    }

    private void SignalFirstAttach()
    {
        try
        {
            _firstAttach.Set();
        }
        catch (ObjectDisposedException)
        {
            // Ya se cerró la barra mientras arrancaba.
        }
    }

    private static void RegisterClass(string name, nint instance, nint cursor)
    {
        fixed (char* className = name)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = &WndProc,
                hInstance = instance,
                hCursor = cursor,
                lpszClassName = className,
            };
            RegisterClassExW(&wc); // si ya existía (segunda vez que se crea la barra), se reutiliza
        }
    }

    private void TakePending()
    {
        lock (_gate)
        {
            if (_pending is not null)
            {
                _state = _pending;
                _pending = null;
            }
        }
    }

    /// <summary>Busca la barra de tareas y pone (o vuelve a poner) la ventana dentro.</summary>
    private void Attach()
    {
        if (_layeredFailed || _stopping)
        {
            return;
        }

        nint taskbar = FindWindowW(TaskbarClassName, null);
        if (taskbar == 0)
        {
            DestroyBar();
            _taskbar = 0;
            SetAvailable(false);
            return;
        }

        if (taskbar == _taskbar && _bar != 0 && IsWindow(_bar))
        {
            Relayout();
            return;
        }

        DestroyBar();
        _taskbar = taskbar;
        nint instance = GetModuleHandleW(null);
        _bar = CreateWindowExW(
            WS_EX_LAYERED | WS_EX_NOPARENTNOTIFY,
            BarClassName,
            "BatteryTray",
            WS_CHILD | WS_CLIPSIBLINGS,
            0, 0, 1, 1,
            taskbar, 0, instance, 0);
        if (_bar == 0)
        {
            AppLog.Warn($"No se pudo poner la barra en la barra de tareas (error {Marshal.GetLastPInvokeError()})");
            SetAvailable(false);
            return;
        }

        InstallHook(taskbar);
        CreateTooltip(instance);
        Relayout();
    }

    private void DestroyBar()
    {
        if (_hook != 0)
        {
            UnhookWinEvent(_hook);
            _hook = 0;
        }

        if (_tooltip != 0)
        {
            if (IsWindow(_tooltip))
            {
                DestroyWindow(_tooltip);
            }

            _tooltip = 0;
        }

        if (_bar != 0)
        {
            if (IsWindow(_bar))
            {
                DestroyWindow(_bar);
            }

            _bar = 0;
        }

        _toolCount = 0;
        _hover = -1;
        _pressed = false;
        _trackingMouse = false;
    }

    /// <summary>Cada segundo: ¿sigue la barra de tareas ahí y en el mismo sitio?</summary>
    private void Check()
    {
        if (_layeredFailed || _stopping)
        {
            return;
        }

        nint taskbar = FindWindowW(TaskbarClassName, null);
        if (taskbar != _taskbar || _bar == 0 || !IsWindow(_bar))
        {
            Attach();
        }
        else if (ReadGeometry() != _geometry)
        {
            Relayout();
        }
    }

    private Geometry ReadGeometry()
    {
        RECT taskbar = default;
        RECT tray = default;
        GetWindowRect(_taskbar, &taskbar);
        _trayWnd = FindWindowExW(_taskbar, 0, "TrayNotifyWnd", null);
        if (_trayWnd != 0)
        {
            GetWindowRect(_trayWnd, &tray);
        }

        return new Geometry(taskbar.Left, taskbar.Top, taskbar.Right, taskbar.Bottom, tray.Left, tray.Right, GetDpiForWindow(_taskbar));
    }

    /// <summary>Calcula el tamaño, coloca la ventana a la izquierda del área de notificación y la dibuja.</summary>
    private void Relayout()
    {
        if (_bar == 0 || !IsWindow(_bar))
        {
            _bar = 0;
            SetAvailable(false);
            return;
        }

        Geometry geo = ReadGeometry();
        _geometry = geo;
        int taskbarWidth = geo.Right - geo.Left;
        int taskbarHeight = geo.Bottom - geo.Top;

        // Barra de tareas vertical (Windows 10): no hay sitio para una barra horizontal.
        if (taskbarWidth <= 0 || taskbarHeight <= 0 || taskbarWidth < taskbarHeight)
        {
            HideBar();
            return;
        }

        RECT client = default;
        GetClientRect(_taskbar, &client);
        int height = client.Height > 0 ? client.Height : taskbarHeight;
        double scale = (geo.Dpi > 0 ? geo.Dpi : 96) / 96.0;
        _layout = TaskbarBarRenderer.Measure(_state.Items, height, scale);
        int width = _layout.Size.Width;

        // Pegada al área de notificación (iconos + reloj), con un pequeño margen.
        int right = geo.TrayRight > geo.TrayLeft ? geo.TrayLeft : geo.Right;
        var position = new POINT { X = right - (int)Math.Round(4 * scale) - width, Y = geo.Top };
        ScreenToClient(_taskbar, &position);

        // Nunca encima del botón de Inicio (barra de tareas muy llena o pantalla estrecha).
        int minX = 0;
        nint start = FindWindowExW(_taskbar, 0, "Start", null);
        RECT startRect = default;
        if (start != 0 && GetWindowRect(start, &startRect) && startRect.Width > 0)
        {
            var startRight = new POINT { X = startRect.Right, Y = startRect.Top };
            ScreenToClient(_taskbar, &startRight);
            minX = Math.Max(0, startRight.X);
        }

        if (position.X < minX)
        {
            HideBar();
            return;
        }

        if (_hover >= _state.Items.Count)
        {
            _hover = -1;
        }

        // Encima de las demás ventanas hijas de la barra (el contenido de Windows 11).
        SetWindowPos(_bar, HWND_TOP, position.X, 0, width, height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        if (!Render())
        {
            return;
        }

        UpdateTooltips();
        SetAvailable(true);
    }

    private void HideBar()
    {
        if (_bar != 0)
        {
            ShowWindow(_bar, SW_HIDE);
        }

        SetAvailable(false);
    }

    /// <summary>
    /// Dibuja en un mapa de bits con transparencia por píxel y se lo da a Windows
    /// (ventana "layered"), así el fondo de la barra de tareas se ve a través.
    /// </summary>
    private bool Render()
    {
        if (_bar == 0 || _layout is null)
        {
            return false;
        }

        int w = _layout.Size.Width;
        int h = _layout.Size.Height;
        var info = new BITMAPINFOHEADER
        {
            biSize = (uint)sizeof(BITMAPINFOHEADER),
            biWidth = w,
            biHeight = -h, // de arriba abajo
            biPlanes = 1,
            biBitCount = 32,
        };

        nint screen = GetDC(0);
        nint memory = CreateCompatibleDC(screen);
        void* bits = null;
        nint dib = CreateDIBSection(screen, &info, DIB_RGB_COLORS, &bits, 0, 0);
        nint previous = dib != 0 && memory != 0 ? SelectObject(memory, dib) : 0;
        try
        {
            if (dib == 0 || memory == 0 || bits == null)
            {
                return false;
            }

            using (var bmp = new Bitmap(w, h, w * 4, PixelFormat.Format32bppPArgb, (nint)bits))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                TaskbarBarRenderer.Draw(g, _layout, _state.Items, _state.LightTaskbar, _hover, _pressed, AppIcon(_layout.Scale));
            }

            var size = new SIZE { Width = w, Height = h };
            var source = default(POINT);
            var blend = new BLENDFUNCTION { BlendOp = AC_SRC_OVER, SourceConstantAlpha = 255, AlphaFormat = AC_SRC_ALPHA };
            if (UpdateLayeredWindow(_bar, screen, null, &size, memory, &source, 0, &blend, ULW_ALPHA))
            {
                return true;
            }

            // Si Windows no lo permite, no se insiste: se vuelve a los iconos junto al reloj.
            AppLog.Warn($"Windows no deja dibujar la barra en la barra de tareas (error {Marshal.GetLastPInvokeError()}); se usan los iconos junto al reloj");
            _layeredFailed = true;
            DestroyBar();
            SetAvailable(false);
            return false;
        }
        finally
        {
            if (previous != 0)
            {
                SelectObject(memory, previous);
            }

            if (dib != 0)
            {
                DeleteObject(dib);
            }

            if (memory != 0)
            {
                DeleteDC(memory);
            }

            ReleaseDC(0, screen);
        }
    }

    private Bitmap? AppIcon(double scale)
    {
        int size = (int)Math.Round(16 * scale);
        if (_appIcon is not null && _appIcon.Width == size)
        {
            return _appIcon;
        }

        _appIcon?.Dispose();
        _appIcon = null;
        try
        {
            using var icon = new Icon(AppInfo.IconPath, size, size);
            _appIcon = icon.ToBitmap();
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            AppLog.Warn($"No se pudo cargar el icono de la app: {ex.Message}");
        }

        return _appIcon;
    }

    // ------------------------------------------------------------ información al pasar el ratón

    private void CreateTooltip(nint instance)
    {
        _tooltip = CreateWindowExW(
            WS_EX_TOPMOST, "tooltips_class32", "",
            WS_POPUP | TTS_ALWAYSTIP | TTS_NOPREFIX,
            CW_USEDEFAULT, CW_USEDEFAULT, CW_USEDEFAULT, CW_USEDEFAULT,
            _bar, 0, instance, 0);
        _toolCount = 0;
        if (_tooltip != 0)
        {
            SendMessageW(_tooltip, TTM_SETMAXTIPWIDTH, 0, 480); // permite varias líneas
        }
    }

    private void UpdateTooltips()
    {
        if (_tooltip == 0 || _layout is null)
        {
            return;
        }

        // Mismo tema que la barra de tareas.
        SetWindowTheme(_tooltip, _state.LightTaskbar ? "Explorer" : "DarkMode_Explorer", null);

        for (int i = 0; i < _toolCount; i++)
        {
            var old = new TOOLINFOW { cbSize = (uint)sizeof(TOOLINFOW), hwnd = _bar, uId = (nuint)i };
            SendMessageW(_tooltip, TTM_DELTOOLW, 0, (nint)(&old));
        }

        _toolCount = 0;
        int count = Math.Min(_state.Items.Count, _layout.Items.Count);
        for (int i = 0; i < count; i++)
        {
            Rectangle r = _layout.Items[i];
            string text = DeviceText.Truncate(_state.Items[i].Tooltip, MaxTooltipLength);
            fixed (char* chars = text)
            {
                var tool = new TOOLINFOW
                {
                    cbSize = (uint)sizeof(TOOLINFOW),
                    uFlags = TTF_SUBCLASS,
                    hwnd = _bar,
                    uId = (nuint)i,
                    rect = new RECT { Left = r.Left, Top = r.Top, Right = r.Right, Bottom = r.Bottom },
                    lpszText = chars, // el control copia el texto
                };
                SendMessageW(_tooltip, TTM_ADDTOOLW, 0, (nint)(&tool));
            }

            _toolCount = i + 1;
        }
    }

    private void SetTooltipActive(bool active)
    {
        if (_tooltip != 0)
        {
            if (!active)
            {
                SendMessageW(_tooltip, TTM_POP, 0, 0);
            }

            SendMessageW(_tooltip, TTM_ACTIVATE, active ? 1 : 0, 0);
        }
    }

    /// <summary>
    /// Como los textos de la barra de tareas de Windows: centrado sobre el elemento y por
    /// encima de la barra (o por debajo si la barra está arriba), sin taparla.
    /// </summary>
    private void PlaceTooltip(int index)
    {
        if (_layout is null || index < 0 || index >= _layout.Items.Count)
        {
            return;
        }

        RECT tip = default;
        GetWindowRect(_tooltip, &tip);
        Rectangle item = _layout.Items[index];
        var anchor = new POINT { X = item.X + item.Width / 2, Y = 0 };
        ClientToScreen(_bar, &anchor);

        var monitor = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        nint handle = MonitorFromWindow(_taskbar, MONITOR_DEFAULTTOPRIMARY);
        bool hasMonitor = handle != 0 && GetMonitorInfoW(handle, &monitor);
        int gap = (int)Math.Round(8 * _layout.Scale);
        bool taskbarAtTop = hasMonitor && _geometry.Top <= monitor.rcMonitor.Top
            && _geometry.Bottom < monitor.rcMonitor.Top + monitor.rcMonitor.Height / 2;

        int x = anchor.X - tip.Width / 2;
        int y = taskbarAtTop ? _geometry.Bottom + gap : _geometry.Top - tip.Height - gap;
        if (hasMonitor)
        {
            x = Math.Clamp(x, monitor.rcMonitor.Left, Math.Max(monitor.rcMonitor.Left, monitor.rcMonitor.Right - tip.Width));
        }

        SetWindowPos(_tooltip, 0, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    // ------------------------------------------------------------ mensajes

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        try
        {
            if (s_instance is { } self)
            {
                nint? result = hwnd == Volatile.Read(ref self._control)
                    ? self.HandleControl(msg)
                    : self.HandleBar(hwnd, msg, lParam);
                if (result is nint value)
                {
                    return value;
                }
            }
        }
        catch (Exception ex)
        {
            // Una excepción aquí cerraría la app: se registra y se sigue.
            AppLog.Error("Error en la barra de la barra de tareas", ex);
        }

        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private nint? HandleControl(uint msg)
    {
        switch (msg)
        {
            case WM_TIMER:
                Check();
                return 0;
            case WM_BAR_UPDATE:
                TakePending();
                if (_bar == 0)
                {
                    Attach();
                }
                else
                {
                    Relayout();
                }

                return 0;
            case WM_BAR_REATTACH:
                Attach();
                return 0;
            case WM_BAR_QUIT:
                _stopping = true;
                DestroyBar();
                PostQuitMessage(0);
                return 0;
            default:
                return null;
        }
    }

    private nint? HandleBar(nint hwnd, uint msg, nint lParam)
    {
        if (hwnd != _bar || _bar == 0)
        {
            return null;
        }

        switch (msg)
        {
            case WM_MOUSEACTIVATE:
                // Un clic no debe activar la barra de tareas ni quitar el foco a nada.
                return MA_NOACTIVATE;

            case WM_MOUSEMOVE:
            {
                if (!_trackingMouse)
                {
                    var track = new TRACKMOUSEEVENT { cbSize = (uint)sizeof(TRACKMOUSEEVENT), dwFlags = TME_LEAVE, hwndTrack = _bar };
                    _trackingMouse = TrackMouseEvent(&track);
                }

                int hit = HitTest(lParam);
                if (hit != _hover)
                {
                    _hover = hit;
                    Render();
                }

                return 0;
            }

            case WM_MOUSELEAVE:
                _trackingMouse = false;
                _pressed = false;
                SetTooltipActive(true);
                if (_hover != -1)
                {
                    _hover = -1;
                    Render();
                }

                return 0;

            case WM_LBUTTONDOWN:
                _pressed = true;
                SetTooltipActive(false); // no debe salir encima del panel recién abierto
                Render();
                return 0;

            case WM_LBUTTONUP:
            {
                bool wasPressed = _pressed;
                _pressed = false;
                int hit = HitTest(lParam);
                Render();
                if (wasPressed && hit >= 0 && hit < _state.Items.Count && _layout is not null)
                {
                    Rectangle r = _layout.Items[hit];
                    var anchor = new POINT { X = r.X + r.Width / 2, Y = 0 };
                    ClientToScreen(_bar, &anchor);
                    string key = _state.Items[hit].Key;
                    (int x, int y) = (anchor.X, anchor.Y);
                    _dispatcher.TryEnqueue(() => Clicked?.Invoke(key, x, y));
                }

                return 0;
            }

            case WM_RBUTTONUP:
            {
                // Se responde aquí (sin pasar a Windows) para que no salga además el menú de la barra de tareas.
                int hit = HitTest(lParam);
                (int cx, int cy) = PointFromLParam(lParam);
                var point = new POINT { X = cx, Y = cy };
                ClientToScreen(_bar, &point);
                SetTooltipActive(false); // no debe salir encima del menú
                string key = hit >= 0 && hit < _state.Items.Count ? _state.Items[hit].Key : TrayController.MainKey;
                (int x, int y) = (point.X, point.Y);
                _dispatcher.TryEnqueue(() => ContextMenuRequested?.Invoke(key, x, y));
                return 0;
            }

            case WM_CONTEXTMENU:
                return 0; // el menú propio ya se mostró en WM_RBUTTONUP

            case WM_NOTIFY:
            {
                // El texto emergente vive en este mismo hilo. Si el aviso viene de otro hilo u
                // otro proceso, lParam no apunta a nuestra memoria y leerlo podría cerrar la app.
                if (InSendMessage() || lParam == 0)
                {
                    return 0;
                }

                var header = (NMHDR*)lParam;
                if (header->hwndFrom == _tooltip && _tooltip != 0 && header->code == TTN_SHOW)
                {
                    PlaceTooltip((int)header->idFrom);
                    return 1; // ya está colocado
                }

                return null;
            }

            case WM_DESTROY:
                // También llega si se cierra el Explorador (se destruye la barra de tareas entera).
                _bar = 0;
                _tooltip = 0;
                _toolCount = 0;
                _hover = -1;
                SetAvailable(false);
                return null;

            default:
                return null;
        }
    }

    private int HitTest(nint lParam)
    {
        (int x, int y) = PointFromLParam(lParam);
        return _layout?.HitTest(new Point(x, y)) ?? -1;
    }

    /// <summary>
    /// Avisa al momento cuando la barra de tareas o el área de notificación cambian de sitio
    /// o tamaño (aparece un icono nuevo, cambia el reloj...). El temporizador es solo el respaldo.
    /// </summary>
    private void InstallHook(nint taskbar)
    {
        if (_hook != 0)
        {
            UnhookWinEvent(_hook);
            _hook = 0;
        }

        uint processId = 0;
        GetWindowThreadProcessId(taskbar, &processId);
        if (processId != 0)
        {
            _hook = SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE, 0, &OnWinEvent, processId, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        }
    }

    [UnmanagedCallersOnly]
    private static void OnWinEvent(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            // Se filtra lo antes posible: el Explorador avisa de muchos movimientos (sus ventanas de carpetas, por ejemplo).
            if (idObject == OBJID_WINDOW && s_instance is { } self && self._bar != 0
                && (hwnd == self._taskbar || (hwnd == self._trayWnd && hwnd != 0))
                && self.ReadGeometry() != self._geometry)
            {
                self.Relayout();
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Error al recolocar la barra", ex);
        }
    }

    private void SetAvailable(bool value)
    {
        if (_available == value)
        {
            return;
        }

        _available = value;
        AppLog.Info(value ? "Barra puesta en la barra de tareas" : "Barra quitada de la barra de tareas (se usan los iconos junto al reloj)");
        if (!_stopping)
        {
            _dispatcher.TryEnqueue(() => AvailabilityChanged?.Invoke());
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stopping = true;
        Post(WM_BAR_QUIT);
        if (!_thread.Join(TimeSpan.FromSeconds(2)))
        {
            AppLog.Warn("La barra de la barra de tareas no se cerró a tiempo");
        }

        _firstAttach.Dispose();
        if (ReferenceEquals(s_instance, this))
        {
            s_instance = null;
        }
    }

    private readonly record struct Geometry(int Left, int Top, int Right, int Bottom, int TrayLeft, int TrayRight, uint Dpi);
}
