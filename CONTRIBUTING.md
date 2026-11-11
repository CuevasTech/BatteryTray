# Cómo colaborar

¡Gracias por querer ayudar! Cualquier aporte vale: avisar de un fallo, probar un dispositivo, traducir o programar.

## Avisar de un fallo o de un dispositivo que no aparece

1. En la app: *Ajustes > Avanzado > Copiar diagnóstico*.
2. [Abre un aviso](../../issues/new/choose) y pega el informe. Indica marca y modelo del dispositivo y si Windows muestra su batería en *Configuración > Bluetooth y dispositivos*.

## Traducir o cambiar un texto

Todos los textos de la app están en [`tools/Strings/Strings.tsv`](tools/Strings/Strings.tsv): una fila por texto, con la clave, el español y el inglés separados por tabuladores. Después de editarlo, genera los archivos de la app:

```powershell
pwsh tools/Strings/Gen-Resw.ps1
```

Para un idioma nuevo hay que añadir su columna al `.tsv` y al generador, el idioma a `Loc.Resolve` y al selector de idioma de los ajustes. Si no programas, abre un aviso con la traducción y la añadimos nosotros.

## Programar

Necesitas el [SDK de .NET 10](https://dotnet.microsoft.com/download) en Windows (opcional: Visual Studio 2026 con «Desarrollo de aplicaciones WinUI»).

```powershell
# Ejecutar en desarrollo
dotnet run --project src/BatteryTray.App -r win-x64 -p:Platform=x64

# Pruebas (tienen que pasar antes de enviar cambios)
dotnet test --project tests/BatteryTray.Core.Tests/BatteryTray.Core.Tests.csproj
dotnet test --project tests/BatteryTray.Windows.Tests/BatteryTray.Windows.Tests.csproj -p:Platform=x64

# Paquete para la Microsoft Store (x64 + ARM64)
pwsh tools/Pack-Store.ps1 -Version 0.1.0
```

Para probar la interfaz sin dispositivos, activa *Ajustes > Avanzado > Modo demostración*.

| Carpeta | Contenido |
|---|---|
| `src/BatteryTray.Core` | Modelos, ajustes, unión de lecturas, avisos y modo demostración |
| `src/BatteryTray.Windows` | Lectura de batería (Bluetooth, equipo) y dibujo de iconos y de la barra |
| `src/BatteryTray.App` | La app WinUI 3: bandeja, barra en la barra de tareas, panel, ajustes, notificaciones |
| `tests/` | Pruebas automáticas |
| `tools/` | Textos, generador de iconos, imágenes de muestra y empaquetado para la Store |
| `docs/` | Logo, imágenes del README, ficha de la Store y privacidad |

### Detalles que conviene saber

- **Iconos junto al reloj.** Windows deja a las apps poner iconos de 16×16 píxeles a escala 100 %, y su propia batería mide unos 20 de ancho. Por eso, a 100 % la batería de BatteryTray es el mismo dibujo un poco más pequeño; a 125 % o más mide exactamente lo mismo.
- **Barra en la barra de tareas.** Windows 11 ya no tiene "barras de herramientas", así que la barra es una ventana hija de la barra de tareas (`Shell_TrayWnd`) colocada a la izquierda del área de notificación (`TaskbarBarHost`). Va en su propio hilo para no bloquear nunca la barra de tareas. Si no cabe, la barra está en vertical o Windows no la deja dibujarse, la app vuelve sola a los iconos junto al reloj.
- **Imágenes del README y de la Store.** `dotnet run tools/IconPreview/ExportIcons.cs -- <carpeta>` exporta los iconos y la barra tal como los dibuja la app.

### Añadir una fuente de batería (otra marca, otro protocolo)

Cada fuente implementa `IBatteryProvider` (`src/BatteryTray.Core/IBatteryProvider.cs`): da una lista de `BatteryDevice` y avisa con `Changed` cuando cambia. `BatteryMonitor` une las listas de todas las fuentes. Ejemplos: `BluetoothBatteryProvider` y `SystemBatteryProvider` en `src/BatteryTray.Windows/Providers`.

Normas:

- Solo lectura: no cambiar la configuración de los dispositivos.
- Tratar los datos de los dispositivos (nombres, valores) como entrada no fiable: usa `DeviceText.Sanitize` y descarta valores fuera de 0-100.
- Nada de red ni telemetría.
- Si reutilizas código de otro proyecto, su licencia debe ser compatible con GPL-3.0 y hay que citarlo.

## Licencia

Al enviar un aporte aceptas que se publique bajo la licencia [GPL-3.0-or-later](LICENSE).
