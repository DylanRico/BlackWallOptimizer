# Blackwall — Muro Negro

Suite de escritorio para Windows 10/11 con auditoría de hardware, ajustes de rendimiento y privacidad, catálogo de aplicaciones, monitor de procesos y herramientas de recuperación.

El ejecutable portable principal está construido con C# y WPF. El repositorio también contiene una variante Rust/Tauri y una interfaz web con bridge de desarrollo; sus funciones y garantías no son equivalentes.

## Documentación vigente

- [Actualizaciones firmadas: preparación, claves, publicación y recuperación](OTA-ACTUALIZACIONES.md).
- [Auditoría anterior de seguridad y OTA](SEGURIDAD-Y-ESTADO-OTA.md), conservada como historial.
- [Origen y empaquetado del emblema rojo](assets/branding/README.md).
- [Versiones y notas de cada publicación](https://github.com/DylanRico/BlackWallOptimizer/releases).

Los planes e informes históricos permanecen disponibles en el historial Git anterior a la limpieza de documentación.

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

La compilación comprobada se escribe en `artifacts/Blackwall.preview.exe`; los artefactos y capturas no se suben a Git. Las pruebas del motor usan una clave temporal de registro HKCU y la eliminan después; no aplican ajustes del catálogo ni instalan programas. La opción `-ScanHardware` añade consultas reales de hardware de solo lectura.

Blackwall incluye desplazamiento suave, modo claro mate, solicitud UAC y actualización verificada mediante un canal integrado que no se puede editar desde la aplicación. Los ejecutables se distribuyen como assets de releases; el portable local y los respaldos de compilación no se versionan en Git.

`Build-Portable-Exe.bat` no crea certificados. `Publish-Release.bat` prepara un paquete firmado en `artifacts/releases/<version>` mediante `Prepare-Release.ps1`; no modifica el portable raíz ni publica archivos. Antes de distribuir clientes hay que respaldar la clave privada y seguir la guía OTA.

## Vista previa web con datos sintéticos

```powershell
npm run preview:ui
```

Abre `http://127.0.0.1:3848`. Esta vista usa datos de prueba y bloquea las operaciones que modifican el sistema. No comprueba la eficacia de los ajustes sobre Windows ni representa datos reales del equipo.

`npm start` utiliza el bridge histórico, que puede ejecutar comandos y cuya ruta OTA simula éxito. El lanzador solo recurre a Rust/Node si falta el portable WPF. No se recomienda ejecutar ese bridge elevado ni exponerlo a la red; consulta los hallazgos de seguridad antes de utilizarlo.

## Estado OTA

**Ruta WPF firmada implementada y comprobada sobre copias.** Se verifican firma RSA/SHA-256, esquema, versión, tamaño y hash; se limita el transporte HTTPS y se conserva/restaura el ejecutable anterior si falla el arranque probado.

`catalogs/version.json` contiene metadatos de compilación; el índice de distribución nuevo es un `update.json` firmado. Los catálogos viajan dentro del binario y la caché antigua sin firma no lo sustituye. La clave privada está protegida fuera del proyecto; solo se embebe la pública.

La versión pública y sus notas se consultan en [Releases](https://github.com/DylanRico/BlackWallOptimizer/releases). La detección, descarga y verificación de la 1.4.6 se comprobaron con el cliente 1.4.5; la instalación interactiva completa quedó pendiente de prueba manual. Consulta [la guía OTA](OTA-ACTUALIZACIONES.md) para los comandos y límites de recuperación. Rust/bridge no están migrados a este protocolo.

## Publicación revisable

Antes de publicar, revisar explícitamente los archivos con el escáner de publicación. Los ejecutables se entregan como assets de la release, no en el árbol Git. Se excluyen respaldos, informes reales, capturas, credenciales y compilaciones temporales.
