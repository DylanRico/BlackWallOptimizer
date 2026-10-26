# Blackwall 1.4.4: fluidez y actualización firmada

Fecha: 26 de octubre de 2026. El usuario aprobó físicamente el scroll, el modo claro y el aviso vertical de la candidata 1.4.3. La versión 1.4.4 conserva esos cambios y elimina el origen OTA editable, incluida la sustitución por un archivo de configuración local.

## Implementación

- `native-wpf/BlackwallApp.cs`: un único seguimiento del scroll por frames, deltas proporcionales, acumulación, inversión desde el offset visible, cancelación y movimiento reducido. No se crean animaciones por cada evento del touchpad; el progreso usa un offset propio para no depender del layout diferido.
- La navegación usa una sola transición de 3 px/135 ms. Los tooltips no se fuerzan al recibir foco y se cierran al pulsar una categoría.
- El sondeo de procesos es silencioso, se pausa al minimizar y mantiene la vista cuando no cambian los procesos. La RAM se actualiza sobre los controles existentes. Los errores conservan su aviso contextual y reintento.
- Modo claro de plata mate: fondo `#D1D6DE`, superficie `#DFE3E8`, relieve superior `#E3E6EB`, texto `#24303E` y acento `#873A36`. Menos luminosidad en las superficies y reflejos, conservando texto oscuro legible.
- UAC una vez por apertura normal; cancelar el consentimiento cancela el arranque. Se retiró el botón fijo de administrador.
- Actualización vertical con marca local, bisel, notas desplazables y acciones fijadas al pie. Una invitación por versión durante la sesión, sólo tras verificar una versión posterior. «Más tarde», Escape y cierre posponen.
- El canal proviene del metadato embebido. No hay campo editable ni lectura de `ota_manifest_url_v2.txt`; las configuraciones antiguas quedan ignoradas. Las firmas y los hashes siguen siendo la protección de autenticidad, además de restringir la configuración visible.
- La UI elevada entrega la instalación al shell interactivo para que el instalador existente siga sin elevación. Conserva firma, tamaño, hash, versión, bloqueo, cancelación, respaldo y rollback. La nueva app confirma su salud antes de solicitar UAC para la reapertura normal.
- `.gitignore` y el escáner excluyen todos los ejecutables del código fuente. El portable raíz permanece local; los paquetes se distribuyen como assets firmados de releases.

## Pruebas realizadas

| Comprobación | Resultado | Alcance |
| --- | --- | --- |
| UI WPF | 136 checks y 29 capturas | Seis secciones, búsqueda, filtros, selección, foco, estados, fallos y acciones simuladas; aplicados también al binario firmado 1.4.4. |
| Frontend histórico | 16 pruebas | Sin cambios en los contratos web existentes. |
| Compilador | 12 checks | Fixture aislado: recursos, versión, icono y conservación del ejecutable anterior. |
| OTA | 44 checks | Protocolo, firma, alteraciones, transacciones y subprocesos sobre copias desechables. |
| Shell de Windows | Aprobado | Lanza un probe sin elevación mediante el shell interactivo. El padre de esta prueba también era estándar: no demuestra por sí sola el traspaso desde una sesión elevada. |
| Contraste | Aprobado | Los pares de texto comprobados superan 4,5:1 en ambos temas. Texto principal/secundario sobre la superficie elevada clara: aproximadamente 8,10:1 y 4,68:1. |
| Configuración OTA | Aprobado | No hay TextBox editable ni Expander de origen en el aviso productivo. |
| Publicación | Revisión de patrones | Archivos generados, certificados, claves privadas, credenciales, rutas personales y logs excluidos; no equivale a una garantía absoluta de ausencia de secretos. |

Las capturas son renders WPF reales fuera de pantalla. Los porcentajes 125/150/200 del harness son render a DPI simulado, no cambios del escalado físico de Windows. No se ejecutaron tweaks, instalaciones de software, reinicios, terminación de procesos del usuario ni una actualización sobre su copia.

## Comparación del scroll

Siete pares alternados, mismo equipo/harness, catálogo real de optimización y 160 entradas sintéticas por ejecución. Referencia: `artifacts/Blackwall-fluid-v2.preview.exe`. Revisión medida: 1.4.3 con el mismo algoritmo y paleta conservados en 1.4.4; esta última añade la retirada del origen editable y ajustes de copia. Datos crudos: `artifacts/scroll-input-benchmark.json`.

La tabla muestra la mediana de los p95 de cada ejecución, excepto CPU y duración, que son medianas de totales.

| Medida | Antes | Después | Lectura |
| --- | ---: | ---: | --- |
| Handler de entrada, p95 | 0,131 ms | 0,031 ms | 76,3 % menos tiempo en el handler. |
| Paso de offset por evento de render, p95 | 14,400 px | 10,675 px | Pasos 25,9 % menores en este recorrido; no es una medida universal de suavidad. |
| Intervalo entre eventos WPF de render, p95 | 18,393 ms | 18,177 ms | Prácticamente estable. No demuestra FPS visibles/GPU. |
| CPU durante el recorrido | 796 ms | 812 ms | +2,0 %; variación entre ejecuciones de 625–875 y 703–1031 ms. No se afirma reducción de CPU. |
| Duración del recorrido y asentamiento | 2511 ms | 2614 ms | La interpolación añade alrededor de 103 ms al asentamiento final del recorrido sintético. |

El usuario informó que la candidata 1.4.3 ya no se trababa y, después del segundo ajuste, que el scroll y el modo claro habían mejorado mucho. Esa evaluación física es distinta del benchmark sintético.

## Construcción, navegación y búsqueda

Se ejecutaron dos series de siete pares alternados con `Measure-VisualStartup.ps1`. La primera conservó construcción y navegación próximas a la referencia (medianas 292→291 ms y 2430→2421 ms). La segunda presentó más variación: construcción 311→314 ms, navegación 2585→2724 ms y búsqueda de 108 cambios 1774→1886 ms. En esta segunda serie el equipo continuaba en uso interactivo; los rangos de navegación se solapan ampliamente (2458–3336 frente a 2440–2889 ms).

No se atribuye a esta iteración una mejora general de arranque, navegación o búsqueda; tampoco se demuestra una regresión causal con esas series. Los benchmarks no incluyen UAC, hardware WMI de arranque ni el compositor visible. Ambas series mantuvieron cero reemplazos de página al escribir. Evidencia: `artifacts/fluidity-1.4.3/visual-startup-firstpass.json` y `artifacts/visual-startup-benchmark.json`.

## Paquetes y prueba manual

- Nuevo paquete inmutable: `artifacts/releases/1.4.4/Blackwall.exe` y su `update.json` firmado.
- SHA-256 del binario 1.4.4: `cde6017dc415e7afb243abdc56011d9adbaef665c877d4f6e7ff25bff9804e71`.
- Copia anterior para probar OTA: `%USERPROFILE%\Desktop\Blackwall-Prueba-OTA-1.4.3\Blackwall.exe`, versión 1.4.3.0. Se conserva separada del portable nuevo.
- Cerrar las instancias antiguas y abrir esa copia anterior cuando la publicación esté confirmada. El aviso debe ofrecer 1.4.4. Elegir «Actualizar ahora» es una decisión del usuario; no se automatiza esa instalación en su escritorio.
- La versión nueva del escritorio debe coincidir por SHA-256 con el asset firmado, no con una recompilación posterior.

## Límites y revisión pendiente

La evaluación física de la apariencia y del gesto se hizo por el usuario sobre 1.4.3. No se verificó automáticamente una aceptación UAC real ni la secuencia completa desde una UI elevada, pasando por el shell, hasta un reinicio posterior a OTA desde GitHub. El helper nativo de control del escritorio no estaba disponible. Los checks de instalación/reversión usan copias desechables; no sustituyen la prueba manual solicitada. Si el Explorador está elevado o no está disponible, el helper rechaza instalar elevado.

El vídeo original de referencia no estaba disponible para reproducirlo otra vez. Se consultó el repositorio público y se usaron los rasgos visuales ya descritos, sin afirmar que se identificó su biblioteca o sus tiempos.

## Estado de publicación

Publicados las fuentes y la etiqueta `v1.4.4` (commit de producto `50657fceab2c82b41668b47fc3af3d6a64c0b735`), la [release 1.4.4](https://github.com/DylanRico/BlackWallOptimizer/releases/tag/v1.4.4) y sus dos assets: `Blackwall.exe` y `update.json`. Después de verificar el binario público se publicó el mismo manifiesto, sin modificar sus bytes, en `catalogs/update.json` de `main` (commit `8edfd43`).

El cliente C# nativo de la copia 1.4.3 consultó la URL predeterminada real, verificó la firma de 1.4.4, detectó la versión posterior, descargó los 1.246.208 bytes y verificó versión PE y SHA-256. El manifiesto público y el paquete firmado local coinciden exactamente. No se ejecutó el instalador sobre el escritorio del usuario.

La copia nueva `%USERPROFILE%\Desktop\Blackwall.exe` se verificó como 1.4.4.0 y con el SHA-256 del asset publicado. La copia anterior de prueba sigue siendo 1.4.3.0. El escáner de patrones del binario público no encontró claves privadas, tokens GitHub ni rutas personales. El conector de GitHub no ofrecía carga de assets de releases; la publicación de esos assets se realizó con la API oficial y la credencial de Git ya configurada, sin guardarla en archivos.
