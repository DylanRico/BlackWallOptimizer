# Plan de acción — Blackwall / Muro Negro

**Revisión de movimiento ampliada:** [ANIMACIONES-MURO-NEGRO.md](ANIMACIONES-MURO-NEGRO.md). Se incorporaron contracción progresiva, cascadas de tarjetas, reflejos y circuitos rojos; se revisó la ventana real y pasaron 68 comprobaciones nativas y 15 pruebas web. FPS/GPU y escalas múltiples continúan sin medición.

**Estado: aprobado por el usuario e implementado. Validación automatizada completada; revisión en ventana visible y rendimiento GPU/FPS pendientes por límites del entorno.** Véase [ENTREGA-MURO-NEGRO.md](ENTREGA-MURO-NEGRO.md) para resultados, ejecutable y respaldo.

Fecha de consulta: 24 de octubre de 2026, America/Bogota.

La dirección aprobada es una interfaz minimalista, negra y carmesí, con navegación contraíble, transiciones y efectos de energía roja discretos. Cada ajuste presenta su efecto, riesgo y explicación de reversión. El plan siguiente conserva la investigación y los criterios originales; la implementación y sus límites de comprobación están registrados en el informe de entrega.

## 1. Qué encontré sobre CeitoTweaks

La coincidencia localizada para «Seito Tweaks» es **CeitoTweaks**, de `ceitooo`. Su README indica que la aplicación está hecha con Electron y que el código fuente es privado. El repositorio público contiene README, licencia y un logotipo, además de las descargas en Releases. Documenta fondos animados, temas Carmesí/OLED y un modo liviano que desactiva efectos. Esto permite confirmar funciones anunciadas, pero no identificar su biblioteca de animación ni examinar su implementación. [Repositorio oficial](https://github.com/ceitooo/ceitotweaks).

La versión **2.0.1**, publicada el 24 de octubre, aporta información concreta: menciona botones de metal líquido, partículas de fondo, brillo en botones y gráficos en vivo. Sus notas describen una corrección para limitar los redibujados, mejorar el desplazamiento en monitores de 144 Hz o más y mantener la velocidad de las partículas entre monitores. Son afirmaciones del desarrollador, no mediciones realizadas aquí. [Notas oficiales de CeitoTweaks 2.0.1](https://github.com/ceitooo/ceitotweaks/releases/tag/v2.0.1).

**Aplicación al plan:** adoptar respuestas visuales cuidadas, brillo localizado y movimiento ambiental contenido. Las duraciones y efectos definidos abajo son propuestas para Blackwall; no se presentan como una reproducción exacta de CeitoTweaks. No se ejecutó su instalador ni se observaron sus animaciones en funcionamiento.

## 2. Base actual de Blackwall

La revisión local confirma:

- El ejecutable principal usa WPF/.NET Framework; también existe el frontend web/Tauri.
- Hay un contenedor común para las secciones, navegación de ancho fijo de 255 unidades y desplazamiento suavizado de 260 ms.
- Ya existe una transición de opacidad al cambiar de sección y soporte para movimiento reducido.
- Las tarjetas ya disponen de título corto, resumen, detalles, beneficios y advertencias desplegables. El catálogo contiene 45 ajustes.
- El ejecutable actual pesa **1.063.424 bytes**, aproximadamente **1,06 MB**. Es la referencia para medir el crecimiento posterior.

Se aprovecharán estos componentes. El maximizado que respeta la barra de tareas, la carga bajo demanda, los indicadores reales de actividad y el manejo de errores forman parte de los criterios de conservación.

## 3. Identidad visual propuesta

| Elemento | Color inicial | Uso |
|---|---|---|
| Fondo | `#08080B` | Negro casi puro, sin ruido detrás del contenido |
| Superficies | `#111114` | Tarjetas y paneles separados por espacio y bordes discretos |
| Rojo profundo | `#8F1828` | Botones principales, zonas seleccionadas y energía ambiental |
| Rojo luminoso | `#EF6878` | Foco de teclado, acentos pequeños y enlaces sobre oscuro |
| Texto principal | `#F3F4F6` | Títulos y contenido esencial |
| Texto secundario | `#B8BBC4` | Descripciones, ayudas y metadatos |

Los colores son una interpretación propuesta del Muro Negro, no una paleta oficial extraída del juego. El amarillo y el cian dejarían de ser los acentos dominantes del tema oscuro.

El contraste calculado de estas combinaciones opacas es 17,13:1 para texto principal sobre superficie, 9,82:1 para texto secundario y 8,19:1 para texto claro sobre el botón carmesí. El rojo profundo sobre negro solo alcanza 2,22:1: por ello se reserva para superficies y decoración; los límites interactivos y el foco usarán un tono más visible. Se verificarán también los estados hover, pulsado, deshabilitado y el tema claro. La referencia de contraste para texto normal será al menos 4,5:1. [W3C: contraste mínimo](https://www.w3.org/WAI/WCAG22/Understanding/contrast-minimum.html).

Los errores y riesgos tendrán siempre un icono y un texto explícitos. El rojo decorativo de la marca no será la única señal para identificar un problema.

## 4. Navegación y organización

**Barra lateral con dos estados:**

- Expandida: aproximadamente 248 unidades, icono y nombre de cada sección.
- Compacta: aproximadamente 72 unidades, iconos reconocibles, indicador de sección activa y nombre al pasar el ratón o enfocar con teclado.
- Botón permanente «Contraer menú / Expandir menú», accesible por teclado y lector de pantalla.
- Recordar la elección al cerrar y abrir la aplicación. Primera apertura expandida.
- Al contraer, ocultar los títulos de grupos y el bloque informativo inferior, conservando accesibles las seis secciones.
- Mantener sección, filtros, selección y posición de desplazamiento al cambiar el tamaño del menú.

**Vistas más despejadas:** encabezados cortos, una acción principal claramente identificada y acciones secundarias agrupadas. Se revisarán los bloques introductorios grandes, la repetición de etiquetas, los emojis y los identificadores técnicos que hoy compiten con el contenido. Se mantendrá una iconografía coherente.

El contenido ganará espacio al contraer el menú. La reorganización de las tarjetas se resolverá al cambiar de estado; las transiciones evitarán recalcular toda la lista en cada fotograma.

## 5. Animaciones propuestas

Estas duraciones son puntos de partida para calibrar en la aplicación real.

| Interacción | Tratamiento | Duración inicial |
|---|---|---|
| Expandir o contraer menú | Deslizamiento del panel, entrada/salida de rótulos y transición del contenido | 220 ms |
| Cambiar de sección | Opacidad y desplazamiento de 6–10 unidades; controles disponibles sin esperar a que termine | 180–220 ms |
| Pasar el ratón o enfocar | Borde más luminoso y brillo corto sobre el botón | 120–160 ms |
| Pulsar un botón | Respuesta pequeña e inmediata, sin mover su área clicable | 80–120 ms |
| Desplegar detalles | Apertura progresiva de una sola tarjeta, conservando el foco | 160–200 ms |
| Finalizar una operación | Cambio breve del icono/estado una vez confirmado el resultado | 160–220 ms |
| Energía del Muro Negro | Uno o dos filamentos rojos en el logo o franja superior, con recorrido lento y pulso contextual | Ciclo de 4–6 s; pulso de unos 600 ms |

Los filamentos no atravesarán texto, controles ni avisos. Se usarán trazados vectoriales reutilizados, con brillo de baja intensidad y sin destellos bruscos. El brillo tipo metal líquido se interpretará como un barrido localizado durante la interacción.

Las animaciones nuevas interrumpirán correctamente las anteriores cuando el usuario actúe rápido. Los efectos decorativos se detendrán al minimizar o desactivar la ventana. Un control sencillo de movimiento reducido, junto con la preferencia de Windows, permitirá desactivar los efectos no esenciales. [W3C: animación por interacción](https://www.w3.org/WAI/WCAG22/Understanding/animation-from-interactions).

## 6. Textos y decisiones comprensibles

Cada tarjeta seguirá una jerarquía estable:

1. **Nombre corto:** qué ajuste se está viendo.
2. **Resumen de una o dos líneas:** qué cambia en el uso del equipo.
3. **Estado explícito:** aplicado, sin aplicar, comprobando o no disponible.
4. **Riesgo y consecuencia principal:** visibles antes de actuar cuando sean relevantes.
5. **Acción clara:** «Aplicar» o «Revertir», según el estado confirmado.
6. **«Ver efecto y reversión»:** explicación completa, beneficios condicionados, compatibilidad y detalles técnicos.

Ejemplo de redacción propuesta para un ajuste existente:

> **Limitar Wi-Fi durante la suspensión**\
> Reduce la actividad de red cuando el portátil está suspendido y usa batería.\
> **Ten en cuenta:** puede afectar tareas que necesitan conexión durante la suspensión.\
> **Estado:** sin aplicar · **Acción:** Aplicar\
> Ver efecto y reversión

La redacción se contrastará con las operaciones reales del catálogo. Se evitarán promesas de rendimiento garantizado y nombres que hagan ambiguo si el interruptor activa una función o activa su bloqueo. Una advertencia importante no quedará escondida dentro de los detalles. La interfaz solo mostrará éxito después de confirmarlo; si una reversión no puede garantizar el estado original, lo explicará.

## 7. Implementación y dependencias

**Ruta recomendada: ampliar la interfaz WPF actual.** `Storyboard`, `DoubleAnimation`, transformaciones, gradientes y trazados vectoriales cubren las transiciones, el menú y los filamentos propuestos. La base web podrá mantener el mismo diseño con CSS y sus capacidades de animación existentes.

Se priorizarán opacidad y `RenderTransform` para el movimiento, reutilizando los elementos ya creados. Microsoft recomienda reducir los recálculos de disposición y actualizar las transformaciones existentes cuando sea posible. [Microsoft: rendimiento del diseño WPF](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/optimizing-performance-layout-and-design).

**Dependencias iniciales propuestas: ninguna nueva.** La autorización del usuario permite incorporarlas si simplifican de forma demostrable un efecto necesario. En ese caso se comprobarán compatibilidad con el entorno actual, licencia, mantenimiento y peso añadido antes de seleccionar una. El plan no presupone migrar el motor de la aplicación. El crecimiento final se medirá al compilar; todavía no existe una cifra verificada.

## 8. Orden de ejecución después de la aprobación

| Fase | Trabajo | Criterio de cierre |
|---|---|---|
| 1. Referencia y muestra visual | Guardar respaldo, medir arranque/consumo/tamaño y preparar una pantalla representativa con ambas variantes del menú | Propuesta negro/carmesí coherente y texto legible |
| 2. Estructura y navegación | Implementar menú contraíble, iconos, foco, etiquetas y persistencia de preferencia | Las seis secciones funcionan en ambos estados sin perder contexto |
| 3. Jerarquía de contenido | Aplicar superficies, tipografía, encabezados y redacción gradual a tarjetas y estados | El usuario entiende el efecto, riesgo y reversión antes de actuar |
| 4. Movimiento | Incorporar transiciones, brillo localizado y filamentos; conectar movimiento reducido | Interacciones rápidas no acumulan animaciones ni bloquean controles |
| 5. Coherencia web/Tauri | Adaptar la presentación y los estados del frontend paralelo | Misma jerarquía y comportamiento de navegación con sus controles de accesibilidad |
| 6. Verificación y entrega | Probar, revisar visualmente, comparar medidas y generar el ejecutable con respaldo | Evidencia de resultados, límites conocidos y procedimiento de reversión |

## 9. Cómo se comprobará

- Navegación expandida/compacta, persistencia, foco de teclado y acceso a todas las secciones.
- Tamaño mínimo de ventana y escalas de Windows de 100 %, 125 %, 150 % y 200 %, en las configuraciones disponibles; ninguna pérdida de controles ni textos esenciales.
- Maximizado con barra de tareas visible, scroll suave, cambios rápidos de dirección y cancelación de movimientos al navegar.
- Carga, éxito, error y reintento, conservando estados reales y protecciones de las acciones del sistema.
- Tema oscuro/carmesí y tema claro, contraste de texto y foco, y señales de riesgo reconocibles sin depender solo del color.
- Movimiento reducido y pausa de los efectos cuando la ventana no esté activa.
- Medición en ventana visible: navegación, menú, detalles y desplazamiento. **Objetivo inicial: movimiento estable equivalente a 60 FPS en el equipo de referencia**, a comprobar; la velocidad de los efectos no debe depender de los Hz del monitor.
- Comparar arranque, CPU/GPU en reposo y durante interacción, memoria y tamaño del ejecutable con la referencia de la fase 1. Investigar cualquier empeoramiento; no atribuir velocidad o ahorro sin mediciones comparables.
- Reutilizar las comprobaciones nativas y web existentes y añadir pruebas solo para navegación compacta, persistencia, accesibilidad y gestión de animaciones. La revisión visual en una ventana real será un paso explícito, además del renderizado fuera de pantalla.

## 10. Punto de aprobación

Se propone aprobar esta dirección: **WPF como base, negro y carmesí, menú contraíble, contenido progresivo y efectos rojos contenidos**. La ejecución empezará tras la conformidad del usuario, como solicitó expresamente. La solicitud de revisión proviene de su instrucción, no de un requisito de las herramientas o habilidades.

El usuario autorizó ejecutar el plan y generar el icono rojo inspirado en David Martínez. Se entregó el ejecutable WPF recompilado con menú contraíble, contenido progresivo, movimiento reducido y logo integrado, sin nuevas dependencias. Pasaron 59 comprobaciones nativas y 14 pruebas web. El ejecutable aumentó 109 568 bytes. No se verificaron las animaciones privadas de CeitoTweaks ni los FPS de Blackwall en una ventana visible; los bloqueos y medidas disponibles se detallan en el informe de entrega.
