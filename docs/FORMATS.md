# Formatos

## Carpeta de sesión (`Videos\Angelito Studio\<fecha>\`)

| Archivo | Contenido |
|---|---|
| `session.json` | Manifiesto: tamaño, FPS, nombre y offset (s desde el inicio de la sesión) de cada stream, `complete`. Se escribe al empezar (`complete: false`) y al terminar. |
| `screen.mp4` | Pantalla (completa o región), H.264. **Sin cursor** si se grabó con «Cursor editable» (por defecto); con el cursor del sistema dentro en grabaciones anteriores o con la opción desactivada. Se graba en tramos `screen_N.mkv` (uno por cada inicio/reanudación; resisten cierres inesperados) que al detener se unen sin re-codificar. |
| `webcam.mp4` | Cámara, opcional. Con un solo tramo se remuxa y conserva su offset; con pausas, cada tramo se recorta a la ventana de su tramo de pantalla y se re-codifica una vez. |
| `mic.wav`, `system.wav` | Audio PCM float de WASAPI, opcionales. Se graban continuos (`*.raw.wav`, silencio durante la pausa) y al detener se recortan exactamente a los tramos de video. |
| `keys.txt` | Solo si se activó el visualizador: `t combinación` por línea (p. ej. `2.7644 Ctrl+S`) en tiempo del video. Únicamente combinaciones de la lista permitida; nunca texto, letras sueltas, Mayús+letra ni AltGr. |
| `cursor.txt` | Una línea por evento: `t x y k` — `t` segundos en tiempo del video final (las pausas no existen), `x y` píxeles físicos relativos al área capturada, `k` 0=movimiento 1=izq 2=der 3=medio. Movimientos del gancho de ratón (120 Hz) y, con cursor editable, también de un sondeo a 60 Hz (capta movimientos por programa: `SetCursorPos`, escritorio remoto). Sin teclado. |
| `cursor-shapes.txt` | Solo con cursor editable: `t id` cuando cambia lo que Windows muestra. `id` es una forma de `cursors\`, `-` = cursor oculto (al escribir, o un programa que dibuja su propio puntero: ese puntero ya está en el video y no se dibuja otro encima) y `?` = no se pudo capturar (se mantiene la última forma real; nunca se inventa una flecha). |
| `cursors\` | `shapes.json` (por forma: `id`, `w`, `h`, `hotX`, `hotY` = punto de clic, `frameMs` = duración de cada fotograma, `arrow`) y `<id>.bgra`: fotogramas BGRA sin premultiplicar apilados en vertical. `id` = hash del contenido: cada apariencia se guarda una vez. Capturado con `GetCursorInfo` → `CopyIcon` → `GetIconInfo` (tamaño y punto de clic) → `DrawIconEx` por cada paso de animación (`GetCursorFrameInfo`), dibujando sobre negro y sobre blanco para obtener la transparencia real y los píxeles de inversión (cursor de texto clásico). |
| `session.json` → `cursorInVideo`, `cursorShapes`, `cursorScale` | `cursorInVideo` vale `true` en grabaciones antiguas (falta el campo): no se dibuja ningún cursor encima. `cursorScale` = tamaño en que Windows mostraba el cursor en ese monitor (DPI) ÷ tamaño del mapa de bits capturado. |
| `session.json` → `recording` | Diario mientras se graba: tramos (archivo e inicio de pantalla/cámara, fin), offsets de audio y qué flujos hay. Se actualiza en cada cambio, de modo que una sesión interrumpida se puede finalizar después solo con lo que hay en disco. Se borra al terminar. |
| `recording.lock` | Abierto en exclusiva mientras se graba; el sistema lo libera si el proceso muere. Evita "recuperar" una grabación en curso. |
| `session.json` tras detener | Todos los offsets quedan en 0 (salvo la cámara de un solo tramo): el editor ve una única línea de tiempo. Si la finalización falla, los tramos y archivos `.raw` se conservan. |
| `.cache\` | Miniaturas (`thumbs-*`, JPG cada 2 s o más, máx. ~600) y picos de audio (`peaks-*`, 100 por segundo, 1 byte). Se regeneran si el medio es más nuevo; se pueden borrar sin perder nada. |
| `proyecto.angelstudio` | Proyecto creado automáticamente al terminar. |

## Proyecto `.angelstudio` (JSON, `formatVersion: 1`)

- `sessionDir` relativo al proyecto (portátil: mover la carpeta entera funciona; si faltan medios el editor pide la carpeta y guarda la nueva ruta).
- `segments`: tramos conservados del video fuente, en orden (cortar/dividir/eliminar).
- `zooms`: `{start, end, scale, x, y, ramp}` — centro normalizado, suavizado smoothstep. El auto-zoom solo propone segmentos; son editables.
- `texts`: `{start, end, text, x, y, size, color, background, bold}` — tiempos de origen (siguen al contenido a través de los cortes), centro y tamaño relativos al cuadro de salida.
- `subtitles`: `{start, end, text}` en tiempo de origen + `subtitleStyle` `{size, y, color, background, burn}`. Importar/exportar SRT convierte a/desde el tiempo del video exportado (los subtítulos dentro de tramos eliminados se omiten; los que cruzan un corte se recortan).
- `annotations`: `{start, end, kind, x, y, x2, y2, color, thickness}` — `kind`: Rectangle, Ellipse, Arrow, Highlight, Blur, Pixelate. Puntos normalizados al cuadro de **origen** (cola→punta en flechas, esquinas en el resto), así siguen al contenido bajo el zoom.
- `canvas`: `{aspect, x, y}` — formato de salida (Source, Wide 16:9, Vertical 9:16, Square 1:1, Classic 4:3) y centro de encuadre normalizado al origen. Los zooms (también de escala 1.0) mueven ese encuadre en el tiempo.
- `export.height`: lado corto de la salida en píxeles (0 = tamaño nativo del encuadre). Tamaños de cámara y textos son relativos al lado corto.
- `keys`: `{visible, look, x, y, size, duration}` — estilo del visualizador de atajos (Capsule/Minimal), centro relativo a la salida.
- `cursor`, `webcam`, `audio`, `export`: estilos y ajustes (`cursor` = efectos de clic).
- `pointer`: cursor dibujado — `visible`, `mode` (`Original` = forma y recorrido reales; `Cinematic` = forma real, recorrido suavizado que pasa exacto por cada clic; `Custom` = una imagen propia sustituye a la flecha, las demás formas siguen siendo reales), `scale`, `smoothing` (0–1), `opacity`, `hideIdle`, `customImage` (PNG dentro de la sesión) y su punto de clic. Solo afecta al dibujo: el video original no cambia.

## Preferencias `%LocalAppData%\AngelitoScreenStudio\keys.json`

`{"extraAllowed": ["Ctrl+Shift+PageUp"], "excludedApps": ["mibanco"]}` — atajos adicionales permitidos (se normalizan; combinaciones con AltGr se rechazan) y procesos donde no se captura nada (además de los gestores de contraseñas por defecto).

Campos nuevos son opcionales: proyectos anteriores abren sin migración.

Cada guardado escribe un temporal y lo reemplaza, dejando la versión anterior en `.bak`; si el proyecto está corrupto se abre el `.bak`. Formatos más nuevos que la app se rechazan con un mensaje claro; las migraciones van en `StudioProject.FromJson`.
