@echo off
title WinApex — Publicador de Actualizacion Remota (OTA) para todos los PCs
cd /d "%~dp0"

echo ====================================================================
echo  PUBLICADOR DE NUEVA VERSION OTA DE WINAPEX
echo ====================================================================
set /p NEW_VER="Ingresa el nuevo numero de version (ej. 1.3.0): "
if "%NEW_VER%"=="" set "NEW_VER=1.3.0"

taskkill /F /IM WinApex.exe >nul 2>&1

set "FW=C:\Windows\Microsoft.NET\Framework64\v4.0.30319"
set "CSC=%FW%\csc.exe"

set "ICON_RES=/resource:%~dp0assets\icons\brave.png,brave.png /resource:%~dp0assets\icons\firefox.png,firefox.png /resource:%~dp0assets\icons\chrome.png,chrome.png /resource:%~dp0assets\icons\vcredist.png,vcredist.png /resource:%~dp0assets\icons\vcredist_2015_2022_x64.png,vcredist_2015_2022_x64.png /resource:%~dp0assets\icons\dotnet.png,dotnet.png /resource:%~dp0assets\icons\dotnet_desktop_8.png,dotnet_desktop_8.png /resource:%~dp0assets\icons\directx.png,directx.png /resource:%~dp0assets\icons\directx_enduser.png,directx_enduser.png /resource:%~dp0assets\icons\7zip.png,7zip.png /resource:%~dp0assets\icons\winrar.png,winrar.png /resource:%~dp0assets\icons\everything.png,everything.png /resource:%~dp0assets\icons\powertoys.png,powertoys.png /resource:%~dp0assets\icons\vscode.png,vscode.png /resource:%~dp0assets\icons\git.png,git.png /resource:%~dp0assets\icons\nodejs.png,nodejs.png /resource:%~dp0assets\icons\nodejs_lts.png,nodejs_lts.png /resource:%~dp0assets\icons\rust.png,rust.png /resource:%~dp0assets\icons\rustup.png,rustup.png /resource:%~dp0assets\icons\discord.png,discord.png /resource:%~dp0assets\icons\steam.png,steam.png /resource:%~dp0assets\icons\vlc.png,vlc.png /resource:%~dp0assets\icons\libreoffice.png,libreoffice.png"

echo [1/4] Compilando nuevo ejecutable WinApex.exe con catalogos e iconos HD...
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
  %ICON_RES% ^
  "%~dp0native-wpf\WinApexApp.cs"

echo [2/4] Firmando digitalmente WinApex.exe (Authenticode)...
powershell -NoProfile -Command "$c = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert | Where-Object { $_.Subject -match 'WinApex' } | Select-Object -First 1; if (-not $c) { $c = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=WinApex Engineering, O=WinApex Suite' -CertStoreLocation 'Cert:\CurrentUser\My' }; Set-AuthenticodeSignature -FilePath '%~dp0WinApex.exe' -Certificate $c | Out-Null"

echo [3/4] Calculando Hash SHA-256 oficial y actualizando catalogs\version.json a v%NEW_VER%...
powershell -NoProfile -Command "$h = (Get-FileHash -Path '%~dp0WinApex.exe' -Algorithm SHA256).Hash.ToLower(); $vPath = '%~dp0catalogs\version.json'; $j = Get-Content $vPath -Raw -Encoding UTF8 | ConvertFrom-Json; $j.appVersion = '%NEW_VER%'; $j.releaseDate = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ'); $j.binary.sha256 = $h; $j | ConvertTo-Json -Depth 5 | Set-Content $vPath -Encoding UTF8; Write-Host ('Hash SHA-256 generado: ' + $h)"

echo [4/4] Re-empaquetando WinApex.exe con el manifiesto actualizado...
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
  %ICON_RES% ^
  "%~dp0native-wpf\WinApexApp.cs"

powershell -NoProfile -Command "$c = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert | Where-Object { $_.Subject -match 'WinApex' } | Select-Object -First 1; Set-AuthenticodeSignature -FilePath '%~dp0WinApex.exe' -Certificate $c | Out-Null; $h = (Get-FileHash -Path '%~dp0WinApex.exe' -Algorithm SHA256).Hash.ToLower(); $vPath = '%~dp0catalogs\version.json'; $j = Get-Content $vPath -Raw -Encoding UTF8 | ConvertFrom-Json; $j.binary.sha256 = $h; $j | ConvertTo-Json -Depth 5 | Set-Content $vPath -Encoding UTF8"

echo ====================================================================
echo  EXITO: Version v%NEW_VER% lista para publicar en tu repositorio!
echo  Archivos que debes subir a tu repositorio GitHub:
echo    1. WinApex.exe (en GitHub Releases o en la raiz del repo)
echo    2. catalogs\version.json
echo    3. catalogs\tweaks.json y catalogs\apps.json
echo ====================================================================
pause
