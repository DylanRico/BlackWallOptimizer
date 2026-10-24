$ErrorActionPreference = 'SilentlyContinue'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

# 1. OEM & Chassis
$enclosure = Get-CimInstance -ClassName Win32_SystemEnclosure | Select-Object -First 1
$bios = Get-CimInstance -ClassName Win32_BIOS | Select-Object -First 1
$csProd = Get-CimInstance -ClassName Win32_ComputerSystemProduct | Select-Object -First 1
$cs = Get-CimInstance -ClassName Win32_ComputerSystem | Select-Object -First 1
$batWin32 = Get-CimInstance -ClassName Win32_Battery | Select-Object -First 1

$laptopCodes = @(8, 9, 10, 11, 12, 14, 18, 21, 30, 31, 32)
$chassisTypes = @()
if ($enclosure.ChassisTypes) { $chassisTypes = @($enclosure.ChassisTypes) }
$isLaptop = $false
foreach ($c in $chassisTypes) {
    if ($laptopCodes -contains [int]$c) { $isLaptop = $true; break }
}
if ($null -ne $batWin32) { $isLaptop = $true }

# 2. Motherboard & UEFI/TPM
$board = Get-CimInstance -ClassName Win32_BaseBoard | Select-Object -First 1
$secureBootState = "No Soportado / Legacy"
try {
    $sbReg = (Get-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\SecureBoot\State' -Name 'UEFISecureBootEnabled' -ErrorAction Stop).UEFISecureBootEnabled
    $secureBootState = if ($sbReg -eq 1) { "Activo (Habilitado)" } else { "Desactivado" }
} catch {}

$bootMode = if (Test-Path 'HKLM:\SYSTEM\CurrentControlSet\Control\SecureBoot\State') { "UEFI" } else { "Legacy BIOS" }

$tpmVersion = "No Detectado"
try {
    $tpmWmi = Get-CimInstance -Namespace "Root\CIMV2\Security\MicrosoftTpm" -ClassName Win32_Tpm -ErrorAction SilentlyContinue
    if ($tpmWmi -and $tpmWmi.SpecVersion) {
        $spec = ($tpmWmi.SpecVersion -split ',')[0].Trim()
        $tpmVersion = "TPM $spec (Listo)"
    } elseif (Test-Path "HKLM:\SYSTEM\CurrentControlSet\Services\TPM\WMI") {
        $tpmVersion = "TPM 2.0 (Servicio Activo)"
    } else {
        $tpmPnp = Get-CimInstance -ClassName Win32_PnPEntity -Filter "Name LIKE '%Trusted Platform Module%' OR Name LIKE '%Security Device%' OR Name LIKE '%TPM%'" -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($tpmPnp) { $tpmVersion = "$($tpmPnp.Name) (Activo)" }
    }
} catch {}

# 3. CPU
$cpu = Get-CimInstance -ClassName Win32_Processor | Select-Object -First 1
$cpuTemp = $null
try {
    $tz = Get-CimInstance -Namespace "root/wmi" -ClassName MSAcpi_ThermalZoneTemperature | Select-Object -First 1
    if ($tz -and $tz.CurrentTemperature -gt 2732) {
        $cpuTemp = [math]::Round(($tz.CurrentTemperature / 10.0) - 273.15, 1)
    }
} catch {}

$archMap = @{ 0 = "x86 (32-bit)"; 5 = "ARM"; 9 = "x64 (AMD64 / Intel 64)"; 12 = "ARM64" }
$cpuArch = if ($archMap.ContainsKey([int]$cpu.Architecture)) { $archMap[[int]$cpu.Architecture] } else { "x64" }

# 4. RAM & Physical Slots
$memArray = Get-CimInstance -ClassName Win32_PhysicalMemoryArray | Select-Object -First 1
$physSticks = @(Get-CimInstance -ClassName Win32_PhysicalMemory)
$totalSlots = if ($memArray.MemoryDevices -and $memArray.MemoryDevices -gt $physSticks.Count) { [int]$memArray.MemoryDevices } else { [math]::Max($physSticks.Count, 2) }

$smbiosTypeMap = @{
    20 = "DDR"; 21 = "DDR2"; 24 = "DDR3"; 26 = "DDR4"; 30 = "LPDDR4"; 34 = "DDR5"; 35 = "LPDDR5"
}

$slotsList = @()
$idx = 1
foreach ($stick in $physSticks) {
    $capGb = [math]::Round($stick.Capacity / 1GB, 2)
    $typeCode = [int]$stick.SMBIOSMemoryType
    $mType = if ($smbiosTypeMap.ContainsKey($typeCode)) { $smbiosTypeMap[$typeCode] } else { "DDR4/DDR5" }
    $slotsList += @{
        slotName     = if ($stick.DeviceLocator) { [string]$stick.DeviceLocator } else { "DIMM $idx" }
        bankLabel    = if ($stick.BankLabel) { [string]$stick.BankLabel } else { "BANK $($idx - 1)" }
        occupied     = $true
        capacityGb   = $capGb
        speedMhz     = [int]($stick.ConfiguredClockSpeed, $stick.Speed | Where-Object { $_ -gt 0 } | Select-Object -First 1)
        memType      = $mType
        manufacturer = if ($stick.Manufacturer) { ([string]$stick.Manufacturer).Trim() } else { "OEM" }
        partNumber   = if ($stick.PartNumber) { ([string]$stick.PartNumber).Trim() } else { "N/A" }
        serialNumber = if ($stick.SerialNumber) { ([string]$stick.SerialNumber).Trim() } else { "N/A" }
        voltageMv    = if ($stick.ConfiguredVoltage) { [int]$stick.ConfiguredVoltage } else { 1200 }
    }
    $idx++
}

while ($slotsList.Count -lt $totalSlots) {
    $emptyIdx = $slotsList.Count + 1
    $slotsList += @{
        slotName     = "Slot DIMM #$emptyIdx"
        bankLabel    = "Disponible"
        occupied     = $false
        capacityGb   = 0
        speedMhz     = 0
        memType      = "Vacío"
        manufacturer = "-"
        partNumber   = "-"
        serialNumber = "-"
        voltageMv    = 0
    }
}

$osInfo = Get-CimInstance -ClassName Win32_OperatingSystem
$totalRamGb = [math]::Round($osInfo.TotalVisibleMemorySize / 1MB, 2)
$freeRamGb = [math]::Round($osInfo.FreePhysicalMemory / 1MB, 2)
$usedRamGb = [math]::Round($totalRamGb - $freeRamGb, 2)
$ramPct = if ($totalRamGb -gt 0) { [int][math]::Round(($usedRamGb / $totalRamGb) * 100) } else { 0 }

$channelMode = if ($physSticks.Count -ge 4) { "Quad / Dual Channel (4 Módulos)" } elseif ($physSticks.Count -eq 2) { "Dual Channel Activo" } elseif ($physSticks.Count -eq 1) { "Single Channel (1 Módulo)" } else { "Multi-Channel" }
$firstStick = $slotsList | Where-Object { $_.occupied } | Select-Object -First 1

# 5. Storage & S.M.A.R.T.
$storageList = @()
$physDisks = @(Get-PhysicalDisk)
$sysDriveFreeGb = 0
$cDrive = Get-CimInstance -ClassName Win32_LogicalDisk -Filter "DeviceID='C:'"
if ($cDrive) { $sysDriveFreeGb = [math]::Round($cDrive.FreeSpace / 1GB, 2) }

foreach ($pd in $physDisks) {
    $rel = $null
    try { $rel = Get-StorageReliabilityCounter -PhysicalDisk $pd -ErrorAction SilentlyContinue } catch {}
    $diskObj = $null
    try { $diskObj = Get-Disk -Number $pd.DeviceId -ErrorAction SilentlyContinue } catch {}

    $tempC = if ($rel -and $rel.Temperature -gt 0) { [int]$rel.Temperature } else { $null }
    $wear = if ($rel -and $null -ne $rel.Wear) { [int]$rel.Wear } else { 0 }
    $healthPct = [math]::Max(0, 100 - $wear)
    $poh = if ($rel -and $rel.PowerOnHours) { [int64]$rel.PowerOnHours } else { $null }

    $storageList += @{
        friendlyName  = [string]$pd.FriendlyName
        serialNumber  = if ($pd.SerialNumber) { ([string]$pd.SerialNumber).Trim() } else { "N/A" }
        mediaType     = [string]$pd.MediaType
        busType       = [string]$pd.BusType
        partitionStyle= if ($diskObj -and $diskObj.PartitionStyle) { [string]$diskObj.PartitionStyle } else { "GPT" }
        sizeGb        = [math]::Round($pd.Size / 1GB, 2)
        freeGb        = $sysDriveFreeGb
        healthStatus  = [string]$pd.HealthStatus
        healthPercent = $healthPct
        temperatureC  = $tempC
        powerOnHours  = $poh
    }
}

# 6. GPUs (Incluye lectura 64-bit qwMemorySize del Registro para evitar límite 4GB de WMI)
$gpuList = @()
$regGpus = Get-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0*" -ErrorAction SilentlyContinue
$wmiGpus = @(Get-CimInstance -ClassName Win32_VideoController)

foreach ($g in $wmiGpus) {
    $vramBytes = [int64]$g.AdapterRAM
    foreach ($rg in $regGpus) {
        if ($rg.DriverDesc -eq $g.Name -and $rg."HardwareInformation.qwMemorySize") {
            $vramBytes = [int64]$rg."HardwareInformation.qwMemorySize"
            break
        }
    }
    $vramGb = if ($vramBytes -gt 0) { [math]::Round($vramBytes / 1GB, 2) } else { 0 }
    $drvDate = if ($g.DriverDate) { $g.DriverDate.ToString("yyyy-MM-dd") } else { "N/A" }
    $res = if ($g.CurrentHorizontalResolution) { "$($g.CurrentHorizontalResolution)x$($g.CurrentVerticalResolution)" } else { "N/A" }

    $gpuList += @{
        name          = [string]$g.Name
        vramGb        = $vramGb
        driverVersion = [string]$g.DriverVersion
        driverDate    = $drvDate
        resolution    = $res
        refreshRateHz = if ($g.CurrentRefreshRate) { [int]$g.CurrentRefreshRate } else { 60 }
    }
}

# 7. Battery & Wear Level (Laptops)
$batteryData = @{
    present               = $false
    status                = "No Aplica (PC de Escritorio)"
    chargePercent         = 100
    designedCapacityMwh   = 0
    fullChargedCapacityMwh= 0
    wearLevelPercent      = 0.0
    healthPercent         = 100.0
    chemistry             = "N/A"
    dischargeRateMw       = 0
}

if ($batWin32) {
    $designed = 0
    $fullCharged = 0
    $rateMw = 0
    try {
        $bStatic = Get-CimInstance -Namespace "root\wmi" -ClassName BatteryStaticData | Select-Object -First 1
        $bFull = Get-CimInstance -Namespace "root\wmi" -ClassName BatteryFullChargedCapacity | Select-Object -First 1
        $bStat = Get-CimInstance -Namespace "root\wmi" -ClassName BatteryStatus | Select-Object -First 1
        if ($bStatic) { $designed = [int64]$bStatic.DesignedCapacity }
        if ($bFull)   { $fullCharged = [int64]$bFull.FullChargedCapacity }
        if ($bStat)   {
            $rateMw = if ($bStat.DischargeRate -gt 0) { [int64]$bStat.DischargeRate } elseif ($bStat.ChargeRate -gt 0) { [int64]$bStat.ChargeRate } else { 0 }
        }
        if ($designed -le 0) {
            $tmpXml = Join-Path $env:TEMP "blackwall_bat.xml"
            powercfg /batteryreport /xml /output $tmpXml | Out-Null
            if (Test-Path $tmpXml) {
                [xml]$xmlDoc = Get-Content $tmpXml -ErrorAction SilentlyContinue
                $batNode = $xmlDoc.BatteryReport.Batteries.Battery | Select-Object -First 1
                if ($batNode.DesignCapacity) { $designed = [int64]$batNode.DesignCapacity }
                if ($fullCharged -le 0 -and $batNode.FullChargeCapacity) { $fullCharged = [int64]$batNode.FullChargeCapacity }
                Remove-Item $tmpXml -Force -ErrorAction SilentlyContinue
            }
        }
    } catch {}

    $wearPct = 0.0
    $healthPct = 100.0
    if ($designed -gt 0 -and $fullCharged -gt 0) {
        $wearPct = [math]::Round((1.0 - ([double]$fullCharged / [double]$designed)) * 100.0, 2)
        if ($wearPct -lt 0) { $wearPct = 0.0 }
        $healthPct = [math]::Round(100.0 - $wearPct, 2)
    }

    $batteryData = @{
        present               = $true
        status                = if ($batWin32.BatteryStatus -eq 2) { "Conectado a CA (Cargando / Enchufado)" } else { "Descargando (En Batería)" }
        chargePercent         = [int]$batWin32.EstimatedChargeRemaining
        designedCapacityMwh   = $designed
        fullChargedCapacityMwh= $fullCharged
        wearLevelPercent      = $wearPct
        healthPercent         = $healthPct
        chemistry             = "Li-Ion / Li-Polymer"
        dischargeRateMw       = $rateMw
    }
}

# 8. OS & Network
$cv = Get-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
$displayVer = if ($cv.DisplayVersion) { $cv.DisplayVersion } else { $cv.ReleaseId }
$uptimeSpan = (Get-Date) - $osInfo.LastBootUpTime
$uptimeStr = "$($uptimeSpan.Days)d $($uptimeSpan.Hours)h $($uptimeSpan.Minutes)m"

$partialKey = "OEM / Digital"
try {
    $lic = Get-CimInstance -ClassName SoftwareLicensingProduct -Filter "PartialProductKey IS NOT NULL AND ApplicationID='55c92734-d682-4d71-983e-d6ec3f16059f'" | Select-Object -First 1
    if ($lic.PartialProductKey) { $partialKey = "XXXXX-XXXXX-XXXXX-XXXXX-$($lic.PartialProductKey)" }
} catch {}

$netList = @()
$adapters = @(Get-NetAdapter | Where-Object { $_.Status -eq 'Up' -or $_.PhysicalMediaType -ne 'Unspecified' } | Select-Object -First 6)
foreach ($na in $adapters) {
    $ipObj = Get-NetIPAddress -InterfaceIndex $na.ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue | Select-Object -First 1
    $netList += @{
        name        = [string]$na.Name
        description = [string]$na.InterfaceDescription
        macAddress  = [string]$na.MacAddress
        linkSpeed   = [string]$na.LinkSpeed
        ipv4        = if ($ipObj.IPAddress) { [string]$ipObj.IPAddress } else { "Sin IPv4" }
        status      = [string]$na.Status
    }
}

$report = @{
    scannedAt = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss")
    chassis = @{
        isLaptop     = $isLaptop
        deviceType   = if ($isLaptop) { "Laptop / Notebook" } else { "Desktop / Torre" }
        chassisTypes = $chassisTypes
        manufacturer = [string]$cs.Manufacturer
        model        = [string]$cs.Model
        serialNumber = if ($bios.SerialNumber) { [string]$bios.SerialNumber } else { [string]$csProd.IdentifyingNumber }
        sku          = if ($cs.SystemSKUNumber) { [string]$cs.SystemSKUNumber } else { "N/A" }
        uuid         = if ($csProd.UUID) { [string]$csProd.UUID } else { "N/A" }
    }
    motherboard = @{
        manufacturer = [string]$board.Manufacturer
        product      = [string]$board.Product
        version      = [string]$board.Version
        serialNumber = [string]$board.SerialNumber
        biosVendor   = [string]$bios.Manufacturer
        biosVersion  = [string]$bios.SMBIOSBIOSVersion
        biosDate     = if ($bios.ReleaseDate) { $bios.ReleaseDate.ToString("yyyy-MM-dd") } else { "N/A" }
        bootMode     = $bootMode
        secureBoot   = $secureBootState
        tpmVersion   = $tpmVersion
    }
    cpu = @{
        name                  = ([string]$cpu.Name).Trim()
        architecture          = $cpuArch
        socket                = [string]$cpu.SocketDesignation
        physicalCores         = [int]$cpu.NumberOfCores
        logicalThreads        = [int]$cpu.NumberOfLogicalProcessors
        baseClockMhz          = [int]$cpu.MaxClockSpeed
        currentClockMhz       = [int]$cpu.CurrentClockSpeed
        l2CacheKb             = [int]$cpu.L2CacheSize
        l3CacheKb             = [int]$cpu.L3CacheSize
        virtualizationEnabled = [bool]$cpu.VirtualizationFirmwareEnabled
        loadPercent           = [int]$cpu.LoadPercentage
        temperatureC          = $cpuTemp
    }
    memory = @{
        totalGb       = $totalRamGb
        usedGb        = $usedRamGb
        availableGb   = $freeRamGb
        usagePercent  = $ramPct
        speedMhz      = if ($firstStick) { [int]$firstStick.speedMhz } else { 0 }
        memoryType    = if ($firstStick) { [string]$firstStick.memType } else { "RAM" }
        channelMode   = $channelMode
        totalSlots    = $totalSlots
        occupiedSlots = $physSticks.Count
        slots         = $slotsList
    }
    storage   = $storageList
    gpus      = $gpuList
    battery   = $batteryData
    osNetwork = @{
        caption           = [string]$osInfo.Caption
        displayVersion    = [string]$displayVer
        buildNumber       = [string]$osInfo.BuildNumber
        installDate       = if ($osInfo.InstallDate) { $osInfo.InstallDate.ToString("yyyy-MM-dd HH:mm") } else { "N/A" }
        uptime            = $uptimeStr
        partialProductKey = $partialKey
        adapters          = $netList
    }
}

$report | ConvertTo-Json -Depth 6 -Compress
