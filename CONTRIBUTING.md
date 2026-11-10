# Cómo colaborar

¡Gracias por querer ayudar! Cualquier aporte vale: avisar de un fallo, probar un dispositivo, traducir o programar.

## Avisar de un fallo o de un dispositivo que no aparece

1. En la app: *Ajustes > Avanzado > Copiar diagnóstico*.
2. [Abre un aviso](../../issues/new/choose) y pega el informe. Indica marca y modelo del dispositivo y si Windows muestra su batería en *Configuración > Bluetooth y dispositivos*.

## Traducir

Los textos están en `src/BatteryTray.App/Strings/<idioma>/Resources.resw`. Copia la carpeta `en-US` con el código de tu idioma (por ejemplo `fr-FR`), traduce los valores (no las claves) y abre un *pull request*. Después hay que añadir el idioma a `Loc.Resolve` y al selector de idioma de los ajustes.

## Programar

- Requisitos: SDK de .NET 10 en Windows.
- Antes de enviar cambios, las pruebas tienen que pasar:

  ```powershell
  dotnet test --project tests/BatteryTray.Core.Tests/BatteryTray.Core.Tests.csproj
  dotnet test --project tests/BatteryTray.Windows.Tests/BatteryTray.Windows.Tests.csproj -p:Platform=x64
  ```

- Para probar la interfaz sin dispositivos, activa *Ajustes > Avanzado > Modo demostración*.

### Añadir una fuente de batería (otra marca, otro protocolo)

Cada fuente implementa `IBatteryProvider` (`src/BatteryTray.Core/IBatteryProvider.cs`): da una lista de `BatteryDevice` y avisa con `Changed` cuando cambia. `BatteryMonitor` une las listas de todas las fuentes. Ejemplos: `BluetoothBatteryProvider` y `SystemBatteryProvider` en `src/BatteryTray.Windows/Providers`.

Normas:

- Solo lectura: no cambiar la configuración de los dispositivos.
- Tratar los datos de los dispositivos (nombres, valores) como entrada no fiable: usa `DeviceText.Sanitize` y descarta valores fuera de 0-100.
- Nada de red ni telemetría.
- Si reutilizas código de otro proyecto, su licencia debe ser compatible con GPL-3.0 y hay que citarlo.

## Licencia

Al enviar un aporte aceptas que se publique bajo la licencia [GPL-3.0-or-later](LICENSE).
