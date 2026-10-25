# Renovación visual de Blackwall

Este documento describe la primera renovación. La identidad vigente negro/carmesí, el menú contraíble y el nuevo icono se documentan en [ENTREGA-MURO-NEGRO.md](ENTREGA-MURO-NEGRO.md).

## Plan ejecutado

1. Revisar las dos interfaces: ejecutable WPF y frontend web/Tauri.
2. Aplicar una identidad común: grafito, amarillo eléctrico, cian, navegación numerada, superficies angulares y tipografía legible.
3. Dejar el arranque centrado en hardware y cargar los inventarios restantes al abrir su módulo.
4. Deduplicar lecturas simultáneas y mostrar actividad real, errores contextuales y reintento.
5. Compilar y verificar sin aplicar ajustes al equipo ni instalar programas.

## Comportamiento

- El escaneo nativo solapa las consultas independientes de CPU, TPM y almacenamiento. El monitor no inicia otra consulta mientras la anterior está pendiente.
- La auditoría nativa muestra las ocho etapas completadas. Es un porcentaje de etapas, no una estimación de tiempo restante.
- Cuando el motor no entrega un total, la barra es indeterminada. Los lotes web cuentan elementos completados.
- Los avisos de lectura aparecen al fallar, identifican la operación y permiten reintentar. Los errores de acciones que modifican Windows no se repiten automáticamente.
- Los ajustes esperan la identificación del equipo y la verificación de sus estados. Un fallo de lectura no debe ofrecer una optimización como si estuviera verificada.
- La revisión OTA nativa queda disponible mediante su botón y se excluye de la carga inicial.
- Las transiciones respetan la preferencia de movimiento reducido; no se añaden filtros o efectos continuos costosos.
- La ventana arranca maximizada usando el área útil de su monitor para respetar la barra de tareas, incluso cuando está arriba o a un lado.
- El desplazamiento con rueda usa una transición de 260 ms con desaceleración en el contenido de todas las secciones, el menú y la búsqueda. Invertir la rueda responde desde la posición actual; teclado, arrastre de la barra y cambio de sección cancelan el movimiento pendiente.

## Verificación reproducible

```powershell
powershell -NoProfile -File .\scripts\Test-Ui.ps1
# Opcional: incluye una lectura real del hardware, sin modificar la configuración.
powershell -NoProfile -File .\scripts\Test-Ui.ps1 -ScanHardware
```

El script genera `artifacts/Blackwall.preview.exe`, ejecuta las comprobaciones WPF y las pruebas web y guarda imágenes en `artifacts/native/`. No cierra aplicaciones ni crea certificados.

Resultado actual: 41 comprobaciones nativas y 10 pruebas web aprobadas, incluyendo límites de maximizado y posiciones intermedias del desplazamiento animado. En la renovación anterior, la lectura real del hardware tardó 5.456 ms en este equipo; construcción y renderizado WPF fuera de pantalla: 1.337 ms. No representan una comparación de arranque frente a la versión anterior.

Se revisaron imágenes de los seis módulos, carga, error, tema claro/oscuro y tamaño mínimo 1080 × 700. La comprobación WPF usa renderizado fuera de pantalla. No se midieron FPS ni animaciones en una ventana real; no se pudo hacer la inspección visual web en navegador porque la sesión no dispone de uno.

## Vista previa web segura

```powershell
npm run preview:ui
# Simular un fallo de hardware y recuperarlo con Intentar de nuevo:
node scripts/preview-ui.mjs --error-once
```

Abrir `http://127.0.0.1:3848`. Usa datos de prueba identificados en pantalla y rechaza las acciones que modificarían el equipo.

## Ejecutable y reversión

El ejecutable actualizado es `Blackwall.exe`. El anterior se conserva como `artifacts/Blackwall.before-ui.exe`; para volver, cerrar Blackwall y copiar ese respaldo sobre el ejecutable actual.

La versión inmediatamente anterior al ajuste de maximizado y desplazamiento se conserva en `artifacts/Blackwall.before-scroll.exe`.

No se publicó una versión OTA ni se ejecutaron optimizaciones, instalaciones, reparaciones o restauraciones. El binario generado no se firmó: Windows puede mostrar su aviso habitual para ejecutables sin firma.
