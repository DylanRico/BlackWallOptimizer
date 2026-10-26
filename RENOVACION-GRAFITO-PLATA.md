# Blackwall · renovación grafito, plata y brasa

> Este informe describe la renovación visual y la corrección inicial del compilador/scroll. Posteriormente se implementó OTA firmada y el cliente pasó a `1.4.0`; los detalles vigentes de versionado, claves, catálogos y publicador están en `OTA-ACTUALIZACIONES.md`.

Entrega local del 25 de octubre de 2026, actualizada con compilación unificada y corrección del touchpad. El ejecutable raíz se recompiló con respaldo por petición del usuario. Sin publicación, push ni firma.

## Qué ejecutable utiliza esta interfaz

El portable `Blackwall.exe` es **WPF sobre .NET Framework**, no el frontend de Tauri. El recorrido verificado es:

`Program.Main` → `Application.Run(MainWindow)` → catálogos embebidos y preferencias → `BuildShellUI` → `Loaded` → lectura de hardware → `RenderCurrentTab` → constructores de cada sección y sus handlers existentes.

`Build-Portable-Exe.bat` compila `native-wpf/BlackwallApp.cs` con `Framework64/v4.0.30319/csc.exe`, referencias WPF, catálogos e iconos. La renovación está integrada en esa interfaz. No se modificaron HTML/CSS/JS, Rust, catálogos, comandos del sistema, elevación, restauración ni implementación OTA.

El ejecutable raíz ya tenía cambios locales al empezar la renovación. La primera entrega lo conservó byte por byte en `artifacts/graphite-baseline/Blackwall.user.exe`. Tras la petición de corregir el compilador, se generó un nuevo `Blackwall.exe`; el reemplazo conserva además el ejecutable anterior en `artifacts/build-backups/`.

**`Build-Portable-Exe.bat` actualiza ahora `Blackwall.exe` con el código, diseño y recursos locales actuales.** Compilación normal, preview y publicador comparten `scripts/Build-Native.ps1`. `Start-Blackwall.bat` abre primero el portable raíz; Rust/Node quedan como alternativas si este falta. La verificación se ejecutó tanto sobre el preview como sobre el nuevo ejecutable raíz.

## Corrección del touchpad

Una prueba de 60 eventos de delta −6 reproducía el reinicio de animaciones de 260 ms. El control compartido `SmoothScrollViewer` ahora procesa deltas fraccionarios y ráfagas separadas por menos de 40 ms como entrada continua: acumula distancias pendientes entre fotogramas sin añadir otra interpolación a la inercia del dispositivo. La rueda por pasos aislados conserva el suavizado; cambiar de dirección responde inmediatamente en entrada continua. Movimiento reducido también conserva deltas pequeños.

Se retiró la búsqueda y animación de tarjetas recién visibles en cada evento de scroll. Las entradas de sección y los reflejos interactivos siguen activos. Las pruebas cubren acumulación antes del layout, inversión, transición entre pasos y ráfagas y movimiento reducido. Esto corrige el mecanismo reproducido con eventos sintéticos; queda pendiente la comprobación física del touchpad porque el servicio de escritorio no está disponible.

La base de entrada está documentada por Microsoft: [WM_MOUSEWHEEL permite deltas menores que 120 y desplazamientos parciales](https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-mousewheel).

## Cambios integrados

- Materiales centralizados en `UpdateThemeBrushes`: grafito, plata y acento rojo cálido, con paletas propias para oscuro y claro.
- Botones cápsula con bisel degradado asimétrico, reflejo superior, centro oscuro o plata, estados de pulsación, foco inmediato y deshabilitado. Destello de 520 ms limitado al contorno del control interactuado; las capas decorativas no reciben clics.
- Tarjetas redondeadas con filo degradado y reflejos cortos al interactuar. Cuadrícula adaptable desde una columna, alrededor de 310 DIP por tarjeta, con alturas calculadas por fila. Acciones al pie, descripciones completas y riesgos visibles fuera de los detalles.
- Rail estable de 72 DIP y menú ampliado de 248 DIP superpuesto, con botón, cierre con Escape, clic fuera y cierre al navegar. No reconstruye ni redimensiona el catálogo al expandirse. Conserva las preferencias existentes; el valor inicial nuevo es compacto.
- Búsqueda global existente con nombre accesible, flechas, Enter, Ctrl+K, Escape, selección y estado sin resultados. Navega al ajuste o aplicación concreto; abrir software desde una búsqueda no lo selecciona para instalarlo. Utiliza los catálogos existentes en memoria.
- Filtro de texto en las secciones de ajustes y software; conserva riesgo, categoría y selección. Las actualizaciones de datos conservan el desplazamiento.
- Dashboard con auditoría puntual, estados consultados del catálogo, recuperación y accesos existentes a ajustes, software, restauración, logs y tareas. Muestra pendientes cuando falta la lectura. No añade sensores, gráficos simulados ni porcentajes de optimización.
- Modal OTA con contenido desplazable, propietario, Escape y devolución del foco. La versión mostrada procede del ensamblado. Se corrigió el texto que confundía SHA-256 con una firma del editor; no se alteró el actualizador.
- Cambios de sección de 210 ms y hasta 6 DIP, entradas cortas sin rebotes, scroll suave existente y preferencia de movimiento reducido. Red estática local muy tenue; los circuitos y el halo dejaron de animarse continuamente.
- Aplicar/revertir mantiene el catálogo y el indicador `apply` originales. La interfaz bloquea ejecuciones repetidas del mismo ajuste pendiente y conserva los errores recibidos. Las acciones por lotes y creación de restauración tienen guardas de interfaz contra clics repetidos.

No se añadieron paquetes, servicios, fuentes remotas, framework ni runtime.

## Cómo compilar y revisar sin tocar el portable

Desde la raíz del proyecto, en PowerShell:

```powershell
powershell -NoProfile -File .\scripts\Build-Preview.ps1
.\artifacts\Blackwall.preview.exe
```

La compilación usa el mismo compilador y recursos locales del portable. Solo permite salidas `.exe` dentro de `artifacts`; no mata procesos, solicita elevación, firma ni crea certificados. Se comprobó el rechazo de una salida hacia `Blackwall.exe` y una compilación en una ruta con espacios.

Para repetir los checks sin ejecutar operaciones de sistema:

```powershell
powershell -NoProfile -File .\scripts\Test-Ui.ps1
```

`-ScanHardware` añade una auditoría real de solo lectura. Las imágenes y resultados quedan en `artifacts`, excluido de Git. Abrir manualmente el preview inicia la ruta productiva normal; las operaciones de sistema siguen siendo reales si el usuario las activa.

## Revisión de los archivos de compilación

| Archivo | Resultado |
| --- | --- |
| `scripts/Build-Native.ps1` | Compilación común desde los archivos locales actuales. Embebe todos los PNG y catálogos, verifica sus hashes y sustituye la salida con respaldo tras compilar. |
| `scripts/Build-Preview.ps1` | Delega en la compilación común y restringe la salida a `artifacts`. |
| `scripts/Test-Ui.ps1` | Ejecuta el harness WPF, las pruebas web y 11 pruebas aisladas del compilador. |
| `Build-Portable-Exe.bat` | Compilado y comprobado sobre el ejecutable raíz. Usa el compilador común; no cierra procesos ni crea certificados. |
| `Publish-Release.bat` | Ambas compilaciones delegan en el mismo BAT y comprueban su resultado; la omisión del logo queda eliminada. Conserva firma y manifiesto históricos. No se ejecutó ni se publicó una versión OTA. |
| `Start-Blackwall.bat` | Prioriza `Blackwall.exe` raíz, conservando la solicitud de administrador. Rust/Node quedan como alternativas si falta. No se ejecutó el lanzador durante las pruebas. |
| `package.json` / Tauri | Ruta alternativa conservada. No se compiló Rust en esta tarea; su versión declarada sigue siendo independiente del ensamblado WPF. |

El `Blackwall.exe` raíz comprobado está **sin firma Authenticode**. El preview también se entrega sin firma. No se cambiaron certificados ni permisos.

## Comprobación

- Compilación real con el compilador antiguo de .NET Framework, sin sintaxis ni dependencias modernas añadidas.
- Harness nativo: seis secciones, tamaño habitual y mínimo de 1080×700 DIP, 1366×768, temas, controles de carga/error/reintento, menú, búsqueda con teclas y sin resultados, filtros, selección y guardas de acciones.
- Aplicar/revertir y fallos de tareas comprobados con delegates de prueba: misma referencia al catálogo, `apply=true/false`, una ejecución mientras está pendiente, sin éxito inventado si devuelve una excepción. Estas pruebas **no invocan el motor de escritura real**.
- Contraste calculado ≥4,5:1 para texto principal/secundario sobre superficie elevada, los tres niveles de riesgo y acento sobre superficie, en ambas paletas.
- Render WPF a 125/150/200 % para revisar rasterización, conservando las dimensiones lógicas mínimas. Esto no equivale a cambiar el DPI de Windows.
- Consulta real de hardware de solo lectura completada en aproximadamente 5,6 segundos en este equipo. No se aplicaron ajustes, instalaron apps, crearon restauraciones, terminaron procesos ni ejecutaron actualizaciones OTA.
- Se corrigió una fragilidad del harness previo: muestrear un fotograma intermedio dependía del planificador; desmontar su presentación invalidaba controles en las capturas siguientes. Ahora comprueba los relojes y su cancelación, y desmonta esa presentación al final.

Resultado de la corrección de compilación y scroll: **106 checks nativos sobre el preview y el ejecutable raíz, 16 pruebas web y 11 pruebas aisladas del compilador**. Estas últimas verifican rutas con espacios, incorporación automática de recursos nuevos, código visual actual, conservación del ejecutable ante fallo de compilación o bloqueo, respaldo exacto y unificación de rutas. Logs: `artifacts/compiler-scroll/`. Capturas del control WPF realmente renderizado, no diseños dibujados: `artifacts/native/`. Las capturas de módulos sin lecturas utilizan el harness y estados controlados; `native-hardware-live*.png` corresponde a la auditoría real anterior y debe mantenerse local por sus identificadores de equipo. El popup y el contenido del modal se capturan por separado porque son superficies distintas.

## Rendimiento observado en la renovación inicial

Medianas de tres pasadas alternadas en el mismo entorno, comparando la compilación inicial local con la renovación. Construcción/render fuera de pantalla, 30 reconstrucciones de sección y 100 desplazamientos programáticos; sin I/O de sistema ni ventana visible. Para reposo se espera un segundo a que terminen callbacks y entradas y se muestrea otro segundo.

| Medida | Inicial | Renovación |
| --- | ---: | ---: |
| Construcción + primer render | 477 ms | 480 ms |
| CPU de construcción | 437 ms | 468 ms |
| 30 reconstrucciones de sección | 3418 ms | 3835 ms |
| 100 desplazamientos programáticos | 18 ms | 14 ms |
| CPU en la muestra de reposo | 31 ms | 0 ms |
| Memoria residente tras el recorrido | 102,5 MiB | 105,1 MiB |
| Ejecutable aislado | 1 207 808 bytes | 1 223 680 bytes |

El coste adicional observado se concentra en reconstruir controles y materiales: unas 14 ms más por sección en este recorrido. No se añadieron animaciones ambientales para limitar ese coste. Un valor de CPU redondeado a cero en una muestra no significa consumo cero. Estas cifras no demuestran arranque completo, scroll mediante rueda, latencia del monitor, GPU ni 60 FPS. Datos originales y comando reproducible: `artifacts/visual-startup-benchmark.json` y `scripts/Measure-VisualStartup.ps1`.

## Límites pendientes

El servicio de control del escritorio devolvió `native pipe is unavailable`. No fue posible verificar una ventana visible, el foco real del modal, maximizado en pantalla ni DPI real 125/150/200 %. Se conserva el mínimo anterior: 1080×700 DIP; una pantalla física pequeña con escalado alto puede no acomodarlo. Las capturas disponibles son renders WPF fuera de pantalla.

El motor de operaciones permanece intacto: algunas excepciones internas ya se convierten en avisos y la detección del catálogo es parcial. Las pruebas de UI acreditan el tratamiento de las excepciones que recibe la interfaz, no el éxito real ni la reversibilidad de todos los cambios del sistema. El estado y los riesgos de OTA documentados en `SEGURIDAD-Y-ESTADO-OTA.md` siguen pendientes; esta renovación no los corrige ni publica una versión nueva.
