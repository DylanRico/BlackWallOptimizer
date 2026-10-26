# Blackwall: revisión de seguridad de publicación y estado OTA

> Auditoría histórica anterior a la implementación de OTA firmada WPF. La ruta nativa, el versionado y el publicador cambiaron después de este informe; consulta `OTA-ACTUALIZACIONES.md` para el estado vigente, las pruebas y los límites. Los hallazgos de Rust/Tauri y del bridge alternativo no se han corregido en esa implementación WPF.

Fecha de revisión: 24 de octubre de 2026. Alcance: árbol de trabajo, cambios salientes de Git, ejecutable WPF y rutas OTA de WPF, Tauri y bridge de desarrollo. No se ejecutó una actualización sobre una instalación real.

## Respuesta: ¿ya sirve el versionamiento por OTA?

**Hay una implementación parcial, pero no está lista para una distribución OTA segura y verificable.** WPF puede consultar manualmente un manifiesto y contiene código de descarga/reemplazo. Eso no demuestra una actualización completa con recuperación ante fallos. Tauri y el bridge tienen rutas que pueden informar éxito sin haber actualizado correctamente.

| Comprobación | Estado observado |
|---|---|
| Repositorio de destino | `DylanRico/BlackWallOptimizer`, público |
| `origin/main` consultado | `4dc7e2659b7578ec306b17c64b1737ac1049571a` |
| Versión publicada en `main` | `1.2.0` |
| Hash del binario publicado frente a su manifiesto | Coincide, comprobado con los objetos obtenidos por `git fetch` |
| Versión declarada en el manifiesto local | `1.3.0` |
| Hash del binario local frente al manifiesto local | **No coincide** |
| Aplicación local WPF | Versión de ensamblado `1.3.0.0`; pantalla OTA declara `1.2.0` de forma fija |
| Versiones de paquete Node/Cargo/configuración Tauri | `1.0.0` |
| Consulta automática en el arranque WPF | No hay llamadas a `CheckRemoteOtaAsync`; la consulta disponible es manual |
| Prueba completa descarga → verificación → reemplazo → reinicio → recuperación | No realizada |

Hashes comprobados:

```text
Publicado main, declarado y real:
78de4f784483ee8a79218105dc373c44ded04b3c8a58e2285be8063cba35f81c

Local, declarado en catalogs/version.json:
f541fe2eb25e90ad9023cd9c24e9c052875950f7bbde76a6a46a0a6021c6b447

Local, Blackwall.exe real:
dc5896418f182be299a2604ab86103ae91633d29fe4b0d1f6b6578707788a774
```

La nueva rama de revisión no cambia `main`, las URLs OTA de producción ni sus archivos publicados. Subir código a una rama no actualiza los otros equipos ni constituye una release OTA.

## Hallazgos que deben resolverse antes de publicar una OTA

### 1. El manifiesto local no corresponde al ejecutable actual

Referencia: `catalogs/version.json`, `Blackwall.exe`, `Publish-Release.bat`.

Publicar ambos archivos tal como están hace que un cliente que compruebe el SHA-256 rechace la descarga. No basta con cambiar el número de versión. Se necesita una secuencia de publicación que prepare la versión, compile y firme si corresponde, calcule el hash del **último** binario y publique una pareja coherente. No debe modificarse el binario después de calcular su hash.

### 2. La integridad no es autenticidad del publicador

Referencia: `MainWindow.PerformBinarySelfUpdateAsync`, `FetchRemoteManifestAsync`, `GetConfiguredOtaManifestUrl`.

En WPF el hash es opcional: si `binary.sha256` está vacío, se omite su comprobación. Las URL son configurables y no hay una restricción explícita a HTTPS ni a un origen autorizado. Tampoco se verifica una firma de manifiesto/paquete con una clave pública de confianza ni una identidad de firmante Authenticode. Un hash obtenido del mismo origen que el archivo no protege contra la sustitución de ambos.

Corrección necesaria: rechazar manifiestos sin hash válido; validar esquemas/orígenes y redirecciones; verificar autenticidad con una raíz de confianza incorporada. Una cabecera `MZ` y un tamaño mínimo no bastan para validar un ejecutable confiable.

### 3. El reemplazo WPF no tiene recuperación transaccional

Referencia: `MainWindow.PerformBinarySelfUpdateAsync`, `Program.CleanupOldOtaBinary`.

El proceso mueve el binario actual a `.old` y luego mueve el nuevo al nombre original. Si falla el segundo movimiento, el bloque de error no devuelve el respaldo a su nombre anterior. Si falla el arranque nuevo, tampoco hay una confirmación de salud ni recuperación automática. El siguiente arranque borra `.old` sin comprobar la salud de la nueva versión.

Corrección necesaria: un reemplazador mínimo, espera de salida, instalación preparada, restauración del ejecutable anterior ante fallos y conservación del respaldo hasta confirmar un arranque válido. También faltan controles contra actualizaciones concurrentes y contra la instalación manual de versiones anteriores.

### 4. Las versiones y los mensajes no son consistentes

Referencia: `MainWindow.ShowOtaUpdateDialog`, `CheckRemoteOtaAsync`, `IsNewerVersion`, atributos de ensamblado, `package.json`, `src-tauri/Cargo.toml`, `src-tauri/tauri.conf.json`.

La pantalla WPF usa `localVer = "1.2.0"` aunque el ensamblado sea 1.3.0.0. `_versionInfo` puede sustituirse por el manifiesto remoto, por lo que deja de ser una fuente fiable de la versión instalada. `IsNewerVersion` interpreta cadenas inválidas distintas como una versión nueva. La ruta manual de reemplazo no exige que la versión remota sea posterior.

Corrección necesaria: una versión instalada inmutable obtenida del binario, un manifiesto remoto separado, formato de versión validado y pruebas para igualdad, actualización, downgrade e inválidos. Los paquetes y el ensamblado deben seguir una política explícita común.

### 5. Los catálogos remotos tienen controles insuficientes

Referencia: `MainWindow.SyncRemoteCatalogsAsync`, `LoadEmbeddedCatalogs`, `ExecuteTweakAsync`.

La sincronización acepta texto que contenga `"tweaks"` o `"apps"` y escribe cada archivo directamente. No verifica firma/hash individual, estructura completa ni operaciones permitidas. Puede escribir un catálogo y fallar en el otro; también puede devolver `true` sin haber actualizado ninguno. Estos catálogos influyen en acciones sobre Registro, servicios y programas.

Corrección necesaria: validar autenticidad, esquema, límites y operaciones; preparar ambos archivos antes de activarlos y conservar la pareja anterior. La selección local de catálogo más reciente incorporada al rediseño evita usar una caché antigua, pero no resuelve estos problemas de confianza.

### 6. La ruta Rust/Tauri puede informar éxito ante un fallo

Referencia: `src-tauri/src/optimizer.rs`, `perform_ota_self_update`; `src-tauri/src/main.rs`.

Ante hash incorrecto se imprime un mensaje de catálogos sincronizados. El `catch` también imprime éxito. No se fuerza un resultado fallido en esos casos. `verify_sha256(b"", expected_sha256)` calcula sobre bytes vacíos y descarta el resultado; no verifica el archivo descargado. URL/hash/ruta se interpolan en un script PowerShell sin una frontera segura de parámetros: una comilla puede romper el script o introducir instrucciones. No se realizó una prueba de explotación.

Corrección necesaria: ejecutar descarga, verificación y reemplazo con datos separados de instrucciones; devolver error real ante cada fallo y comprobar reinicio/recuperación. Esta variante no se compiló en la sesión porque Cargo no está disponible.

### 7. El bridge de desarrollo simula la OTA

Referencia: `dev-bridge.mjs`, rama `perform_ota_update`; `src/app.js`, confirmación del modal.

El bridge cambia `version.json`, devuelve `success: true` y dice que verificó integridad, pero no descarga ni calcula el hash del ejecutable. Incluso puede cambiar la versión a `1.1.0`. Por ello el éxito del modal web no prueba una OTA real.

Además, el bridge expone ejecución de comandos PowerShell sin autenticación y llama `server.listen(PORT)` sin limitar el host. Node escucha en direcciones no especificadas cuando se omite el host, lo que puede exponer el servidor a otras interfaces según la red/firewall. `Start-Blackwall.bat` solicita elevación y puede abrir ese bridge. **No se recomienda iniciar ese modo con privilegios administrativos ni exponerlo a la red.** [Comportamiento documentado por Node.js](https://nodejs.org/download/release/v24.21.0/docs/api/net.html).

### 8. El publicador y la documentación histórica requieren revisión

`Publish-Release.bat` compila dos veces y recalcula el hash final, pero no es una prueba de coherencia/actualización completa. No comprueba explícitamente cada fallo de compilación/firma y omite el recurso `blackwall-mark.png` usado por la ventana/cabecera nueva. Genera certificados autofirmados si no encuentra uno; eso no equivale a confianza pública del firmante. También finaliza procesos mediante `taskkill /F`.

No se ejecutó el publicador en esta revisión. La documentación principal se corrigió para no prometer arranque menor a 0,35 s, cero falsos positivos, seguridad militar, reversión total ni OTA silenciosa validada.

## Seguridad de la subida a Git

- Rama de revisión prevista: `review/muro-negro-docs-ota-audit`; destino remoto verificado: `https://github.com/DylanRico/BlackWallOptimizer.git`.
- Se incluyen código, catálogos existentes, documentos, pruebas con datos sintéticos, iconos/emblema y el `Blackwall.exe` portable previsto por este repositorio. El binario no se sube a `main` ni se publica como release.
- Se excluyen respaldos, capturas, informes de hardware reales, binarios de prueba, temporales, cachés, dependencias descargadas, compilación Rust y configuraciones locales.
- `.gitignore` se amplió para credenciales/certificados privados, `.env`, datos locales y ejecutables adicionales; la excepción intencional es `/Blackwall.exe`.
- Se inspeccionó el commit local todavía no publicado y se revisaron los archivos actuales. Las búsquedas de claves privadas, formatos de tokens GitHub/AWS, asignaciones de credenciales y rutas personales no detectaron coincidencias en los candidatos revisados. La inspección del ejecutable tampoco encontró rutas personales ni identificadores reales de las consultas de hardware realizadas durante la sesión.
- Los fixtures declaran datos de prueba y números de serie sintéticos. Las capturas/mediciones locales permanecen en `artifacts/`, excluido de Git.
- Se incorporó `scripts/check-publish-safety.mjs` para repetir la revisión sobre el índice y el historial saliente sin imprimir el valor de posibles secretos. Es un escáner de patrones específico; no garantiza ausencia de toda información confidencial ni sustituye una auditoría completa.
- Validación de esta preparación: 68 comprobaciones nativas sobre el `Blackwall.exe` que se subirá, 16 pruebas web/recursos y autoevaluación del escáner satisfactorias. La revisión inicial del índice y el historial saliente examinó 34 versiones de archivo sin coincidencias de los patrones controlados. Se revisaron expresamente los 30 archivos preparados para el commit.

Reproducción:

```powershell
node scripts/check-publish-safety.mjs --self-test
node scripts/check-publish-safety.mjs
node scripts/check-publish-safety.mjs --staged --history origin/main..HEAD
./scripts/Test-Ui.ps1
```

## Criterio para declarar la OTA lista

En una copia desechable de la aplicación, probar una actualización válida y los rechazos por hash/firma incorrectos, versión inválida/anterior, redirección no autorizada, archivo truncado y descarga interrumpida. Simular fallo al reemplazar y fallo del primer arranque; el binario anterior debe quedar utilizable. Probar también instalación sin permisos, ruta con espacios y concurrencia. La verificación debe comprobar la versión del proceso que reinició y la conservación del respaldo.

**Veredicto:** se pueden revisar y versionar estos cambios en una rama; no se debe anunciar ni distribuir todavía la implementación actual como OTA segura de producción.
