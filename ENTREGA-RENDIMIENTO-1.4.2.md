# Blackwall 1.4.2 — rendimiento y preview

26 de octubre de 2026. Código integrado y preview compilada/abierta; esta versión no se publicó ni se preparó como paquete OTA firmado. El portable raíz anterior y el paquete publicado 1.4.1 se conservaron.

## Cambios implementados

- El filtro de tweaks y apps actualiza la visibilidad de los controles existentes. Conserva el buscador, la selección de texto, los handlers de acciones, los filtros de riesgo/equipo/categoría y la selección de software, sin reconstruir la página por tecla.
- La cuadrícula adaptable reordena las tarjetas visibles sin huecos; reutiliza su lista y las alturas de fila cuando es posible. Se mantienen los mismos anchos y materiales.
- Seleccionar una app cambia su borde y el contador de seleccionadas en el lugar. No dispara una instalación ni reconstruye el catálogo.
- La consola añade las líneas nuevas en lugar de reasignar todo su texto; al salir del monitor se libera la referencia al control retirado. Los registros conservan su orden y contenido.
- Los reflejos de tarjetas y las cargas del trazo eléctrico reutilizan pinceles/trazos congelados. Permanecen los colores, geometría, duraciones y efectos. Se evita iniciar entradas sobre tarjetas colapsadas.
- Si una lectura obliga a refrescar la misma sección mientras el buscador tiene foco, se intenta restaurar foco y selección con un callback protegido frente a navegación posterior.
- El metadato local pasa a 1.4.2. `Build-Portable-Exe.bat` compilará esa versión desde el código actual; esta tarea solo compiló las previews bajo `artifacts`.

No se cambiaron los motores de tweaks, descargas, permisos, restauración o actualización, ni se añadieron dependencias. El arranque y las lecturas ya diferidas se mantienen: las mediciones no justificaron añadir otra caché, un nuevo índice o virtualización personalizada.

## Comparación reproducible

Referencia: asset firmado 1.4.1 intacto. Candidata: `artifacts/Blackwall.performance.preview.exe`, versión 1.4.2.0, SHA-256 `419755fc021bc682ad8b2dad1026b383c870929b50e6f2bb1b46a7154602c94a`.

Siete pares de procesos independientes, alternando antes/después y después/antes. Mismo entorno Windows/WPF, preferencias aisladas de benchmark, catálogos embebidos reales, superficie de 1400×880 a 96 DPI y mismo harness. No se inicia `Program.Main` ni `Loaded`: no se ejecutan consultas de hardware ni cambios del sistema. El harness marca módulos preparados para medir sus vistas, cuyos estados reales no consultados siguen desconocidos.

| Medida: mediana de siete ejecuciones | 1.4.1 | Candidata 1.4.2 | Interpretación |
| --- | ---: | ---: | --- |
| Construcción y primer render fuera de pantalla | 481 ms | 480 ms | Equivalente en esta prueba; no demuestra una mejora de arranque. |
| 30 cambios/reconstrucciones de sección | 3852 ms | 3936 ms | +2,18 %; variación pequeña, sin mejora demostrada de navegación. |
| 108 actualizaciones de búsqueda con layout | 13183 ms | 2514 ms | 80,93 % menos tiempo acumulado. |
| p95 por actualización de búsqueda, mediana de los siete p95 | 361,562 ms | 113,138 ms | 68,71 % menor. Cada ejecución tiene 108 observaciones; no es un p95 combinado de todas ellas. |
| Reconstrucciones de página durante la búsqueda | 108 | 0 | Se conserva el árbol de controles. |
| Recolecciones de generación 0 durante la búsqueda | 106 | 12 | Menor presión de asignaciones en ese escenario. |
| Memoria privada al terminar la secuencia | 179556352 B | 102412288 B | 42,96 % menor tras esta carga; no representa toda sesión o equipo. |
| Working set al terminar la secuencia | 192274432 B | 114774016 B | 40,31 % menor tras esta carga. |

La secuencia de búsqueda repite `a`, `au`, `aud`, `audio`, un texto sin coincidencias y vacío. Incluye el coste de filtrado y `UpdateLayout`; no simula la latencia de un teclado físico. Sus entradas pueden ocultar y volver a mostrar todas las tarjetas.

Los resultados originales permanecen en `artifacts/performance-1.4.2-benchmark.json`. El CPU de reposo tuvo granularidad/variación insuficiente para anunciar una mejora; no se declara consumo cero. El scroll programático del harness tampoco demuestra FPS ni sensación del touchpad.

## Comprobaciones

- 121 checks WPF, 16 pruebas web y 12 casos del compilador: pasaron. Incluyen los contratos simulados de aplicar/revertir y la protección contra doble ejecución existentes; no se ejecutaron operaciones destructivas del sistema.
- Se añadieron regresiones para identidad del buscador/página/handlers durante el filtrado, resultados vacíos, retorno de tarjetas, selección de apps, contador, consola incremental y geometría de filas colapsadas.
- Se produjeron 28 capturas fuera de pantalla. Se inspeccionaron grafito, modo claro y resultados vacíos. Los PNG de referencia anteriores `native-initial`, `native-graphite` y `native-cards` coincidieron byte por byte con los nuevos en esas condiciones. Esto protege esos estados, no certifica todas las combinaciones visuales o la composición de una ventana real.
- La prueba adicional de vida útil recorrió 102 cambios de sección y 108 cambios de búsqueda. Tras drenar callbacks y recolectar solo dentro del harness, quedaron cero páginas retiradas vivas entre las referencias débiles registradas. No demuestra ausencia universal de fugas en una sesión visible larga. Esa ejecución se usó para referencias, no para comparar tiempos, porque coincidió con otros checks.
- La preview 1.4.2.0 se abrió como proceso separado y expuso una ventana de Blackwall en modo estándar; Windows la informó respondiendo. No se terminó ninguna instancia anterior.
- `git diff --check` pasó y el escáner de publicación no encontró secretos en el árbol revisado. No constituye garantía absoluta ni reemplaza revisar los archivos exactos antes de un futuro push.

## Usar la preview

Archivo: `artifacts/Blackwall.preview.exe`. Es la compilación de revisión validada por `Test-Ui.ps1` y abierta para el usuario. La otra preview de rendimiento contiene el mismo código/producto para la comparación; sus bytes difieren por la compilación independiente.

Prueba recomendada en la ventana ya abierta: escribir y borrar en los filtros de optimización/apps, seleccionar varias apps sin instalarlas, cambiar categorías y desplazar con el touchpad en ambos sentidos. No aplicar tweaks ni instalar programas solo para evaluar la fluidez.

## Reproducir las comprobaciones

```powershell
powershell -NoProfile -File .\scripts\Test-Ui.ps1
powershell -NoProfile -File .\scripts\Measure-VisualStartup.ps1 -BeforeExe .\artifacts\releases\1.4.1\Blackwall.exe -AfterExe .\artifacts\Blackwall.preview.exe -Trials 7
.\artifacts\NativeVisualBenchmark.exe .\artifacts\Blackwall.preview.exe --lifetime
node .\scripts\check-publish-safety.mjs
```

Cerrar la preview antes de recompilarla. El script del benchmark genera su ejecutable desde el harness actual; la modalidad `--lifetime` añade el recorrido prolongado y recolección diagnóstica exclusivamente a la prueba.

## Límites y paso siguiente

El acceso automático al escritorio mediante Computer Use no estuvo disponible. Se abrió la aplicación por Windows, se confirmó su ventana y se inspeccionaron capturas fuera de pantalla; no se automatizaron interacciones sobre la ventana visible. No se midieron FPS/GPU, latencia física del touchpad, arranque completo ni otro equipo.

Los escalados y tamaños del harness son pruebas de layout/render, no cambios físicos de DPI de Windows. La confirmación física de 125/150/200 % sigue pendiente.

No se considera completada la etapa de publicación del plan. Antes de distribuir: validar la interacción visible/touchpad, revisar las fuentes exactas y su commit, preparar la nueva entrega firmada, ejecutar los checks de paquete/OTA para ella y comprobar una instalación real sobre una copia. La release nueva deberá apuntar a las fuentes de esa entrega. No se cambiaron `catalogs/update.json` remoto ni los clientes existentes.
