# Plan de rendimiento de Blackwall — propuesta 1.4.2

Fecha: 26 de octubre de 2026. Estado: mejoras principales implementadas y medidas; preview 1.4.2 abierta. Informe y límites en [ENTREGA-RENDIMIENTO-1.4.2.md](ENTREGA-RENDIMIENTO-1.4.2.md). No se preparó/publicó la nueva OTA; faltan validación visible del touchpad y los controles finales de distribución. Las etapas de este documento conservan sus criterios de aceptación.

## Resultado buscado

Conservar el material grafito/plata, los colores, reflejos, animaciones, legibilidad y funciones actuales, reduciendo el trabajo que hace la interfaz para responder. El usuario debe poder escribir, desplazarse, navegar y abrir controles sin que una lectura del sistema o la creación de tarjetas interrumpa la interacción.

El objetivo es mejorar el rendimiento de la aplicación, no aplicar optimizaciones a Windows. No se ejecutarán tweaks, instalaciones, restauraciones, reinicios, terminación de procesos u OTA sobre una instalación del usuario como parte del benchmark.

## Fuente de verdad y límites

- Interfaz activa: WPF, `native-wpf/BlackwallApp.cs`; portable compilado por `Build-Portable-Exe.bat` y `scripts/Build-Native.ps1` con .NET Framework. No cambiar de framework, runtime ni arquitectura para esta mejora.
- Referencia inicial de entrega: paquete firmado 1.4.1, conservado intacto en `artifacts/releases/1.4.1`. La fuente para editar sigue siendo la copia local, con todos los cambios existentes del usuario.
- Los cambios locales completos de interfaz/OTA todavía no están sincronizados en GitHub. La nueva publicación debe vincular fuentes revisadas, etiqueta y ejecutable de la misma entrega, evitando repetir esa discrepancia.
- No se quitarán efectos para conseguir cifras mejores. Se optimizará su implementación y su trabajo fuera de pantalla. Se conservan las pausas existentes al minimizar/desactivar y el modo de movimiento reducido.
- Sin nuevas dependencias salvo evidencia de una necesidad que no cubran WPF y los componentes existentes. No se propone ninguna ahora.

## Hallazgos comprobados

| Punto | Evidencia local | Implicación y comprobación pendiente |
| --- | --- | --- |
| Filtro de sección | `CreateSectionSearch`, líneas 3242–3253: cada `TextChanged` llama a `RenderCurrentTab`, reconstruye el campo y devuelve el foco/caret. | Hay reconstrucción por tecla. Medir tiempo y asignaciones; separar el campo estable del contenido filtrado. |
| Navegación | `RenderCurrentTab`, desde línea 2964: invoca los constructores de la sección y limpia los actualizadores de tarjetas. | Medir coste por sección y actualizar solo lo necesario donde exista un beneficio, sin conservar vistas obsoletas. |
| Materiales animados | `MaterialCard.OnRender` crea pinceles y un `Pen`; `EnergyTrace.OnRender` también crea recursos y su temporizador invalida a intervalos de 33 ms cuando está activo. | Reducir asignaciones y reutilizar recursos inmutables sin alterar el aspecto. Falta atribuir su coste real con una traza. |
| Cuadrícula | `AdaptiveCardGrid` ya distribuye las tarjetas sin reconstruirlas durante un cambio de tamaño. Catálogos actuales: 45 tweaks y 18 apps. | Reutilizarla; no introducir virtualización personalizada por anticipación. Considerarla solo si sigue dominando el coste de layout. |
| Iconos | `GetAppIconSource` ya tiene caché y congela bitmaps cargados. | Mantenerlo. Revisar búsquedas de recursos, fallos y tamaños decodificados; no crear una segunda caché. |
| Movimiento ambiental | Ya hay gestión por activación, desactivación y estado de ventana, y un modo de movimiento reducido. | Probar que siguen funcionando y que no quedan relojes o eventos activos innecesarios. No presentar estas funciones existentes como una novedad. |
| Medición actual | `NativeVisualBenchmark` mide construcción/render fuera de pantalla; no ejecuta `Loaded`. El scroll usa `ScrollToVerticalOffset` y `UpdateLayout`. | No mide arranque completo, hardware, FPS, latencia física del touchpad ni composición GPU de una ventana visible. |

Los hallazgos son mecanismos observados, no una afirmación de que uno de ellos explique todos los tirones. Las prioridades se ajustarán con mediciones.

## Referencia inicial obtenida ahora

Tres procesos independientes contra el ejecutable firmado 1.4.1, mismo harness y entorno, render a 1400×880:

| Medida | Ejecución 1 | Ejecución 2 | Ejecución 3 |
| --- | ---: | ---: | ---: |
| Construcción y primer render fuera de pantalla | 1497 ms | 541 ms | 517 ms |
| Navegación: 30 reconstrucciones entre seis secciones | 4107 ms | 3893 ms | 3879 ms |
| 100 cambios programáticos de offset/layout | 6 ms | 6 ms | 9 ms |
| CPU del proceso durante 1 s de reposo del harness | 15 ms | 15 ms | 15 ms |
| Working set al final | 110641152 B | 109146112 B | 109072384 B |
| Memoria privada al final | 98226176 B | 96534528 B | 96759808 B |

Estos resultados no demuestran fluidez de una ventana real. No se atribuye la diferencia de la primera ejecución a una causa no medida. El coste de navegación es acumulado, no un percentil ni la latencia de cada clic. Las muestras de memoria no demuestran una fuga o su ausencia.

## Etapa 1 — Medir bien antes de editar

1. Conservar hashes, versiones y una copia inmutable del ejecutable anterior; registrar entorno, tema, escalado, alimentación, preferencia de movimiento y escenario.
2. Ampliar el harness actual únicamente donde falta cobertura: latencia por sección, búsqueda con catálogo real, construcción de tarjetas, tareas pendientes y referencias a elementos retirados. Sin framework de pruebas nuevo.
3. Usar lecturas de sistema simuladas solo en pruebas. Medir por separado la primera respuesta visual, la disponibilidad de datos y la duración del I/O; no sumar todo bajo «arranque».
4. Hacer al menos siete pares antes/después, alternados, en el mismo entorno y escenario. Separar primeras ejecuciones de las posteriores; informar mediana, dispersión y muestras originales. Calcular p95 solo con una serie suficientemente amplia, al menos 100 observaciones del evento estudiado.
5. Para scroll y animaciones: traza o muestreo de ventana visible y compositor en la máquina de referencia, con entradas continuas y giro de dirección. Si no está disponible esa instrumentación, marcar esa parte como pendiente; el benchmark fuera de pantalla no la sustituye.

Entrega: comparación reproducible bajo `artifacts`, sin cambios funcionales ni publicación.

## Etapa 2 — Reducir reconstrucciones al escribir y navegar

Prioridad máxima: el filtro de sección.

- Mantener el `TextBox`, foco, selección, caret y composición de texto mientras cambian resultados. Actualizar solo la colección o el contenedor de tarjetas afectado.
- Agrupar peticiones pendientes y descartar resultados de filtros antiguos. Si las mediciones justifican una espera corta para agrupar pulsaciones, limitarla aproximadamente a 80–120 ms y verificar que no se perciba retraso. No añadirla si actualizar parcialmente ya resuelve el coste.
- Conservar riesgo, categoría, perfil de equipo, selección de apps y desplazamiento con el mismo significado actual.
- Medir navegación por sección antes de retener vistas. Si conviene reutilizar elementos, definir su invalidación por datos/tema y su liberación; no cachear indiscriminadamente árboles visuales completos.
- Usar los actualizadores existentes para estados y contadores; evitar renderizar la pantalla completa por el cambio de una tarjeta.

Pruebas: texto largo, escritura rápida, borrado, pegado, búsqueda vacía, sin resultados, cambio de filtro, navegación durante una lectura y resultado atrasado. Una búsqueda nunca ejecuta una acción del sistema.

## Etapa 3 — Mantener el acabado con menos trabajo de dibujo

- Reutilizar y congelar recursos gráficos que sean realmente inmutables. No congelar pinceles o transformaciones que deban cambiar con tema o animación.
- Reutilizar trazos/geometrías estables y recursos del material; actualizar sus componentes variables sin regenerar toda la decoración.
- Mantener destellos, relieve, contornos y duraciones perceptibles. Comparar capturas equivalentes en oscuro/claro, reposo, hover, foco, pulsación y selección.
- Medir qué tarjetas se dibujan y animan fuera del viewport; limitar trabajo innecesario cuando sea relevante, manteniendo coherencia al volver a mostrarlas.
- Revisar el scroll continuo en `SmoothScrollViewer`: acumulación de entrada, inversión, límites, paso a otro scroll, interrupción por teclado y sincronización de callbacks. No añadir una segunda inercia sobre el movimiento preciso que ya entrega el touchpad.
- Conservar la cuadrícula adaptable. Solo estudiar reciclaje/virtualización si una traza demuestra que las tarjetas fuera de pantalla siguen siendo un cuello de botella; debe mantener alturas variables, accesibilidad y foco.

Pruebas: rueda convencional, precisión pequeña y ráfagas sintetizadas, además de touchpad físico en Windows. No declarar corregido un problema exclusivo del dispositivo basándose solo en eventos sintéticos.

## Etapa 4 — Arranque y trabajo en segundo plano

- Medir catálogos, preferencias, imágenes, construcción del shell, lecturas de hardware, comprobación OTA y render de resultados por separado.
- Quitar del hilo de interfaz únicamente trabajo de CPU/I/O que lo esté bloqueando; crear y modificar controles WPF siempre en su Dispatcher.
- Mantener lecturas compartidas en curso y carga diferida por módulo. No duplicar procesos, solicitudes o suscripciones al navegar.
- Revisar monitor/logs: refrescar solo lo que cambió, no superponer consultas, limitar el trabajo visual de un lote y conservar los datos/registros con el comportamiento actual.
- Probar desconexión, errores y lecturas lentas: la interfaz sigue respondiendo y no inventa progreso o éxito. Mantener controles de reintento y cancelación donde ya corresponden.
- Verificar que minimizar y cerrar detienen tareas decorativas y liberan suscripciones. Medir referencias después de ciclos repetidos; no usar `GC.Collect` periódico ni ajustes de prioridad global para ocultar el problema.

## Etapa 5 — Validación que decide si se entrega

| Área | Condición para aceptar |
| --- | --- |
| Búsqueda y navegación | Mejora reproducible superior al ruido medido en el flujo intervenido. Como objetivo inicial, reducir al menos 20 % el coste de la ruta dominante; si no ocurre, explicar la evidencia y ajustar el cambio, sin anunciar esa cifra como conseguida. |
| Scroll visible | Sin acumulación creciente de retraso ni saltos al invertir dirección; comparar tiempos de frame e interrupciones con la misma entrada y pantalla. Una meta de frame depende de la frecuencia del monitor; no prometer «60 FPS» sin traza. |
| Reposo y memoria | Sin crecimiento sostenido de tareas, referencias o memoria después de estabilizar al menos 100 ciclos de navegación. No aceptar una regresión de CPU/memoria repetida fuera de la variación de referencia; investigar una subida superior al 10 %. |
| Diseño | Mismos materiales, contraste, texto, avisos y jerarquía. Capturas y revisión visible de ambos temas; diferencias decorativas solo si conservan la apariencia acordada. |
| Accesibilidad | Teclado, Ctrl+K, Escape, foco visible, navegación de resultados y movimiento reducido sin regresiones. |
| Ventana y escala | Tamaño habitual, mínimo actual y 1366×768; escalados 125/150/200 % con configuración real cuando esté disponible. Sin recortes ni scroll horizontal accidental. |
| Funciones | Todas las secciones y filtros; aplicar/revertir, apps, auditoría, restauración, consola y monitor con I/O simulado para acciones destructivas. Mismos comandos, payloads y manejo de errores. |
| Integración | Compilador aislado, recursos locales y versión coherentes. Pruebas existentes más regresiones pequeñas para la lógica modificada. Repetir solo los checks afectados durante el trabajo y el conjunto final antes de entregar. |
| OTA | Firma, tamaño, hash y versión del paquete nuevo; errores de red, cancelación, instalación y recuperación sobre copias de prueba. Verificar después los archivos descargados del servidor publicado. |

Reutilizar `Test-Ui.ps1`, `Test-NativeBuild.ps1`, `Test-Ota.ps1`, las pruebas HTTPS/Postman y el escáner de publicación. Los resultados anteriores sirven de referencia, no cuentan como una ejecución de esta futura versión.

## Etapa 6 — Entrega y publicación de la nueva versión

1. Proponer `1.4.2` como siguiente versión, comprobando antes que no exista ni haya otra entrega posterior. No modificar la release 1.4.1 ya publicada.
2. Revisar el diff completo y el estado de Git. Preservar trabajo local y separar qué archivos pertenecen a la entrega. Escanear contenido y archivos que se van a subir: nunca `.pfx`, `.dpapi`, claves privadas, temporales, logs del equipo ni artefactos de prueba.
3. Crear un commit revisado con las fuentes necesarias y las pruebas/documentación de la entrega, sin añadir trabajo ajeno al alcance. La etiqueta nueva debe apuntar a esas fuentes, no al código remoto antiguo.
4. Actualizar el metadato de versión, compilar el portable y preparar un paquete firmado e inmutable con `Prepare-Release.ps1`. No reescribir scripts de seguridad, URLs, permisos o claves para obtener rendimiento.
5. Emitir informe antes/después con hashes, entorno, datos originales, capturas, checks pasados y pendientes. Si falta la validación visual/touchpad necesaria, mantener el paquete en evaluación y no anunciar el problema como resuelto.
6. Publicar primero el ejecutable exacto en la nueva release; descargarlo y verificarlo. Después activar el manifiesto firmado. Conservar la clave pública existente y los respaldos; no volver a compilar el asset tras firmarlo.
7. Probar instalación/reinicio y versión mostrada desde una copia 1.4.1 con el servidor real. La publicación se considera validada cuando coincide el asset y se completa esa prueba, no cuando GitHub acepta la subida.

La publicación corresponde a una etapa posterior a implementar y validar este plan. Este documento no hace cambios remotos ni autoriza reemplazos sobre aplicaciones en uso.

## Orden de trabajo y condición de cierre

Medición → filtro estable → actualizaciones parciales → materiales/scroll → cargas/vida útil → regresiones visuales y operativas → paquete firmado → publicación y prueba real.

Cerrar cuando existan mejoras medidas en los flujos prioritarios, se conserve el diseño, pasen los checks pertinentes y el informe distinga lo comprobado de lo pendiente. No seguir añadiendo optimizaciones sin beneficio demostrado.

## Fuentes técnicas

- [Microsoft: layout y diseño WPF](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/optimizing-performance-layout-and-design): coste de reconstrucción/layout y uso de transformaciones.
- [Microsoft: comportamiento de objetos WPF](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/optimizing-performance-object-behavior): recursos Freezable y notificaciones.
- [Microsoft: controles WPF](https://github.com/dotnet/docs-desktop/blob/main/dotnet-desktop-guide/wpf/advanced/optimizing-performance-controls.md): virtualización y reciclaje, condicionados al control y al escenario.

Limitaciones actuales: no se midieron FPS de ventana visible, entrada física del touchpad, arranque completo ni otro equipo. La etapa de planificación no demuestra todavía una mejora de rendimiento.
