# BatteryTray

**La batería de tus dispositivos Bluetooth, en la barra de tareas de Windows.**
Gratis, de código abierto, sin anuncios y sin recoger ningún dato.

*[English below](#english)*

<p>
  <img src="docs/images/flyout.png" alt="Panel de BatteryTray con cuatro dispositivos y su nivel de batería" width="320">
  &nbsp;
  <img src="docs/images/tray-icons.png" alt="Ejemplos de iconos de bandeja con porcentajes y barras de colores" width="460">
</p>

> **Estado: versión preliminar (0.x).** Funciona, pero aún se está probando con dispositivos reales.
> Si algo no va bien, [abre un aviso](../../issues/new/choose) con el diagnóstico (ver más abajo).

## Qué hace

- **Un icono por dispositivo** junto al reloj, con su porcentaje o el icono del dispositivo, y una barrita de color (amarilla si baja, roja si está casi vacía, verde si carga). También puede mostrar **un único icono** con el nivel más bajo.
- **Panel al estilo de Windows 11** al hacer clic: todos tus dispositivos, su batería y si están conectados o cargando.
- **Aviso de batería baja** (el nivel lo eliges tú) y, si quieres, aviso al conectar o desconectar.
- **Recuerda el último nivel** de los dispositivos desconectados.
- Se adapta al **tema claro u oscuro**, al color de acento y al escalado de pantalla; funciona con **lector de pantalla**.
- **Español e inglés.**
- **Modo demostración** para probarla sin tener dispositivos.

## Dispositivos

| Funciona | Cómo |
|---|---|
| Auriculares y altavoces Bluetooth | Si Windows muestra su batería en *Configuración > Bluetooth y dispositivos* |
| Ratones, teclados y mandos Bluetooth LE | Por el mismo dato de Windows o por el servicio estándar de batería (GATT) |
| La batería del propio portátil | Desactivada por defecto en la bandeja (Windows ya la muestra) |

**Todavía no:** receptores USB de 2,4 GHz (Logitech Unifying/Bolt, Razer, SteelSeries…). Cada marca usa su propio protocolo; están en la [hoja de ruta](PLAN.md).

Algunos dispositivos no informan de su batería (muchos auriculares económicos) o lo hacen a saltos de 10 % o en tres niveles (0/50/100 %).

## Instalar

- **Microsoft Store:** próximamente.
- **Desde GitHub:**
  1. Descarga `BatteryTray-…-win-x64.zip` (o `win-arm64` para procesadores ARM) de [Releases](../../releases).
  2. Descomprímelo en una carpeta fija, por ejemplo `%LOCALAPPDATA%\Programs\BatteryTray`.
  3. Abre `BatteryTray.exe`. Como el ejecutable aún no está firmado, Windows puede mostrar un aviso de SmartScreen: *Más información → Ejecutar de todas formas*.

Requisitos: Windows 10 (2004) o Windows 11, de 64 bits (x64 o ARM64). No hace falta instalar nada más.

## Primeros pasos

- **Windows 11 esconde los iconos nuevos** tras la flecha `^`. Para verlos siempre: *Configuración > Personalización > Barra de tareas > Otros iconos de la bandeja del sistema* (también hay un botón en los ajustes de la app).
- **Clic** en el icono: abre el panel. **Clic derecho:** menú (actualizar, ajustes, iniciar con Windows, salir).
- `BatteryTray.exe --settings` abre directamente los ajustes.

## Si algo no funciona

En *Ajustes > Avanzado > Copiar diagnóstico* se copia un informe de lo que ve la app (las direcciones Bluetooth van ocultas). [Abre un aviso](../../issues/new/choose), pégalo y cuenta qué dispositivo usas.
Los registros están en `%LOCALAPPDATA%\BatteryTray\logs`.

## Privacidad

BatteryTray **no usa Internet** y no recoge, envía ni comparte ningún dato. Solo guarda tus ajustes y el último nivel de cada dispositivo en tu propio equipo. Detalles en la [política de privacidad](docs/PRIVACY.md).

## Compilar

Necesitas el [SDK de .NET 10](https://dotnet.microsoft.com/download) en Windows (opcional: Visual Studio 2026 con «Desarrollo de aplicaciones WinUI»).

```powershell
# Pruebas
dotnet test --project tests/BatteryTray.Core.Tests/BatteryTray.Core.Tests.csproj
dotnet test --project tests/BatteryTray.Windows.Tests/BatteryTray.Windows.Tests.csproj -p:Platform=x64

# App portable (carpeta lista para comprimir)
dotnet publish src/BatteryTray.App/BatteryTray.App.csproj -c Release -r win-x64 -p:Platform=x64 -o artifacts/BatteryTray-win-x64

# Paquete MSIX para la Microsoft Store
dotnet publish src/BatteryTray.App/BatteryTray.App.csproj -c Release -r win-x64 -p:Platform=x64 -p:Packaged=true
```

| Carpeta | Contenido |
|---|---|
| `src/BatteryTray.Core` | Modelos, ajustes, unión de lecturas, avisos y modo demostración (sin dependencias de Windows) |
| `src/BatteryTray.Windows` | Lectura de batería (Bluetooth, equipo) y dibujo de los iconos |
| `src/BatteryTray.App` | La app WinUI 3: bandeja, panel, ajustes, notificaciones |
| `tests/` | Pruebas automáticas |
| `tools/` | Generador del icono y vista previa de los iconos de bandeja |

¿Quieres añadir una marca, un idioma o corregir algo? Lee [CONTRIBUTING.md](CONTRIBUTING.md).

## Licencia

[GPL-3.0-or-later](LICENSE). Puedes usarla, estudiarla, modificarla y compartirla; las versiones modificadas que distribuyas deben seguir siendo libres.

Gracias a [BlueGauge](https://github.com/iKineticate/BlueGauge) por documentar cómo expone Windows la batería de los dispositivos Bluetooth.

---

## English

**Battery levels of your Bluetooth devices, right in the Windows taskbar.** Free, open source, no ads, no data collection.

- One tray icon per device (percentage or device icon with a colored level bar), or a single icon with the lowest level.
- Windows 11–style flyout with all your devices, plus a settings window that looks like Windows Settings.
- Low-battery alerts (configurable level), optional connect/disconnect notifications, last known level for disconnected devices.
- Light/dark theme, accent color, high DPI and screen reader support. English and Spanish.
- Demo mode to try it without any devices.

**Supported:** Bluetooth headphones/speakers whose battery Windows already shows in Settings, Bluetooth LE mice/keyboards/controllers with the standard battery service, and the laptop's own battery. **Not yet:** 2.4 GHz USB receivers (Logitech, Razer…).

**Install:** download the `win-x64` (or `win-arm64`) zip from [Releases](../../releases), extract it to a permanent folder and run `BatteryTray.exe`. The executable is not signed yet, so SmartScreen may warn you (*More info → Run anyway*). Microsoft Store version coming soon. Requires Windows 10 2004+ or Windows 11, 64-bit.

**Problems?** *Settings > Advanced > Copy diagnostics* (Bluetooth addresses are masked) and [open an issue](../../issues/new/choose).

**Privacy:** no internet access, no telemetry. See [docs/PRIVACY.md](docs/PRIVACY.md).

**License:** [GPL-3.0-or-later](LICENSE).
