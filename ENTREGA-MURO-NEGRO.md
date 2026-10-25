# Blackwall — entrega Muro Negro

La ampliación posterior de animaciones y los datos del ejecutable vigente se documentan en [ANIMACIONES-MURO-NEGRO.md](ANIMACIONES-MURO-NEGRO.md). Las cifras y el hash siguientes corresponden a la primera entrega.

Implementación autorizada por el usuario el 24 de octubre de 2026. Ejecutable entregado: `Blackwall.exe`.

## Cambios

- Paleta negra/carmesí, texto claro y acentos rojos luminosos para mantener contraste. Tema claro alternativo.
- Menú de 248 DIP expandido y 72 DIP contraído, seis iconos, nombres accesibles y ayuda al enfocar con teclado. Contraerlo conserva contenido, desplazamiento, filtros y selección.
- Preferencias de menú, tema y movimiento guardadas en `%LOCALAPPDATA%\Blackwall\ui-preferences.json`; la web usa almacenamiento local.
- Entrada de secciones de 200 ms, transición del menú de 220 ms, pulsación breve, detalles nativos de 180 ms y filamento rojo discreto de ciclo de cinco segundos. El ancho del menú cambia una vez y se anima su presentación para evitar recalcular todo el contenido en cada fotograma.
- Desplazamiento de 260 ms con inversión y cancelación. Movimiento reducido y preferencia de Windows; el filamento se pausa cuando la ventana está inactiva o minimizada.
- Tarjetas con título corto, resumen y detalles desplegables. Advertencias fuera del desplegable, acciones accesibles y estado pendiente cuando aún no hay datos. La explicación de reversión indica que los valores del catálogo pueden diferir de la configuración anterior del usuario.
- Se conservan las cargas diferidas y el aviso compacto de error/reintento. El porcentaje de hardware representa etapas completadas; las lecturas sin progreso medible muestran actividad indeterminada, sin porcentajes inventados.
- Icono rojo generado, incrustado en el ejecutable, ventana y cabecera. ICO con siete tamaños y PNG transparente.
- La base web/Tauri comparte paleta, icono, menú, preferencias y movimiento. Tauri queda configurado para maximizar sin pantalla completa.
- Corrección detectada durante la revisión: un catálogo OTA local de 2026-10-23, esquema 2.0.0 y 14 ajustes desplazaba al integrado 2.2.0 de 45 ajustes. Ahora una caché más antigua no sustituye el catálogo integrado; una caché posterior válida conserva prioridad. No se borró la caché del usuario ni se aplicaron ajustes del sistema.

## Evidencia

`./scripts/Test-Ui.ps1` compila el ejecutable y ejecuta **59 comprobaciones nativas y 14 pruebas web**, todas satisfactorias. Incluyen navegación compacta, persistencia, preferencias corruptas, cancelación/reversión del scroll, detalles accesibles, advertencias visibles, estados pendientes, progreso, errores/reintentos y selección del catálogo.

Se generaron 13 capturas fuera de pantalla en `artifacts/native`, con las seis secciones y muestras de 1080 × 700 y 1400 × 880. Se revisaron el menú expandido/compacto, tema oscuro/claro, software, procesos, seguridad y detalle de riesgo. Estas capturas no sustituyen la prueba de una ventana visible.

El ejecutable final coincide por SHA-256 con el binario compilado y comprobado:

```text
F1CE774388CBE66786714D0241B3ABFFBE0A9E4E1A7A2B95523314CB46CAB3D3
```

## Tamaño y rendimiento

| Medida | Referencia | Muro Negro |
|---|---:|---:|
| Ejecutable | 1 063 424 bytes | 1 172 992 bytes |
| Mediana de construcción + dibujo fuera de pantalla | 386 ms | 396 ms |
| Mediana de CPU del mismo recorrido | 359 ms | 390 ms |
| Mediana de memoria de trabajo del proceso de prueba | 68 505 600 bytes | 70 479 872 bytes |

Aumento de tamaño: 109 568 bytes, aproximadamente 107 KiB / 10,3 %. No se añadieron dependencias de ejecución. Medición en tres procesos nuevos por versión, alternando referencia y nueva compilación; datos en `artifacts/visual-startup-benchmark.json`, script `scripts/Measure-VisualStartup.ps1`. La pequeña muestra y los arranques en caliente no permiten afirmar una mejora de velocidad. La medición corresponde a construcción/dibujo, no al arranque completo con WMI, reposo o GPU.

## Límites de esta validación

- No se pudo comprobar la ventana visible, los FPS, GPU ni escalas reales de Windows 125 %, 150 % y 200 %: el servicio de control de ventanas devolvió `native pipe is unavailable` y no había superficies de navegador disponibles.
- La lectura WMI real dentro del aislamiento devolvió acceso denegado; se comprobó su salida recuperable. El intento de verificar fuera del aislamiento no se ejecutó porque la revisión automática de aprobación falló por un límite de uso; no fue un diagnóstico de inseguridad del ejecutable.
- No se aplicaron ni revirtieron tweaks, no se instalaron aplicaciones y no se modificaron servicios ni permisos. El motor heredado de ejecución y su detección parcial de estados no se sometieron a una auditoría funcional completa; un estado detectado no prueba todas las operaciones del ajuste.
- Se compiló WPF. La base web pasó pruebas de lógica y servidor de fixtures; no se compiló el empaquetado Rust/Tauri ni se hizo una revisión visual de navegador.
- La compilación entregada no tiene una nueva firma Authenticode. No se crearon certificados, no se publicó una release y no se cambió el manifiesto OTA remoto ni su hash de publicación.

## Respaldo y reproducción

Respaldo anterior al cambio: `artifacts/before-muro-negro/`, con ejecutable, código e iconos. Para volver al binario previo, cierra Blackwall y copia `artifacts/before-muro-negro/Blackwall.exe` sobre `Blackwall.exe`. Esto no revierte ajustes de Windows.

Para recompilar y comprobar: `./scripts/Test-Ui.ps1`. El resultado se escribe en `artifacts/Blackwall.preview.exe`. El script no mata aplicaciones ni instala certificados. La generación y el empaquetado del icono se documentan en `assets/branding/README.md`.
