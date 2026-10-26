@echo off
setlocal
title Blackwall - Compilador portable WPF
powershell.exe -NoProfile -File "%~dp0scripts\Build-Native.ps1" -OutputPath "%~dp0Blackwall.exe"
if errorlevel 1 (
    echo [ERROR] No se ha actualizado Blackwall.exe. Revisa el error anterior.
    exit /b 1
)
echo EXITO: Blackwall.exe actualizado con el codigo, diseno, catalogos y logo locales.
exit /b 0
