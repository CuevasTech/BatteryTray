# Plan del proyecto: indicador de baterías para Windows

Nombre provisional: **BatteryTray** (se decide en la fase 0).

> **Estado (11/11/2026):** versión 0.1.0.
> - Hecho: fase 0 (repositorio, licencia GPL-3.0, CI) y fase 1 completa: Bluetooth vía Windows y GATT, batería del equipo, iconos de bandeja iguales que la batería de Windows 11 (relleno del color de cada dispositivo, borde rojo con batería baja), panel, ajustes, avisos, inicio con Windows, español/inglés, modo demostración y diagnóstico. Logo, portada y ficha de la Store (`docs/store`).
> - Nuevo: modo **barra en la barra de tareas** (ventana hija de `Shell_TrayWnd`, con vuelta automática a los iconos si no cabe).
> - Distribución: **solo Microsoft Store** (sin ejecutables en GitHub). Paquete con `tools/Pack-Store.ps1`. Identidad de Partner Center ya en el manifiesto; versión de la Store probada en local (Modo de desarrollador).
> - Pendiente: primer envío a la Store, probar con dispositivos reales y añadir más marcas (fase 3).

Una app pequeña, gratuita y de código abierto que muestra la batería de tus dispositivos inalámbricos (auriculares, ratón, teclado, mandos...) junto al reloj de la barra de tareas, con el aspecto de Windows 11.

---

## 1. ¿Es posible?

Sí. Windows ya conoce la batería de muchos dispositivos Bluetooth (la que enseña en *Configuración > Bluetooth y dispositivos*), pero no la muestra en la barra de tareas. Una app puede leer ese dato y enseñarlo.

Una aclaración sobre "en la barra de tareas": Windows 11 **no permite** que las apps metan contenido propio en mitad de la barra (las antiguas "deskbands" desaparecieron). El sitio oficial y estable es el **área de notificación** (los iconos junto al reloj). El usuario puede arrastrar ahí los iconos para que estén siempre visibles. Es lo que usan todas las apps parecidas y lo que acepta la Microsoft Store.

## 2. Lo que ya existe y cómo diferenciarse

| Proyecto | Qué cubre | Pegas |
|---|---|---|
| [HaloBattery](https://github.com/HeyOkay/HaloBattery) (Python, MIT, ~740★) | Muchas marcas con receptor 2.4 GHz y Bluetooth | Sin firmar, se distribuye en ZIP, no está en la Store, solo menú contextual |
| [BlueGauge](https://github.com/iKineticate/BlueGauge) (Rust, MIT, ~450★) | Bluetooth clásico y BLE | No lee receptores 2.4 GHz, no está en la Store, sin ventana |
| [XBatteryStatus](https://github.com/tommaier123/XBatteryStatus) (C#, GPL-3.0) | Solo mandos BLE | Solo mandos. Sí está en Store y winget |
| [LGSTrayEx](https://github.com/strain08/LGSTrayEx) (C#) | Solo Logitech | Solo una marca |

**Hueco que podemos ocupar:**

1. **Aspecto nativo de Windows 11.** Ninguno tiene un panel desplegable como el de volumen o Wi-Fi (Mica, esquinas redondeadas, tema claro/oscuro, color de acento).
2. **Todo en una app:** Bluetooth, mandos, la batería del portátil y, más adelante, receptores de marcas.
3. **Instalación fácil y de confianza:** Microsoft Store y winget, sin avisos de SmartScreen.
4. **En español e inglés** desde el primer día.
5. **Ligera:** reacciona a eventos en vez de estar preguntando todo el rato.

## 3. Funciones de la versión 1.0

- Icono en la bandeja con el % dibujado. Dos modos:
  - **Un icono por dispositivo** (el usuario elige cuáles).
  - **Un único icono** con el nivel más bajo.
- **Clic izquierdo:** panel estilo Windows 11 con todos los dispositivos, su barra de batería, si se están cargando y si están desconectados.
- **Pasar el ratón:** tooltip con el resumen.
- **Clic derecho:** Ajustes, Iniciar con Windows, Salir.
- **Avisos** (notificaciones de Windows) de batería baja, con umbral configurable (por defecto 20 %).
- **Ajustes** con el estilo de la app Configuración: dispositivos visibles, renombrar dispositivos, umbral, modo de icono, idioma.
- Respeta el tema claro/oscuro, el color de acento, el escalado de pantalla, el alto contraste y el lector de pantalla.
- Se inicia con Windows (opcional).
- **Cero telemetría:** no recoge datos ni usa la red, salvo para buscar actualizaciones en la versión de GitHub.

### Boceto

```
Barra de tareas:   ...  ^   [82]  [18]  [95]   🔊 📶  12:30
                         ratón en rojo (bajo)

Panel al hacer clic:
┌───────────────────────────────────────────┐
│  Baterías                              ⚙  │
│                                           │
│  Auriculares WH-1000XM5   ████████░░  82% │
│  Ratón MX Master 3S       ██░░░░░░░░  18% │  ← rojo
│  Mando Xbox               █████████░  95% │  ⚡ cargando
│  Teclado K3               (desconectado)  │
│  Este equipo              ██████████  98% │
│                                           │
│  Actualizado hace 1 min                   │
└───────────────────────────────────────────┘
```

## 4. De dónde se saca la batería

Cada fuente es un **"proveedor"** independiente, así se pueden añadir marcas sin tocar el resto.

| Fase | Fuente | Dispositivos | Cómo |
|---|---|---|---|
| 1 | Propiedad de batería de Windows | Bluetooth clásico (auriculares con HFP) y BLE (ratones, teclados) | Propiedad PnP `{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2`, vigilada con `DeviceWatcher` (avisa cuando cambia) |
| 1 | Batería del equipo | Portátiles, tablets | `Windows.Devices.Power` / `PowerManager` |
| 3 | Servicio de batería BLE (GATT 0x180F) | Dispositivos BLE que Windows no muestra; mandos | `Windows.Devices.Bluetooth.GenericAttributeProfile` |
| 3 | Mandos de juego | Xbox (Bluetooth, adaptador y USB) | `Windows.Gaming.Input` / XInput (hay que comprobar que funciona en segundo plano) |
| 3 | HID de Sony | DualSense / DualShock 4 | Informe HID |
| 3 | Logitech HID++ 2.0 | Receptores Unifying, Bolt, Lightspeed | HID (`hidapi` o API de Windows) |
| 4 | Otras marcas | Razer, SteelSeries, HyperX, Corsair... | Contribuciones de la comunidad |

## 5. Tecnología

- **C# + .NET 10 (LTS)**: ya lo tienes instalado (SDK 10.0.403).
- **WinUI 3 / Windows App SDK**: los controles y el aspecto auténticos de Windows 11. Visual Studio 2026 ya tiene el componente de WinUI instalado.
- **H.NotifyIcon.WinUI** para el icono de bandeja, o Win32 directo (`Shell_NotifyIcon`) con CsWin32 si queremos algo más ligero.
- **CommunityToolkit.Mvvm** para separar la interfaz de la lógica.
- **Empaquetado MSIX** (necesario para la Store, el inicio con Windows y las notificaciones).
- **Native AOT + trimming** para arrancar rápido y gastar menos memoria.

Alternativa: WPF + la librería WPF-UI. Gasta algo menos de memoria, pero el aspecto se parece a Windows 11 sin ser el auténtico. La recomendación es WinUI 3, porque el estilo nativo es el principal argumento del proyecto.

## 6. Estructura del repositorio

```
/
├─ src/
│  ├─ BatteryTray.Core/          Modelos (Dispositivo, Lectura), interfaz IBatteryProvider,
│  │                             servicio que junta los proveedores y quita duplicados
│  ├─ BatteryTray.Providers/     Proveedores: Bluetooth (Windows), equipo, GATT, mandos...
│  └─ BatteryTray.App/           WinUI 3: bandeja, panel, ajustes, notificaciones, idiomas
├─ tests/
│  └─ BatteryTray.Core.Tests/    Pruebas de la lógica (con proveedores simulados)
├─ docs/
│  ├─ PRIVACY.md                 Política de privacidad (la Store pide una URL; vale la de GitHub)
│  └─ devices.md                 Lista de dispositivos probados
├─ .github/workflows/            Compilar, probar y publicar versiones
├─ README.md (ES + EN)
├─ LICENSE
└─ CONTRIBUTING.md
```

## 7. Publicación

### GitHub
- Repositorio público con licencia, README en español e inglés y capturas.
- **GitHub Actions** compila cada cambio y crea las versiones.
- En *Releases*: enlace a la Store y un ZIP portable para quien no use la Store.
- Firma del ejecutable: sin firma, Windows muestra el aviso de SmartScreen. **SignPath Foundation** firma gratis proyectos de código abierto tras aprobarlos (XBatteryStatus lo usa). Se solicita cuando el proyecto tenga algo de recorrido.

### Microsoft Store
- **Cuenta de desarrollador individual gratuita** desde septiembre de 2025. Se crea con tu cuenta Microsoft personal y una verificación de identidad con DNI y selfie. ([anuncio](https://blogs.windows.com/windowsdeveloper/2025/09/10/free-developer-registration-for-individual-developers-on-microsoft-store/), [alta](https://learn.microsoft.com/en-us/windows/apps/get-started/sign-up))
- Aparecerás como desarrollador con tu nombre. No hace falta empresa, marca ni dominio.
- **Microsoft firma el MSIX al publicarlo**: no hay que comprar certificado, no salen avisos y las actualizaciones son automáticas.
- Hace falta: reservar el nombre de la app, capturas, descripción (ES/EN), cuestionario de clasificación por edades, precio "gratis" y la URL de la política de privacidad (la de GitHub sirve).
- Ojo: la cuenta individual no se puede convertir después en cuenta de empresa.

### winget
- Las apps de la Store ya se instalan con `winget install --source msstore`.
- Opcional: subir un manifiesto a `microsoft/winget-pkgs` para la versión de GitHub.

### Licencia (a decidir)
- **GPL-3.0 (recomendada):** garantiza que el código y cualquier copia sigan siendo libres. Además permite reutilizar código de proyectos GPL que ya conocen los protocolos de las marcas (HeadsetControl, Solaar...). Es compatible con la Store (XBatteryStatus es GPL-3.0 y está publicada).
- **MIT:** más permisiva, cualquiera puede reutilizar el código incluso en apps cerradas. Con MIT no se puede copiar código de proyectos GPL.

## 8. Limitaciones

- **Solo se ve la batería de los dispositivos que la informan.** Muchos auriculares Bluetooth baratos no la envían.
- **Los receptores USB de 2.4 GHz** usan protocolos propios de cada marca. Hay que implementarlos uno a uno (fase 3 en adelante), y las apps del fabricante (G HUB, Synapse...) a veces bloquean el receptor.
- Algunos dispositivos solo informan a saltos (10 % en 10 %, o 0/50/100 %).
- AirPods: Windows da un único valor, no el de cada auricular y el estuche.
- En este PC ahora mismo no hay dispositivos Bluetooth emparejados. Para probar habrá que emparejar algunos. La batería del portátil sí se lee (98 % en la prueba).

## 9. Hoja de ruta

**Fase 0. Preparación**
- Elegir nombre y licencia.
- Crear el repositorio en GitHub con la estructura, el README y la CI.
- Crear la cuenta de Partner Center y reservar el nombre en la Store.

**Fase 1. Versión mínima que funciona**
- Proveedores de Bluetooth (vía Windows) y de la batería del equipo.
- Icono de bandeja dinámico, tooltip, menú contextual.
- Panel desplegable estilo Windows 11.
- Aviso de batería baja, inicio con Windows, español e inglés.

**Fase 2. Publicación 1.0**
- Ajustes completos, pulido visual y accesibilidad.
- MSIX, ficha en la Store, política de privacidad.
- Release en GitHub y enviar a certificación.

**Fase 3. Más dispositivos**
- GATT directo, mandos Xbox y PlayStation, Logitech HID++.
- Lista pública de dispositivos probados.

**Fase 4. Comunidad**
- Guía para añadir marcas y traducciones.
- Firma con SignPath para la versión de GitHub.
- Ideas opcionales: widget para el panel de Widgets de Windows, historial de batería.

## 10. Decisiones pendientes

1. Nombre de la app.
2. Licencia: GPL-3.0 o MIT.
3. Modo de icono por defecto: uno por dispositivo o uno único.
4. Qué dispositivos tienes para probar (sirve para priorizar proveedores).
