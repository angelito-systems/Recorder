# Hoja de ruta

Estado verificado el 2026-11-03 en i7-13650HX / RTX 4050 Laptop / Intel UHD, Windows 11, FFmpeg 8.0.1.

## Hecho (v0.1) — funcional y probado

- Grabación: pantalla completa (DDA, 24/30/60 FPS, codificador detectado por hardware), webcam, micrófono, audio del sistema, cursor y clics; ventana del grabador excluida de la captura; medidores de pico con aviso de saturación; aviso si se desconecta un dispositivo (lo grabado se conserva); comprobación de espacio libre.
- Sesión editable de streams separados (ver FORMATS.md). Grabación de prueba real: 1080p60 vía QSV zero-copy, mic + sistema en WAV, offsets de sincronía registrados.
- Editor: vista previa con zoom, anillos de clic y webcam en vivo; dividir, eliminar tramos, auto-zoom editable, añadir/quitar zoom, intensidad, suavizado, inicio/fin, centrar con clic; webcam círculo/redondeada, tamaño, espejo, arrastrar, presets; ganancia, silencio y sincronía manual de audio; deshacer/rehacer; guardado automático, `.bak`, medios faltantes.
- Exportación MP4 H.264/AAC (original, 4K, 1440p, 1080p, 720p; 3 calidades) con progreso y cancelación, NVENC/QSV/AMF/x264. Real: 6,2 s 1080p exportados en 1,2 s con NVENC.
- 11 pruebas automáticas (zoom, auto-zoom, proyecto, historial, recuperación de `.bak`, formato de cursor, layout, exportación FFmpeg real con cortes+zoom+clics+webcam+audio).

## Pendiente, por prioridad

1. **Región personalizada y ventana** (ddagrab `offset_x/offset_y/video_size`; ventana requiere WGC).
2. **Pausa** de grabación (segmentar archivos por tramo).
3. Mover/reordenar clips y arrastrar bordes de zoom en el timeline; miniaturas y forma de onda (caché en disco).
4. Textos, flechas, rectángulos, desenfoque/pixelado, subtítulos SRT (importar/exportar/incrustar).
5. Visualizador de atajos con **lista positiva** de combinaciones y exclusión por aplicación (hook de teclado solo con modificadores).
6. Cursor ampliado/suavizado en posproducción (requiere grabar sin cursor y dibujarlo, `draw_mouse=0` + `sendcmd`).
7. Compositor GPU propio (D3D11) que reemplace `zoompan` (que recorta en píxeles enteros y puede vibrar levemente en zooms lentos) y sirva a vista previa y exportación a la vez.
8. Proyectos verticales 9:16 con reencuadre editable; MKV/HEVC/WAV/PNG en exportación; cola de render.
9. Instalador (MSIX o WiX), icono, firma de código, actualizaciones firmadas.
10. Multimonitor: el emparejamiento salida DXGI ↔ monitor es por tamaño y orden; **no verificado** con varios monitores (este equipo tiene uno).

## No se hará sin requisitos

Licencias Free/Pro, IA (transcripción, reducción de ruido avanzada), nube.
