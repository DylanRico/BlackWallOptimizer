# Blackwall — Muro Negro

Utilidad portable para Windows 10/11 que audita el hardware, aplica y revierte optimizaciones de rendimiento y privacidad, instala software habitual y muestra en vivo todo lo que hace. Escrita en C# y WPF sobre .NET Framework; un único ejecutable, sin instalador.

**Descarga:** la última versión firmada está en [Releases](https://github.com/DylanRico/BlackWallOptimizer/releases).

## Funciones

| Sección | Qué hace |
| --- | --- |
| Hardware | Auditoría WMI nativa de equipo, firmware, CPU, memoria por ranura, discos (S.M.A.R.T.), GPU, batería, Windows y red. Exporta informes en HTML, Excel, Markdown, texto, CSV y JSON, con opción para ocultar datos identificativos. |
| Rendimiento y Privacidad | Catálogo de optimizaciones con nivel de riesgo, detalles, aplicación y reversión. Perfiles de un clic; las avanzadas quedan agrupadas aparte. |
| Software | Instalación desatendida con WinGet y respaldo por descarga directa del fabricante. |
| Actividad | Operaciones en curso con sus pasos, registro filtrable y exportable, y scripts en segundo plano con confirmación antes de terminarlos. |
| Seguridad | Puntos de restauración, SFC, DISM, limpieza de DNS y copia del registro. |

Antes de aplicar un ajuste conviene leer sus detalles: revertir restaura los valores de fábrica de Windows, no necesariamente la configuración exacta que tenías.

## Requisitos

- Windows 10 u 11 de 64 bits con .NET Framework 4.8 (incluido en Windows).
- Permisos de administrador: la app los solicita al abrirse.

## Compilar

```powershell
.\Build-Portable-Exe.bat                         # compila Blackwall.exe en la raíz (con respaldo del anterior)
.\scripts\Build-Preview.ps1                      # compila artifacts\Blackwall.preview.exe para revisar
```

`scripts\Build-Native.ps1` es el único punto de compilación: valida herramientas, fuentes, icono y catálogos antes de compilar, embebe catálogos, clave pública e iconos, y comprueba la versión y los recursos del ejecutable resultante.

## Probar

```powershell
.\scripts\Test-Ui.ps1                            # interfaz WPF, motor de optimizaciones, informes y compilación
.\scripts\Test-Ota.ps1                           # protocolo, firma y transacciones de actualización
node .\scripts\check-publish-safety.mjs          # busca claves, tokens y rutas personales antes de publicar
```

Las pruebas no aplican ajustes reales ni instalan programas; el motor se prueba sobre una clave temporal de `HKCU` que se elimina al terminar.

## Publicar una versión (OTA firmada)

1. Actualiza `catalogs/version.json` (versión y notas).
2. `.\Publish-Release.bat` prepara `artifacts\releases\<versión>\` con `Blackwall.exe` y `update.json` firmado (RSA-3072). Necesita la clave privada del publicador, protegida con DPAPI fuera del proyecto.
3. Crea la release `v<versión>` en GitHub con esos dos archivos.
4. Comprueba la descarga pública y solo entonces publica el mismo `update.json` en `catalogs/update.json` de `main`.

Los clientes verifican firma, versión, tamaño y SHA-256 antes de instalar y conservan el ejecutable anterior para recuperarse si el nuevo no arranca. La firma OTA no es una firma Authenticode: Windows puede mostrar «editor desconocido».

## Estructura

```
native-wpf/   código de la aplicación (BlackwallApp.cs, OtaUpdate.cs)
catalogs/     optimizaciones, aplicaciones, versión e índice OTA firmado
assets/       iconos, emblema y clave pública de actualizaciones
scripts/      compilación, publicación, pruebas y mediciones
tests/        pruebas y benchmarks de la interfaz nativa
```
