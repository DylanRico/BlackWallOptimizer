# Blackwall: fluidez, modo claro y actualizaciones

Fecha: 26 de octubre de 2026. Alcance autorizado: plan, implementación, pruebas y copia final en el escritorio. La fuente es la copia local; interfaz activa WPF/.NET Framework en `native-wpf/BlackwallApp.cs`, compilada por `scripts/Build-Native.ps1` y `Build-Portable-Exe.bat`.

Estado de entrega: iteración visual 1.4.3 aprobada por el usuario; versión 1.4.4 añade el canal OTA integrado. El usuario autorizó publicar los cambios y dejar una copia anterior para su prueba manual. Evidencia y límites en [ENTREGA-FLUIDEZ-1.4.4.md](ENTREGA-FLUIDEZ-1.4.4.md).

## 1. Diagnóstico y referencia

- Conservar los cambios locales y los ejecutables previos. Usar `artifacts/Blackwall-fluid-v2.preview.exe` como referencia anterior de esta iteración.
- El portable raíz es anterior a la preview: no representa los arreglos que el usuario acaba de evaluar.
- El scroll actual cambia entre desplazamiento directo y animaciones de 260 ms según los deltas y un umbral de 40 ms entre eventos. Un mismo gesto de touchpad puede cambiar de camino y de origen pendiente.
- El modo claro combina fondos blancos, biseles con contrastes fuertes y campos con colores oscuros independientes de los tokens. Revisar combinaciones reales, estados y selección, no sólo cambiar el fondo.
- La comprobación OTA inicial verifica la firma, pero sólo modifica el texto del botón superior. No anuncia la versión mediante un panel de decisión.
- El instalador OTA rechaza privilegios elevados de forma deliberada. No eliminar esa barrera para lograr el nuevo arranque.

## 2. Referencias consultadas

- [WPF: layout y rendimiento](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/optimizing-performance-layout-and-design): reducir pasadas de layout y reutilizar transformaciones.
- [WPF: controles y reciclaje](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/optimizing-performance-controls): no añadir virtualización sin demostrar que el catálogo y la estructura la necesitan.
- [Discusión técnica de WPF sobre deltas del touchpad](https://github.com/dotnet/wpf/issues/5936): preservar deltas fraccionarios y evitar convertir cada entrada en un salto fijo.
- [Discusión sobre scrolls anidados](https://github.com/dotnet/wpf/issues/8353): comprobar consola, notas y contenedores internos, además del scroll principal.
- [UAC en Windows](https://learn.microsoft.com/en-us/windows/security/application-security/application-control/user-account-control/how-it-works): el usuario eligió aceptar UAC una vez por apertura normal de la aplicación.
- [Microsoft: iniciar sin elevación desde una aplicación elevada](https://devblogs.microsoft.com/oldnewthing/20131118-00/?p=2643): solicitar al shell interactivo el lanzamiento del actualizador con el usuario original, conservando su barrera contra elevación.
- [Repositorio público de CeitoTweaks](https://github.com/ceitooo/ceitotweaks): referencia de producto. No se encontró el clip adjunto ni un vídeo reproducible en la copia local/README durante esta revisión. La dirección visual parte de los rasgos ya descritos por el usuario; no se atribuye una biblioteca ni una animación medida a ese producto.
- [Buenas prácticas de Windows 11](https://learn.microsoft.com/en-us/windows/apps/get-started/best-practices): tonos suaves y separación por capas; se aplica como referencia visual sobre WPF, sin añadir WinUI ni Mica como dependencia.

Después de la primera prueba física, el usuario informó que el scroll ya no se trababa, pero pidió más suavidad y menos luminosidad. La revisión final usa un offset de render independiente del layout diferido, una respuesta de 24 ms y una sola actualización por frame. La paleta clara final baja el fondo a `#D1D6DE` y el reflejo de tarjeta a `#E3E6EB`, con texto grafito y rojo brasa; no usa blanco puro para las superficies principales.

## 3. Scroll compartido

1. Sustituir los reinicios de animación por entrada por una única progresión de render activa durante el gesto.
2. Acumular proporcionalmente todos los deltas, incluidos los inferiores a 120, sin cambiar de algoritmo por su frecuencia.
3. Usar el offset visible al invertir la dirección para eliminar el arrastre pendiente contrario.
4. Limitar el destino a los límites reales del contenido. Conservar la preferencia de líneas/página de Windows.
5. Coalescer entradas dentro del frame; evitar `UpdateLayout` en el callback productivo.
6. Detener el seguimiento al terminar, cambiar contenido, usar teclado/barra o descargar el control. Movimiento reducido conserva entrada directa y precisión.
7. Probar gestos largos, ráfagas antes del layout, pausas, inversión, límites y cambio de sección.

## 4. Paleta clara y legibilidad

1. Fondo gris cálido suave; tarjetas en porcelana gris y superficies elevadas con una separación tonal corta.
2. Texto principal grafito; texto secundario oscuro suficiente para lectura prolongada; rojo de marca moderado.
3. Reducir el blanco puro y el contraste de los biseles en modo claro sin perder el reflejo localizado.
4. Mantener semántica de verde, ámbar y rojo de riesgo. Verificar contraste mínimo de 4,5:1 en textos habituales y de 3:1 donde corresponda a contornos/foco.
5. Revisar selección de texto, consola, notas OTA, buscadores, deshabilitados y ambos temas. Usar pinceles compartidos para cambios en caliente.

## 5. Arranque y permisos

1. Apertura normal: si no hay token elevado, relanzar una sola vez mediante el verbo nativo `runas`. Cancelar UAC cancela la apertura, sin ciclos de reintento.
2. Retirar el botón fijo «Administrador» de la cabecera. Conservar la información de permisos en consola/estado.
3. Mantener un modo interno de actualización sin elevación. El instalador y sus hashes, firmas, límites, bloqueo, cancelación, respaldo y rollback siguen funcionando bajo el usuario normal.
4. Desde la UI elevada, entregar la actualización al shell interactivo de Windows. Si ese canal no está disponible, mostrar un error recuperable y mantener abierta la app; no instalar con privilegios elevados.
5. La nueva versión confirma su arranque al instalador antes de solicitar la elevación para la sesión normal. Una reapertura posterior a actualizar puede mostrar UAC porque es una nueva apertura.
6. Pruebas automatizadas no aceptan UAC ni cambian políticas de Windows. La validación interactiva de consentimiento y de la transición entre tokens se informa por separado.

## 6. Aviso vertical de actualización

1. Mantener el botón superior y la comprobación automática de arranque existente.
2. Mostrar la tarjeta sólo después de verificar una versión realmente posterior. Una sola instancia y una sola invitación por versión durante la sesión.
3. Formato vertical adaptable al área de trabajo: marca, distintivo «Actualización disponible», versión, explicación breve y notas de cambios desplazables.
4. Pie estable con «Actualizar ahora» como acción principal y «Más tarde» como alternativa; Escape y cierre equivalen a posponer.
5. Material grafito/plata con acento brasa, bisel asimétrico y destello localizado en hover/foco. Entrada breve por opacidad/desplazamiento; respetar movimiento reducido.
6. No descargar ni instalar hasta la aceptación. Revalidar manifiesto y paquete al instalar; informar bytes reales, error, cancelación y recuperación.
7. Si se abre manualmente, permitir comprobar aunque no haya actualización. No presentar notas remotas sin firma ni mostrar éxito por terminar una animación.

## 7. Validación y comparación

- Ejecutar el harness WPF existente y sus regresiones de scroll, estados de lectura y navegación; reparar sus callers si cambia una firma interna.
- Añadir sólo comprobaciones relevantes para progresión continua, entrada fraccionaria, cancelación del gesto, contraste y aviso verificado sin hacer instalaciones reales.
- Usar manifiestos y operaciones simuladas en pruebas de UI. Ejecutar además las pruebas OTA existentes sobre copias temporales y claves de fixture, nunca sobre el portable del usuario.
- Comparar siete pares alternados antes/después en el mismo harness para construcción, navegación, búsqueda, scroll programático y reposo; conservar datos crudos. Esas cifras no demuestran FPS ni latencia física del touchpad.
- Capturar pantallas reales del render WPF fuera de pantalla para oscuro/claro y el panel vertical. Intentar comprobación visible si el canal de escritorio está disponible; informar el bloqueo si sigue sin estarlo.
- Comprobar compilación local aislada, recursos embebidos, versión e icono. Ninguna prueba ejecuta tweaks, instala software, reinicia Windows ni termina procesos del usuario.

## 8. Entrega

1. Compilar un portable con todo el código local revisado en una carpeta de `artifacts` nueva.
2. Copiarlo al escritorio resuelto por Windows. Si ya existe un archivo con el mismo nombre, respaldarlo antes del reemplazo; no cerrar la aplicación del usuario.
3. Comparar hashes entre compilación y copia del escritorio; entregar ruta exacta, informe, capturas y límites.
4. Publicar las fuentes revisadas y el paquete firmado 1.4.4, con el binario público antes del manifiesto. Conservar una copia 1.4.3 separada para que el usuario pruebe la actualización voluntariamente. La interacción OTA sobre su copia la realiza el usuario.

## Criterio de cierre

Código integrado; compilación y checks relevantes aprobados; captura del modo claro y aviso vertical revisadas; comparación reproducible; portable nuevo verificado en escritorio. Distinguir claramente las pruebas sintéticas y fuera de pantalla de la validación pendiente con touchpad físico/UAC interactivo.
