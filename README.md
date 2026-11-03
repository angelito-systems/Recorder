# Angelito Screen Studio Pro

[![ci](https://github.com/angelito-systems/Recorder/actions/workflows/ci.yml/badge.svg)](https://github.com/angelito-systems/Recorder/actions/workflows/ci.yml)
[![Licencia: MIT](https://img.shields.io/badge/licencia-MIT-blue.svg)](LICENSE)

Grabador y editor de pantalla para Windows 10/11 — Angelito Systems S.A.C. · https://angelitosystems.net

## Qué hace

- **Graba** pantalla completa, una región o una sola ventana (aunque esté tapada), con webcam, micrófono, audio del sistema, cursor y clics, cada uno en su propio archivo. Codificador por GPU detectado solo (NVENC, QSV, AMF) o CPU.
- **Pausa y atajos globales**: Ctrl+Mayús+F9 pausa/reanuda, Ctrl+Mayús+F10 graba/detiene.
- **Recupera** grabaciones interrumpidas por un cierre inesperado o un corte de luz.
- **Edita**: cortes, zoom automático hacia los clics y zoom manual, webcam con forma y posición, textos, subtítulos (SRT), anotaciones (flechas, formas, desenfoque y pixelado), visualizador de atajos de teclado y formatos 16:9, 9:16, 1:1 y 4:3.
- **Exporta** a MP4 H.264/HEVC, MKV, WAV o PNG con presets, en una cola que deja seguir editando.

Estado detallado y pendientes: [hoja de ruta](docs/ROADMAP.md).

## Requisitos

- Windows 10 2004+ (x64) y .NET 10 SDK para compilar
- `ffmpeg.exe` en el PATH, o en `ffmpeg\` junto al ejecutable (o variable `ANGELITO_FFMPEG`)

## Compilar y ejecutar

```
dotnet test
dotnet run --project src/Angelito.ScreenStudio.Desktop
```

Grabar → Detener abre el editor con la grabación. Las grabaciones se guardan en `Videos\Angelito Studio`.

## Instalador

```
pwsh build/build-installer.ps1
```

Genera `artifacts/AngelitoScreenStudio-<versión>-x64.msi` (por usuario, sin administrador). Detalles, firma y FFmpeg: [docs/RELEASE.md](docs/RELEASE.md).

## Estructura

| Carpeta | Contenido |
|---|---|
| `src/Angelito.ScreenStudio.Core` | Proyecto, línea de tiempo, zoom, exportación con FFmpeg (sin UI, probado) |
| `src/Angelito.ScreenStudio.Desktop` | App WPF: grabador, captura, editor, cola de exportación |
| `tests/` | Pruebas, incluidas exportaciones reales con FFmpeg |
| `build/` | Instalador MSI (WiX) e icono |
| `docs/` | [Arquitectura](docs/ARCHITECTURE.md) · [formatos](docs/FORMATS.md) · [publicar](docs/RELEASE.md) · [hoja de ruta](docs/ROADMAP.md) |

## Contribuir

Issues y pull requests son bienvenidos. Antes de abrir un PR, `dotnet test` debe pasar (necesita FFmpeg en el PATH). Mantén el estilo del código que tocas.

## Licencia

El código de este repositorio es [MIT](LICENSE). FFmpeg no se incluye: se ejecuta como proceso aparte y tiene su propia licencia (LGPL o GPL según la compilación); ver [docs/RELEASE.md](docs/RELEASE.md#ffmpeg).
