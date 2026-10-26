# Blackwall: actualizaciones WPF firmadas

Implementación local del 25 de octubre de 2026, publicada el 26 de octubre de 2026. Sustituye el flujo WPF experimental descrito en `SEGURIDAD-Y-ESTADO-OTA.md`. La release `v1.4.1` y el manifiesto firmado están publicados; se verificó su descarga desde GitHub con el cliente OTA real. Rust/Tauri y el bridge de desarrollo conservan sus implementaciones anteriores y no están cubiertos por este protocolo.

## Versiones y compilación

- `catalogs/version.json` es metadato de compilación: versión predeterminada local `1.4.4`, dirección del índice firmado y notas. Ya no contiene el hash de su propio ejecutable. La URL se integra en el ejecutable: no existe campo editable ni se lee una URL sustitutiva desde AppData. Cambiar el metadato local no publica ni instala una OTA.
- `scripts/Build-Native.ps1` genera los atributos de versión del ensamblado y el recurso `version.json` desde el mismo valor. No hay una segunda versión fija en C#.
- `Prepare-Release.ps1 -Version 1.4.1` aplica esa versión tanto al PE como al metadato embebido del paquete, sin cambiar la versión predeterminada del árbol ni reemplazar el portable raíz.
- El paquete preparado es `artifacts/releases/1.4.1/Blackwall.exe` junto con `update.json`. El ejecutable inicial con el verificador y la clave pública se compila como `Blackwall.exe` raíz, versión `1.4.0`.
- Cada carpeta de versión preparada es inmutable para el publicador: si existe, el script falla en lugar de sobrescribirla. Los borradores anteriores conservados en otras carpetas de `artifacts` no son entregas publicadas.

Para compilar el portable actual:

```powershell
.\Build-Portable-Exe.bat
```

Para preparar otra versión, después de cambiar y comprobar el código:

```powershell
powershell -NoProfile -File .\scripts\Prepare-Release.ps1 -Version 1.4.2
```

`Publish-Release.bat` solicita la versión y llama a ese preparador. La entrada se pasa por una variable de entorno, sin insertarla en código de shell. No sube archivos, crea certificados Authenticode, cambia el almacén de confianza, mata instancias ni recompila después de calcular el hash firmado.

## Confianza y claves

El paquete usa RSA de 3072 bits, PKCS#1 v1.5 y SHA-256. El cliente verifica con `assets/update-public-key.xml`, embebido como recurso del ejecutable. La clave privada se creó en `%LOCALAPPDATA%\BlackwallPublisher\update-signing.dpapi`, fuera del proyecto, protegida con DPAPI para el usuario actual y permisos de archivo para ese usuario y SYSTEM. No se ha mostrado ni subido su contenido.

`Initialize-UpdateKey.ps1` conserva una identidad existente y falla si falta uno de sus dos archivos; no genera otra clave silenciosamente. El preparador comprueba que la clave privada corresponde a la pública antes de construir o firmar.

**Antes de distribuir clientes, respalda la clave.** DPAPI depende del perfil de Windows. La pérdida del perfil sin un respaldo recuperable impediría firmar actualizaciones que acepten los clientes existentes.

```powershell
# El directorio externo debe existir. Solicita la contraseña sin mostrarla.
powershell -NoProfile -File .\scripts\Backup-UpdateKey.ps1 -Path 'E:\RespaldoOffline\Blackwall-OTA.pfx'

# Restaurar en otro perfil/equipo conservando la misma clave pública del proyecto:
powershell -NoProfile -File .\scripts\Backup-UpdateKey.ps1 -Restore -Path 'E:\RespaldoOffline\Blackwall-OTA.pfx'
```

Usa una contraseña larga y exclusiva, guardada por separado. El PFX final se cifra con AES-256/SHA-256 mediante [PKI de Windows](https://learn.microsoft.com/en-us/powershell/module/pki/export-pfxcertificate). La restauración compara la identidad pública, trabaja sobre una copia temporal en memoria y vuelve a proteger la clave con DPAPI; no sobrescribe una identidad existente. Las pruebas verificaron respaldo/restauración con una clave desechable, rechazo de contraseña incorrecta y ausencia de cambios en el almacén de certificados. **El propietario informó haber generado y copiado su respaldo; no se abrió ni comprobó el PFX real o su restauración offline.**

No incluyas `.dpapi`, `.pfx` ni contraseñas en Git. El escáner también rechaza componentes privados RSA en XML. La firma OTA acredita el paquete frente a la clave incorporada; no equivale a una firma Authenticode ni a reputación de SmartScreen.

## Contrato `update.json`

El índice remoto es un sobre JSON con exactamente dos cadenas:

```json
{"payload":"BASE64_DE_LOS_BYTES_JSON","signature":"BASE64_FIRMA_RSA_SHA256"}
```

Se verifican los bytes decodificados antes de interpretar el contenido firmado. No se reconstruye ni normaliza JSON para comprobar la firma. El contenido firmado tiene exactamente estos campos:

```json
{
  "schemaVersion": 1,
  "appVersion": "1.4.1",
  "binary": {
    "url": "https://github.com/DylanRico/BlackWallOptimizer/releases/download/v1.4.1/Blackwall.exe",
    "sha256": "64_caracteres_hexadecimales_minusculos",
    "size": 1234567
  },
  "changelog": ["Descripción de los cambios"]
}
```

El ejemplo muestra la forma del contrato; no es un manifiesto instalable. Solo utiliza el `update.json` generado junto con su binario real. La versión admite tres componentes numéricos canónicos; no acepta versiones mal formadas, campos desconocidos, hash ausente, tamaños fuera de 40 KiB–100 MiB ni notas sin límites. El sobre tiene un límite de 256 KiB y el contenido firmado de 64 KiB. Se comprueban hash, tamaño y versión PE del binario. La instalación rechaza versiones iguales o anteriores.

La dirección predeterminada del índice es `https://raw.githubusercontent.com/DylanRico/BlackWallOptimizer/main/catalogs/update.json`. Es un contrato nuevo; `catalogs/version.json` dejó de ser el índice de distribución. **Los clientes antiguos requieren instalar manualmente una primera versión con verificación y clave pública, obtenida por un canal de confianza.** No se emplea la OTA antigua sin firma para esa transición.

## Comprobación, descarga e instalación

1. Al abrir la aplicación se consulta en segundo plano; un error de red no bloquea el arranque. El botón de actualizaciones permite comprobar y revisar las notas manualmente.
2. Solo se aceptan HTTPS y certificados que valide Windows. Se comprueba cada redirección para impedir una bajada a HTTP. Hay un límite de redirecciones, tamaño, tiempos de lectura/respuesta y duración total de la descarga.
3. El usuario solicita descargar e instalar. Se vuelve a comprobar el manifiesto y que versión, hash y tamaño sigan siendo los revisados. El progreso usa bytes reales. Cerrar el modal cancela antes del reemplazo.
4. El paquete incluye los catálogos embebidos, protegidos por la misma firma del ejecutable. La antigua caché de catálogos sin firma se conserva en disco pero no se carga ni se sincroniza independientemente.
5. Una copia del propio cliente actúa como instalador. Valida de nuevo firma y binario; obtiene el destino del proceso padre y verifica su identidad. Un plan remoto no puede elegir otro archivo de destino.
6. Los bloqueos impiden instalaciones simultáneas. El cliente solo cierra después de que el instalador confirme que está preparado; este espera la salida del padre antes de sustituir el archivo.
7. El reemplazo crea `.previous.<id>`. La nueva aplicación confirma que ha renderizado su contenido y sigue viva durante la comprobación. Si falla, se restaura el ejecutable anterior y se conserva la candidata como `.failed.<id>`. Se avisa en el estado/consola de la versión anterior.

No se elevan permisos para instalar una actualización. Desde la candidata 1.4.3 la UI normal solicita UAC al abrir; al aceptar «Actualizar ahora», pide al shell interactivo abrir el mismo ejecutable en un modo interno de actualización sin elevación, ligado a la versión y hash elegidos. Ese proceso vuelve a verificar el manifiesto y utiliza el instalador existente. Sus rechazos de elevación, firma/hash, bloqueo, respaldo y rollback permanecen activos. Una carpeta sin permiso de escritura provoca un error; no se amplían sus permisos. La nueva versión confirma primero su arranque sin elevación y, cuando el instalador registra éxito, solicita UAC para la reapertura normal. Si UAC se cancela después de actualizar, la versión instalada permanece abierta sin elevación hasta volver a abrirla. La transición interactiva completa entre tokens requiere validación manual; el informe 1.4.3 distingue los checks simulados y la prueba del shell de una OTA real.

El instalador solo puede cerrar el proceso candidato que acaba de lanzar cuando este no confirma salud ni responde al cierre. Las pruebas de procesos se hicieron sobre ejecutables desechables, sin actualizar una instalación real ni terminar aplicaciones del usuario.

## Publicación y comprobación real

Secuencia de publicación (binario y manifiesto completados el 26 de octubre de 2026):

1. Distribuir por un canal de confianza el primer cliente con la clave pública; en esta entrega es el portable inicial `1.4.0`.
2. Crear una release de GitHub con etiqueta `v1.4.1` y subir `artifacts/releases/1.4.1/Blackwall.exe`, sin editar ni volver a firmar ese archivo. Su URL debe ser exactamente la del contenido firmado.
3. Cuando el binario sea accesible, publicar **el mismo** `artifacts/releases/1.4.1/update.json` como `catalogs/update.json` en `main`.
4. Comprobar desde una copia instalada que el servidor real entrega los bytes y la firma preparados, y ejecutar una actualización controlada antes de ampliar la distribución.

Publicados: [release v1.4.1](https://github.com/DylanRico/BlackWallOptimizer/releases/tag/v1.4.1) y [manifiesto](https://github.com/DylanRico/BlackWallOptimizer/blob/main/catalogs/update.json), commit `419e24d4a1e9c88aa7cc6bf414e4507898f77e78`. Se verificó el binario público de 1.232.896 bytes y SHA-256 `6f8d486d097618716a05990a54324517693c53c85d055a4ce687ad166caa1cca`. El código C# productivo de descarga/verificación pasó contra GitHub real: HTTPS, firma, tamaño, hash, versión PE y progreso por bytes. No se instaló automáticamente sobre una aplicación del usuario.

La etiqueta apunta al estado remoto previo de `main`; la publicación no sincronizó los cambios locales completos de código. La release lo aclara: el asset `Blackwall.exe` es el portable firmado; los archivos fuente automáticos de GitHub todavía no representan todos sus cambios.

Copia inicial `1.4.0.0` preparada para la prueba manual: `%USERPROFILE%\Blackwall-Prueba-OTA\Blackwall.exe`. Abrir directamente sin administrador y usar Actualizaciones para comprobar la instalación/reinicio a `1.4.1`. La interacción visible, la instalación completa desde GitHub y otro equipo siguen pendientes.

## Registros y recuperación

Los archivos temporales de comprobación se limpian; los paquetes e instaladores preparados quedan en `%LOCALAPPDATA%\Blackwall\Updates\<id>`. `result.txt` registra el resultado del instalador. Junto al ejecutable, `.ota-state.json` registra las fases y la ruta del respaldo. Los archivos `.ota-session.lock` y `.ota.lock` son bloqueos, no indicadores de éxito.

Los respaldos no se borran automáticamente al iniciar. En una recuperación manual, cierra las instancias y conserva el binario fallido antes de restaurar el `.previous.<id>` indicado por el registro. No copies sobre un ejecutable en uso.

La recuperación automática cubre los fallos gestionados de reemplazo y de arranque probado. Un corte de energía o la terminación forzada del instalador puede requerir recuperación manual desde el respaldo. El diario es diagnóstico, no una garantía de transacción frente a una avería del disco. La señal de salud comprueba arranque/render y supervivencia inicial, no certifica todas las funciones ni el estado del sistema.

## Comprobaciones reproducibles

```powershell
powershell -NoProfile -File .\scripts\Test-Ui.ps1
powershell -NoProfile -File .\scripts\Test-Ota.ps1 -PackagePath .\artifacts\releases\1.4.1
powershell -NoProfile -File .\tests\Test-UpdateKey.ps1
node .\scripts\Test-OtaNetwork.mjs
node .\scripts\check-publish-safety.mjs --self-test
node .\scripts\check-publish-safety.mjs
```

El último check de red requiere Postman CLI y OpenSSL de Git for Windows. Ejecuta el cliente C# real frente a una simulación local de Postman y un relay TLS con certificado efímero. Solo el proceso de prueba acepta ese certificado exacto; la aplicación distribuida no incorpora excepciones TLS. No se creó ni publicó un workspace o mock remoto. Contrato y contexto: `postman/specs/ota.yaml` y `postman/simulations/ota.context.md`.

La evidencia queda en `artifacts/ota-*-checks.log`, `artifacts/ota-network/`, `artifacts/github-live-check/` y las carpetas de copias de prueba. Se mantiene excluida de Git. La interfaz visible, la instalación completa contra GitHub, otros equipos y una interrupción física de energía siguen sin verificar.

Resultado local: 106 comprobaciones WPF y 26 capturas fuera de pantalla, 16 pruebas web, 12 casos del compilador, 44 casos OTA de protocolo/transacción/subprocesos, 11 casos HTTPS/Postman y 5 casos de respaldo/restauración con identidad desechable. El escáner revisó 91 archivos/versiones publicables sin hallazgos; no constituye una garantía de ausencia de secretos. El portable raíz se recompiló y verificó como `1.4.0.0`; el paquete preparado conserva `1.4.1.0`. Las capturas fuera de pantalla no sustituyen la validación de una ventana visible en otro Windows.
