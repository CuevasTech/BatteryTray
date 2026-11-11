# Ficha de BatteryTray en la Microsoft Store

Todo lo que pide Partner Center, listo para copiar y pegar.

## Pasos para publicar (Partner Center)

1. **Reservar el nombre:** *Aplicaciones y juegos → Nuevo producto → Aplicación MSIX o PWA* → `BatteryTray`.
2. **Identidad del paquete:** en *Administración de productos → Identidad del producto* copia `Package/Identity/Name`, `Package/Identity/Publisher` y `Package/Properties/PublisherDisplayName` en [`src/BatteryTray.App/Package.appxmanifest`](../../src/BatteryTray.App/Package.appxmanifest).
3. **Generar el paquete:** `pwsh tools/Pack-Store.ps1 -Version 0.1.0` → `artifacts/store/BatteryTray_0.1.0.0.msixbundle`
   (o etiqueta la versión en GitHub y descarga el paquete del flujo *Store package*).
4. **Crear el envío** con los datos de abajo:
   - *Precios y disponibilidad:* gratis, todos los mercados. Visibilidad pública (o «Público privado» para probarla antes con tu cuenta).
   - *Propiedades:* categoría y privacidad.
   - *Clasificación por edades:* cuestionario IARC.
   - *Paquetes:* sube el `.msixbundle`.
   - *Fichas de Store:* español (España) e inglés (Estados Unidos).
   - *Opciones de envío:* notas para certificación y justificación de `runFullTrust`.
5. **Enviar a certificación.** Suele tardar de unas horas a 3 días laborables.
6. Cuando esté publicada, cambia en el README el enlace provisional por el de la ficha (`https://apps.microsoft.com/detail/<Id. de Store>`).

## Propiedades

| Campo | Valor |
|---|---|
| Categoría | Utilidades y herramientas |
| Subcategoría | (ninguna) |
| URL de la política de privacidad | https://github.com/CuevasTech/BatteryTray/blob/main/docs/PRIVACY.md |
| Sitio web | https://github.com/CuevasTech/BatteryTray |
| Contacto de soporte | https://github.com/CuevasTech/BatteryTray/issues |
| Requisitos de hardware recomendados | Bluetooth |
| ¿Accede, recoge o transmite información personal? | No |

## Clasificación por edades (IARC)

Respuestas al cuestionario: tipo **Utilidad / herramienta**. Sin violencia, sin contenido sexual, sin lenguaje malsonante, sin drogas ni apuestas, **sin interacción entre usuarios, sin compartir ubicación, sin compras dentro de la app y sin acceso a Internet**. Resultado esperado: apta para todos los públicos (3+ / PEGI 3).

## Opciones de envío

**Notas para la certificación** (en inglés, las leen los revisores):

> BatteryTray is a notification-area (system tray) utility. When launched manually it opens its flyout next to the clock; afterwards it lives in the notification area (it may be hidden behind the "^" overflow arrow, as Windows does with new icons). It shows the battery level of paired Bluetooth devices. If the test machine has no Bluetooth devices, enable the demo mode to see simulated devices: right-click the tray icon → Settings… → Advanced → Demo mode. Optionally (Settings → Taskbar → "Where they appear" → "Bar on the taskbar") it shows the levels as a small bar inside the taskbar, to the left of the notification area; if it doesn't fit, it falls back to the tray icons automatically. No account, sign-in or internet connection is required.

**Justificación de la funcionalidad restringida `runFullTrust`:**

> BatteryTray is a WinUI 3 desktop app (Windows App SDK). It needs full trust to show icons in the notification area (Shell_NotifyIcon), to place its optional bar inside the taskbar as a child window of the taskbar, and to read the battery level that Windows stores for Bluetooth devices through the Configuration Manager API (cfgmgr32, read-only). The app does not access the internet and does not collect personal data.

## Ficha en español (es-ES)

**Nombre:** BatteryTray

**Descripción:**

```
La batería de tus dispositivos Bluetooth, siempre a la vista en la barra de tareas.

BatteryTray pone en la barra de tareas la batería de tus auriculares, ratón, teclado o mando, con el mismo aspecto que el icono de batería de Windows 11. Como en Windows, se pone roja cuando queda poca batería (y el icono del dispositivo también, para que lo veas de un vistazo) y verde con el rayo mientras se carga.

Tú eliges dónde: como iconos junto al reloj o como una barra dentro de la propia barra de tareas, con la batería y el porcentaje de cada dispositivo. Pasa el ratón por encima para ver su nombre.

Con un clic se abre un panel al estilo Windows 11 con todos tus dispositivos: su nivel, si se están cargando y el último nivel conocido de los que están desconectados. Y te avisa cuando un dispositivo se está quedando sin batería.

Qué dispositivos funcionan
• Auriculares y altavoces Bluetooth cuya batería ya muestra Windows en Configuración > Bluetooth y dispositivos.
• Ratones, teclados y mandos Bluetooth LE con el servicio estándar de batería.
• La batería del propio portátil.
Algunos dispositivos no informan de su batería a Windows; en ese caso no pueden aparecer. Los receptores USB de 2,4 GHz (Logitech, Razer…) aún no son compatibles.

Gratis, de código abierto (GPL-3.0), sin anuncios y sin recoger ningún dato: la app no usa Internet.

Consejo: Windows 11 esconde los iconos nuevos tras la flecha ^. Para verlos siempre, elige la barra en la barra de tareas o actívalos en Configuración > Personalización > Barra de tareas > Otros iconos de la bandeja del sistema (en los ajustes de la app hay un botón que te lleva).
```

**Características del producto** (una por línea):

```
Batería de cada dispositivo Bluetooth en la barra de tareas
Como iconos junto al reloj o como una barra dentro de la barra de tareas
Iconos iguales que la batería de Windows 11
En rojo con batería baja y en verde al cargar, como en Windows
Panel al estilo Windows 11 con todos tus dispositivos
Aviso cuando un dispositivo se queda sin batería
Último nivel conocido de los dispositivos desconectados
Un icono por dispositivo o uno solo con el nivel más bajo
Tema claro y oscuro, español e inglés
Modo demostración para probarla sin dispositivos
Sin anuncios y sin recoger datos
```

**Términos de búsqueda** (máx. 7):

```
batería
bluetooth
auriculares
ratón
mando
barra de tareas
bandeja
```

**Descripción breve:** La batería de tus auriculares, ratón, teclado o mando Bluetooth, siempre a la vista en la barra de tareas.

**Copyright:** © 2026 BatteryTray contributors

**Capturas de pantalla:** [`screenshots/es-1.png`](screenshots/es-1.png), [`screenshots/es-3.png`](screenshots/es-3.png), [`screenshots/es-2.png`](screenshots/es-2.png)

**Logotipos de la Store (opcional):** [`boxart-1080.png`](boxart-1080.png) (1:1), [`logo-300.png`](logo-300.png)

## Ficha en inglés (en-US)

**Name:** BatteryTray

**Description:**

```
Your Bluetooth devices' battery, always in sight on the taskbar.

BatteryTray puts the battery level of your headphones, mouse, keyboard or controller in the taskbar, looking just like the Windows 11 battery icon. Just like Windows, it turns red when the battery is low (and so does the device's icon, so you notice at a glance) and green with a bolt while charging.

You choose where: as icons next to the clock, or as a bar right inside the taskbar with each device's battery and percentage. Hover over it to see its name.

One click opens a Windows 11–style flyout with all your devices: their level, whether they're charging, and the last known level of disconnected ones. It also lets you know when a device is running low.

Supported devices
• Bluetooth headphones and speakers whose battery Windows already shows in Settings > Bluetooth & devices.
• Bluetooth LE mice, keyboards and controllers with the standard battery service.
• Your laptop's own battery.
Some devices don't report their battery to Windows, so they can't be shown. 2.4 GHz USB receivers (Logitech, Razer…) are not supported yet.

Free, open source (GPL-3.0), no ads and no data collection: the app doesn't use the internet.

Tip: Windows 11 hides new tray icons behind the ^ arrow. To always see them, choose the bar on the taskbar, or turn them on in Settings > Personalization > Taskbar > Other system tray icons (the app's settings have a button that takes you there).
```

**Product features:**

```
Battery level of each Bluetooth device in the taskbar
As icons next to the clock or as a bar inside the taskbar
Icons that look just like the Windows 11 battery
Red when low and green while charging, like Windows
Windows 11–style flyout with all your devices
Low-battery notifications
Last known level of disconnected devices
One icon per device, or a single icon with the lowest level
Light and dark theme, English and Spanish
Demo mode to try it without devices
No ads, no data collection
```

**Search terms:**

```
battery
bluetooth
headphones
mouse
controller
taskbar
tray
```

**Short description:** Your Bluetooth headphones, mouse, keyboard or controller battery, always in sight on the taskbar.

**Screenshots:** [`screenshots/en-1.png`](screenshots/en-1.png), [`screenshots/en-3.png`](screenshots/en-3.png), [`screenshots/en-2.png`](screenshots/en-2.png)
