@echo off
setlocal
title Blackwall - Preparar paquete OTA firmado (local, no publica)
echo Este asistente PREPARA un paquete firmado en artifacts\releases. No sube nada a GitHub.
set /p "BLACKWALL_RELEASE_VERSION=Version a preparar (debe coincidir con catalogs\version.json): "
if not defined BLACKWALL_RELEASE_VERSION exit /b 1
powershell.exe -NoProfile -File "%~dp0scripts\Prepare-Release.ps1"
if errorlevel 1 (
    echo [ERROR] No se ha preparado el paquete. Revisa el error anterior.
    exit /b 1
)
echo Paquete preparado localmente. No se han subido archivos.
exit /b 0
