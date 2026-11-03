# Formatos

## Carpeta de sesión (`Videos\Angelito Studio\<fecha>\`)

| Archivo | Contenido |
|---|---|
| `session.json` | Manifiesto: tamaño, FPS, nombre y offset (s desde el inicio de la sesión) de cada stream, `complete`. Se escribe al empezar (`complete: false`) y al terminar. |
| `screen.mp4` | Pantalla, H.264, cursor del sistema incluido. Se graba como `.mkv` (resiste cierres inesperados) y se remuxa a MP4 al detener. Si el remux falla se conserva el `.mkv`. |
| `webcam.mp4` | Cámara, opcional. |
| `mic.wav`, `system.wav` | Audio PCM float de WASAPI, opcionales. |
| `cursor.txt` | Una línea por evento: `t x y k` — `t` segundos desde el inicio de la sesión, `x y` píxeles relativos a la pantalla capturada, `k` 0=movimiento 1=izq 2=der 3=medio. Sin teclado. |
| `proyecto.angelstudio` | Proyecto creado automáticamente al terminar. |

## Proyecto `.angelstudio` (JSON, `formatVersion: 1`)

- `sessionDir` relativo al proyecto (portátil: mover la carpeta entera funciona; si faltan medios el editor pide la carpeta y guarda la nueva ruta).
- `segments`: tramos conservados del video fuente, en orden (cortar/dividir/eliminar).
- `zooms`: `{start, end, scale, x, y, ramp}` — centro normalizado, suavizado smoothstep. El auto-zoom solo propone segmentos; son editables.
- `cursor`, `webcam`, `audio`, `export`: estilos y ajustes.

Cada guardado escribe un temporal y lo reemplaza, dejando la versión anterior en `.bak`; si el proyecto está corrupto se abre el `.bak`. Formatos más nuevos que la app se rechazan con un mensaje claro; las migraciones van en `StudioProject.FromJson`.
