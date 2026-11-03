# Formatos

## Carpeta de sesión (`Videos\Angelito Studio\<fecha>\`)

| Archivo | Contenido |
|---|---|
| `session.json` | Manifiesto: tamaño, FPS, nombre y offset (s desde el inicio de la sesión) de cada stream, `complete`. Se escribe al empezar (`complete: false`) y al terminar. |
| `screen.mp4` | Pantalla (completa o región), H.264, cursor del sistema incluido. Se graba en tramos `screen_N.mkv` (uno por cada inicio/reanudación; resisten cierres inesperados) que al detener se unen sin re-codificar. |
| `webcam.mp4` | Cámara, opcional. Con un solo tramo se remuxa y conserva su offset; con pausas, cada tramo se recorta a la ventana de su tramo de pantalla y se re-codifica una vez. |
| `mic.wav`, `system.wav` | Audio PCM float de WASAPI, opcionales. Se graban continuos (`*.raw.wav`, silencio durante la pausa) y al detener se recortan exactamente a los tramos de video. |
| `cursor.txt` | Una línea por evento: `t x y k` — `t` segundos en tiempo del video final (las pausas no existen), `x y` píxeles relativos al área capturada, `k` 0=movimiento 1=izq 2=der 3=medio. Sin teclado. |
| `session.json` tras detener | Todos los offsets quedan en 0 (salvo la cámara de un solo tramo): el editor ve una única línea de tiempo. Si la finalización falla, los tramos y archivos `.raw` se conservan. |
| `proyecto.angelstudio` | Proyecto creado automáticamente al terminar. |

## Proyecto `.angelstudio` (JSON, `formatVersion: 1`)

- `sessionDir` relativo al proyecto (portátil: mover la carpeta entera funciona; si faltan medios el editor pide la carpeta y guarda la nueva ruta).
- `segments`: tramos conservados del video fuente, en orden (cortar/dividir/eliminar).
- `zooms`: `{start, end, scale, x, y, ramp}` — centro normalizado, suavizado smoothstep. El auto-zoom solo propone segmentos; son editables.
- `texts`: `{start, end, text, x, y, size, color, background, bold}` — tiempos de origen (siguen al contenido a través de los cortes), centro y tamaño relativos al cuadro de salida.
- `subtitles`: `{start, end, text}` en tiempo de origen + `subtitleStyle` `{size, y, color, background, burn}`. Importar/exportar SRT convierte a/desde el tiempo del video exportado (los subtítulos dentro de tramos eliminados se omiten; los que cruzan un corte se recortan).
- `cursor`, `webcam`, `audio`, `export`: estilos y ajustes.

Campos nuevos son opcionales: proyectos anteriores abren sin migración.

Cada guardado escribe un temporal y lo reemplaza, dejando la versión anterior en `.bak`; si el proyecto está corrupto se abre el `.bak`. Formatos más nuevos que la app se rechazan con un mensaje claro; las migraciones van en `StudioProject.FromJson`.
