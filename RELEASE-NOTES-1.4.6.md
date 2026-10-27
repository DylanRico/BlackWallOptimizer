# Blackwall 1.4.6 — navegación, actividad y ajustes revisados

Esta versión integra los cambios locales de la interfaz WPF y del catálogo que todavía no estaban en el paquete público 1.4.5.

## Novedades

- Navegación lateral con iconos y etiquetas revisados; reorganización de las tarjetas y sus detalles.
- Incorporación progresiva de tarjetas al cambiar de sección, manteniendo la búsqueda, los filtros y los controles existentes.
- Pantalla de Actividad con operaciones en curso, contadores, filtros del registro y acciones para copiar, exportar y limpiar entradas.
- Entradas de registro agrupadas para actualizar la interfaz durante ráfagas de mensajes, con límites de historial y filas visibles.
- Se conservan el acabado grafito/plata/rojo, el modo claro mate, el scroll suave y el aviso vertical de actualizaciones.

## Correcciones

- Manejo de valores REG_DWORD de 32 bits superiores a int.MaxValue.
- Detección que comprueba todos los criterios verificables de un ajuste, en lugar de considerar suficiente una coincidencia parcial.
- Errores identificados por ajuste y resumen de fallos al aplicar un lote.
- Revisión de las operaciones del plan de energía, OneDrive y varias reversiones del catálogo. Las entradas Wi-Fi Sense y Cortana Web Search dejan de ofrecerse; el catálogo pasa de 45 a 43 ajustes.
- Las herramientas de mantenimiento distinguen códigos de error y tiempos de espera; un fallo no termina con un mensaje de éxito.
- El botón de limpieza de DNS describe la operación que realmente ejecuta.

## Actualización y seguridad

El paquete y el manifiesto OTA se verifican con el mecanismo de firma existente. Las novedades resumidas están incluidas en el manifiesto firmado para que la aplicación las muestre antes de actualizar. Se conserva el canal integrado, sin un campo de origen editable.

Las reversiones del catálogo aplican las operaciones definidas; no representan una copia exacta de la configuración previa de cada equipo. El resultado de un ajuste depende del hardware, la edición de Windows y los permisos disponibles. Esta publicación no promete aumentos universales de FPS, rendimiento ni duración de batería.

## Validación de la candidata

- 161 comprobaciones WPF sobre el ejecutable firmado y 30 capturas fuera de pantalla.
- 16 pruebas web y 16 comprobaciones aisladas del compilador.
- 44 pruebas OTA de protocolo y transacciones en copias desechables.
- Verificación de firma e integridad de los paquetes 1.4.5 y 1.4.6. La copia anterior coincide con el digest público de GitHub.

Las pruebas no ejecutan optimizaciones, instalaciones ni actualizaciones reales sobre el equipo del usuario. El harness del motor utiliza una clave HKCU de prueba con nombre único y la elimina al terminar. La instalación OTA completa desde la copia anterior y la interacción física con el touchpad quedan para la prueba manual.
