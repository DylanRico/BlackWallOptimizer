# Movimiento Muro Negro — implementación ampliada

La versión anterior tenía transiciones demasiado discretas y el menú cambiaba su ancho inmediatamente. Esta revisión incorpora movimiento visible en los componentes compartidos de las seis secciones del ejecutable WPF y de la interfaz web.

## Efectos implementados

| Interacción | Ejecutable WPF | Base web |
|---|---|---|
| Contraer/expandir menú | Ancho real 248 ↔ 72 DIP, 420 ms, aceleración y frenado progresivos; una nueva pulsación parte del ancho actual | Interpolación de columnas durante 420 ms |
| Cambiar de sección | Entrada de 360 ms; se repite al llegar el contenido de una carga diferida | Desplazamiento y aparición de 360 ms |
| Aparición de tarjetas | Cascada de 38 ms entre tarjetas, elevación de 30 px, leve rebote y expansión horizontal; hasta 760 ms en conjunto | Cascada de 45 ms, inclinación de entrada y asentamiento; 540 ms por tarjeta |
| Desplazarse | Las tarjetas que entran al área visible se revelan una vez por página | Observación de visibilidad y revelación individual |
| Botones | Reflejo diagonal carmesí de 650 ms, elevación al pasar el cursor/enfocar y presión breve | Reflejo metálico de 700 ms, desplazamiento leve siguiendo el cursor y presión |
| Tarjetas al pasar el cursor | Halo rojo localizado | Contorno luminoso, elevación y reflejo tenue |
| Identidad | Halo respiratorio alrededor del emblema, circuito rojo con tres cargas en la cabecera y otro vertical en el margen del menú | Logo pulsante, circuito y carga vertical en el menú; brillo de esqueletos de carga |

Los circuitos WPF se dibujan en superficies pequeñas y separadas del texto, con temporizadores de aproximadamente 30 actualizaciones por segundo. Ese límite del adorno **no es una medición de los FPS de toda la aplicación**. La velocidad depende del tiempo transcurrido, no del número de fotogramas del monitor.

El movimiento reducido detiene los circuitos y el halo animado, cancela entradas y reflejos activos y deja el menú y el desplazamiento directos. Los adornos se detienen al perder actividad o minimizar la ventana. Cambiar de sección lleva al comienzo de su contenido; contraer el menú conserva la posición de la sección actual.

## Investigación aplicada

- [Motion: animación y cascadas](https://motion.dev/docs/quick-start): referencia para coordinar entradas, retrasos entre elementos y asentamiento.
- [Motion: stagger](https://motion.dev/docs/stagger): distribución de retrasos en listas y cuadrículas.
- [React Bits: catálogo oficial](https://www.reactbits.dev/get-started/index): referencias visuales de bordes eléctricos, reflejos y atracción al cursor.
- [Microsoft: animación WPF](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/graphics-multimedia/animation-overview): animación nativa de propiedades y temporización.
- [Microsoft: transformaciones WPF](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/graphics-multimedia/transforms-overview): transformaciones de presentación para reducir recálculos del diseño.
- [Microsoft: animar gradientes](https://learn.microsoft.com/es-es/dotnet/desktop/wpf/graphics-multimedia/how-to-animate-the-position-or-color-of-a-gradient-stop): base para los reflejos luminosos.

Son referencias adaptadas al proyecto. No se añadió React, Electron ni una dependencia de Motion al ejecutable. Se usan WPF, CSS y Web Animations ya disponibles.

## Comprobación y entrega

- Compilación WPF y **68 comprobaciones nativas + 15 pruebas web satisfactorias** mediante `scripts/Test-Ui.ps1`.
- La prueba de temporización registró un ancho intermedio del menú de 106,3 DIP y una tarjeta desplazada 19,8 px con opacidad 0,41. Comprueba valores durante el recorrido, inversión rápida del menú, reflejos, detención de temporizadores y cancelación con movimiento reducido; no mide FPS de presentación.
- Se abrió el ejecutable en una ventana real y se revisaron Hardware, Rendimiento, Privacidad, Software, Procesos y Seguridad, además de menú compacto, maximizado, detalles y desplazamiento. Se observaron las tarjetas en distintas fases de entrada y las cargas rojas en posiciones sucesivas.
- La lectura real de hardware terminó en unos 5,2 segundos. La consulta de puntos de restauración devolvió acceso denegado en la sesión estándar y mostró el aviso contextual previsto. No se aplicaron ajustes, instalaron programas ni ejecutaron acciones de mantenimiento.
- Se generaron 14 capturas de pruebas fuera de pantalla. No se midieron FPS/GPU ni se comprobaron todas las escalas reales de Windows; tampoco se compiló Rust/Tauri ni se revisó visualmente la web en un navegador.

Binario vigente tras corregir el icono pequeño: `Blackwall.exe`, 1 207 808 bytes. Creció 33 344 bytes frente al ejecutable respaldado al iniciar la revisión de movimiento. El icono se verificó en las vistas grande y de detalles del Explorador; las comprobaciones nativas siguen siendo 68 y las pruebas web/recursos ahora son 16. El SHA-256 coincide con la compilación comprobada:

```text
DC5896418F182BE299A2604AB86103AE91633D29FE4B0D1F6B6578707788A774
```

Respaldo: `artifacts/before-exotic-motion/`. Contiene el ejecutable anterior y los archivos modificados al empezar esta revisión. La entrega continúa siendo una compilación local sin nueva firma Authenticode; no se publicó una actualización OTA.
