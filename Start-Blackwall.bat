@echo off
title Blackwall — Administrador
cd /d "%~dp0"

:: Verificar privilegios de Administrador (UAC)
net session >nul 2>&1
if %errorLevel% neq 0 (
    echo Solicitando permisos de Administrador (UAC) para auditoria WMI/SMART y Registro...
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b 0
)

if exist "%~dp0Blackwall.exe" (
    start "" "%~dp0Blackwall.exe"
    exit /b 0
)

if exist "src-tauri\target\release\blackwall.exe" (
    start "" "src-tauri\target\release\blackwall.exe"
    exit /b 0
)

echo Iniciando motor nativo Blackwall con privilegios de Administrador...
start /b node dev-bridge.mjs
timeout /t 1 /nobreak >nul
start "" msedge.exe --app=http://localhost:3847 --window-size=1380,860
