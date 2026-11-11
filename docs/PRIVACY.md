# Política de privacidad / Privacy policy

*Última actualización / Last updated: 2026-11-11*

## Español

Battery Tray es una app de código abierto que muestra la batería de tus dispositivos en la barra de tareas.

**No recoge, envía ni comparte ningún dato.** La app no se conecta a Internet, no tiene telemetría, no muestra publicidad y no usa servicios de terceros.

Para funcionar, lee en tu propio equipo:

- La lista de dispositivos Bluetooth emparejados (nombre, tipo, estado de conexión y nivel de batería).
- Los ratones, teclados, cascos y mandos conectados por USB o con su receptor (fabricante, modelo, nombre y nivel de batería). Para saber su batería, la app les hace solo consultas de lectura; nunca cambia su configuración.
- La batería del propio equipo, si la tiene.

Y guarda, **solo en tu equipo**, en la carpeta privada de la app (puedes abrirla desde *Ajustes > Avanzado > Registros*):

- `settings.json`: tus ajustes (idioma, iconos, avisos, nombres que pongas a los dispositivos).
- `cache.json`: el último nivel conocido de cada dispositivo, con su nombre y un identificador basado en su dirección Bluetooth o, en los USB, un resumen (hash) de su número de serie que no permite recuperarlo.
- `logs\`: registros técnicos para resolver fallos. Las direcciones Bluetooth se guardan ocultas en parte.

El botón *Copiar diagnóstico* copia un informe al portapapeles; solo sale de tu equipo si tú decides pegarlo en algún sitio. Las direcciones Bluetooth aparecen ocultas en ese informe.

Para borrar todos los datos, desinstala la app: Windows elimina su carpeta automáticamente.

Si tienes dudas, abre un aviso en https://github.com/CuevasTech/BatteryTray/issues

## English

Battery Tray is an open-source app that shows your devices' battery levels in the taskbar.

**It does not collect, send or share any data.** The app does not connect to the internet, has no telemetry, shows no ads and uses no third-party services.

To work, it reads on your own computer:

- The list of paired Bluetooth devices (name, type, connection state and battery level).
- Mice, keyboards, headsets and controllers connected by USB or through their receiver (manufacturer, model, name and battery level). To read their battery, the app only sends read queries; it never changes their settings.
- The computer's own battery, if it has one.

It stores, **only on your computer**, in the app's private folder (you can open it from *Settings > Advanced > Logs*):

- `settings.json`: your settings (language, icons, alerts, custom device names).
- `cache.json`: the last known level of each device, with its name and an identifier based on its Bluetooth address or, for USB devices, a hash of its serial number.
- `logs\`: technical logs to troubleshoot problems. Bluetooth addresses are partially masked.

The *Copy diagnostics* button copies a report to the clipboard; it only leaves your computer if you choose to paste it somewhere. Bluetooth addresses are masked in that report.

To delete all data, uninstall the app: Windows removes its folder automatically.

Questions: open an issue at https://github.com/CuevasTech/BatteryTray/issues
