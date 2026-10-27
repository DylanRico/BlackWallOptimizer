# Revisión previa a publicación de Blackwall 1.4.6

Fecha: 27 de octubre de 2026. Revisión previa: el usuario fue informado y autorizó la publicación de la candidata.

## Hallazgos

1. GitHub main y el último commit local coincidían en `9c63ef4`, con release pública v1.4.5. Su descripción pública estaba vacía al consultarla.
2. El ejecutable del escritorio declara 1.4.5.0 y mide 1 281 024 bytes. El paquete publicado 1.4.5 mide 1 247 232 bytes. Compartir número de versión no demuestra que dos ejecutables contengan el mismo código.
3. Había modificaciones locales en BlackwallApp.cs, tweaks.json y NativeUiSelfCheck.cs, además de dos benchmarks nuevos. Se conservaron e integraron en la candidata.
4. El catálogo cambia de 45 a 43 entradas e incluye cambios operativos, no sólo visuales. Se revisaron sus diferencias y el contrato estático; no se ejecutaron esas operaciones sobre ajustes reales del sistema.
5. Se detectó y corrigió un fallo de información: herramientas de mantenimiento podían finalizar con un mensaje de éxito tras un error. Se conservaron sus comandos y se corrigió la propagación del resultado.
6. Las pruebas existentes del motor utilizaban una clave HKCU fija. Ahora cada ejecución utiliza una subclave con GUID y sólo elimina su propia clave de prueba.

## Archivos propuestos para Git

- `native-wpf/BlackwallApp.cs`
- `catalogs/tweaks.json`
- `catalogs/version.json`
- `tests/NativeUiSelfCheck.cs`
- `tests/NativeLogBenchmark.cs`
- `tests/NativeNavigationBenchmark.cs`
- `RELEASE-NOTES-1.4.6.md`
- `REVISION-PUBLICACION-1.4.6.md`

Los documentos personales de contexto y planificación no forman parte de esta propuesta. Tampoco se subirán artifacts, ejecutables al árbol de código, respaldos, logs ni claves privadas. El ejecutable y su manifiesto firmado se adjuntarían como assets de la release.

## Paquete preparado

- Versión: 1.4.6.0.
- Ejecutable: `artifacts/releases/1.4.6/Blackwall.exe`.
- Tamaño: 1 282 048 bytes, aproximadamente 2,8 % más que el paquete público 1.4.5.
- SHA-256: `cd904a3c584537ea7a0b313bb17a220e266e35db640d72c7798b06bb7027a2ac`.
- Manifiesto firmado: `artifacts/releases/1.4.6/update.json`.
- Notas públicas preparadas: `RELEASE-NOTES-1.4.6.md`.
- Las mismas novedades resumidas están en `catalogs/version.json` y en el manifiesto OTA firmado.

El paquete se preparó con Windows PowerShell clásico. Una primera invocación desde PowerShell moderno falló al verificar System.Web de .NET Framework; la carpeta temporal se retiró y no se creó un paquete parcial. La invocación correcta completó firma y verificación. No se sustituyeron claves ni el ejecutable raíz.

## Validaciones realizadas

- Suite de UI: 161 checks nativos, 16 tests web y 16 checks del compilador.
- Segunda comprobación nativa contra el ejecutable firmado exacto: 161 checks y 30 capturas fuera de pantalla.
- Suite OTA: 44 pruebas de protocolo, firma, transacción y subprocesos en copias desechables.
- Verificación directa de firma y binario de 1.4.5 y 1.4.6; digest de 1.4.5 contrastado con GitHub.
- Inspección visual de capturas del panel Actividad oscuro y de la interfaz clara. Son renders WPF fuera de pantalla, con fixtures de prueba cuando corresponde.
- Escáner de publicación: autocheck correcto y ocho archivos propuestos revisados en un índice Git temporal, sin hallazgos de los patrones de secretos y rutas prohibidas. El índice real no se alteró. Esto no garantiza la ausencia de cualquier dato confidencial imaginable.
- Diff propuesto sin errores de whitespace; Git emitió únicamente avisos de normalización LF/CRLF.

Logs y evidencias locales: `artifacts/revision-publicacion-ui.log`, `artifacts/revision-publicacion-paquete-ui.log`, `artifacts/revision-publicacion-ota.log`, `artifacts/revision-publicacion-identidades.json`, `artifacts/native-release-1.4.6/`.

## Comparación de rendimiento

Tres pares alternados, en el mismo entorno, comparando los paquetes firmados exactos 1.4.5 y 1.4.6 con los harness WPF existentes. Cada ejecución de navegación mide 18 cambios después del calentamiento. El registro utiliza 2 000 entradas sintéticas. Los módulos se marcan como cargados para evitar WMI y operaciones reales mientras se mide.

Los valores de la tabla son medianas entre las tres ejecuciones; para navegación, las métricas internas de cada ejecución también son medianas salvo donde se indica máximo.

| Métrica | 1.4.5 | 1.4.6 |
| --- | ---: | ---: |
| Bloqueo síncrono al cambiar sección | 11,1 ms | 9,9 ms |
| Primer evento de render tras cambiar sección | 62,0 ms | 25,5 ms |
| Mayor separación de eventos de render por cambio, mediana | 62,0 ms | 43,5 ms |
| Tiempo de añadir y presentar 2 000 entradas | 5 265,2 ms | 1 998,6 ms |
| CPU durante esa prueba de registro | 3 812,5 ms | 1 593,8 ms |
| Abrir Actividad con el registro cargado | 199,2 ms | 37,8 ms |
| Memoria privada al final de la prueba de registro | 104,5 MiB | 103,9 MiB |

La CPU de la prueba de navegación fue prácticamente equivalente: 2 921,9 frente a 2 953,1 ms. No se afirma menor consumo en reposo ni una mejora global de arranque. La prueba nueva de registro presenta un máximo de filas visibles diferente al TextBox anterior; mide la experiencia resultante con historial limitado, no la misma cantidad de filas simultáneamente dibujadas.

Rangos de primer evento de render: 55,3–62,8 ms en 1.4.5 y 25,2–26,0 ms en 1.4.6. Rango de la prueba de 2 000 entradas: 5 231,0–5 265,3 ms frente a 1 997,2–1 999,9 ms. Son pruebas fuera de pantalla con datos sintéticos; no acreditan FPS de GPU, tiempo hasta presentación física ni suavidad del touchpad real. Datos completos en `artifacts/revision-publicacion-benchmarks.json`.

## Copias de escritorio

- `Blackwall-Prueba-OTA-1.4.5/Blackwall.exe`: copia auténtica anterior del paquete publicado, con firma/hash comprobados.
- `Blackwall-Candidata-1.4.6/Blackwall.exe`: copia exacta del nuevo paquete firmado, preparada para revisión.

El Blackwall.exe que ya tenía el usuario en el escritorio permanece intacto. La copia antigua no podrá detectar 1.4.6 en el canal público mientras no se publique la nueva release y se actualice el índice firmado.

## Publicación realizada

El usuario autorizó la publicación. El código y el tag `v1.4.6` se publicaron en el commit `c1cc1c6`. La release incluye las notas de `RELEASE-NOTES-1.4.6.md` y los dos assets firmados; se verificaron sus tamaños y hashes tanto en la API de GitHub como mediante descarga pública.

Después de verificar la descarga se publicó el manifiesto firmado exacto en `catalogs/update.json` mediante el commit `367db10`. Con el código OTA real del ejecutable 1.4.5 se consultó el canal público, se comprobó la firma y las notas de 1.4.6, y se descargó y verificó el ejecutable de 1 282 048 bytes. Esta comprobación no instaló la actualización.

Release pública: https://github.com/DylanRico/BlackWallOptimizer/releases/tag/v1.4.6

## Límites

No se validaron mejoras universales de hardware ni todas las combinaciones de Windows y escalado físico. Tampoco se ejecutó la instalación OTA real sobre la copia del usuario ni se comprobó aquí el ciclo UAC completo de esa instalación. Los riesgos de ajustes como desactivar VBS siguen vigentes. Una firma OTA propia no equivale a firma Authenticode de Windows.
