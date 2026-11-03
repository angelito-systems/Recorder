# Angelito Screen Studio Pro — arquitectura (v0.1)

## Decisiones

| Tema | Elección | Por qué / compromiso |
|---|---|---|
| UI | **WPF .NET 10** con `ThemeMode="System"` (Fluent) | Maduro, DPI por monitor, claro/oscuro nativo. WinUI 3 descartado: empaquetado y `MediaElement` más frágiles, sin ganancia real para este alcance. |
| Captura de pantalla | **FFmpeg `ddagrab`** (Desktop Duplication, D3D11) | Fotogramas en GPU; con QSV/NVENC/AMF en la misma GPU no hay copia a CPU. Un proceso FFmpeg por stream y por sesión, nunca por fotograma. |
| Codificador | Detección real (`EncoderProbe`): se codifican fotogramas de prueba en orden: NVENC → QSV (zero-copy) → AMF → NVENC/AMF vía `hwdownload` → libx264 | En portátiles híbridos la pantalla está en la iGPU: NVENC no puede leer esas texturas directamente; la detección lo resuelve sin suponer hardware. |
| Webcam | FFmpeg `dshow`, archivo propio | Capa independiente, editable después. |
| Audio | **NAudio** (MIT): WASAPI micrófono + loopback del sistema, WAV float | Loopback no entrega datos cuando hay silencio; se reproduce silencio durante la grabación para que la pista sea continua. |
| Cursor | Hook `WH_MOUSE_LL`: posición (≤120 Hz) y botones | **Teclado no se registra**. |
| Vista previa | Composición WPF (`MediaElement` + transformaciones) | Decodificación por Media Foundation (GPU), sin cargar video en RAM. |
| Exportación | Un grafo `filter_complex` de FFmpeg | Mismo orden de capas que la vista previa y las **mismas funciones** (`ZoomMath`, `Layout`). |
| Persistencia | JSON versionado (`.angelstudio`), escritura atómica + `.bak` | SQLite no aporta nada a un documento de pocos KB; se reconsidera para la biblioteca de medios. |

## Módulos

- `Angelito.ScreenStudio.Core` — sin dependencias de Windows: formatos, zoom, auto-zoom, proyecto, historial, FFmpeg, exportador. 100% testeable.
- `Angelito.ScreenStudio.Desktop` — WPF: grabador, editor, captura (`Capture/`).
- `Angelito.ScreenStudio.Tests` — xUnit, incluye una exportación real con FFmpeg.

Se fusionaron los 16 módulos propuestos en 3: separar en ensamblados lo que hoy son pocas clases solo añade fricción. Se dividirán cuando un módulo crezca o necesite aislarse (p. ej. un compositor GPU).

## Contratos

```
Grabador ──► carpeta de sesión (session.json + streams) ──► StudioProject (.angelstudio)
                                                             │
                     Editor: vista previa WPF ◄── ZoomMath / Layout ──► Exporter (FFmpeg)
```

El tiempo del proyecto es el tiempo del video de pantalla. Cada stream guarda su offset de inicio respecto al reloj de la sesión; el editor y el exportador los alinean, y el usuario puede corregir audio ±1 s.

## Licencias

- FFmpeg se ejecuta como **proceso separado** (`ffmpeg.exe` junto a la app, en `ffmpeg\`, o en PATH). Para distribución comercial use una compilación **LGPL** (sin libx264/GPL) o cumpla la GPL del binario que se distribuya; el código de la app no se enlaza con FFmpeg.
- NAudio: MIT.
- H.264/HEVC pueden requerir licencias de patentes según el país y canal de distribución; los codificadores de hardware del fabricante suelen cubrir el uso en el equipo del usuario, pero valídelo legalmente antes de vender.
