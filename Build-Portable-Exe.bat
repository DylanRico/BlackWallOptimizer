@echo off
title WinApex — Compilador de Ejecutable Nativo Certificado (.exe)
cd /d "%~dp0"

taskkill /F /IM WinApex.exe >nul 2>&1
taskkill /F /IM WinApex-Admin.exe >nul 2>&1

set "FW=C:\Windows\Microsoft.NET\Framework64\v4.0.30319"
set "CSC=%FW%\csc.exe"

echo Compilando WinApex.exe con recursos de catalogos e iconos HD integrados...
"%CSC%" /nologo /target:winexe /optimize+ ^
  "/out:%~dp0WinApex.exe" ^
  "/win32icon:%~dp0src-tauri\icons\icon.ico" ^
  "/r:%FW%\WPF\PresentationFramework.dll" ^
  "/r:%FW%\WPF\PresentationCore.dll" ^
  "/r:%FW%\WPF\WindowsBase.dll" ^
  "/r:%FW%\System.Xaml.dll" ^
  "/r:%FW%\System.Web.Extensions.dll" ^
  "/r:%FW%\System.Management.dll" ^
  "/resource:%~dp0catalogs\tweaks.json,tweaks.json" ^
  "/resource:%~dp0catalogs\apps.json,apps.json" ^
  "/resource:%~dp0catalogs\version.json,version.json" ^
  "/resource:%~dp0assets\icons\brave.png,brave.png" ^
  "/resource:%~dp0assets\icons\firefox.png,firefox.png" ^
  "/resource:%~dp0assets\icons\chrome.png,chrome.png" ^
  "/resource:%~dp0assets\icons\vcredist.png,vcredist.png" ^
  "/resource:%~dp0assets\icons\vcredist_2015_2022_x64.png,vcredist_2015_2022_x64.png" ^
  "/resource:%~dp0assets\icons\dotnet.png,dotnet.png" ^
  "/resource:%~dp0assets\icons\dotnet_desktop_8.png,dotnet_desktop_8.png" ^
  "/resource:%~dp0assets\icons\directx.png,directx.png" ^
  "/resource:%~dp0assets\icons\directx_enduser.png,directx_enduser.png" ^
  "/resource:%~dp0assets\icons\7zip.png,7zip.png" ^
  "/resource:%~dp0assets\icons\winrar.png,winrar.png" ^
  "/resource:%~dp0assets\icons\everything.png,everything.png" ^
  "/resource:%~dp0assets\icons\powertoys.png,powertoys.png" ^
  "/resource:%~dp0assets\icons\vscode.png,vscode.png" ^
  "/resource:%~dp0assets\icons\git.png,git.png" ^
  "/resource:%~dp0assets\icons\nodejs.png,nodejs.png" ^
  "/resource:%~dp0assets\icons\nodejs_lts.png,nodejs_lts.png" ^
  "/resource:%~dp0assets\icons\rust.png,rust.png" ^
  "/resource:%~dp0assets\icons\rustup.png,rustup.png" ^
  "/resource:%~dp0assets\icons\discord.png,discord.png" ^
  "/resource:%~dp0assets\icons\steam.png,steam.png" ^
  "/resource:%~dp0assets\icons\vlc.png,vlc.png" ^
  "/resource:%~dp0assets\icons\libreoffice.png,libreoffice.png" ^
  "%~dp0native-wpf\WinApexApp.cs"

if %ERRORLEVEL% NEQ 0 (
    echo [ERROR] La compilacion ha fallado. Revisa los mensajes de error arriba.
    exit /b %ERRORLEVEL%
)

echo Firmando digitalmente con certificado Authenticode...
powershell -NoProfile -Command "$c = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert | Where-Object { $_.Subject -match 'WinApex' } | Select-Object -First 1; if (-not $c) { $c = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=WinApex Engineering, O=WinApex Suite' -CertStoreLocation 'Cert:\CurrentUser\My' }; Set-AuthenticodeSignature -FilePath '%~dp0WinApex.exe' -Certificate $c | Out-Null"

echo ====================================================================
echo EXITO: WinApex.exe compilado con iconos HD integrados y certificado.
echo ====================================================================
