# Blackwall — Muro Negro

Suite de escritorio para Windows 10/11 con auditoría de hardware, ajustes de rendimiento y privacidad, catálogo de aplicaciones, monitor de procesos y herramientas de recuperación.

El ejecutable portable principal está construido con C# y WPF. El repositorio también contiene una variante Rust/Tauri y una interfaz web con bridge de desarrollo; sus funciones y garantías no son equivalentes.

## Documentación vigente

- [Actualizaciones firmadas: preparación, claves, publicación y recuperación](OTA-ACTUALIZACIONES.md).
- [Auditoría anterior de seguridad y OTA](SEGURIDAD-Y-ESTADO-OTA.md), conservada como historial.
- [Diseño grafito/plata, compilación unificada y corrección del touchpad](RENOVACION-GRAFITO-PLATA.md).
- [Animaciones, comprobaciones y ejecutable actual](ANIMACIONES-MURO-NEGRO.md).
- [Plan visual y criterios de aceptación](PLAN-UX-MURO-NEGRO.md).
- [Primera entrega del rediseño](ENTREGA-MURO-NEGRO.md), conservada como historial.
- [Origen y empaquetado del emblema rojo](assets/branding/README.md).

## Ejecutable e interfaz

`Blackwall.exe` incorpora el emblema rojo, materiales grafito/plata con acento brasa, menú superpuesto, búsqueda por teclado, reflejos localizados y movimiento reducido. Se abre maximizado respetando el área de trabajo de Windows. El touchpad conserva los deltas pequeños sin reiniciar una animación por cada evento; la rueda por pasos mantiene el suavizado.

La apertura normal solicita UAC una vez y continúa con permisos de administrador; cancelar el aviso cancela la apertura. El instalador OTA conserva una sesión sin elevación mediante el shell de Windows. Antes de aplicar un ajuste, revisa sus efectos y advertencias. Revertir ejecuta valores del catálogo y puede no recuperar exactamente tu configuración anterior. Un punto de restauración depende de que Windows permita crearlo; su existencia no se debe dar por hecha.

No se promete un tiempo universal de arranque, compatibilidad con todos los equipos, ausencia de alertas antivirus ni seguridad de producción ya auditada. El binario local de esta entrega no tiene una nueva firma Authenticode de confianza pública.

## Comprobar y compilar WPF

Requisitos: Windows, compilador .NET Framework de 64 bits con WPF y Node.js para las pruebas web.

**Para actualizar tu portable con todo el código y diseño locales, ejecuta `Build-Portable-Exe.bat` y abre `Blackwall.exe`.** `Start-Blackwall.bat` también da prioridad a ese ejecutable; conserva su solicitud de administrador.

La compilación normal, el preview y el preparador de versiones utilizan `scripts/Build-Native.ps1`. Siempre compila el C# actual, embebe los catálogos, la clave pública y todos los PNG locales, incluye el ICO y compara los recursos embebidos con sus archivos. La versión del PE y su metadato se generan desde el mismo valor. Reemplaza el destino únicamente después de compilar y verificar, y conserva el anterior en `artifacts/build-backups/`. Si el destino está bloqueado, falla sin cerrar procesos ni truncarlo.

```powershell
./scripts/Test-Ui.ps1
node scripts/check-publish-safety.mjs --self-test
node scripts/check-publish-safety.mjs
```

La compilación comprobada se escribe en `artifacts/Blackwall.preview.exe`; los artefactos y capturas no se suben a Git. El script de comprobación no aplica tweaks ni instala programas. La opción `-ScanHardware` añade consultas reales de hardware de solo lectura.

La versión 1.4.4 añade scroll por frames, modo claro de plata mate, UAC al abrir y un aviso vertical para actualizaciones verificadas, con un canal integrado que no se puede editar desde la aplicación. [Plan de esta iteración](PLAN-FLUIDEZ-Y-ACTUALIZACIONES.md) e [informe de validación](ENTREGA-FLUIDEZ-1.4.4.md). El trabajo previo de filtros, selección y consola está documentado en el [informe 1.4.2](ENTREGA-RENDIMIENTO-1.4.2.md). Los ejecutables se distribuyen como assets de releases; el portable local y los respaldos de compilación no se versionan en Git.

`Build-Portable-Exe.bat` no crea certificados. `Publish-Release.bat` prepara un paquete firmado en `artifacts/releases/<version>` mediante `Prepare-Release.ps1`; no modifica el portable raíz ni publica archivos. Antes de distribuir clientes hay que respaldar la clave privada y seguir la guía OTA.

## Vista previa web con datos sintéticos

```powershell
npm run preview:ui
```

Abre `http://127.0.0.1:3848`. Esta vista usa datos de prueba y bloquea las operaciones que modifican el sistema. No comprueba la eficacia de los ajustes sobre Windows ni representa datos reales del equipo.

`npm start` utiliza el bridge histórico, que puede ejecutar comandos y cuya ruta OTA simula éxito. El lanzador solo recurre a Rust/Node si falta el portable WPF. No se recomienda ejecutar ese bridge elevado ni exponerlo a la red; consulta los hallazgos de seguridad antes de utilizarlo.

## Estado OTA

**Ruta WPF firmada implementada y comprobada localmente sobre copias.** El cliente inicial es `1.4.0` y se ha preparado un paquete posterior `1.4.1`. Se verifican firma RSA/SHA-256, esquema, versión, tamaño y hash; se limita el transporte HTTPS y se conserva/restaura el ejecutable anterior si falla el arranque probado.

`catalogs/version.json` contiene metadatos de compilación; el índice de distribución nuevo es un `update.json` firmado. Los catálogos viajan dentro del binario y la caché antigua sin firma no lo sustituye. La clave privada está protegida fuera del proyecto; solo se embebe la pública.

La release `v1.4.1` y su manifiesto firmado se publicaron el 26 de octubre de 2026; descarga y verificación pasaron contra GitHub real con el código OTA productivo. El propietario informó haber guardado el respaldo de su clave; su restauración real no se comprobó. Faltan la instalación manual inicial en clientes antiguos, la prueba completa de instalación/reinicio y la validación en otro Windows antes de ampliar la distribución. La publicación solo añadió el manifiesto a `main`, sin sincronizar todos los cambios locales de código; la release distingue el portable firmado de sus fuentes automáticas. Consulta [la guía vigente](OTA-ACTUALIZACIONES.md) para los comandos y límites de recuperación. Las rutas alternativas de Rust/bridge no están migradas a este protocolo.

## Publicación revisable

Los cambios se preparan en una rama de revisión para conservar intactas las URLs OTA que apuntan a `main`. Antes del commit y del push, revisar el índice explícito con el escáner de publicación. El único ejecutable previsto en Git es `Blackwall.exe`; se excluyen respaldos, informes reales, capturas, credenciales y compilaciones temporales.
