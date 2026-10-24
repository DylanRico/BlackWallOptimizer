use serde::{Deserialize, Serialize};
use std::os::windows::process::CommandExt;
use tokio::process::Command;

const CREATE_NO_WINDOW: u32 = 0x08000000;

#[derive(Debug, Serialize, Deserialize, Clone)]
pub struct HardwareReport {
    pub scanned_at: String,
    pub chassis: ChassisInfo,
    pub motherboard: MotherboardInfo,
    pub cpu: CpuInfo,
    pub memory: MemorySummary,
    pub storage: Vec<DiskDriveInfo>,
    pub gpus: Vec<GpuInfo>,
    pub battery: BatteryInfo,
    pub os_network: OsNetworkInfo,
}

#[derive(Debug, Serialize, Deserialize, Clone)]
pub struct ChassisInfo {
    pub is_laptop: bool,
    pub device_type: String,
    pub chassis_types: Vec<u32>,
    pub manufacturer: String,
    pub model: String,
    pub serial_number: String,
    pub sku: String,
    pub uuid: String,
}

#[derive(Debug, Serialize, Deserialize, Clone)]
pub struct MotherboardInfo {
    pub manufacturer: String,
    pub product: String,
    pub version: String,
    pub serial_number: String,
    pub bios_vendor: String,
    pub bios_version: String,
    pub bios_date: String,
    pub boot_mode: String,
    pub secure_boot: String,
    pub tpm_version: String,
}

#[derive(Debug, Serialize, Deserialize, Clone)]
pub struct CpuInfo {
    pub name: String,
    pub architecture: String,
    pub socket: String,
    pub physical_cores: u32,
    pub logical_threads: u32,
    pub base_clock_mhz: u32,
    pub current_clock_mhz: u32,
    pub l2_cache_kb: u32,
    pub l3_cache_kb: u32,
    pub virtualization_enabled: bool,
    pub load_percent: u32,
    pub temperature_c: Option<f32>,
}

#[derive(Debug, Serialize, Deserialize, Clone)]
pub struct MemorySlot {
    pub slot_name: String,
    pub bank_label: String,
    pub occupied: bool,
    pub capacity_gb: f64,
    pub speed_mhz: u32,
    pub mem_type: String,
    pub manufacturer: String,
    pub part_number: String,
    pub serial_number: String,
    pub voltage_mv: u32,
}

#[derive(Debug, Serialize, Deserialize, Clone)]
pub struct MemorySummary {
    pub total_gb: f64,
    pub used_gb: f64,
    pub available_gb: f64,
    pub usage_percent: u32,
    pub speed_mhz: u32,
    pub memory_type: String,
    pub channel_mode: String,
    pub total_slots: u32,
    pub occupied_slots: u32,
    pub slots: Vec<MemorySlot>,
}

#[derive(Debug, Serialize, Deserialize, Clone)]
pub struct DiskDriveInfo {
    pub friendly_name: String,
    pub serial_number: String,
    pub media_type: String,
    pub bus_type: String,
    pub partition_style: String,
    pub size_gb: f64,
    pub free_gb: f64,
    pub health_status: String,
    pub wear_or_life_remaining_pct: u32,
    pub temperature_c: Option<u32>,
    pub power_on_hours: Option<u64>,
    pub total_tb_written: Option<f64>,
}

#[derive(Debug, Serialize, Deserialize, Clone)]
pub struct GpuInfo {
    pub name: String,
    pub adapter_dac_type: String,
    pub vram_gb: f64,
    pub driver_version: String,
    pub driver_date: String,
    pub resolution: String,
    pub refresh_rate_hz: u32,
}

#[derive(Debug, Serialize, Deserialize, Clone)]
pub struct BatteryInfo {
    pub present: bool,
    pub status: String,
    pub charge_percent: u32,
    pub designed_capacity_mwh: u64,
    pub full_charged_capacity_mwh: u64,
    pub wear_level_percent: f64,
    pub health_percent: f64,
    pub chemistry: String,
    pub discharge_rate_mw: i64,
}

#[derive(Debug, Serialize, Deserialize, Clone)]
pub struct NetAdapterInfo {
    pub name: String,
    pub description: String,
    pub mac_address: String,
    pub link_speed: String,
    pub ipv4: String,
    pub status: String,
}

#[derive(Debug, Serialize, Deserialize, Clone)]
pub struct OsNetworkInfo {
    pub caption: String,
    pub display_version: String,
    pub build_number: String,
    pub install_date: String,
    pub uptime: String,
    pub partial_product_key: String,
    pub adapters: Vec<NetAdapterInfo>,
}

/// Ejecuta el escaneo profundo WMI/CIM/SMBIOS/SMART/Batería en un subproceso asíncrono sin bloquear el hilo UI.
pub async fn collect_deep_hardware_audit() -> Result<serde_json::Value, String> {
    let ps_script = include_str!("../../scripts/deep_hardware_scan.ps1");
    let output = Command::new("powershell.exe")
        .args([
            "-NoProfile",
            "-NonInteractive",
            "-ExecutionPolicy",
            "Bypass",
            "-Command",
            ps_script,
        ])
        .creation_flags(CREATE_NO_WINDOW)
        .output()
        .await
        .map_err(|e| format!("Error al invocar el motor CIM/WMI: {}", e))?;

    let stdout = String::from_utf8_lossy(&output.stdout);
    serde_json::from_str::<serde_json::Value>(stdout.trim())
        .map_err(|e| format!("Error parseando salida JSON de auditoría: {} | Raw: {}", e, stdout))
}
