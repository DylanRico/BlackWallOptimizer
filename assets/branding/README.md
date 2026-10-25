# Emblema Blackwall — Muro Negro

Generado con la herramienta integrada `imagegen`, con fondo transparente. Inspiración solicitada: el emblema angular de David Martínez / Edgerunners, reinterpretado en carmesí. No es un recurso extraído del juego.

- `blackwall-source.png`: original generado, RGBA, 1254 × 1254, 607 833 bytes.
- `../icons/blackwall-mark.png`: recurso WPF de 256 × 256; también se copia a `src/blackwall-mark.png` y `src-tauri/icons/icon.png`.
- `../../src-tauri/icons/icon.ico`: 16, 24, 32, 48, 64, 128 y 256 px, con transparencia.

Los tamaños de 16 a 64 px se empaquetan como DIB/BGRA clásico, con máscara de transparencia; 128 y 256 px usan PNG. Se recompiló `Blackwall.exe` y se comprobó el símbolo rojo en el Explorador, tanto en iconos grandes como en la vista de detalles, después de recargar su caché.

El recurso se incrusta en el PE del ejecutable y en la cabecera/ventana WPF. La base web lo usa como marca y favicon. No requiere una descarga al iniciar.

Para reproducir únicamente el empaquetado desde el original:

```powershell
./scripts/New-AppIcon.ps1 -SourcePng assets/branding/blackwall-source.png
```

Prompt de generación:

> Use case: logo-brand. Create a single finished Windows executable app icon for BLACKWALL, inspired by the iconic angular ER / Edgerunners insignia on David Martinez's jacket in Cyberpunk: Edgerunners, reinterpreted in crimson red instead of green. Subject: a bold compact interlocking ER-like lightning glyph, diagonal stepped strokes, angular triangular negative spaces, jagged but deliberate cyberpunk silhouette, instantly legible when reduced to a 32px application icon. Not a portrait or a jacket. Style: pristine flat graphic emblem, vector-like edges, strong thick strokes and minimal internal detail; deep crimson body with a restrained brighter red upper edge for contrast. Palette strictly crimson #8F1828, red #EF4055, subtle dark near-black edge; no green, no yellow, no lettering or caption. Center the glyph in a square 1024x1024 composition, occupy approximately 82% of the square, even safe padding, entire mark visible. True transparent background, no tile, no square background, no scene, no mockup, no texture, no watermark, no tiny ornamental circuit lines, no blur, no excessive glow. The output is the actual isolated icon asset, not a presentation of the logo.

La herramienta devolvió 1254 × 1254 aunque se solicitaron 1024 × 1024. Solo se redimensionó y empaquetó el resultado; no se alteró el diseño por código.
