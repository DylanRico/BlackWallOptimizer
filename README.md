# 🔴 BLACKWALL // CYBERPUNK WINDOWS ARCHITECTURE & ICE PROTOCOL

```
  ██████╗ ██╗      █████╗  ██████╗██╗  ██╗██╗    ██╗ █████╗ ██╗     ██╗     
  ██╔══██╗██║     ██╔══██╗██╔════╝██║ ██╔╝██║    ██║██╔══██╗██║     ██║     
  ██████╔╝██║     ███████║██║     █████╔╝ ██║ █╗ ██║███████║██║     ██║     
  ██╔══██╗██║     ██╔══██║██║     ██╔═██╗ ██║███╗██║██╔══██║██║     ██║     
  ██████╔╝███████╗██║  ██║╚██████╗██║  ██╗╚███╔███╔╝██║  ██║███████╗███████╗
  ╚═════╝ ╚══════╝╚═╝  ╚═╝ ╚═════╝╚═╝  ╚═╝ ╚══╝╚══╝ ╚═╝  ╚═╝╚══════╝╚══════╝
              [ MILITARY-GRADE ICE PROTOCOL // NETWATCH LEVEL 5 ]
```

> **BLACKWALL** es una suite de escritorio portable de alto rendimiento, inspirada en la barrera digital de *Cyberpunk 2077*, diseñada para aislar telemetría, erradicar bloatware parásito, auditar hasta el último componente de silicio y desplegar software sin dejar rastro en Windows 10 y 11.
> 
> *Construida en arquitectura C# nativa sin scripts lentos de PowerShell. Peso inferior a 1 MB, arranque en <0.35s y 0 falsos positivos en antivirus.*

---

## ⚡ Módulos del Protocolo BLACKWALL

### 1. 🔍 Deep Hardware & Silicon Audit (< 0.35s)
Inspección profunda mediante llamadas nativas al kernel (WMI/CIM y SMBIOS):
- **Identificación OEM y Chasis:** Detección automática de Laptop vs. Desktop / Estación de combate. Marca, Modelo exacto, Número de Serie de placa y BIOS.
- **Placa Base y Seguridad:** UEFI/Legacy, Secure Boot y versión física de TPM (TPM 2.0).
- **Procesador (CPU):** Arquitectura, Socket, núcleos físicos/hilos, frecuencias Base/Boost, memorias caché L1/L2/L3 y soporte de virtualización.
- **Memoria RAM Slot-by-Slot:** Desglose módulo por módulo (`Win32_PhysicalMemory`): fabricante, número de parte, serial y canales activos (Dual/Quad Channel).
- **Almacenamiento NVMe/SSD:** Estado de salud (%), horas de encendido, lectura S.M.A.R.T. y estilo de partición GPT.
- **Batería y Desgaste (Laptops):** Capacidad de diseño vs. Carga completa, porcentaje exacto de **Wear Level (Desgaste)** y tasa de descarga en tiempo real.

### 2. 🛡️ ICE Debloat Barrier (Aislamiento de Telemetría)
- **Eliminación Quirúrgica:** Purga selectiva de aplicaciones basura preinstaladas (AppX/MSIX), Cortana, Copilot, Widgets y publicidad del menú Inicio.
- **Semáforo de Riesgo:**
  - 🟢 **Bajo (Seguro):** Optimización estándar recomendada para todos los equipos.
  - 🟡 **Medio (Precaución):** Desactiva servicios secundarios opcionales.
  - 🔴 **Alto (Overdrive):** Modificaciones avanzadas de latencia.
- **Reversión Total (*Undo*):** Cada tweak puede activarse o deshacerse en caliente.
- **Punto de Restauración Automático:** Crea un punto de recuperación de Windows antes de tocar el sistema.

### 3. ⚡ Perfiles de Energía Adaptativos (Laptop vs. Desktop)
- **Modo Laptop (Cyberware Efficiency):** Gestión térmica optimizada, control de *Modern Standby* y suspensión selectiva sin drenar la batería.
- **Modo Desktop (Max Overdrive):** Plan *Ultimate Performance*, reducción de latencia de red, HAGS, Game Mode y desactivación de ahorros PCIe/USB.

### 4. 📦 Blackwall Package Hub (Instalador Desatendido Dual)
- Catálogo de software esencial (Brave, Chrome, Firefox, VS Code, Git, Discord, Steam, VLC, PowerToys, etc.).
- **Iconografía Oficial HD:** Iconos maestros vectoriales y de alta definición integrados en el binario con encuadre Fluent.
- **Motor Dual Autónomo:** Integración con **WinGet** + descargador directo **HTTPS nativo de C#** con barra de progreso por si el PC no tiene WinGet configurado.

### 5. 📡 Daemon & Script Monitor en Vivo
- Detección en tiempo real de motores de scripts en ejecución (`powershell.exe`, `cmd.exe`, `wscript.exe`, `mshta.exe`), exponiendo su PID, ruta, usuario, consumo y su **línea de comandos exacta (`CommandLine`)**, con opción de terminación inmediata (`Kill`).

### 6. 🚀 Actualizaciones Remotas Silenciosas (OTA Zero-Lock)
- Consulta de manifiesto remoto en GitHub/servidor propio con validación criptográfica **SHA-256**.
- Actualización en caliente mediante reemplazo de binario (*Zero-Lock technique*).

---

## 🚀 Despliegue Rápido (Netrunner Quickstart)

1. Descarga el ejecutable: **`Blackwall.exe`** (~946 KB).
2. Haz clic derecho y selecciona **"Ejecutar como administrador"**.
3. ¡Listo! 100% portable, no requiere instalación ni paquetes adicionales.

---

## 🛠️ Compilación y Desarrollo

```cmd
:: 1. Compilar y firmar el binario localmente (4 segundos):
Build-Portable-Exe.bat

:: 2. Publicar una nueva versión OTA para todos los clientes:
Publish-Release.bat
```

---

## 📄 Licencia

Licencia MIT — Desarrollado por Dylan. Libre para uso personal, gaming y despliegues técnicos.
