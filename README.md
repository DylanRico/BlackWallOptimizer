# Blackwall — Muro Negro

Suite de escritorio para Windows 10/11 con auditoría de hardware, ajustes de rendimiento y privacidad, catálogo de aplicaciones, monitor de procesos y herramientas de recuperación.

El ejecutable portable principal está construido con C# y WPF. El repositorio también contiene una variante Rust/Tauri y una interfaz web con bridge de desarrollo; sus funciones y garantías no son equivalentes.

## Documentación vigente

- [Seguridad de publicación y estado real de OTA](SEGURIDAD-Y-ESTADO-OTA.md).
- [Animaciones, comprobaciones y ejecutable actual](ANIMACIONES-MURO-NEGRO.md).
- [Plan visual y criterios de aceptación](PLAN-UX-MURO-NEGRO.md).
- [Primera entrega del rediseño](ENTREGA-MURO-NEGRO.md), conservada como historial.
- [Origen y empaquetado del emblema rojo](assets/branding/README.md).

## Ejecutable e interfaz

`Blackwall.exe` incorpora el emblema rojo, paleta negra/carmesí, menú contraíble, entradas escalonadas de tarjetas, reflejos en botones, circuitos animados y movimiento reducido. Se abre maximizado respetando el área de trabajo de Windows.

La auditoría de hardware puede iniciarse como usuario estándar. Algunas consultas y las acciones de modificación requieren permisos adicionales. Antes de aplicar un ajuste, revisa sus efectos y advertencias. Revertir ejecuta valores del catálogo y puede no recuperar exactamente tu configuración anterior. Un punto de restauración depende de que Windows permita crearlo; su existencia no se debe dar por hecha.

No se promete un tiempo universal de arranque, compatibilidad con todos los equipos, ausencia de alertas antivirus ni seguridad de producción ya auditada. El binario local de esta entrega no tiene una nueva firma Authenticode de confianza pública.

## Comprobar y compilar WPF

Requisitos: Windows, compilador .NET Framework de 64 bits con WPF y Node.js para las pruebas web.

```powershell
./scripts/Test-Ui.ps1
node scripts/check-publish-safety.mjs --self-test
node scripts/check-publish-safety.mjs
```

La compilación comprobada se escribe en `artifacts/Blackwall.preview.exe`; los artefactos y capturas no se suben a Git. El script de comprobación no aplica tweaks ni instala programas. La opción `-ScanHardware` añade consultas reales de hardware de solo lectura.

`Build-Portable-Exe.bat` y `Publish-Release.bat` son herramientas históricas que finalizan procesos y pueden crear/usar certificados locales. No deben confundirse con el script de comprobación ni con una publicación OTA ya validada.

## Vista previa web con datos sintéticos

```powershell
npm run preview:ui
```

Abre `http://127.0.0.1:3848`. Esta vista usa datos de prueba y bloquea las operaciones que modifican el sistema. No comprueba la eficacia de los ajustes sobre Windows ni representa datos reales del equipo.

`npm start` y `Start-Blackwall.bat` utilizan el bridge histórico, que puede ejecutar comandos y cuya ruta OTA simula éxito. No se recomienda ejecutarlo elevado ni exponerlo a la red; consulta los hallazgos de seguridad antes de utilizarlo.

## Estado OTA

**Implementación parcial; pendiente de endurecimiento y prueba completa.** El manifiesto publicado en `main` declara 1.2.0 y su hash coincide con el binario publicado. El árbol local declara 1.3.0, pero su manifiesto no coincide con el ejecutable nuevo. Subir esta revisión a una rama no actualiza automáticamente los equipos.

Faltan, entre otros controles, autenticidad del paquete, validación estricta del manifiesto, coherencia de versiones y recuperación ante fallos. Los detalles, evidencias y criterios de cierre están en [SEGURIDAD-Y-ESTADO-OTA.md](SEGURIDAD-Y-ESTADO-OTA.md).

## Publicación revisable

Los cambios se preparan en una rama de revisión para conservar intactas las URLs OTA que apuntan a `main`. Antes del commit y del push, revisar el índice explícito con el escáner de publicación. El único ejecutable previsto en Git es `Blackwall.exe`; se excluyen respaldos, informes reales, capturas, credenciales y compilaciones temporales.
