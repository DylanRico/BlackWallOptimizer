// Synthetic data for visual checks. This module never inspects or changes Windows.
export const hardwareFixture = {
  scannedAt: "VISTA PREVIA · DATOS DE PRUEBA",
  chassis: { isLaptop: false, deviceType: "Desktop · prueba", manufacturer: "VISTA PREVIA", model: "Blackwall Workstation", serialNumber: "TEST-0001", sku: "TEST", uuid: "00000000-0000-0000-0000-000000000000" },
  motherboard: { manufacturer: "Fabricante de prueba", product: "Placa base", serialNumber: "TEST-MB", bootMode: "UEFI", biosVersion: "TEST 1.0", biosDate: "24/10/2026", secureBoot: "Habilitado · prueba", tpmVersion: "TPM 2.0 · prueba" },
  cpu: { name: "Procesador de prueba / 12 núcleos", architecture: "x64", socket: "TEST", physicalCores: 12, logicalThreads: 24, baseClockMhz: 3200, currentClockMhz: 4500, l2CacheKb: 12288, l3CacheKb: 32768, virtualizationEnabled: true, loadPercent: 8, temperatureC: null },
  memory: { totalGb: 32, memoryType: "DDR5", speedMhz: 5600, channelMode: "Dual Channel", occupiedSlots: 2, totalSlots: 4, usedGb: 10, usagePercent: 31, slots: [{ slotName: "DIMM_A2", bankLabel: "BANK 0", occupied: true, capacityGb: 16, memType: "DDR5", speedMhz: 5600, manufacturer: "Módulo de prueba", partNumber: "TEST-DDR5-16", serialNumber: "TEST-RAM", voltageMv: 1100 }, { slotName: "DIMM_B2", bankLabel: "BANK 1", occupied: false }] },
  storage: [{ friendlyName: "Unidad NVMe de prueba", busType: "NVMe", mediaType: "SSD", partitionStyle: "GPT", sizeGb: 1000, freeGb: 640, serialNumber: "TEST-SSD", healthStatus: "Normal · prueba", healthPercent: 100 }],
  gpus: [{ name: "Adaptador gráfico de prueba", vramGb: 8, driverVersion: "TEST", driverDate: "24/10/2026", resolution: "2560 × 1440", refreshRateHz: 144 }],
  battery: { present: false, status: "Sin batería", wearLevelPercent: 0, healthPercent: 0, chargePercent: 0, designedCapacityMwh: 0, fullChargedCapacityMwh: 0, dischargeRateMw: 0, chemistry: "No aplica" },
  osNetwork: { caption: "Windows 11 · prueba", displayVersion: "TEST", buildNumber: "TEST", installDate: "24/10/2026", uptime: "2 h · prueba", partialProductKey: "*****", adapters: [{ name: "Ethernet de prueba", linkSpeed: "1 Gbps", ipv4: "192.0.2.10", macAddress: "00:00:00:00:00:00" }] }
};
