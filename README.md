# Angelito Screen Studio Pro

Grabador y editor de pantalla para Windows 10/11 — Angelito Systems S.A.C. · https://angelitosystems.net

## Requisitos

- .NET 10 SDK, Windows 10 2004+ (x64)
- `ffmpeg.exe` en el PATH, o en `ffmpeg\` junto al ejecutable (o variable `ANGELITO_FFMPEG`)

## Uso

```
dotnet test
dotnet run --project src/Angelito.ScreenStudio.Desktop
```

Grabar → Detener abre el editor con la grabación. Las grabaciones se guardan en `Videos\Angelito Studio`.

Documentación: [arquitectura](docs/ARCHITECTURE.md) · [formatos](docs/FORMATS.md) · [hoja de ruta](docs/ROADMAP.md)
