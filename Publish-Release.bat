@echo off
setlocal
title Blackwall - Preparar paquete OTA firmado
set /p "BLACKWALL_RELEASE_VERSION=Version a preparar (mayor.menor.parche): "
if not defined BLACKWALL_RELEASE_VERSION exit /b 1
powershell.exe -NoProfile -File "%~dp0scripts\Prepare-Release.ps1"
if errorlevel 1 exit /b 1
echo Paquete preparado localmente. No se han subido archivos.
exit /b 0
