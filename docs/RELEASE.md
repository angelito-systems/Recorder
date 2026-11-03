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

Compilación elegida para incluir: **FFmpeg 8.1 GPL de [BtbN](https://github.com/BtbN/FFmpeg-Builds)** (`ffmpeg-n8.1-latest-win64-gpl-8.1.zip`). Trae x264/x265/libaom (exportar sin GPU), NVENC/QSV/AMF y libplacebo/Vulkan. Con `-FfmpegDir <carpeta bin>` el script copia `ffmpeg.exe`, su `LICENSE.txt` (obligatorio, falla si no está) y un `LEEME-FFmpeg.txt` con la versión y dónde obtener el código fuente. El código de la app sigue siendo MIT: FFmpeg corre como proceso aparte.

```
pwsh build/build-installer.ps1 -FfmpegDir <ruta>fmpeg-n8.1-latest-win64-gpl-8.1in -CertThumbprint <huella>
```

## Firma y actualizaciones

- Firmar con un certificado de firma de código (idealmente EV, para la reputación de SmartScreen). El script firma con SHA-256 y sello de tiempo.
- **Certificado autofirmado** (gratis, para empezar): `pwsh build/new-selfsigned-cert.ps1` lo crea en el almacén del usuario (o reutiliza uno vigente), exporta `artifacts/AngelitoSystems-codesign.cer` e imprime la huella para `-CertThumbprint`. La firma prueba que el exe y el MSI no se alteraron, pero Windows solo confía en ella donde se importó el `.cer` en *Entidades de certificación raíz de confianza* y *Editores de confianza* (p. ej. TI por directiva de grupo). En cualquier otro PC `Get-AuthenticodeSignature` da `UnknownError` y SmartScreen sigue avisando de editor desconocido; eso solo lo quita un certificado de una CA pública. La clave privada no sale del equipo: **no se puede firmar desde el CI** con ella.
- No hay actualización automática por red. El camino previsto: un manifiesto de versiones publicado por HTTPS con URL y SHA-256 del MSI; la app solo descargaría un MSI cuya firma Authenticode sea de Angelito Systems **y** cuyo hash coincida, y lo instalaría con `msiexec` (el mismo `UpgradeCode` hace el reemplazo). Canales previstos: `stable` y `beta` (dos manifiestos).
- Herramienta: WiX 5.0.2 (licencia MS-RL, herramienta local en `dotnet-tools.json`). Las versiones 6+ añaden una cuota de mantenimiento para uso comercial.
