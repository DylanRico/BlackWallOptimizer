# ⚡ WinApex — Ultimate Windows 10 & 11 Suite

> **Herramienta portable de optimización adaptativa, auditoría profunda de hardware, debloat seguro e instalación desatendida para Windows 10 y 11.**  
> *Inspirada en la filosofía de Chris Titus Tech (WinUtil), pero reescrita en C# nativo sin dependencias de scripts externos para ofrecer máxima velocidad (<0.35s), cero falsos positivos en antivirus y un ejecutable de menos de 1 MB.*

---

## 🌟 Características Principales

- 🔍 **Auditoría Profunda de Hardware (< 0.35s):**
  - Detección automática de chasis (Laptop vs. Desktop/Torre).
  - Placa Base, BIOS/UEFI, Secure Boot y versión de TPM 2.0.
  - Procesador (frecuencias, núcleos, hilos, cachés L1/L2/L3, virtualización).
  - Memoria RAM total y **desglose por slot físico** (marca, número de parte, serie, voltaje).
  - Almacenamiento NVMe/SSD con lectura de salud y particionado GPT/MBR.
  - Batería (exclusivo Laptops): cálculo de desgaste (*Wear Level*), salud real y tasa de descarga.
- ⚡ **Optimización Adaptativa (Laptop vs. Desktop):**
  - **Perfil Laptop:** Eficiencia térmica, control de Modern Standby y suspensión selectiva.
  - **Perfil Desktop:** Máxima potencia, baja latencia, plan Ultimate Performance y ajuste de red.
- 🛡️ **Debloat Seguro y Reversible:**
  - Desinstalación de apps basura (AppX), telemetría, Cortana, Copilot, widgets y Bing search.
  - Semáforo de riesgo claro (🟢 Bajo, 🟡 Medio, 🔴 Alto) y comandos reversibles (*Undo*).
  - Creación automática de Punto de Restauración antes de modificar el sistema.
- 📦 **Instalador Desatendido Dual con Iconos HD:**
  - Catálogo de aplicaciones indispensables (Brave, Chrome, Firefox, VS Code, Git, Discord, Steam, etc.).
  - Motor dual inteligente: **WinGet Nativo** + descarga directa **HTTPS oficial de respaldo** con barra de progreso.
  - Iconos oficiales modernos en alta definición integrados en el binario.
- 📡 **Monitor de Scripts en Vivo:**
  - Muestra procesos activos (`powershell.exe`, `cmd.exe`, `wscript.exe`, etc.) con su línea de comandos exacta y consumo de CPU/RAM.
- 🚀 **Actualizaciones Remotas (OTA - Over The Air):**
  - Consulta remota de novedades con verificación de integridad criptográfica SHA-256 y reemplazo en caliente (*Zero-Lock*).

---

## 🚀 Inicio Rápido

1. Descarga el ejecutable único: **`WinApex.exe`** (pesa menos de 1 MB).
2. Haz clic derecho y selecciona **"Ejecutar como administrador"**.
3. ¡Listo! No requiere instalación, no extrae archivos temporales y no requiere conexión inicial.

---

## 🛠️ Compilación y Desarrollo

WinApex está construido en **C# nativo (.NET 4.0/4.8 + WPF)**. No requiere Visual Studio ni descargas gigantes:

- **Compilar el ejecutable:** Doble clic en `Build-Portable-Exe.bat`.
- **Publicar una nueva versión OTA:** Doble clic en `Publish-Release.bat` e ingresa el nuevo número de versión.

---

## 📄 Licencia

Este proyecto está bajo la Licencia MIT. Libre para uso personal y corporativo.
