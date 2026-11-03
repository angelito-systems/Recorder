# Publicar una versión

```
pwsh build/build-installer.ps1                       # pruebas → exe único → MSI + .sha256 en artifacts/
pwsh build/build-installer.ps1 -CertThumbprint <SHA1> # además firma exe y MSI con sello de tiempo
pwsh build/build-installer.ps1 -FfmpegDir <carpeta>   # además incluye FFmpeg en el instalador
```

1. Subir `<Version>` en `src/Angelito.ScreenStudio.Desktop/Angelito.ScreenStudio.Desktop.csproj` (x.y.z).
2. Ejecutar el script. Si fallan las pruebas, no se genera instalador.
3. Publicar el `.msi` junto a su `.sha256`.

## Qué hace el instalador

- MSI **por usuario** (`%LocalAppData%\Programs\Angelito Screen Studio Pro`): no pide administrador.
- Ejecutable único autocontenido (.NET incluido, ~73 MB; MSI ~67 MB).
- Accesos directos en Inicio y Escritorio; `.angelstudio` se abre con doble clic.
- Aparece en *Configuración → Aplicaciones* con icono, editor, web y contacto.
- Una versión más nueva reemplaza a la instalada (mismo `UpgradeCode`, que **no debe cambiar nunca**); instalar una más antigua se bloquea.
- Desinstalar elimina programa, accesos, registro y asociación. Las grabaciones (`Videos\Angelito Studio`) y las preferencias (`%LocalAppData%\AngelitoScreenStudio`) se conservan.

Verificado en Windows 11: instalación silenciosa (`msiexec /i … /qn`), arranque desde el acceso directo, actualización 0.2.0 → 0.2.1, bloqueo de la vuelta a 0.2.0 y desinstalación completa.

## FFmpeg

Por defecto **no** se incluye: la app usa el FFmpeg del PATH (o `ffmpeg\ffmpeg.exe` junto al programa) y lo indica en *Acerca de → Diagnóstico*. Incluirlo es distribuirlo:

- Compilación **LGPL** (sin x264/x265): permitida en software comercial si se cumplen sus condiciones (aviso, enlace a la fuente). Exportará con NVENC/QSV/AMF; sin GPU compatible no habrá codificador H.264 por CPU.
- Compilación **GPL** (p. ej. la "essentials" de gyan.dev usada en desarrollo): obliga a ofrecer el código fuente de FFmpeg. La app sigue siendo independiente porque FFmpeg corre como proceso separado.

Decisión comercial pendiente; consultar asesoría legal.

## Firma y actualizaciones

- Firmar con un certificado de firma de código (idealmente EV, para la reputación de SmartScreen). El script firma con SHA-256 y sello de tiempo.
- No hay actualización automática por red. El camino previsto: un manifiesto de versiones publicado por HTTPS con URL y SHA-256 del MSI; la app solo descargaría un MSI cuya firma Authenticode sea de Angelito Systems **y** cuyo hash coincida, y lo instalaría con `msiexec` (el mismo `UpgradeCode` hace el reemplazo). Canales previstos: `stable` y `beta` (dos manifiestos).
- Herramienta: WiX 5.0.2 (licencia MS-RL, herramienta local en `dotnet-tools.json`). Las versiones 6+ añaden una cuota de mantenimiento para uso comercial.
