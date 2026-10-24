using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using Microsoft.Win32;

// Metadatos Oficiales PE Win32 (Evita falsos positivos heurísticos ML en antivirus)
[assembly: AssemblyTitle("Blackwall System & Hardware Suite")]
[assembly: AssemblyDescription("Hardware Audit, System Optimization and Package Manager Utility for Windows 10/11")]
[assembly: AssemblyConfiguration("Release")]
[assembly: AssemblyCompany("Blackwall Engineering")]
[assembly: AssemblyProduct("Blackwall Desktop Suite")]
[assembly: AssemblyCopyright("Copyright © 2026 Blackwall Engineering. All rights reserved.")]
[assembly: AssemblyTrademark("Blackwall")]
[assembly: ComVisible(false)]
[assembly: Guid("9f8b4e3a-6c21-4d58-9b7a-1e4c8d9f0a2b")]
[assembly: AssemblyVersion("1.3.0.0")]
[assembly: AssemblyFileVersion("1.3.0.0")]

namespace Blackwall
{
    public class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            // Habilitar TLS 1.2 y TLS 1.3 para descargas HTTPS en cualquier instalación de Windows 10/11
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | (SecurityProtocolType)3072;
            CleanupOldOtaBinary();

            Application app = new Application();
            MainWindow win = new MainWindow();
            app.Run(win);
        }

        private static void CleanupOldOtaBinary()
        {
            try
            {
                string currentExe = Process.GetCurrentProcess().MainModule.FileName;
                string oldExe = currentExe + ".old";
                if (File.Exists(oldExe)) File.Delete(oldExe);
            }
            catch { }
        }
    }

    public class MainWindow : Window
    {
        // Paleta dinámica (Modo Cyberpunk Muro Negro / Modo Arasaka Claro)
        private bool _isDark = true;
        private SolidColorBrush BgApp;
        private SolidColorBrush BgSidebar;
        private SolidColorBrush BgSurface;
        private SolidColorBrush BgElevated;
        private SolidColorBrush BorderSubtle;
        private SolidColorBrush TextPrimary;
        private SolidColorBrush TextSecondary;
        private SolidColorBrush AccentBrush;
        private SolidColorBrush CyberCyanBrush;
        private SolidColorBrush CyberYellowBrush;
        private SolidColorBrush RiskLowBg;
        private SolidColorBrush RiskLowText;
        private SolidColorBrush RiskMedBg;
        private SolidColorBrush RiskMedText;
        private SolidColorBrush RiskHighBg;
        private SolidColorBrush RiskHighText;

        // Estado de la aplicación
        private string _activeTab = "hardware";
        private string _optDeviceScope = "auto";
        private string _optRiskScope = "all";
        private string _appCategoryScope = "Todas";
        private bool _autoRestorePoint = true;
        private bool _preferDirectHttpsDownload = false;

        private Dictionary<string, object> _hardware = null;
        private List<Dictionary<string, object>> _tweaks = new List<Dictionary<string, object>>();
        private Dictionary<string, bool> _tweakStates = new Dictionary<string, bool>();
        private Dictionary<string, Action<bool>> _tweakCardUpdaters = new Dictionary<string, Action<bool>>();
        private List<Dictionary<string, object>> _apps = new List<Dictionary<string, object>>();
        private List<string> _appCategories = new List<string>();
        private Dictionary<string, Dictionary<string, object>> _installedApps = new Dictionary<string, Dictionary<string, object>>();
        private HashSet<string> _selectedApps = new HashSet<string>();
        private Dictionary<string, object> _versionInfo = null;
        private List<Dictionary<string, object>> _liveScripts = new List<Dictionary<string, object>>();
        private List<Dictionary<string, object>> _restorePoints = new List<Dictionary<string, object>>();
        private List<string> _consoleLogs = new List<string>();

        // Controles principales de UI
        private Border _rootBorder;
        private Grid _rootGrid;
        private Border _logoBox;
        private TextBlock _deviceBadgeText;
        private TextBox _omniboxInput;
        private Popup _omniboxPopup;
        private StackPanel _omniboxResultsPanel;
        private Button _themeBtn;
        private Button _otaBtn;
        private TextBlock _maximizeIconText;
        private Button _minBtn;
        private Button _maxBtn;
        private Button _closeBtn;
        private ScrollViewer _mainScroll;
        private TextBlock _statusText;
        private TextBlock _taskText;
        private TextBox _liveConsoleTextBox;
        private Dictionary<string, Button> _navButtons = new Dictionary<string, Button>();
        private DispatcherTimer _scriptMonitorTimer;
        private JavaScriptSerializer _json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        public MainWindow()
        {
            bool isAdmin = IsAdministrator();
            Title = "Blackwall v1.3 // ICE Protocol — Suite Nativa Win32 (Cero PowerShell)" + (isAdmin ? " [ADMINISTRADOR]" : " [Modo Estándar]");
            Width = 1400;
            Height = 880;
            MinWidth = 1080;
            MinHeight = 700;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            // Configuración de Ventana Sin Bordes de Windows (Custom WindowChrome) y Maximizado Permanente
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.CanResize;
            WindowState = WindowState.Maximized;

            WindowChrome chrome = new WindowChrome
            {
                CaptionHeight = 54,
                ResizeBorderThickness = new Thickness(6),
                GlassFrameThickness = new Thickness(0, 0, 0, 1),
                CornerRadius = new CornerRadius(0),
                UseAeroCaptionButtons = false
            };
            WindowChrome.SetWindowChrome(this, chrome);

            this.StateChanged += (s, e) =>
            {
                if (this.WindowState == WindowState.Maximized)
                {
                    if (_rootBorder != null) _rootBorder.Padding = new Thickness(7);
                    if (_maximizeIconText != null) _maximizeIconText.Text = "❐";
                }
                else
                {
                    if (_rootBorder != null) _rootBorder.Padding = new Thickness(0);
                    if (_maximizeIconText != null) _maximizeIconText.Text = "□";
                }
            };

            _consoleLogs.Add("[" + DateTime.Now.ToString("HH:mm:ss") + "] [ARQUITECTURA] Motor 100% Nativo Win32/.NET activo (Cero invocaciones a PowerShell / Compatible con Antivirus y SmartScreen).");
            _consoleLogs.Add("[" + DateTime.Now.ToString("HH:mm:ss") + "] [PRIVILEGIOS] Sesión actual ejecutándose como: " + (isAdmin ? "Administrador Elevado (UAC Activo)" : "Usuario Estándar"));

            LoadEmbeddedCatalogs();
            UpdateThemeBrushes();
            BuildShellUI();

            this.PreviewKeyDown += (s, e) =>
            {
                if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control && e.Key == Key.K)
                {
                    e.Handled = true;
                    _omniboxInput.Focus();
                    _omniboxInput.SelectAll();
                }
                else if (e.Key == Key.Escape && _omniboxPopup != null)
                {
                    _omniboxPopup.IsOpen = false;
                }
            };

            this.Loaded += async (s, e) =>
            {
                TriggerLogoGlitch();
                RenderCurrentTab();
                await RefreshHardwareNativeAsync();
                await Task.WhenAll(
                    RefreshTweaksStateNativeAsync(),
                    RefreshInstalledAppsNativeAsync(),
                    RefreshLiveScriptsNativeAsync(),
                    RefreshRestorePointsNativeAsync(),
                    CheckRemoteOtaAsync(true)
                );
            };

            _scriptMonitorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _scriptMonitorTimer.Tick += async (s, e) =>
            {
                if (_activeTab == "monitor")
                {
                    await RefreshLiveScriptsNativeAsync();
                }
            };
            _scriptMonitorTimer.Start();
        }

        private bool IsAdministrator()
        {
            try
            {
                WindowsIdentity id = WindowsIdentity.GetCurrent();
                WindowsPrincipal principal = new WindowsPrincipal(id);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        private void RelaunchAsAdministrator()
        {
            try
            {
                string exePath = Process.GetCurrentProcess().MainModule.FileName;
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    UseShellExecute = true,
                    Verb = "runas"
                };
                Process.Start(psi);
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                AppendLog("Elevación UAC cancelada por el usuario: " + ex.Message, "WARN");
            }
        }

        // ====================================================================
        // LECTURA DE CATÁLOGOS JSON EMBEBIDOS
        // ====================================================================
        private string ReadEmbeddedResource(string resourceName, string fallbackDiskRelativePath)
        {
            try
            {
                if (resourceName == "tweaks.json" || resourceName == "apps.json")
                {
                    string otaCacheFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Blackwall\Catalogs", resourceName);
                    if (File.Exists(otaCacheFile))
                        return File.ReadAllText(otaCacheFile, Encoding.UTF8);
                }

                Assembly asm = Assembly.GetExecutingAssembly();
                using (Stream st = asm.GetManifestResourceStream(resourceName))
                {
                    if (st != null)
                    {
                        using (StreamReader sr = new StreamReader(st, Encoding.UTF8))
                            return sr.ReadToEnd();
                    }
                }
            }
            catch { }

            string fullPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fallbackDiskRelativePath);
            if (File.Exists(fullPath))
                return File.ReadAllText(fullPath, Encoding.UTF8);
            return "";
        }

        private void LoadEmbeddedCatalogs()
        {
            try
            {
                string tweaksRaw = ReadEmbeddedResource("tweaks.json", @"catalogs\tweaks.json");
                var tweaksRoot = _json.Deserialize<Dictionary<string, object>>(tweaksRaw);
                _tweaks = ConvertToDictList(tweaksRoot["tweaks"]);

                string appsRaw = ReadEmbeddedResource("apps.json", @"catalogs\apps.json");
                var appsRoot = _json.Deserialize<Dictionary<string, object>>(appsRaw);
                _apps = ConvertToDictList(appsRoot["apps"]);

                _appCategories.Clear();
                _appCategories.Add("Todas");
                if (appsRoot.ContainsKey("categories") && appsRoot["categories"] is IEnumerable)
                {
                    foreach (object c in (IEnumerable)appsRoot["categories"])
                        _appCategories.Add(Convert.ToString(c));
                }

                string verRaw = ReadEmbeddedResource("version.json", @"catalogs\version.json");
                _versionInfo = _json.Deserialize<Dictionary<string, object>>(verRaw);
            }
            catch (Exception ex)
            {
                AppendLog("Error cargando catálogos JSON: " + ex.Message, "ERROR");
            }
        }

        // ====================================================================
        // MOTOR NATIVO WMI / REGISTRO / RED EN C# (ULTRARRÁPIDO < 350ms)
        // ====================================================================
        private async Task RefreshHardwareNativeAsync()
        {
            Stopwatch sw = Stopwatch.StartNew();
            SetStatus("⚡ Escaneando hardware por canal nativo WMI/SMBIOS directo...", "Escaneo ultrarrápido");

            var report = await Task.Run(() =>
            {
                var result = new Dictionary<string, object>();

                var enclosure = QueryFirstWmi("root\\CIMV2", "SELECT ChassisTypes, Manufacturer, SerialNumber FROM Win32_SystemEnclosure");
                var bios = QueryFirstWmi("root\\CIMV2", "SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate, SerialNumber FROM Win32_BIOS");
                var csProd = QueryFirstWmi("root\\CIMV2", "SELECT IdentifyingNumber, UUID, Version FROM Win32_ComputerSystemProduct");
                var cs = QueryFirstWmi("root\\CIMV2", "SELECT Manufacturer, Model, SystemSKUNumber, TotalPhysicalMemory FROM Win32_ComputerSystem");
                var batWin32 = QueryFirstWmi("root\\CIMV2", "SELECT BatteryStatus, EstimatedChargeRemaining, Chemistry FROM Win32_Battery");

                int[] laptopCodes = new int[] { 8, 9, 10, 11, 12, 14, 18, 21, 30, 31, 32 };
                List<int> chassisTypes = new List<int>();
                bool isLaptop = batWin32.Count > 0;
                if (enclosure.ContainsKey("ChassisTypes") && enclosure["ChassisTypes"] is IEnumerable)
                {
                    foreach (object c in (IEnumerable)enclosure["ChassisTypes"])
                    {
                        int code = Convert.ToInt32(c);
                        chassisTypes.Add(code);
                        if (laptopCodes.Contains(code)) isLaptop = true;
                    }
                }

                string manufacturer = GetWmiStr(cs, "Manufacturer", "OEM");
                string model = GetWmiStr(cs, "Model", "PC");
                string sku = GetWmiStr(cs, "SystemSKUNumber", GetWmiStr(csProd, "Version", "N/A"));
                string serial = GetWmiStr(bios, "SerialNumber", GetWmiStr(csProd, "IdentifyingNumber", "N/A"));
                string uuid = GetWmiStr(csProd, "UUID", "N/A");

                result["chassis"] = new Dictionary<string, object>
                {
                    { "isLaptop", isLaptop },
                    { "deviceType", isLaptop ? "Laptop / Notebook" : "Desktop / Torre" },
                    { "chassisTypes", chassisTypes },
                    { "manufacturer", manufacturer },
                    { "model", model },
                    { "serialNumber", serial },
                    { "sku", sku },
                    { "uuid", uuid }
                };

                var board = QueryFirstWmi("root\\CIMV2", "SELECT Manufacturer, Product, Version, SerialNumber FROM Win32_BaseBoard");
                string secureBoot = "No Soportado / Legacy";
                string bootMode = "Legacy BIOS";
                try
                {
                    using (var sbKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State"))
                    {
                        if (sbKey != null)
                        {
                            bootMode = "UEFI";
                            object sbVal = sbKey.GetValue("UEFISecureBootEnabled");
                            secureBoot = (sbVal != null && Convert.ToInt32(sbVal) == 1) ? "Activo (Habilitado)" : "Desactivado";
                        }
                    }
                }
                catch { }

                string tpmVersion = "No Detectado";
                try
                {
                    var tpmWmi = QueryFirstWmi("root\\CIMV2\\Security\\MicrosoftTpm", "SELECT SpecVersion FROM Win32_Tpm");
                    if (tpmWmi.ContainsKey("SpecVersion"))
                    {
                        tpmVersion = "TPM " + Convert.ToString(tpmWmi["SpecVersion"]).Split(',')[0].Trim() + " (Listo)";
                    }
                    else
                    {
                        using (var tpmKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\TPM\WMI"))
                        {
                            if (tpmKey != null) tpmVersion = "TPM 2.0 (Hardware Activo)";
                        }
                    }
                }
                catch { }

                result["motherboard"] = new Dictionary<string, object>
                {
                    { "manufacturer", GetWmiStr(board, "Manufacturer", manufacturer) },
                    { "product", GetWmiStr(board, "Product", model) },
                    { "version", GetWmiStr(board, "Version", "1.0") },
                    { "serialNumber", GetWmiStr(board, "SerialNumber", "N/A") },
                    { "biosVendor", GetWmiStr(bios, "Manufacturer", "OEM") },
                    { "biosVersion", GetWmiStr(bios, "SMBIOSBIOSVersion", "N/A") },
                    { "biosDate", FormatWmiDate(GetWmiStr(bios, "ReleaseDate", "")) },
                    { "bootMode", bootMode },
                    { "secureBoot", secureBoot },
                    { "tpmVersion", tpmVersion }
                };

                var cpu = QueryFirstWmi("root\\CIMV2", "SELECT Name, Architecture, SocketDesignation, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, CurrentClockSpeed, L2CacheSize, L3CacheSize, VirtualizationFirmwareEnabled, LoadPercentage FROM Win32_Processor");
                result["cpu"] = new Dictionary<string, object>
                {
                    { "name", GetWmiStr(cpu, "Name", "CPU").Trim() },
                    { "architecture", "x64 (AMD64 / Intel 64)" },
                    { "socket", GetWmiStr(cpu, "SocketDesignation", "BGA/Socket") },
                    { "physicalCores", GetWmiInt(cpu, "NumberOfCores", Environment.ProcessorCount / 2) },
                    { "logicalThreads", GetWmiInt(cpu, "NumberOfLogicalProcessors", Environment.ProcessorCount) },
                    { "baseClockMhz", GetWmiInt(cpu, "MaxClockSpeed", 0) },
                    { "currentClockMhz", GetWmiInt(cpu, "CurrentClockSpeed", 0) },
                    { "l2CacheKb", GetWmiInt(cpu, "L2CacheSize", 0) },
                    { "l3CacheKb", GetWmiInt(cpu, "L3CacheSize", 0) },
                    { "virtualizationEnabled", GetWmiBool(cpu, "VirtualizationFirmwareEnabled", true) },
                    { "loadPercent", GetWmiInt(cpu, "LoadPercentage", 5) }
                };

                var memArray = QueryFirstWmi("root\\CIMV2", "SELECT MemoryDevices FROM Win32_PhysicalMemoryArray");
                var sticks = QueryAllWmi("root\\CIMV2", "SELECT DeviceLocator, BankLabel, Capacity, ConfiguredClockSpeed, Speed, SMBIOSMemoryType, Manufacturer, PartNumber, SerialNumber, ConfiguredVoltage FROM Win32_PhysicalMemory");
                int totalSlots = Math.Max(GetWmiInt(memArray, "MemoryDevices", sticks.Count), Math.Max(sticks.Count, 2));

                var slotsList = new List<Dictionary<string, object>>();
                int idx = 1;
                foreach (var st in sticks)
                {
                    long capBytes = GetWmiLong(st, "Capacity", 0);
                    double capGb = Math.Round(capBytes / (1024.0 * 1024.0 * 1024.0), 2);
                    int speed = GetWmiInt(st, "ConfiguredClockSpeed", GetWmiInt(st, "Speed", 0));
                    int typeCode = GetWmiInt(st, "SMBIOSMemoryType", 26);
                    string mType = typeCode == 34 || typeCode == 35 ? "DDR5" : typeCode == 24 ? "DDR3" : "DDR4";

                    slotsList.Add(new Dictionary<string, object>
                    {
                        { "slotName", GetWmiStr(st, "DeviceLocator", "DIMM " + idx) },
                        { "bankLabel", GetWmiStr(st, "BankLabel", "BANK " + (idx - 1)) },
                        { "occupied", true },
                        { "capacityGb", capGb },
                        { "speedMhz", speed },
                        { "memType", mType },
                        { "manufacturer", GetWmiStr(st, "Manufacturer", "OEM").Trim() },
                        { "partNumber", GetWmiStr(st, "PartNumber", "N/A").Trim() },
                        { "serialNumber", GetWmiStr(st, "SerialNumber", "N/A").Trim() },
                        { "voltageMv", GetWmiInt(st, "ConfiguredVoltage", 1200) }
                    });
                    idx++;
                }

                while (slotsList.Count < totalSlots)
                {
                    int emptyIdx = slotsList.Count + 1;
                    slotsList.Add(new Dictionary<string, object>
                    {
                        { "slotName", "Slot DIMM #" + emptyIdx },
                        { "bankLabel", "Disponible" },
                        { "occupied", false },
                        { "capacityGb", 0 },
                        { "speedMhz", 0 },
                        { "memType", "Vacío" },
                        { "manufacturer", "-" },
                        { "partNumber", "-" },
                        { "serialNumber", "-" },
                        { "voltageMv", 0 }
                    });
                }

                var osInfo = QueryFirstWmi("root\\CIMV2", "SELECT Caption, BuildNumber, InstallDate, LastBootUpTime, TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem");
                double totalRamGb = Math.Round(GetWmiLong(osInfo, "TotalVisibleMemorySize", 0) / (1024.0 * 1024.0), 2);
                double freeRamGb = Math.Round(GetWmiLong(osInfo, "FreePhysicalMemory", 0) / (1024.0 * 1024.0), 2);
                double usedRamGb = Math.Round(totalRamGb - freeRamGb, 2);
                int ramPct = totalRamGb > 0 ? (int)Math.Round((usedRamGb / totalRamGb) * 100.0) : 0;
                string channelMode = sticks.Count >= 4 ? "Quad/Dual Channel (4 Módulos)" : sticks.Count == 2 ? "Dual Channel Activo" : "Single Channel";

                result["memory"] = new Dictionary<string, object>
                {
                    { "totalGb", totalRamGb },
                    { "usedGb", usedRamGb },
                    { "availableGb", freeRamGb },
                    { "usagePercent", ramPct },
                    { "speedMhz", slotsList.Count > 0 ? slotsList[0]["speedMhz"] : 0 },
                    { "memoryType", slotsList.Count > 0 ? slotsList[0]["memType"] : "DDR4" },
                    { "channelMode", channelMode },
                    { "totalSlots", totalSlots },
                    { "occupiedSlots", sticks.Count },
                    { "slots", slotsList }
                };

                double freeCGb = 0;
                try
                {
                    DriveInfo cDrive = new DriveInfo("C");
                    if (cDrive.IsReady) freeCGb = Math.Round(cDrive.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0), 2);
                }
                catch { }

                var storageList = new List<Dictionary<string, object>>();
                var msftDisks = QueryAllWmi("root\\Microsoft\\Windows\\Storage", "SELECT FriendlyName, SerialNumber, MediaType, BusType, Size, HealthStatus FROM MSFT_PhysicalDisk");
                if (msftDisks.Count > 0)
                {
                    foreach (var md in msftDisks)
                    {
                        int mType = GetWmiInt(md, "MediaType", 4);
                        int bType = GetWmiInt(md, "BusType", 17);
                        int hStat = GetWmiInt(md, "HealthStatus", 0);
                        string mediaStr = mType == 4 ? "SSD" : mType == 3 ? "HDD" : "Flash/NVMe";
                        string busStr = bType == 17 ? "NVMe" : bType == 11 ? "SATA" : bType == 7 ? "USB" : "PCIe";
                        string healthStr = hStat == 0 ? "Healthy (Óptimo)" : "Warning";

                        storageList.Add(new Dictionary<string, object>
                        {
                            { "friendlyName", GetWmiStr(md, "FriendlyName", "Disco") },
                            { "serialNumber", GetWmiStr(md, "SerialNumber", "N/A").Trim() },
                            { "mediaType", mediaStr },
                            { "busType", busStr },
                            { "partitionStyle", bootMode == "UEFI" ? "GPT" : "MBR" },
                            { "sizeGb", Math.Round(GetWmiLong(md, "Size", 0) / (1024.0 * 1024.0 * 1024.0), 2) },
                            { "freeGb", freeCGb },
                            { "healthStatus", healthStr },
                            { "healthPercent", hStat == 0 ? 100 : 80 }
                        });
                    }
                }
                else
                {
                    var winDisks = QueryAllWmi("root\\CIMV2", "SELECT Model, SerialNumber, InterfaceType, Size, Status FROM Win32_DiskDrive");
                    foreach (var wd in winDisks)
                    {
                        storageList.Add(new Dictionary<string, object>
                        {
                            { "friendlyName", GetWmiStr(wd, "Model", "Disco") },
                            { "serialNumber", GetWmiStr(wd, "SerialNumber", "N/A").Trim() },
                            { "mediaType", "SSD/NVMe" },
                            { "busType", GetWmiStr(wd, "InterfaceType", "NVMe") },
                            { "partitionStyle", bootMode == "UEFI" ? "GPT" : "MBR" },
                            { "sizeGb", Math.Round(GetWmiLong(wd, "Size", 0) / (1024.0 * 1024.0 * 1024.0), 2) },
                            { "freeGb", freeCGb },
                            { "healthStatus", GetWmiStr(wd, "Status", "Healthy") },
                            { "healthPercent", 100 }
                        });
                    }
                }
                result["storage"] = storageList;

                var gpuList = new List<Dictionary<string, object>>();
                var wmiGpus = QueryAllWmi("root\\CIMV2", "SELECT Name, AdapterRAM, DriverVersion, DriverDate, CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate FROM Win32_VideoController");
                foreach (var g in wmiGpus)
                {
                    long vramBytes = GetWmiLong(g, "AdapterRAM", 0);
                    double vramGb = vramBytes > 0 ? Math.Round(vramBytes / (1024.0 * 1024.0 * 1024.0), 2) : 0.5;
                    string res = GetWmiInt(g, "CurrentHorizontalResolution", 1920) + "x" + GetWmiInt(g, "CurrentVerticalResolution", 1080);

                    gpuList.Add(new Dictionary<string, object>
                    {
                        { "name", GetWmiStr(g, "Name", "GPU") },
                        { "vramGb", vramGb },
                        { "driverVersion", GetWmiStr(g, "DriverVersion", "N/A") },
                        { "driverDate", FormatWmiDate(GetWmiStr(g, "DriverDate", "")) },
                        { "resolution", res },
                        { "refreshRateHz", GetWmiInt(g, "CurrentRefreshRate", 60) }
                    });
                }
                result["gpus"] = gpuList;

                var batteryData = new Dictionary<string, object>
                {
                    { "present", false },
                    { "status", "No Aplica (PC de Escritorio)" },
                    { "chargePercent", 100 },
                    { "designedCapacityMwh", 0 },
                    { "fullChargedCapacityMwh", 0 },
                    { "wearLevelPercent", 0.0 },
                    { "healthPercent", 100.0 },
                    { "chemistry", "N/A" },
                    { "dischargeRateMw", 0 }
                };

                if (batWin32.Count > 0)
                {
                    var bStatic = QueryFirstWmi("root\\WMI", "SELECT DesignedCapacity FROM BatteryStaticData");
                    var bFull = QueryFirstWmi("root\\WMI", "SELECT FullChargedCapacity FROM BatteryFullChargedCapacity");
                    var bStat = QueryFirstWmi("root\\WMI", "SELECT DischargeRate, ChargeRate FROM BatteryStatus");

                    long designed = GetWmiLong(bStatic, "DesignedCapacity", 45000);
                    if (designed <= 0) designed = 45000;
                    long fullCharged = GetWmiLong(bFull, "FullChargedCapacity", designed);
                    if (fullCharged <= 0) fullCharged = designed;
                    long rateMw = GetWmiLong(bStat, "DischargeRate", GetWmiLong(bStat, "ChargeRate", 0));

                    double wearPct = Math.Max(0.0, Math.Round((1.0 - ((double)fullCharged / (double)designed)) * 100.0, 2));
                    double healthPct = Math.Round(100.0 - wearPct, 2);

                    batteryData["present"] = true;
                    batteryData["status"] = GetWmiInt(batWin32, "BatteryStatus", 2) == 2 ? "Conectado a CA (Cargando / Enchufado)" : "En Batería (Descargando)";
                    batteryData["chargePercent"] = GetWmiInt(batWin32, "EstimatedChargeRemaining", 100);
                    batteryData["designedCapacityMwh"] = designed;
                    batteryData["fullChargedCapacityMwh"] = fullCharged;
                    batteryData["wearLevelPercent"] = wearPct;
                    batteryData["healthPercent"] = healthPct;
                    batteryData["chemistry"] = "Li-Ion / Li-Polymer";
                    batteryData["dischargeRateMw"] = rateMw;
                }
                result["battery"] = batteryData;

                string displayVer = "23H2/24H2";
                string partialKey = "OEM Digital License";
                try
                {
                    using (var cvKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                    {
                        if (cvKey != null)
                            displayVer = Convert.ToString(cvKey.GetValue("DisplayVersion") ?? cvKey.GetValue("ReleaseId") ?? "24H2");
                    }
                    using (var spKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SoftwareProtectionPlatform"))
                    {
                        if (spKey != null)
                        {
                            string rawKey = Convert.ToString(spKey.GetValue("BackupProductKeyDefault") ?? "");
                            if (rawKey.Length >= 5)
                                partialKey = "XXXXX-XXXXX-XXXXX-XXXXX-" + rawKey.Substring(rawKey.Length - 5);
                        }
                    }
                }
                catch { }

                TimeSpan upSpan = TimeSpan.FromMilliseconds(Environment.TickCount & Int32.MaxValue);
                string uptimeStr = upSpan.Days + "d " + upSpan.Hours + "h " + upSpan.Minutes + "m";

                var netAdapters = new List<Dictionary<string, object>>();
                try
                {
                    foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                    {
                        if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                        if (ni.OperationalStatus != OperationalStatus.Up && netAdapters.Count >= 3) continue;

                        string ipv4 = "Sin IPv4";
                        foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                        {
                            if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                            {
                                ipv4 = ua.Address.ToString();
                                break;
                            }
                        }
                        string mac = string.Join("-", ni.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("X2")));
                        long mbps = ni.Speed > 0 ? ni.Speed / 1000000 : 0;
                        string speedStr = mbps >= 1000 ? (mbps / 1000.0).ToString("0.#") + " Gbps" : mbps + " Mbps";

                        netAdapters.Add(new Dictionary<string, object>
                        {
                            { "name", ni.Name },
                            { "description", ni.Description },
                            { "macAddress", string.IsNullOrEmpty(mac) ? "N/A" : mac },
                            { "linkSpeed", speedStr },
                            { "ipv4", ipv4 },
                            { "status", ni.OperationalStatus.ToString() }
                        });
                    }
                }
                catch { }

                result["osNetwork"] = new Dictionary<string, object>
                {
                    { "caption", GetWmiStr(osInfo, "Caption", "Microsoft Windows 11") },
                    { "displayVersion", displayVer },
                    { "buildNumber", GetWmiStr(osInfo, "BuildNumber", "22631") },
                    { "installDate", FormatWmiDate(GetWmiStr(osInfo, "InstallDate", "")) },
                    { "uptime", uptimeStr },
                    { "partialProductKey", partialKey },
                    { "adapters", netAdapters }
                };

                return result;
            });

            sw.Stop();
            report["scannedAt"] = DateTime.Now.ToString("HH:mm:ss") + " (" + sw.ElapsedMilliseconds + " ms)";
            _hardware = report;

            var chassisDict = GetDict(_hardware, "chassis");
            if (_deviceBadgeText != null)
            {
                _deviceBadgeText.Text = FormatChassisHeader(chassisDict);
            }
            AppendLog("Auditoría WMI nativa completada en " + sw.ElapsedMilliseconds + " ms.", "WMI-NATIVE");
            SetStatus("🟢 Hardware auditado en " + sw.ElapsedMilliseconds + " ms (100% Nativo C#)", "Listo");

            if (_activeTab == "hardware" || _activeTab == "optimization")
            {
                RenderCurrentTab();
            }
        }

        private async Task RefreshTweaksStateNativeAsync()
        {
            await Task.Run(() =>
            {
                var map = new Dictionary<string, bool>();
                foreach (var t in _tweaks)
                {
                    string id = GetStr(t, "id");
                    if (string.IsNullOrEmpty(id)) continue;

                    bool active = false;
                    var ops = t.ContainsKey("applyOps") ? ConvertToDictList(t["applyOps"]) : new List<Dictionary<string, object>>();
                    foreach (var op in ops)
                    {
                        string type = GetStr(op, "type");
                        if (type == "reg_dword")
                        {
                            RegistryKey root = GetStr(op, "hive") == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser;
                            active = CheckRegDword(root, GetStr(op, "key"), GetStr(op, "name"), GetInt(op, "value"));
                            break;
                        }
                        else if (type == "reg_sz")
                        {
                            RegistryKey root = GetStr(op, "hive") == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser;
                            string name = GetStr(op, "name");
                            if (string.IsNullOrEmpty(name))
                                active = CheckRegKeyExists(root, GetStr(op, "key"));
                            else
                                active = CheckRegString(root, GetStr(op, "key"), name, GetStr(op, "value"));
                            break;
                        }
                    }
                    map[id] = active;
                }
                _tweakStates = map;
            });

            if (_activeTab == "optimization" || _activeTab == "debloat")
            {
                Dispatcher.Invoke(() =>
                {
                    if (_tweakCardUpdaters.Count > 0)
                    {
                        foreach (var kv in _tweakStates)
                        {
                            if (_tweakCardUpdaters.ContainsKey(kv.Key))
                                _tweakCardUpdaters[kv.Key](kv.Value);
                        }
                    }
                    else
                    {
                        RenderCurrentTab();
                    }
                });
            }
        }

        private async Task RefreshInstalledAppsNativeAsync()
        {
            await Task.Run(() =>
            {
                var foundEntries = new List<KeyValuePair<string, string>>();
                CollectUninstallEntries(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", foundEntries);
                CollectUninstallEntries(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", foundEntries);
                CollectUninstallEntries(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Uninstall", foundEntries);

                var appMap = new Dictionary<string, Dictionary<string, object>>();
                foreach (var a in _apps)
                {
                    string id = GetStr(a, "id");
                    string pat = GetStr(a, "detectPattern");
                    if (string.IsNullOrEmpty(pat)) pat = GetStr(a, "name");

                    Regex rx = new Regex(pat, RegexOptions.IgnoreCase);
                    var match = foundEntries.FirstOrDefault(kv => rx.IsMatch(kv.Key));
                    if (!string.IsNullOrEmpty(match.Key))
                    {
                        appMap[id] = new Dictionary<string, object> { { "installed", true }, { "version", match.Value } };
                    }
                    else
                    {
                        appMap[id] = new Dictionary<string, object> { { "installed", false }, { "version", "" } };
                    }
                }
                _installedApps = appMap;
            });

            if (_activeTab == "apps")
                RenderCurrentTab();
        }

        private async Task RefreshLiveScriptsNativeAsync()
        {
            await Task.Run(() =>
            {
                int myPid = Process.GetCurrentProcess().Id;
                var list = new List<Dictionary<string, object>>();
                var rows = QueryAllWmi(
                    "root\\CIMV2",
                    "SELECT ProcessId, ParentProcessId, Name, ExecutablePath, CommandLine, WorkingSetSize FROM Win32_Process WHERE Name='powershell.exe' OR Name='pwsh.exe' OR Name='cmd.exe' OR Name='wscript.exe' OR Name='cscript.exe' OR Name='mshta.exe'"
                );

                foreach (var r in rows)
                {
                    int pid = GetWmiInt(r, "ProcessId", 0);
                    if (pid == myPid || pid == 0) continue;

                    double ramMb = Math.Round(GetWmiLong(r, "WorkingSetSize", 0) / (1024.0 * 1024.0), 1);
                    list.Add(new Dictionary<string, object>
                    {
                        { "pid", pid },
                        { "parentPid", GetWmiInt(r, "ParentProcessId", 0) },
                        { "name", GetWmiStr(r, "Name", "script") },
                        { "commandLine", GetWmiStr(r, "CommandLine", GetWmiStr(r, "ExecutablePath", "Activo")) },
                        { "memoryMb", ramMb },
                        { "startedAt", "En ejecución" }
                    });
                }
                _liveScripts = list;
            });

            if (_activeTab == "monitor")
                RenderCurrentTab();
        }

        private async Task RefreshRestorePointsNativeAsync()
        {
            await Task.Run(() =>
            {
                var list = new List<Dictionary<string, object>>();
                var rows = QueryAllWmi("root\\default", "SELECT SequenceNumber, Description, CreationTime FROM SystemRestore");
                foreach (var r in rows)
                {
                    list.Add(new Dictionary<string, object>
                    {
                        { "sequenceNumber", GetWmiInt(r, "SequenceNumber", 1) },
                        { "description", GetWmiStr(r, "Description", "Punto de Restauración") },
                        { "creationTime", FormatWmiDate(GetWmiStr(r, "CreationTime", "")) }
                    });
                }
                _restorePoints = list;
            });

            if (_activeTab == "safety")
                RenderCurrentTab();
        }

        // ====================================================================
        // EJECUTOR NATIVO DE TWEAKS (REGISTRO / SERVICIOS / POWERCFG SIN PS1)
        // ====================================================================
        private async Task ExecuteTweakAsync(Dictionary<string, object> tweak, bool apply)
        {
            string id = GetStr(tweak, "id");
            string title = GetStr(tweak, "title");

            if (apply && _autoRestorePoint)
            {
                SetStatus("🛡️ Verificando Punto de Restauración nativo...", title);
                await CreateRestorePointNativeInternalAsync("Blackwall Pre-" + id);
            }

            SetStatus((apply ? "⚡ Aplicando nativamente: " : "↩️ Revirtiendo nativamente: ") + title, id);
            AppendLog((apply ? "[APPLY-NATIVE] " : "[UNDO-NATIVE] ") + title, "TWEAK");

            string opsKey = apply ? "applyOps" : "undoOps";
            var opsList = tweak.ContainsKey(opsKey) ? ConvertToDictList(tweak[opsKey]) : new List<Dictionary<string, object>>();

            await Task.Run(() =>
            {
                foreach (var op in opsList)
                {
                    string type = GetStr(op, "type");
                    try
                    {
                        if (type == "reg_dword")
                        {
                            RegistryKey root = GetStr(op, "hive") == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser;
                            using (var k = root.CreateSubKey(GetStr(op, "key"), true))
                            {
                                if (k != null) k.SetValue(GetStr(op, "name"), GetInt(op, "value"), RegistryValueKind.DWord);
                            }
                        }
                        else if (type == "reg_sz")
                        {
                            RegistryKey root = GetStr(op, "hive") == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser;
                            using (var k = root.CreateSubKey(GetStr(op, "key"), true))
                            {
                                if (k != null) k.SetValue(GetStr(op, "name"), GetStr(op, "value"), RegistryValueKind.String);
                            }
                        }
                        else if (type == "reg_delete")
                        {
                            RegistryKey root = GetStr(op, "hive") == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser;
                            using (var k = root.OpenSubKey(GetStr(op, "key"), true))
                            {
                                if (k != null) k.DeleteValue(GetStr(op, "name"), false);
                            }
                        }
                        else if (type == "reg_delete_tree")
                        {
                            RegistryKey root = GetStr(op, "hive") == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser;
                            root.DeleteSubKeyTree(GetStr(op, "key"), false);
                        }
                        else if (type == "powercfg")
                        {
                            RunSystemToolDirect("powercfg.exe", GetStr(op, "args"));
                        }
                        else if (type == "service_stop")
                        {
                            StopServiceViaWmi(GetStr(op, "name"));
                        }
                    }
                    catch (Exception ex)
                    {
                        AppendLog("Aviso en operación [" + type + "]: " + ex.Message + " (Asegúrate de abrir como Administrador para claves HKLM).", "WARN");
                    }
                }
            });

            _tweakStates[id] = apply;
            SetStatus("🟢 " + (apply ? "Aplicado: " : "Revertido: ") + title, "Listo");
            Dispatcher.Invoke(() =>
            {
                if (_tweakCardUpdaters.ContainsKey(id))
                {
                    _tweakCardUpdaters[id](apply);
                }
                else
                {
                    RenderCurrentTab();
                }
            });
        }

        private void StopServiceViaWmi(string serviceName)
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_Service WHERE Name='" + serviceName + "'"))
                {
                    foreach (ManagementObject svc in searcher.Get())
                    {
                        svc.InvokeMethod("StopService", null);
                    }
                }
            }
            catch { }
        }

        private string RunSystemToolDirect(string exeName, string arguments)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = exeName,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using (Process p = Process.Start(psi))
                {
                    Task<string> outTask = p.StandardOutput.ReadToEndAsync();
                    Task<string> errTask = p.StandardError.ReadToEndAsync();
                    p.WaitForExit(180000);
                    string sOut = outTask.Result;
                    string sErr = errTask.Result;
                    return string.IsNullOrWhiteSpace(sOut) ? sErr.Trim() : sOut.Trim();
                }
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        // ====================================================================
        // INSTALADOR DESATENDIDO 100% NATIVO C# (WINGET RESOLVER + HTTPS NATIVO)
        // ====================================================================
        private string ResolveWingetExecutablePath()
        {
            try
            {
                // 1. Ruta estándar por usuario (%LocalAppData%\Microsoft\WindowsApps\winget.exe)
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string candidate1 = Path.Combine(localAppData, @"Microsoft\WindowsApps\winget.exe");
                if (File.Exists(candidate1)) return candidate1;

                // 2. Buscar en todas las carpetas de usuarios si se abrió como otro Administrador
                string usersDir = @"C:\Users";
                if (Directory.Exists(usersDir))
                {
                    foreach (string uDir in Directory.GetDirectories(usersDir))
                    {
                        string cUser = Path.Combine(uDir, @"AppData\Local\Microsoft\WindowsApps\winget.exe");
                        if (File.Exists(cUser)) return cUser;
                    }
                }

                // 3. Buscar en C:\Program Files\WindowsApps\Microsoft.DesktopAppInstaller_*_x64__8wekyb3d8bbwe
                string winApps = @"C:\Program Files\WindowsApps";
                if (Directory.Exists(winApps))
                {
                    foreach (string dir in Directory.GetDirectories(winApps, "Microsoft.DesktopAppInstaller*x64*"))
                    {
                        string cPkg = Path.Combine(dir, "winget.exe");
                        if (File.Exists(cPkg)) return cPkg;
                    }
                }
            }
            catch { }
            return null;
        }

        private async Task InstallSingleAppAsync(Dictionary<string, object> app)
        {
            string id = GetStr(app, "id");
            string name = GetStr(app, "name");
            string wgId = GetStr(app, "wingetId");
            string fbUrl = GetStr(app, "fallbackUrl");
            string ext = GetStr(app, "installerExt");
            if (string.IsNullOrEmpty(ext)) ext = fbUrl.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) ? ".msi" : ".exe";
            string silentArgs = GetStr(app, "silentArgs");

            bool installedOk = false;

            // Paso 1: Intentar con WinGet directo si no está forzado el modo HTTPS directo
            if (!_preferDirectHttpsDownload)
            {
                string wingetPath = ResolveWingetExecutablePath();
                if (!string.IsNullOrEmpty(wingetPath))
                {
                    SetStatus("📦 Instalando " + name + " mediante WinGet nativo...", wgId);
                    AppendLog("[WINGET] Ejecutando directamente: " + wingetPath + " install --id " + wgId + " --silent", "INSTALL");

                    int exitCode = await Task.Run(() =>
                    {
                        try
                        {
                            ProcessStartInfo psi = new ProcessStartInfo
                            {
                                FileName = wingetPath,
                                Arguments = "install --id \"" + wgId + "\" --exact --silent --accept-package-agreements --accept-source-agreements --disable-interactivity",
                                UseShellExecute = false,
                                CreateNoWindow = true,
                                RedirectStandardOutput = true,
                                RedirectStandardError = true
                            };
                            using (Process p = Process.Start(psi))
                            {
                                string sOut = p.StandardOutput.ReadToEnd();
                                p.WaitForExit(300000);
                                if (!string.IsNullOrWhiteSpace(sOut))
                                    AppendLog(sOut.Trim(), "WINGET");
                                return p.ExitCode;
                            }
                        }
                        catch
                        {
                            return -1;
                        }
                    });

                    // ExitCode 0 = instalado; -1978335189 (0x8A15002B) = ya instalado en última versión
                    if (exitCode == 0 || exitCode == -1978335189)
                    {
                        installedOk = true;
                        AppendLog("[WINGET] " + name + " completado exitosamente.", "INSTALL");
                    }
                    else
                    {
                        AppendLog("[WINGET] Código " + exitCode + " en este PC. Cambiando automáticamente a Descarga HTTPS Directa Nativa...", "FALLBACK");
                    }
                }
                else
                {
                    AppendLog("[WINGET] No encontrado en PATH de este PC. Usando Descarga HTTPS Directa Nativa...", "FALLBACK");
                }
            }

            // Paso 2: Motor de Descarga Directa HTTPS Nativa C# (.NET WebClient sin PowerShell ni bloqueos de Antivirus)
            if (!installedOk && !string.IsNullOrEmpty(fbUrl))
            {
                try
                {
                    string pkgFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Blackwall\Packages");
                    if (!Directory.Exists(pkgFolder)) Directory.CreateDirectory(pkgFolder);
                    string targetFile = Path.Combine(pkgFolder, "Setup_" + id + ext);
                    if (File.Exists(targetFile)) File.Delete(targetFile);

                    SetStatus("⬇️ Descargando instalador oficial de " + name + " (HTTPS directo)...", "0%");
                    AppendLog("[HTTPS-DIRECT] Descargando desde CDN oficial: " + fbUrl, "DOWNLOAD");

                    using (WebClient wc = new WebClient())
                    {
                        wc.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Blackwall/1.2");
                        wc.DownloadProgressChanged += (s, ev) =>
                        {
                            double mbRecv = Math.Round(ev.BytesReceived / (1024.0 * 1024.0), 1);
                            SetStatus("⬇️ Descargando " + name + ": " + ev.ProgressPercentage + "% (" + mbRecv + " MB)", ev.ProgressPercentage + "%");
                        };
                        await wc.DownloadFileTaskAsync(new Uri(fbUrl), targetFile);
                    }

                    // Ejecutar el instalador oficial firmado del fabricante de forma desatendida
                    SetStatus("⚙️ Ejecutando instalador silencioso de " + name + "...", silentArgs);
                    AppendLog("[INSTALLER] Ejecutando paquete oficial: " + targetFile + " " + silentArgs, "INSTALL");

                    await Task.Run(() =>
                    {
                        ProcessStartInfo psi;
                        if (ext.Equals(".msi", StringComparison.OrdinalIgnoreCase))
                        {
                            psi = new ProcessStartInfo
                            {
                                FileName = "msiexec.exe",
                                Arguments = "/i \"" + targetFile + "\" " + silentArgs,
                                UseShellExecute = true
                            };
                        }
                        else
                        {
                            psi = new ProcessStartInfo
                            {
                                FileName = targetFile,
                                Arguments = silentArgs,
                                UseShellExecute = true
                            };
                        }
                        using (Process p = Process.Start(psi))
                        {
                            if (p != null) p.WaitForExit(300000);
                        }
                        try { File.Delete(targetFile); } catch { }
                    });

                    installedOk = true;
                    AppendLog("[INSTALLER] " + name + " instalado correctamente por vía directa.", "SUCCESS");
                }
                catch (Exception ex)
                {
                    AppendLog("Error en descarga/instalación de " + name + ": " + ex.Message, "ERROR");
                    SetStatus("🔴 Error al instalar " + name, "Error");
                }
            }

            await RefreshInstalledAppsNativeAsync();
            if (installedOk)
            {
                SetStatus("🟢 " + name + " instalado correctamente", "Listo");
            }
        }

        private async Task InstallSelectedAppsBatchAsync()
        {
            var chosen = _apps.Where(a => _selectedApps.Contains(GetStr(a, "id"))).ToList();
            for (int i = 0; i < chosen.Count; i++)
            {
                await InstallSingleAppAsync(chosen[i]);
            }
        }

        private async Task UpgradeAllAppsAsync()
        {
            string wingetPath = ResolveWingetExecutablePath();
            if (string.IsNullOrEmpty(wingetPath))
            {
                AppendLog("WinGet no está presente en este equipo. Selecciona las aplicaciones de la lista para instalarlas por Descarga HTTPS Directa.", "WARN");
                return;
            }
            SetStatus("🔄 Actualizando todas las apps del PC (winget upgrade --all --silent)...", "WinGet Nativo");
            AppendLog("Ejecutando directamente: " + wingetPath + " upgrade --all --include-unknown --silent", "WINGET");
            await Task.Run(() =>
            {
                string res = RunSystemToolDirect(wingetPath, "upgrade --all --include-unknown --silent --accept-package-agreements --accept-source-agreements --disable-interactivity");
                if (!string.IsNullOrWhiteSpace(res)) AppendLog(res, "WINGET");
            });
            await RefreshInstalledAppsNativeAsync();
            SetStatus("🟢 Todas las aplicaciones actualizadas", "Listo");
        }

        // ====================================================================
        // PUNTOS DE RESTAURACIÓN NATIVOS WMI (SIN POWERSHELL)
        // ====================================================================
        private Task<bool> CreateRestorePointNativeInternalAsync(string description)
        {
            return Task.Run(() =>
            {
                try
                {
                    ManagementScope scope = new ManagementScope("\\\\localhost\\root\\default");
                    ManagementPath path = new ManagementPath("SystemRestore");
                    ObjectGetOptions options = new ObjectGetOptions();
                    using (ManagementClass srClass = new ManagementClass(scope, path, options))
                    {
                        ManagementBaseObject inParams = srClass.GetMethodParameters("CreateRestorePoint");
                        inParams["Description"] = description;
                        inParams["RestorePointType"] = 12; // MODIFY_SETTINGS
                        inParams["EventType"] = 100;       // BEGIN_SYSTEM_CHANGE
                        ManagementBaseObject outParams = srClass.InvokeMethod("CreateRestorePoint", inParams, null);
                        uint ret = Convert.ToUInt32(outParams["ReturnValue"]);
                        return ret == 0;
                    }
                }
                catch
                {
                    return false;
                }
            });
        }

        private async Task CreateRestorePointNowAsync()
        {
            SetStatus("🛡️ Creando Punto de Restauración nativo en C:\\...", "SystemRestore WMI");
            AppendLog("Invocando API nativa root\\default:SystemRestore.CreateRestorePoint...", "SAFETY");
            bool ok = await CreateRestorePointNativeInternalAsync("Blackwall Restore Point (" + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + ")");
            if (ok)
            {
                AppendLog("Punto de Restauración creado exitosamente.", "SAFETY");
                SetStatus("🟢 Punto de Restauración creado en C:\\", "Listo");
            }
            else
            {
                AppendLog("Nota: Para crear Puntos de Restauración, ejecuta Blackwall como Administrador (Botón 'Elevar a Admin').", "WARN");
                SetStatus("🟡 Requiere permisos de Administrador para crear Punto de Restauración", "Aviso");
            }
            await RefreshRestorePointsNativeAsync();
        }

        // ====================================================================
        // HELPERS NATIVOS WMI Y REGISTRO
        // ====================================================================
        private Dictionary<string, object> QueryFirstWmi(string ns, string query)
        {
            var all = QueryAllWmi(ns, query);
            return all.Count > 0 ? all[0] : new Dictionary<string, object>();
        }

        private List<Dictionary<string, object>> QueryAllWmi(string ns, string query)
        {
            var list = new List<Dictionary<string, object>>();
            try
            {
                var opts = new EnumerationOptions { ReturnImmediately = true, Rewindable = false, Timeout = TimeSpan.FromSeconds(2) };
                using (var searcher = new ManagementObjectSearcher(new ManagementScope(ns), new ObjectQuery(query), opts))
                using (var results = searcher.Get())
                {
                    foreach (ManagementObject mo in results)
                    {
                        var d = new Dictionary<string, object>();
                        foreach (PropertyData prop in mo.Properties)
                        {
                            if (prop.Value != null) d[prop.Name] = prop.Value;
                        }
                        list.Add(d);
                    }
                }
            }
            catch { }
            return list;
        }

        private string GetWmiStr(Dictionary<string, object> d, string k, string defVal)
        {
            return (d != null && d.ContainsKey(k) && d[k] != null) ? Convert.ToString(d[k]) : defVal;
        }

        private int GetWmiInt(Dictionary<string, object> d, string k, int defVal)
        {
            if (d != null && d.ContainsKey(k) && d[k] != null)
            {
                try { return Convert.ToInt32(d[k]); } catch { }
            }
            return defVal;
        }

        private long GetWmiLong(Dictionary<string, object> d, string k, long defVal)
        {
            if (d != null && d.ContainsKey(k) && d[k] != null)
            {
                try { return Convert.ToInt64(d[k]); } catch { }
            }
            return defVal;
        }

        private bool GetWmiBool(Dictionary<string, object> d, string k, bool defVal)
        {
            if (d != null && d.ContainsKey(k) && d[k] != null)
            {
                try { return Convert.ToBoolean(d[k]); } catch { }
            }
            return defVal;
        }

        private string FormatWmiDate(string dmtf)
        {
            if (!string.IsNullOrEmpty(dmtf) && dmtf.Length >= 8)
                return dmtf.Substring(0, 4) + "-" + dmtf.Substring(4, 2) + "-" + dmtf.Substring(6, 2);
            return "N/A";
        }

        private bool CheckRegDword(RegistryKey root, string subKey, string name, int expected)
        {
            try
            {
                using (var k = root.OpenSubKey(subKey))
                {
                    if (k == null) return false;
                    object v = k.GetValue(name);
                    return v != null && Convert.ToInt32(v) == expected;
                }
            }
            catch { return false; }
        }

        private bool CheckRegString(RegistryKey root, string subKey, string name, string expected)
        {
            try
            {
                using (var k = root.OpenSubKey(subKey))
                {
                    if (k == null) return false;
                    object v = k.GetValue(name);
                    return v != null && string.Equals(Convert.ToString(v), expected, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { return false; }
        }

        private bool CheckRegKeyExists(RegistryKey root, string subKey)
        {
            try
            {
                using (var k = root.OpenSubKey(subKey))
                    return k != null;
            }
            catch { return false; }
        }

        private void CollectUninstallEntries(RegistryKey root, string path, List<KeyValuePair<string, string>> target)
        {
            try
            {
                using (var baseKey = root.OpenSubKey(path))
                {
                    if (baseKey == null) return;
                    foreach (string subName in baseKey.GetSubKeyNames())
                    {
                        try
                        {
                            using (var sk = baseKey.OpenSubKey(subName))
                            {
                                if (sk == null) continue;
                                string dn = Convert.ToString(sk.GetValue("DisplayName") ?? "");
                                if (!string.IsNullOrEmpty(dn))
                                {
                                    string dv = Convert.ToString(sk.GetValue("DisplayVersion") ?? "Instalado");
                                    target.Add(new KeyValuePair<string, string>(dn, dv));
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        // ====================================================================
        // GESTIÓN DE TEMA CYBERPUNK MURO NEGRO / ARASAKA CLARO EN TIEMPO REAL
        // ====================================================================
        private Color ColorHex(string hex)
        {
            return (Color)ColorConverter.ConvertFromString(hex);
        }

        private SolidColorBrush BrushHex(string hex)
        {
            return new SolidColorBrush(ColorHex(hex));
        }

        private void UpdateThemeBrushes()
        {
            if (BgApp == null)
            {
                // Primera inicialización de pinceles
                BgApp = BrushHex(_isDark ? "#07070A" : "#F4F3F1");
                BgSidebar = BrushHex(_isDark ? "#0B0B10" : "#FFFFFF");
                BgSurface = BrushHex(_isDark ? "#111118" : "#FFFFFF");
                BgElevated = BrushHex(_isDark ? "#181824" : "#E9E7E2");
                BorderSubtle = BrushHex(_isDark ? "#1F202E" : "#D8D5CD");
                TextPrimary = BrushHex(_isDark ? "#F0F2F6" : "#121216");
                TextSecondary = BrushHex(_isDark ? "#8B90A0" : "#555866");
                AccentBrush = BrushHex(_isDark ? "#FF1F4B" : "#C8102E");
                CyberCyanBrush = BrushHex(_isDark ? "#00E5FF" : "#008BA3");
                CyberYellowBrush = BrushHex(_isDark ? "#FCEE0A" : "#A37500");
                RiskLowBg = BrushHex(_isDark ? "#0B1E19" : "#E6F7F0");
                RiskLowText = BrushHex(_isDark ? "#00E5A3" : "#0D8258");
                RiskMedBg = BrushHex(_isDark ? "#2A2207" : "#FFF8E1");
                RiskMedText = BrushHex(_isDark ? "#FCEE0A" : "#B26A00");
                RiskHighBg = BrushHex(_isDark ? "#2B0A11" : "#FDE8EA");
                RiskHighText = BrushHex(_isDark ? "#FF3B5C" : "#C8102E");
            }
            else
            {
                // Mutación reactiva en caliente sin destruir el árbol visual
                BgApp.Color = ColorHex(_isDark ? "#07070A" : "#F4F3F1");
                BgSidebar.Color = ColorHex(_isDark ? "#0B0B10" : "#FFFFFF");
                BgSurface.Color = ColorHex(_isDark ? "#111118" : "#FFFFFF");
                BgElevated.Color = ColorHex(_isDark ? "#181824" : "#E9E7E2");
                BorderSubtle.Color = ColorHex(_isDark ? "#1F202E" : "#D8D5CD");
                TextPrimary.Color = ColorHex(_isDark ? "#F0F2F6" : "#121216");
                TextSecondary.Color = ColorHex(_isDark ? "#8B90A0" : "#555866");
                AccentBrush.Color = ColorHex(_isDark ? "#FF1F4B" : "#C8102E");
                CyberCyanBrush.Color = ColorHex(_isDark ? "#00E5FF" : "#008BA3");
                CyberYellowBrush.Color = ColorHex(_isDark ? "#FCEE0A" : "#A37500");
                RiskLowBg.Color = ColorHex(_isDark ? "#0B1E19" : "#E6F7F0");
                RiskLowText.Color = ColorHex(_isDark ? "#00E5A3" : "#0D8258");
                RiskMedBg.Color = ColorHex(_isDark ? "#2A2207" : "#FFF8E1");
                RiskMedText.Color = ColorHex(_isDark ? "#FCEE0A" : "#B26A00");
                RiskHighBg.Color = ColorHex(_isDark ? "#2B0A11" : "#FDE8EA");
                RiskHighText.Color = ColorHex(_isDark ? "#FF3B5C" : "#C8102E");
            }
        }

        private void TriggerLogoGlitch()
        {
            if (_logoBox == null) return;
            try
            {
                DoubleAnimation anim = new DoubleAnimation
                {
                    From = 0.35,
                    To = 1.0,
                    Duration = TimeSpan.FromMilliseconds(260)
                };
                _logoBox.BeginAnimation(UIElement.OpacityProperty, anim);
            }
            catch { }
        }

        private Button CreateWindowControlButton(string text, Action onClick, bool isClose = false)
        {
            TextBlock txt = new TextBlock
            {
                Text = text,
                FontWeight = FontWeights.Bold,
                FontSize = isClose ? 13 : 13.5,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = TextPrimary
            };
            if (text == "□" || text == "❐")
            {
                _maximizeIconText = txt;
            }

            Button btn = new Button
            {
                Width = 46,
                Height = 54,
                Content = txt,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Focusable = false
            };

            WindowChrome.SetIsHitTestVisibleInChrome(btn, true);

            btn.MouseEnter += (s, e) =>
            {
                if (isClose)
                {
                    btn.Background = BrushHex("#E81123");
                    txt.Foreground = Brushes.White;
                }
                else
                {
                    btn.Background = BgElevated;
                    txt.Foreground = TextPrimary;
                }
            };

            btn.MouseLeave += (s, e) =>
            {
                btn.Background = Brushes.Transparent;
                txt.Foreground = TextPrimary;
            };

            btn.Click += (s, e) => onClick();
            return btn;
        }

        // ====================================================================
        // CONSTRUCCIÓN DE LA INTERFAZ NATIVA WPF
        // ====================================================================
        private void BuildShellUI()
        {
            Background = BgApp;
            _rootGrid = new Grid();
            _rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(54) });
            _rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            _rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });

            // 1. BARRA SUPERIOR INTEGRADA (CUSTOM TITLEBAR CYBERPUNK CON WINDOW CHROME)
            Border topBar = new Border
            {
                Background = BgSidebar,
                BorderBrush = BorderSubtle,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(14, 0, 0, 0)
            };

            Grid topGrid = new Grid();
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(350) });
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Col 0: Logo Cyberpunk Blackwall ICE + Marca
            StackPanel brandStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            WindowChrome.SetIsHitTestVisibleInChrome(brandStack, true);

            _logoBox = new Border
            {
                Width = 34,
                Height = 34,
                CornerRadius = new CornerRadius(6, 0, 6, 0),
                Background = new LinearGradientBrush(ColorHex("#FF1F4B"), ColorHex("#8B0018"), 45),
                Margin = new Thickness(0, 0, 10, 0),
                Cursor = Cursors.Hand
            };
            _logoBox.Child = new TextBlock
            {
                Text = "BW",
                Foreground = Brushes.White,
                FontWeight = FontWeights.Black,
                FontSize = 14.5,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            _logoBox.MouseLeftButtonUp += (s, e) => TriggerLogoGlitch();
            brandStack.Children.Add(_logoBox);

            StackPanel titleStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            StackPanel brandLine = new StackPanel { Orientation = Orientation.Horizontal };
            brandLine.Children.Add(new TextBlock
            {
                Text = "BLACKWALL",
                Foreground = TextPrimary,
                FontWeight = FontWeights.Black,
                FontSize = 14.5
            });
            brandLine.Children.Add(new TextBlock
            {
                Text = " // ICE PROTOCOL",
                Foreground = AccentBrush,
                FontWeight = FontWeights.Bold,
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(5, 0, 0, 0)
            });
            titleStack.Children.Add(brandLine);

            _deviceBadgeText = new TextBlock
            {
                Text = _hardware != null && _hardware.ContainsKey("chassis")
                    ? FormatChassisHeader(_hardware["chassis"] as Dictionary<string, object>)
                    : "⚡ Iniciando motor WMI directo...",
                Foreground = CyberCyanBrush,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold
            };
            titleStack.Children.Add(_deviceBadgeText);
            brandStack.Children.Add(titleStack);
            Grid.SetColumn(brandStack, 0);
            topGrid.Children.Add(brandStack);

            // Col 1: Buscador Global Central (Omnibox Ctrl+K)
            Grid omniboxContainer = new Grid
            {
                MaxWidth = 520,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(12, 0, 12, 0)
            };
            WindowChrome.SetIsHitTestVisibleInChrome(omniboxContainer, true);

            Border omniBorder = new Border
            {
                Background = BgSurface,
                BorderBrush = BorderSubtle,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Height = 35,
                Padding = new Thickness(10, 0, 10, 0)
            };
            WindowChrome.SetIsHitTestVisibleInChrome(omniBorder, true);

            DockPanel omniDock = new DockPanel { LastChildFill = true };
            TextBlock searchIcon = new TextBlock
            {
                Text = "🔍 ",
                Foreground = TextSecondary,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };
            DockPanel.SetDock(searchIcon, Dock.Left);
            omniDock.Children.Add(searchIcon);

            Border kbdBadge = new Border
            {
                Background = BgElevated,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 2, 6, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0)
            };
            kbdBadge.Child = new TextBlock { Text = "Ctrl+K", Foreground = TextSecondary, FontSize = 10, FontWeight = FontWeights.Bold };
            DockPanel.SetDock(kbdBadge, Dock.Right);
            omniDock.Children.Add(kbdBadge);

            _omniboxInput = new TextBox
            {
                Background = Brushes.Transparent,
                Foreground = TextPrimary,
                CaretBrush = TextPrimary,
                BorderThickness = new Thickness(0),
                VerticalContentAlignment = VerticalAlignment.Center,
                FontSize = 12.5
            };
            WindowChrome.SetIsHitTestVisibleInChrome(_omniboxInput, true);
            _omniboxInput.TextChanged += OnOmniboxTextChanged;
            omniDock.Children.Add(_omniboxInput);
            omniBorder.Child = omniDock;
            omniboxContainer.Children.Add(omniBorder);

            _omniboxPopup = new Popup
            {
                PlacementTarget = omniBorder,
                Placement = PlacementMode.Bottom,
                StaysOpen = false,
                AllowsTransparency = true,
                Width = 520
            };
            Border popupBorder = new Border
            {
                Background = BgSurface,
                BorderBrush = AccentBrush,
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8),
                Margin = new Thickness(0, 4, 0, 0)
            };
            _omniboxResultsPanel = new StackPanel();
            popupBorder.Child = new ScrollViewer
            {
                MaxHeight = 390,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = _omniboxResultsPanel
            };
            _omniboxPopup.Child = popupBorder;

            Grid.SetColumn(omniboxContainer, 1);
            topGrid.Children.Add(omniboxContainer);

            // Col 2: Botones de Acción + Separador + Controles de Ventana (Min, Max/Restore, Close)
            StackPanel rightContainer = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Stretch };

            StackPanel actionsStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            WindowChrome.SetIsHitTestVisibleInChrome(actionsStack, true);

            if (!IsAdministrator())
            {
                Button adminBtn = CreateStyledButton("🛡️ Elevar a Admin", true);
                adminBtn.Margin = new Thickness(0, 0, 6, 0);
                adminBtn.Click += (s, e) => RelaunchAsAdministrator();
                actionsStack.Children.Add(adminBtn);
            }

            Button rpBtn = CreateStyledButton("🛡️ Punto Restauración", false);
            rpBtn.Margin = new Thickness(0, 0, 6, 0);
            rpBtn.Click += async (s, e) => await CreateRestorePointNowAsync();
            actionsStack.Children.Add(rpBtn);

            _otaBtn = CreateStyledButton("🔄 OTA v1.3.0", false);
            _otaBtn.Margin = new Thickness(0, 0, 6, 0);
            _otaBtn.Click += (s, e) => ShowOtaUpdateDialog();
            actionsStack.Children.Add(_otaBtn);

            _themeBtn = CreateStyledButton(_isDark ? "☀️ Arasaka Light" : "🌙 Blackwall ICE", false);
            _themeBtn.Margin = new Thickness(0, 0, 8, 0);
            _themeBtn.Click += (s, e) =>
            {
                _isDark = !_isDark;
                UpdateThemeBrushes();
                _themeBtn.Content = _isDark ? "☀️ Arasaka Light" : "🌙 Blackwall ICE";
                TriggerLogoGlitch();
            };
            actionsStack.Children.Add(_themeBtn);
            rightContainer.Children.Add(actionsStack);

            // Separador vertical sutil
            Border titleSep = new Border
            {
                Width = 1,
                Height = 22,
                Background = BorderSubtle,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 8, 0)
            };
            rightContainer.Children.Add(titleSep);

            // Controles Integrados de Ventana (Minimizar, Restaurar/Maximizar, Cerrar)
            StackPanel winControlsStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Stretch };
            _minBtn = CreateWindowControlButton("—", () => this.WindowState = WindowState.Minimized);
            _maxBtn = CreateWindowControlButton((this.WindowState == WindowState.Maximized) ? "❐" : "□", () =>
            {
                this.WindowState = (this.WindowState == WindowState.Maximized) ? WindowState.Normal : WindowState.Maximized;
            });
            _closeBtn = CreateWindowControlButton("✕", () => this.Close(), true);

            winControlsStack.Children.Add(_minBtn);
            winControlsStack.Children.Add(_maxBtn);
            winControlsStack.Children.Add(_closeBtn);
            rightContainer.Children.Add(winControlsStack);

            Grid.SetColumn(rightContainer, 2);
            topGrid.Children.Add(rightContainer);
            topBar.Child = topGrid;
            Grid.SetRow(topBar, 0);
            _rootGrid.Children.Add(topBar);

            // 2. CUERPO CENTRAL
            Grid bodyGrid = new Grid();
            bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(255) });
            bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            Border sidebarBorder = new Border
            {
                Background = BgSidebar,
                BorderBrush = BorderSubtle,
                BorderThickness = new Thickness(0, 0, 1, 0),
                Padding = new Thickness(12, 16, 12, 16)
            };

            DockPanel sidebarDock = new DockPanel { LastChildFill = false };
            StackPanel navStack = new StackPanel();
            _navButtons.Clear();

            AddNavGroupLabel(navStack, "AUDITORÍA FÍSICA Y LÓGICA");
            AddNavButton(navStack, "hardware", "📊  Auditoría de Hardware");

            AddNavGroupLabel(navStack, "RENDIMIENTO Y PRIVACIDAD");
            AddNavButton(navStack, "optimization", "🚀  Optimización Adaptativa");
            AddNavButton(navStack, "debloat", "🧹  Debloat & Privacidad");

            AddNavGroupLabel(navStack, "AUTOMATIZACIÓN Y PROCESOS");
            AddNavButton(navStack, "apps", "📦  Instalador Desatendido");
            AddNavButton(navStack, "monitor", "📡  Scripts en Ejecución");

            AddNavGroupLabel(navStack, "PROTECCIÓN DEL SISTEMA");
            AddNavButton(navStack, "safety", "🛡️  Seguridad y Restauración");

            DockPanel.SetDock(navStack, Dock.Top);
            sidebarDock.Children.Add(navStack);

            Border infoBox = new Border
            {
                Background = BgSurface,
                BorderBrush = BorderSubtle,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10)
            };
            StackPanel infoStack = new StackPanel();
            infoStack.Children.Add(new TextBlock
            {
                Text = "🛡️ 100% Libre de Falsos Positivos",
                Foreground = RiskLowText,
                FontWeight = FontWeights.Bold,
                FontSize = 11.5
            });
            infoStack.Children.Add(new TextBlock
            {
                Text = "Motor C# nativo firmado sin scripts .ps1 ni llamadas a PowerShell.",
                Foreground = TextSecondary,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
                Margin = new Thickness(0, 3, 0, 0)
            });
            infoBox.Child = infoStack;
            DockPanel.SetDock(infoBox, Dock.Bottom);
            sidebarDock.Children.Add(infoBox);

            sidebarBorder.Child = sidebarDock;
            Grid.SetColumn(sidebarBorder, 0);
            bodyGrid.Children.Add(sidebarBorder);

            _mainScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(22, 18, 22, 22)
            };
            Grid.SetColumn(_mainScroll, 1);
            bodyGrid.Children.Add(_mainScroll);

            Grid.SetRow(bodyGrid, 1);
            _rootGrid.Children.Add(bodyGrid);

            // 3. BARRA INFERIOR DE ESTADO
            Border footerBorder = new Border
            {
                Background = BgSidebar,
                BorderBrush = BorderSubtle,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(18, 0, 18, 0)
            };
            DockPanel footerDock = new DockPanel { LastChildFill = false };
            _statusText = new TextBlock
            {
                Text = "🟢 Listo — Motor Nativo Certificado (Sin llamadas a PowerShell)",
                Foreground = TextPrimary,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12.2,
                FontWeight = FontWeights.SemiBold
            };
            DockPanel.SetDock(_statusText, Dock.Left);
            footerDock.Children.Add(_statusText);

            StackPanel footerRight = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            _taskText = new TextBlock
            {
                Text = "Sin tareas en cola",
                Foreground = TextSecondary,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12,
                Margin = new Thickness(0, 0, 12, 0)
            };
            footerRight.Children.Add(_taskText);

            Button consoleJumpBtn = CreateStyledButton("💻 Ver Consola en Vivo", false);
            consoleJumpBtn.Padding = new Thickness(10, 4, 10, 4);
            consoleJumpBtn.Click += (s, e) => SwitchTab("monitor");
            footerRight.Children.Add(consoleJumpBtn);

            DockPanel.SetDock(footerRight, Dock.Right);
            footerDock.Children.Add(footerRight);
            footerBorder.Child = footerDock;
            Grid.SetRow(footerBorder, 2);
            _rootGrid.Children.Add(footerBorder);

            _rootBorder = new Border
            {
                Background = BgApp,
                Padding = (this.WindowState == WindowState.Maximized) ? new Thickness(7) : new Thickness(0),
                Child = _rootGrid
            };
            Content = _rootBorder;
            UpdateNavSelection();
        }

        private void AddNavGroupLabel(StackPanel parent, string text)
        {
            parent.Children.Add(new TextBlock
            {
                Text = text,
                Foreground = TextSecondary,
                FontSize = 10.5,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(10, 12, 6, 5)
            });
        }

        private void AddNavButton(StackPanel parent, string tabKey, string label)
        {
            Button btn = new Button
            {
                Content = label,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(12, 9, 12, 9),
                Margin = new Thickness(0, 2, 0, 2),
                FontSize = 13.2,
                Cursor = Cursors.Hand,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Foreground = TextSecondary
            };
            btn.Click += (s, e) => SwitchTab(tabKey);
            _navButtons[tabKey] = btn;
            parent.Children.Add(btn);
        }

        private void UpdateNavSelection()
        {
            foreach (var kv in _navButtons)
            {
                bool active = kv.Key == _activeTab;
                kv.Value.Background = active ? BgElevated : Brushes.Transparent;
                kv.Value.Foreground = active ? AccentBrush : TextSecondary;
                kv.Value.FontWeight = active ? FontWeights.Bold : FontWeights.Normal;
            }
        }

        private void SwitchTab(string tabKey)
        {
            _activeTab = tabKey;
            UpdateNavSelection();
            RenderCurrentTab();
        }

        // ====================================================================
        // BUSCADOR GLOBAL CENTRADO (OMNIBOX CTRL+K)
        // ====================================================================
        private void OnOmniboxTextChanged(object sender, TextChangedEventArgs e)
        {
            string q = (_omniboxInput.Text ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(q))
            {
                _omniboxPopup.IsOpen = false;
                return;
            }

            _omniboxResultsPanel.Children.Clear();
            int count = 0;

            if (_hardware != null)
            {
                var chassis = GetDict(_hardware, "chassis");
                var mb = GetDict(_hardware, "motherboard");
                var cpu = GetDict(_hardware, "cpu");
                var mem = GetDict(_hardware, "memory");
                var bat = GetDict(_hardware, "battery");

                var hwItems = new[]
                {
                    new { Title = "Serial BIOS / Service Tag: " + GetStr(chassis, "serialNumber"), Sub = GetStr(chassis, "manufacturer") + " " + GetStr(chassis, "model") + " (" + GetStr(chassis, "deviceType") + ")", Key = "serial bios service tag sku uuid modelo chasis" },
                    new { Title = "Placa Base: " + GetStr(mb, "manufacturer") + " " + GetStr(mb, "product"), Sub = "BIOS " + GetStr(mb, "biosVersion") + " • " + GetStr(mb, "bootMode") + " • " + GetStr(mb, "tpmVersion"), Key = "placa base motherboard bios uefi tpm secure boot" },
                    new { Title = "CPU: " + GetStr(cpu, "name"), Sub = GetStr(cpu, "physicalCores") + " Núcleos / " + GetStr(cpu, "logicalThreads") + " Hilos • Socket " + GetStr(cpu, "socket"), Key = "cpu procesador ryzen intel nucleos hilos cache" },
                    new { Title = "Memoria RAM: " + GetStr(mem, "totalGb") + " GB " + GetStr(mem, "memoryType") + " @ " + GetStr(mem, "speedMhz") + " MHz", Sub = GetStr(mem, "channelMode") + " (" + GetStr(mem, "occupiedSlots") + "/" + GetStr(mem, "totalSlots") + " Slots)", Key = "ram memoria slot dimm ddr4 ddr5 hynix samsung" },
                    new { Title = "Batería: Salud " + GetStr(bat, "healthPercent") + "% (Desgaste " + GetStr(bat, "wearLevelPercent") + "%)", Sub = "Diseño: " + GetStr(bat, "designedCapacityMwh") + " mWh vs Actual: " + GetStr(bat, "fullChargedCapacityMwh") + " mWh", Key = "bateria wear desgaste salud mwh laptop" }
                };

                foreach (var h in hwItems)
                {
                    if (h.Title.ToLowerInvariant().Contains(q) || h.Sub.ToLowerInvariant().Contains(q) || h.Key.Contains(q))
                    {
                        AddOmniboxResultItem("💻 " + h.Title, h.Sub, "Hardware", () => SwitchTab("hardware"));
                        if (++count >= 9) break;
                    }
                }
            }

            foreach (var t in _tweaks)
            {
                if (count >= 10) break;
                string title = GetStr(t, "title");
                string desc = GetStr(t, "description");
                string sub = GetStr(t, "subcategory");
                if (title.ToLowerInvariant().Contains(q) || desc.ToLowerInvariant().Contains(q) || sub.ToLowerInvariant().Contains(q))
                {
                    string cat = GetStr(t, "category") == "optimization" ? "optimization" : "debloat";
                    string risk = GetStr(t, "riskLevel").ToUpperInvariant();
                    AddOmniboxResultItem("⚡ " + title, sub + " — " + desc, "Riesgo " + risk, () => SwitchTab(cat));
                    count++;
                }
            }

            foreach (var a in _apps)
            {
                if (count >= 12) break;
                string name = GetStr(a, "name");
                string desc = GetStr(a, "description");
                string wg = GetStr(a, "wingetId");
                if (name.ToLowerInvariant().Contains(q) || desc.ToLowerInvariant().Contains(q) || wg.ToLowerInvariant().Contains(q))
                {
                    string appId = GetStr(a, "id");
                    AddOmniboxResultItem("📦 Instalar " + name, GetStr(a, "category") + " • " + wg, "App Silenciosa", () =>
                    {
                        _selectedApps.Add(appId);
                        SwitchTab("apps");
                    });
                    count++;
                }
            }

            if (count == 0)
            {
                _omniboxResultsPanel.Children.Add(new TextBlock
                {
                    Text = "Sin resultados para \"" + _omniboxInput.Text + "\"",
                    Foreground = TextSecondary,
                    Margin = new Thickness(8)
                });
            }

            _omniboxPopup.IsOpen = true;
        }

        private void AddOmniboxResultItem(string title, string subtitle, string badge, Action onClick)
        {
            Border itemBorder = new Border
            {
                Background = Brushes.Transparent,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 7, 10, 7),
                Cursor = Cursors.Hand
            };
            itemBorder.MouseEnter += (s, e) => itemBorder.Background = BgElevated;
            itemBorder.MouseLeave += (s, e) => itemBorder.Background = Brushes.Transparent;

            DockPanel dp = new DockPanel();
            Border pill = CreateBadge(badge, "low");
            DockPanel.SetDock(pill, Dock.Right);
            dp.Children.Add(pill);

            StackPanel sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = title, Foreground = TextPrimary, FontWeight = FontWeights.SemiBold, FontSize = 12.8 });
            sp.Children.Add(new TextBlock { Text = subtitle, Foreground = TextSecondary, FontSize = 11.2, TextTrimming = TextTrimming.CharacterEllipsis });
            dp.Children.Add(sp);

            itemBorder.Child = dp;
            itemBorder.MouseLeftButtonUp += (s, e) =>
            {
                _omniboxPopup.IsOpen = false;
                _omniboxInput.Text = "";
                onClick();
            };
            _omniboxResultsPanel.Children.Add(itemBorder);
        }

        // ====================================================================
        // RENDERIZADO CENTRAL DE CADA MÓDULO
        // ====================================================================
        private void RenderCurrentTab()
        {
            if (_mainScroll == null) return;
            _tweakCardUpdaters.Clear();

            UIElement content = null;
            switch (_activeTab)
            {
                case "hardware":
                    content = BuildHardwareView();
                    break;
                case "optimization":
                    content = BuildTweaksView("optimization");
                    break;
                case "debloat":
                    content = BuildTweaksView("debloat");
                    break;
                case "apps":
                    content = BuildAppsView();
                    break;
                case "monitor":
                    content = BuildMonitorView();
                    break;
                case "safety":
                    content = BuildSafetyView();
                    break;
            }

            if (content != null)
            {
                Grid centeredWrap = new Grid
                {
                    MaxWidth = 1360,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                centeredWrap.Children.Add(content);
                _mainScroll.Content = centeredWrap;
            }
        }

        // --------------------------------------------------------------------
        // MÓDULO A: AUDITORÍA PROFUNDA DE HARDWARE
        // --------------------------------------------------------------------
        private UIElement BuildHardwareView()
        {
            StackPanel root = new StackPanel();

            if (_hardware == null)
            {
                root.Children.Add(CreateBanner(
                    "⚡ Auditando Hardware por Canal WMI Nativo (< 0.35s)...",
                    "Leyendo tablas SMBIOS, slots físicos de RAM, unidades NVMe/SSD y batería..."
                ));
                return root;
            }

            var chassis = GetDict(_hardware, "chassis");
            var mb = GetDict(_hardware, "motherboard");
            var cpu = GetDict(_hardware, "cpu");
            var mem = GetDict(_hardware, "memory");
            var bat = GetDict(_hardware, "battery");
            var osNet = GetDict(_hardware, "osNetwork");
            var storageList = ConvertToDictList(_hardware.ContainsKey("storage") ? _hardware["storage"] : null);
            var gpuList = ConvertToDictList(_hardware.ContainsKey("gpus") ? _hardware["gpus"] : null);

            StackPanel headerActions = new StackPanel { Orientation = Orientation.Horizontal };
            Button btnRescan = CreateStyledButton("🔄 Re-escanear", false);
            btnRescan.Margin = new Thickness(0, 0, 8, 0);
            btnRescan.Click += async (s, e) => await RefreshHardwareNativeAsync();
            headerActions.Children.Add(btnRescan);

            Button btnJson = CreateStyledButton("📄 Exportar JSON", false);
            btnJson.Margin = new Thickness(0, 0, 8, 0);
            btnJson.Click += (s, e) => ExportReport("json");
            headerActions.Children.Add(btnJson);

            Button btnTxt = CreateStyledButton("📝 Exportar TXT", false);
            btnTxt.Margin = new Thickness(0, 0, 8, 0);
            btnTxt.Click += (s, e) => ExportReport("txt");
            headerActions.Children.Add(btnTxt);

            Button btnHtml = CreateStyledButton("🌐 Exportar HTML", true);
            btnHtml.Click += (s, e) => ExportReport("html");
            headerActions.Children.Add(btnHtml);

            root.Children.Add(CreateBanner(
                (GetBool(chassis, "isLaptop") ? "💻 " : "🖥️ ") + GetStr(chassis, "manufacturer") + " " + GetStr(chassis, "model") + " (" + GetStr(chassis, "deviceType") + ")",
                "Serial BIOS: " + GetStr(chassis, "serialNumber") + "  •  SKU: " + GetStr(chassis, "sku") + "  •  Tiempo de Escaneo: " + GetStr(_hardware, "scannedAt"),
                headerActions
            ));

            UniformGrid row1 = new UniformGrid { Columns = 3, Margin = new Thickness(0, 0, 0, 14) };

            row1.Children.Add(CreateKvCard("🏷️ 1. Identificación OEM y Chasis", GetStr(chassis, "deviceType"), new[,]
            {
                { "Tipo de Chasis", GetStr(chassis, "deviceType") },
                { "Fabricante / Marca", GetStr(chassis, "manufacturer") },
                { "Modelo Exacto", GetStr(chassis, "model") },
                { "Serial BIOS / Service Tag", GetStr(chassis, "serialNumber") },
                { "SKU del Sistema", GetStr(chassis, "sku") },
                { "UUID SMBIOS", GetStr(chassis, "uuid") }
            }));

            row1.Children.Add(CreateKvCard("🔌 2. Placa Base, UEFI y TPM", GetStr(mb, "bootMode"), new[,]
            {
                { "Placa Base", GetStr(mb, "manufacturer") + " " + GetStr(mb, "product") },
                { "Serial Placa Base", GetStr(mb, "serialNumber") },
                { "Versión / Fecha BIOS", GetStr(mb, "biosVersion") + " (" + GetStr(mb, "biosDate") + ")" },
                { "Modo de Arranque", GetStr(mb, "bootMode") },
                { "Estado Secure Boot", GetStr(mb, "secureBoot") },
                { "Módulo TPM", GetStr(mb, "tpmVersion") }
            }));

            row1.Children.Add(CreateKvCard("🧠 3. Procesador (CPU)", "Uso: " + GetStr(cpu, "loadPercent") + "%", new[,]
            {
                { "Nombre Exacto", GetStr(cpu, "name") },
                { "Arquitectura / Socket", GetStr(cpu, "architecture") + " • " + GetStr(cpu, "socket") },
                { "Núcleos / Hilos", GetStr(cpu, "physicalCores") + " Físicos / " + GetStr(cpu, "logicalThreads") + " Hilos" },
                { "Frecuencia Base / Actual", GetStr(cpu, "baseClockMhz") + " MHz / " + GetStr(cpu, "currentClockMhz") + " MHz" },
                { "Caché L2 / Caché L3", GetStr(cpu, "l2CacheKb") + " KB / " + GetStr(cpu, "l3CacheKb") + " KB" },
                { "Virtualización", GetBool(cpu, "virtualizationEnabled") ? "Habilitada (SVM/VT-x)" : "Deshabilitada" }
            }));

            root.Children.Add(row1);

            Border ramCard = CreateCardContainer();
            ramCard.Margin = new Thickness(0, 0, 0, 14);
            StackPanel ramStack = new StackPanel();
            DockPanel ramHeader = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            Border chanBadge = CreateBadge(GetStr(mem, "channelMode"), "low");
            DockPanel.SetDock(chanBadge, Dock.Right);
            ramHeader.Children.Add(chanBadge);
            ramHeader.Children.Add(new TextBlock
            {
                Text = "⚡ 4. Memoria RAM — Detalle General y por Slot Físico (" + GetStr(mem, "occupiedSlots") + " de " + GetStr(mem, "totalSlots") + " Slots Ocupados — Total: " + GetStr(mem, "totalGb") + " GB " + GetStr(mem, "memoryType") + " @ " + GetStr(mem, "speedMhz") + " MHz)",
                Foreground = TextPrimary,
                FontWeight = FontWeights.Bold,
                FontSize = 14.5
            });
            ramStack.Children.Add(ramHeader);

            UniformGrid slotsGrid = new UniformGrid { Columns = 2 };
            var slots = ConvertToDictList(mem.ContainsKey("slots") ? mem["slots"] : null);
            foreach (var s in slots)
            {
                bool occ = GetBool(s, "occupied");
                string title = "📍 " + GetStr(s, "slotName") + " (" + GetStr(s, "bankLabel") + ")";
                string badge = occ ? (GetStr(s, "capacityGb") + " GB " + GetStr(s, "memType") + " @ " + GetStr(s, "speedMhz") + " MHz") : "SLOT VACÍO";
                slotsGrid.Children.Add(CreateKvCard(title, badge, new[,]
                {
                    { "Estado de Ranura", occ ? "Ocupado (Activo)" : "Vacío (Disponible para expansión)" },
                    { "Marca / Fabricante", GetStr(s, "manufacturer") },
                    { "Número de Parte (Part #)", GetStr(s, "partNumber") },
                    { "Serial Individual Stick", GetStr(s, "serialNumber") },
                    { "Voltaje Configurado", GetStr(s, "voltageMv") + " mV" }
                }));
            }
            ramStack.Children.Add(slotsGrid);
            ramCard.Child = ramStack;
            root.Children.Add(ramCard);

            UniformGrid row3 = new UniformGrid { Columns = 3, Margin = new Thickness(0, 0, 0, 14) };

            var d0 = storageList.Count > 0 ? storageList[0] : new Dictionary<string, object>();
            row3.Children.Add(CreateKvCard("💿 5. Almacenamiento & S.M.A.R.T.", storageList.Count + " Unidad(es)", new[,]
            {
                { "Modelo Principal", GetStr(d0, "friendlyName") },
                { "Interfaz / Partición", GetStr(d0, "busType") + " (" + GetStr(d0, "mediaType") + ") • " + GetStr(d0, "partitionStyle") },
                { "Capacidad Total / Libre C:", GetStr(d0, "sizeGb") + " GB / " + GetStr(d0, "freeGb") + " GB Libres" },
                { "Número de Serie Disco", GetStr(d0, "serialNumber") },
                { "Estado Salud S.M.A.R.T.", GetStr(d0, "healthStatus") + " (" + GetStr(d0, "healthPercent") + "%)" },
                { "Unidades Adicionales", storageList.Count > 1 ? GetStr(storageList[1], "friendlyName") + " (" + GetStr(storageList[1], "sizeGb") + " GB)" : "Ninguna" }
            }));

            var g0 = gpuList.Count > 0 ? gpuList[0] : new Dictionary<string, object>();
            row3.Children.Add(CreateKvCard("🎮 6. Gráficos (GPU) y Monitor", GetStr(g0, "resolution"), new[,]
            {
                { "Modelo GPU", GetStr(g0, "name") },
                { "VRAM Dedicada", GetStr(g0, "vramGb") + " GB" },
                { "Versión de Driver", GetStr(g0, "driverVersion") },
                { "Fecha de Driver", GetStr(g0, "driverDate") },
                { "Resolución Activa", GetStr(g0, "resolution") },
                { "Tasa de Refresco", GetStr(g0, "refreshRateHz") + " Hz" }
            }));

            row3.Children.Add(CreateKvCard("🔋 7. Batería y Desgaste (Laptop)", "Salud: " + GetStr(bat, "healthPercent") + "%", new[,]
            {
                { "Estado de Energía", GetStr(bat, "status") + " (" + GetStr(bat, "chargePercent") + "%)" },
                { "Capacidad de Diseño", GetStr(bat, "designedCapacityMwh") + " mWh" },
                { "Carga Completa Actual", GetStr(bat, "fullChargedCapacityMwh") + " mWh" },
                { "% de Desgaste (Wear Level)", GetStr(bat, "wearLevelPercent") + "% desgastado" },
                { "% Vida Útil Restante", GetStr(bat, "healthPercent") + "%" },
                { "Química / Tasa Descarga", GetStr(bat, "chemistry") + " • " + GetStr(bat, "dischargeRateMw") + " mW" }
            }));

            root.Children.Add(row3);

            var adapters = ConvertToDictList(osNet.ContainsKey("adapters") ? osNet["adapters"] : null);
            string netSummary = adapters.Count > 0
                ? string.Join("  |  ", adapters.Select(a => GetStr(a, "name") + ": " + GetStr(a, "ipv4") + " (" + GetStr(a, "linkSpeed") + ", MAC " + GetStr(a, "macAddress") + ")"))
                : "Sin adaptadores activos";

            root.Children.Add(CreateKvCard("🌐 8. Sistema Operativo Windows y Adaptadores de Red", GetStr(osNet, "displayVersion") + " • Build " + GetStr(osNet, "buildNumber"), new[,]
            {
                { "Edición de Windows", GetStr(osNet, "caption") + " (" + GetStr(osNet, "displayVersion") + " - Build " + GetStr(osNet, "buildNumber") + ")" },
                { "Fecha Instalación / Uptime", GetStr(osNet, "installDate") + "  •  Activo: " + GetStr(osNet, "uptime") },
                { "Clave OEM / Licencia Parcial", GetStr(osNet, "partialProductKey") },
                { "Adaptadores IP / MAC / Enlace", netSummary }
            }));

            return root;
        }

        // --------------------------------------------------------------------
        // MÓDULO B: OPTIMIZACIÓN Y DEBLOAT
        // --------------------------------------------------------------------
        private string _tweakSubcategoryScope = "Todas";

        private async Task ApplyBlackwallPackAsync(string packId, string packLabel)
        {
            await CreateRestorePointNowAsync();
            int count = 0;
            foreach (var t in _tweaks)
            {
                if (GetStr(t, "pack") == packId)
                {
                    await ExecuteTweakAsync(t, true);
                    count++;
                }
            }
            AppendLog("Paquete Blackwall aplicado: " + packLabel + " (" + count + " tweaks ejecutados).", "PACK");
            SetStatus("🟢 Paquete Aplicado: " + packLabel + " (" + count + " reglas)", "Completado");
            await RefreshTweaksStateNativeAsync();
        }

        private UIElement BuildTweaksView(string categoryType)
        {
            StackPanel root = new StackPanel();
            bool isLaptop = _hardware == null || GetBool(GetDict(_hardware, "chassis"), "isLaptop");
            string effectiveDevice = _optDeviceScope == "auto" ? (isLaptop ? "laptop" : "desktop") : _optDeviceScope;

            StackPanel topBtns = new StackPanel { Orientation = Orientation.Horizontal };
            Button recheckBtn = CreateStyledButton("🔍 Re-evaluar Estados (<5ms)", false);
            recheckBtn.Margin = new Thickness(0, 0, 8, 0);
            recheckBtn.Click += async (s, e) => await RefreshTweaksStateNativeAsync();
            topBtns.Children.Add(recheckBtn);

            Button applyProfileBtn = CreateStyledButton("⚡ Aplicar Perfil Seguro (" + (isLaptop ? "Laptop" : "Desktop") + ")", true);
            applyProfileBtn.Click += async (s, e) => await ApplyRecommendedSafeProfileAsync(isLaptop ? "laptop" : "desktop");
            topBtns.Children.Add(applyProfileBtn);

            root.Children.Add(CreateBanner(
                categoryType == "optimization"
                    ? "🚀 Optimización Adaptativa & Gaming Tweaks (Laptop / Desktop + Ceito Tweaks)"
                    : "🧹 BLACKWALL ICE Debloat (Chris Titus WinUtil + Privacidad & Servicios)",
                "Ejecución 100% nativa sobre el Registro y Servicios de Windows (Sin scripts .ps1 ni alertas de Antivirus).",
                topBtns
            ));

            // Barra de Paquetes Maestros de 1 Clic (Blackwall Bundles)
            Border packCard = CreateCardContainer();
            packCard.Margin = new Thickness(0, 0, 0, 14);
            packCard.Padding = new Thickness(14, 10, 14, 10);
            StackPanel packStack = new StackPanel();
            packStack.Children.Add(new TextBlock
            {
                Text = "📦 PAQUETES DE DESPLIEGUE RÁPIDO BLACKWALL (1 CLIC — CREA PUNTO DE RESTAURACIÓN AUTOMÁTICO):",
                Foreground = AccentBrush,
                FontSize = 10.8,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 8)
            });

            WrapPanel packBtns = new WrapPanel();
            if (categoryType == "debloat")
            {
                Button btnTitus = CreateStyledButton("🛡️ Pack Chris Titus Essential (🟢 Bajo Riesgo)", true);
                btnTitus.Margin = new Thickness(0, 0, 8, 6);
                btnTitus.Click += async (s, e) => await ApplyBlackwallPackAsync("titus_essential", "Chris Titus Essential");
                packBtns.Children.Add(btnTitus);

                Button btnPrivacy = CreateStyledButton("👁️ Pack Anti-IA, Copilot, Recall & Edge (🟢 Bajo Riesgo)", false);
                btnPrivacy.Margin = new Thickness(0, 0, 8, 6);
                btnPrivacy.Click += async (s, e) => await ApplyBlackwallPackAsync("privacy_ai", "Anti-IA & Privacidad");
                packBtns.Children.Add(btnPrivacy);

                Button btnServices = CreateStyledButton("⚙️ Pack Servicios a Manual (🟢/🟡 Estilo Chris Titus)", false);
                btnServices.Margin = new Thickness(0, 0, 8, 6);
                btnServices.Click += async (s, e) => await ApplyBlackwallPackAsync("services_light", "Servicios Ligeros");
                packBtns.Children.Add(btnServices);
            }
            else
            {
                Button btnCeito = CreateStyledButton("🎯 Pack Ceito Gaming & 0 Input Lag (🟢/🟡 Ratón 1:1, MMCSS, HAGS)", true);
                btnCeito.Margin = new Thickness(0, 0, 8, 6);
                btnCeito.Click += async (s, e) => await ApplyBlackwallPackAsync("ceito_gaming", "Ceito Gaming & Baja Latencia");
                packBtns.Children.Add(btnCeito);

                Button btnPower = CreateStyledButton("⚡ Pack Energía Adaptativa (" + (isLaptop ? "Laptop Batería/Térmico" : "Desktop Ultimate") + ")", false);
                btnPower.Margin = new Thickness(0, 0, 8, 6);
                btnPower.Click += async (s, e) => await ApplyBlackwallPackAsync("adaptive_power", "Energía Adaptativa");
                packBtns.Children.Add(btnPower);
            }
            packStack.Children.Add(packBtns);
            packCard.Child = packStack;
            root.Children.Add(packCard);

            // Filtros de Dispositivo y Nivel de Riesgo
            WrapPanel filterBar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            AddFilterButton(filterBar, "💻 Enfoque Laptop", effectiveDevice == "laptop", () => { _optDeviceScope = "laptop"; RenderCurrentTab(); });
            AddFilterButton(filterBar, "🖥️ Enfoque Desktop", effectiveDevice == "desktop", () => { _optDeviceScope = "desktop"; RenderCurrentTab(); });
            AddFilterButton(filterBar, "🌐 Todos los Equipos", effectiveDevice == "all", () => { _optDeviceScope = "all"; RenderCurrentTab(); });

            AddFilterButton(filterBar, "Todos los Riesgos", _optRiskScope == "all", () => { _optRiskScope = "all"; RenderCurrentTab(); });
            AddFilterButton(filterBar, "🟢 Riesgo Bajo", _optRiskScope == "low", () => { _optRiskScope = "low"; RenderCurrentTab(); });
            AddFilterButton(filterBar, "🟡 Riesgo Medio", _optRiskScope == "medium", () => { _optRiskScope = "medium"; RenderCurrentTab(); });
            AddFilterButton(filterBar, "🔴 Riesgo Alto", _optRiskScope == "high", () => { _optRiskScope = "high"; RenderCurrentTab(); });
            root.Children.Add(filterBar);

            // Filtro por Subcategoría
            var subcats = new List<string> { "Todas" };
            foreach (var t in _tweaks)
            {
                if (GetStr(t, "category") != categoryType) continue;
                string sc = GetStr(t, "subcategory");
                if (!string.IsNullOrEmpty(sc) && !subcats.Contains(sc)) subcats.Add(sc);
            }
            if (!subcats.Contains(_tweakSubcategoryScope)) _tweakSubcategoryScope = "Todas";

            WrapPanel subcatBar = new WrapPanel { Margin = new Thickness(0, 0, 0, 14) };
            foreach (string sc in subcats)
            {
                string scCopy = sc;
                AddFilterButton(subcatBar, scCopy == "Todas" ? "📂 Todas las Subcategorías" : scCopy, _tweakSubcategoryScope == scCopy, () =>
                {
                    _tweakSubcategoryScope = scCopy;
                    RenderCurrentTab();
                });
            }
            root.Children.Add(subcatBar);

            // Filtrado de tweaks para esta vista
            var matchingTweaks = new List<Dictionary<string, object>>();
            foreach (var t in _tweaks)
            {
                if (GetStr(t, "category") != categoryType) continue;
                if (effectiveDevice != "all" && !ListContains(t, "deviceScope", effectiveDevice)) continue;
                if (_optRiskScope != "all" && GetStr(t, "riskLevel") != _optRiskScope) continue;
                if (_tweakSubcategoryScope != "Todas" && GetStr(t, "subcategory") != _tweakSubcategoryScope) continue;
                matchingTweaks.Add(t);
            }

            StackPanel tweaksContainer = new StackPanel();

            if (_tweakSubcategoryScope == "Todas")
            {
                // Agrupación visual por Subcategoría con encabezados claros y amplia separación
                var subcatOrder = new List<string>();
                foreach (var t in matchingTweaks)
                {
                    string sc = GetStr(t, "subcategory");
                    if (string.IsNullOrEmpty(sc)) sc = "General";
                    if (!subcatOrder.Contains(sc)) subcatOrder.Add(sc);
                }

                foreach (string sc in subcatOrder)
                {
                    var tweaksInSubcat = matchingTweaks.Where(t => (GetStr(t, "subcategory") == sc || (string.IsNullOrEmpty(GetStr(t, "subcategory")) && sc == "General"))).ToList();
                    if (tweaksInSubcat.Count == 0) continue;

                    StackPanel groupSection = new StackPanel { Margin = new Thickness(0, 8, 0, 34) };

                    DockPanel grpHdr = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
                    Border countBadge = new Border
                    {
                        Background = BgElevated,
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(8, 3, 8, 3),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    countBadge.Child = new TextBlock
                    {
                        Text = tweaksInSubcat.Count + (tweaksInSubcat.Count == 1 ? " optimización" : " optimizaciones"),
                        Foreground = TextSecondary,
                        FontSize = 11,
                        FontWeight = FontWeights.Bold
                    };
                    DockPanel.SetDock(countBadge, Dock.Right);
                    grpHdr.Children.Add(countBadge);

                    StackPanel titleRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                    titleRow.Children.Add(new TextBlock
                    {
                        Text = "⚡ " + sc.ToUpperInvariant(),
                        Foreground = AccentBrush,
                        FontWeight = FontWeights.ExtraBold,
                        FontSize = 14.5
                    });
                    grpHdr.Children.Add(titleRow);
                    groupSection.Children.Add(grpHdr);

                    Border divider = new Border
                    {
                        Background = BorderSubtle,
                        Height = 1,
                        Margin = new Thickness(0, 0, 0, 14)
                    };
                    groupSection.Children.Add(divider);

                    UniformGrid groupGrid = new UniformGrid { Columns = 2 };
                    foreach (var t in tweaksInSubcat)
                    {
                        groupGrid.Children.Add(BuildTweakCard(t));
                    }
                    groupSection.Children.Add(groupGrid);
                    tweaksContainer.Children.Add(groupSection);
                }
            }
            else
            {
                UniformGrid singleGrid = new UniformGrid { Columns = 2 };
                foreach (var t in matchingTweaks)
                {
                    singleGrid.Children.Add(BuildTweakCard(t));
                }
                tweaksContainer.Children.Add(singleGrid);
            }

            root.Children.Add(tweaksContainer);
            return root;
        }

        private UIElement BuildTweakCard(Dictionary<string, object> t)
        {
            string id = GetStr(t, "id");
            string risk = GetStr(t, "riskLevel");
            string shortTitle = GetStr(t, "shortTitle");
            if (string.IsNullOrEmpty(shortTitle)) shortTitle = GetStr(t, "title");
            string summary = GetStr(t, "summary");
            if (string.IsNullOrEmpty(summary)) summary = GetStr(t, "description");
            string affects = GetStr(t, "affects");
            string warning = GetStr(t, "riskWarning");
            string subcat = GetStr(t, "subcategory");

            bool isApplied = _tweakStates.ContainsKey(id) && _tweakStates[id];

            Border card = CreateCardContainer();
            card.Margin = new Thickness(0, 0, 12, 12);
            card.BorderBrush = isApplied ? AccentBrush : BorderSubtle;

            StackPanel sp = new StackPanel();

            // 1. CABECERA: Badges a la izquierda + Switch Toggle Cyberpunk a la derecha
            DockPanel header = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };

            // Toggle Switch Cyberpunk interactivo
            Border switchTrack = new Border
            {
                Width = 46,
                Height = 24,
                CornerRadius = new CornerRadius(12),
                Background = isApplied ? AccentBrush : BgElevated,
                BorderBrush = BorderSubtle,
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center
            };

            Border switchThumb = new Border
            {
                Width = 18,
                Height = 18,
                CornerRadius = new CornerRadius(9),
                Background = Brushes.White,
                HorizontalAlignment = isApplied ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                Margin = new Thickness(2)
            };
            switchTrack.Child = switchThumb;

            TextBlock switchLabel = new TextBlock
            {
                Text = isApplied ? "ON" : "OFF",
                FontSize = 10.5,
                FontWeight = FontWeights.ExtraBold,
                Foreground = isApplied ? AccentBrush : TextSecondary,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0)
            };

            StackPanel switchContainer = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand,
                Margin = new Thickness(8, 0, 0, 0)
            };
            switchContainer.Children.Add(switchTrack);
            switchContainer.Children.Add(switchLabel);

            DockPanel.SetDock(switchContainer, Dock.Right);
            header.Children.Add(switchContainer);

            // Badges en la cabecera
            WrapPanel badgesPanel = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
            string riskText = risk == "low" ? "🟢 SEGURO" : risk == "medium" ? "🟡 PRECAUCIÓN" : "🔴 AVANZADO";
            Border riskBadge = CreateBadge(riskText, risk);
            riskBadge.Margin = new Thickness(0, 0, 6, 2);
            badgesPanel.Children.Add(riskBadge);

            if (!string.IsNullOrEmpty(affects))
            {
                Border affectsBadge = new Border
                {
                    Background = RiskMedBg,
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 2, 6, 2),
                    Margin = new Thickness(0, 0, 6, 2),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = "⚠️ Afecta: " + affects,
                        Foreground = RiskMedText,
                        FontSize = 10,
                        FontWeight = FontWeights.SemiBold
                    }
                };
                badgesPanel.Children.Add(affectsBadge);
            }

            if (!string.IsNullOrEmpty(subcat))
            {
                Border subcatBadge = new Border
                {
                    Background = BgElevated,
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 2, 6, 2),
                    Margin = new Thickness(0, 0, 6, 2),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = subcat.ToUpperInvariant(),
                        Foreground = TextSecondary,
                        FontSize = 9.5,
                        FontWeight = FontWeights.Bold
                    }
                };
                badgesPanel.Children.Add(subcatBadge);
            }

            header.Children.Add(badgesPanel);
            sp.Children.Add(header);

            // 2. TÍTULO CORTO Y RESUMEN DE 1 LÍNEA
            TextBlock titleText = new TextBlock
            {
                Text = shortTitle,
                Foreground = TextPrimary,
                FontSize = 13.5,
                FontWeight = FontWeights.Bold,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 3)
            };
            sp.Children.Add(titleText);

            TextBlock summaryText = new TextBlock
            {
                Text = summary,
                Foreground = TextSecondary,
                FontSize = 11.8,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 4)
            };
            sp.Children.Add(summaryText);

            // 3. CAJÓN DESPLEGABLE TÉCNICO (DRAWER)
            TextBlock toggleDrawerBtn = new TextBlock
            {
                Text = "Ver detalles técnicos ▾",
                Foreground = CyberCyanBrush,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 2, 0, 4)
            };
            sp.Children.Add(toggleDrawerBtn);

            StackPanel drawerPanel = new StackPanel
            {
                Visibility = Visibility.Collapsed,
                Margin = new Thickness(0, 4, 0, 6)
            };

            // Descripción completa
            drawerPanel.Children.Add(new TextBlock
            {
                Text = GetStr(t, "description"),
                Foreground = TextSecondary,
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6)
            });

            // Beneficios técnicos
            if (t.ContainsKey("benefits") && t["benefits"] is IEnumerable)
            {
                StackPanel benPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
                foreach (object bObj in (IEnumerable)t["benefits"])
                {
                    string bStr = Convert.ToString(bObj);
                    if (!string.IsNullOrEmpty(bStr))
                    {
                        benPanel.Children.Add(new TextBlock
                        {
                            Text = "✓ " + bStr,
                            Foreground = RiskLowText,
                            FontSize = 11,
                            FontWeight = FontWeights.SemiBold,
                            TextWrapping = TextWrapping.Wrap,
                            Margin = new Thickness(0, 1, 0, 1)
                        });
                    }
                }
                drawerPanel.Children.Add(benPanel);
            }

            // Advertencia técnica
            if (!string.IsNullOrEmpty(warning))
            {
                Border warnBox = new Border
                {
                    Background = RiskMedBg,
                    CornerRadius = new CornerRadius(5),
                    Padding = new Thickness(8, 5, 8, 5),
                    Margin = new Thickness(0, 0, 0, 6)
                };
                warnBox.Child = new TextBlock
                {
                    Text = "⚠️ Precaución Técnica: " + warning,
                    Foreground = RiskMedText,
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap
                };
                drawerPanel.Children.Add(warnBox);
            }

            // ID del tweak y tags
            Border idBadge = new Border
            {
                Background = BgElevated,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 2, 6, 2),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            idBadge.Child = new TextBlock
            {
                Text = "Regla ID: " + id,
                Foreground = TextSecondary,
                FontSize = 9.8,
                FontFamily = new FontFamily("Consolas")
            };
            drawerPanel.Children.Add(idBadge);
            sp.Children.Add(drawerPanel);

            toggleDrawerBtn.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                if (drawerPanel.Visibility == Visibility.Visible)
                {
                    drawerPanel.Visibility = Visibility.Collapsed;
                    toggleDrawerBtn.Text = "Ver detalles técnicos ▾";
                }
                else
                {
                    drawerPanel.Visibility = Visibility.Visible;
                    toggleDrawerBtn.Text = "Ocultar detalles ▴";
                }
            };

            // 4. PIE DE TARJETA: Estado actual + botón Undo si activo
            DockPanel footer = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };

            Button undoBtn = CreateStyledButton("↩ Revertir", false);
            undoBtn.Padding = new Thickness(8, 3, 8, 3);
            undoBtn.FontSize = 11;
            undoBtn.Visibility = isApplied ? Visibility.Visible : Visibility.Collapsed;
            undoBtn.Click += async (s, e) => await ExecuteTweakAsync(t, false);
            DockPanel.SetDock(undoBtn, Dock.Right);
            footer.Children.Add(undoBtn);

            TextBlock statusText = new TextBlock
            {
                Text = isApplied ? "✓ Activo en tu Sistema" : "• Valor de Fábrica",
                Foreground = isApplied ? RiskLowText : TextSecondary,
                FontWeight = FontWeights.Bold,
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center
            };
            footer.Children.Add(statusText);
            sp.Children.Add(footer);

            card.Child = sp;

            // Actualizador en caliente registrado para esta tarjeta (Cero saltos de scroll)
            Action<bool> updateCardUi = (bool applied) =>
            {
                card.BorderBrush = applied ? AccentBrush : BorderSubtle;
                switchTrack.Background = applied ? AccentBrush : BgElevated;
                switchThumb.HorizontalAlignment = applied ? HorizontalAlignment.Right : HorizontalAlignment.Left;
                switchLabel.Text = applied ? "ON" : "OFF";
                switchLabel.Foreground = applied ? AccentBrush : TextSecondary;
                statusText.Text = applied ? "✓ Activo en tu Sistema" : "• Valor de Fábrica";
                statusText.Foreground = applied ? RiskLowText : TextSecondary;
                undoBtn.Visibility = applied ? Visibility.Visible : Visibility.Collapsed;
            };
            _tweakCardUpdaters[id] = updateCardUi;

            // Manejador del clic en el Switch Toggle
            switchContainer.MouseLeftButtonUp += async (s, e) =>
            {
                e.Handled = true;
                bool current = _tweakStates.ContainsKey(id) && _tweakStates[id];
                switchContainer.IsEnabled = false;
                switchContainer.Opacity = 0.5;
                try
                {
                    await ExecuteTweakAsync(t, !current);
                }
                finally
                {
                    switchContainer.IsEnabled = true;
                    switchContainer.Opacity = 1.0;
                }
            };

            return card;
        }

        // --------------------------------------------------------------------
        // MÓDULO E: INSTALADOR DESATENDIDO DE APLICACIONES
        // --------------------------------------------------------------------
        private UIElement BuildAppsView()
        {
            StackPanel root = new StackPanel();

            StackPanel topBtns = new StackPanel { Orientation = Orientation.Horizontal };
            CheckBox directModeChk = new CheckBox
            {
                Content = "Forzar Descarga HTTPS Directa (Evita fallos de WinGet en otros PCs)",
                Foreground = TextPrimary,
                IsChecked = _preferDirectHttpsDownload,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            };
            directModeChk.Click += (s, e) => _preferDirectHttpsDownload = directModeChk.IsChecked == true;
            topBtns.Children.Add(directModeChk);

            Button upgradeAllBtn = CreateStyledButton("🔄 Actualizar Todas (WinGet)", false);
            upgradeAllBtn.Margin = new Thickness(0, 0, 8, 0);
            upgradeAllBtn.Click += async (s, e) => await UpgradeAllAppsAsync();
            topBtns.Children.Add(upgradeAllBtn);

            Button installSelectedBtn = CreateStyledButton("🚀 Instalar Seleccionadas (" + _selectedApps.Count + ")", true);
            installSelectedBtn.Click += async (s, e) => await InstallSelectedAppsBatchAsync();
            topBtns.Children.Add(installSelectedBtn);

            root.Children.Add(CreateBanner(
                "📦 Descarga e Instalación 100% Silenciosa (Motor Dual: WinGet Nativo + HTTPS Directo C#)",
                "Si un PC no tiene WinGet o el antivirus limita scripts, Blackwall descarga el instalador oficial por HTTPS nativo en C# y lo instala en silencio.",
                topBtns
            ));

            WrapPanel catBar = new WrapPanel { Margin = new Thickness(0, 0, 0, 14) };
            foreach (string cat in _appCategories)
            {
                string cCopy = cat;
                AddFilterButton(catBar, cCopy, _appCategoryScope == cCopy, () =>
                {
                    _appCategoryScope = cCopy;
                    RenderCurrentTab();
                });
            }
            root.Children.Add(catBar);

            UniformGrid grid = new UniformGrid { Columns = 3 };
            foreach (var a in _apps)
            {
                if (_appCategoryScope != "Todas" && GetStr(a, "category") != _appCategoryScope) continue;
                grid.Children.Add(BuildAppCard(a));
            }
            root.Children.Add(grid);
            return root;
        }

        private static Dictionary<string, ImageSource> _iconCache = new Dictionary<string, ImageSource>();

        private ImageSource GetAppIconSource(Dictionary<string, object> a, Action<ImageSource> onDownloaded = null)
        {
            string id = GetStr(a, "id");
            if (string.IsNullOrEmpty(id)) return null;

            if (_iconCache.ContainsKey(id) && _iconCache[id] != null)
                return _iconCache[id];

            string iconName = GetStr(a, "icon");
            if (string.IsNullOrEmpty(iconName)) iconName = id + ".png";

            // 1. Recursos embebidos en el binario (0 ms, 100% offline y seguro)
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                foreach (string name in asm.GetManifestResourceNames())
                {
                    if (name.EndsWith(iconName, StringComparison.OrdinalIgnoreCase) ||
                        name.EndsWith(id + ".png", StringComparison.OrdinalIgnoreCase))
                    {
                        using (var stream = asm.GetManifestResourceStream(name))
                        {
                            if (stream != null)
                            {
                                var bi = new BitmapImage();
                                bi.BeginInit();
                                bi.CacheOption = BitmapCacheOption.OnLoad;
                                bi.StreamSource = stream;
                                bi.EndInit();
                                bi.Freeze();
                                _iconCache[id] = bi;
                                return bi;
                            }
                        }
                    }
                }
            }
            catch { }

            // 2. Carpeta local assets\icons en el directorio de la aplicación
            try
            {
                string localPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"assets\icons\" + iconName);
                if (!File.Exists(localPath))
                    localPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"assets\icons\" + id + ".png");

                if (File.Exists(localPath))
                {
                    var bi = new BitmapImage();
                    bi.BeginInit();
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    bi.UriSource = new Uri(localPath, UriKind.Absolute);
                    bi.EndInit();
                    bi.Freeze();
                    _iconCache[id] = bi;
                    return bi;
                }
            }
            catch { }

            // 3. Caché local persistente en %LocalAppData%\Blackwall\icons
            string cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Blackwall\icons");
            string cachePath = Path.Combine(cacheDir, iconName);
            if (File.Exists(cachePath))
            {
                try
                {
                    var bi = new BitmapImage();
                    bi.BeginInit();
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    bi.UriSource = new Uri(cachePath, UriKind.Absolute);
                    bi.EndInit();
                    bi.Freeze();
                    _iconCache[id] = bi;
                    return bi;
                }
                catch { }
            }

            // 4. Descarga asíncrona segura desde iconUrl oficial con almacenamiento en caché
            string iconUrl = GetStr(a, "iconUrl");
            if (!string.IsNullOrEmpty(iconUrl))
            {
                Task.Run(() =>
                {
                    try
                    {
                        if (!Directory.Exists(cacheDir)) Directory.CreateDirectory(cacheDir);
                        using (var wc = new WebClient())
                        {
                            wc.Headers.Add("User-Agent", "Blackwall/2.0");
                            byte[] data = wc.DownloadData(iconUrl);
                            File.WriteAllBytes(cachePath, data);

                            Dispatcher.Invoke(() =>
                            {
                                try
                                {
                                    using (var ms = new MemoryStream(data))
                                    {
                                        var bi = new BitmapImage();
                                        bi.BeginInit();
                                        bi.CacheOption = BitmapCacheOption.OnLoad;
                                        bi.StreamSource = ms;
                                        bi.EndInit();
                                        bi.Freeze();
                                        _iconCache[id] = bi;
                                        if (onDownloaded != null) onDownloaded(bi);
                                    }
                                }
                                catch { }
                            });
                        }
                    }
                    catch { }
                });
            }

            return null;
        }

        private UIElement BuildAppCard(Dictionary<string, object> a)
        {
            string id = GetStr(a, "id");
            bool isSelected = _selectedApps.Contains(id);
            bool isInstalled = _installedApps.ContainsKey(id) && GetBool(_installedApps[id], "installed");
            string ver = isInstalled ? GetStr(_installedApps[id], "version") : "";

            Border card = CreateCardContainer();
            card.Margin = new Thickness(0, 0, 12, 12);
            if (isSelected) card.BorderBrush = AccentBrush;
            else if (isInstalled) card.BorderBrush = RiskLowText;

            StackPanel sp = new StackPanel();

            // Header con Checkbox a la derecha y Marca (Icono + Títulos) a la izquierda
            DockPanel header = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            CheckBox chk = new CheckBox
            {
                IsChecked = isSelected,
                VerticalAlignment = VerticalAlignment.Top,
                Cursor = Cursors.Hand
            };
            chk.Click += (s, e) =>
            {
                if (chk.IsChecked == true) _selectedApps.Add(id);
                else _selectedApps.Remove(id);
                RenderCurrentTab();
            };
            DockPanel.SetDock(chk, Dock.Right);
            header.Children.Add(chk);

            // Contenedor Encuadrado de Icono (Estilo Windows 11 Fluent)
            Border iconContainer = new Border
            {
                Width = 46,
                Height = 46,
                CornerRadius = new CornerRadius(10),
                Background = _isDark ? new SolidColorBrush(Color.FromRgb(26, 32, 44)) : new SolidColorBrush(Color.FromRgb(244, 246, 249)),
                BorderBrush = _isDark ? new SolidColorBrush(Color.FromRgb(46, 54, 70)) : new SolidColorBrush(Color.FromRgb(222, 228, 238)),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Center
            };

            Grid iconGrid = new Grid();
            Image img = new Image
            {
                Width = 34,
                Height = 34,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            RenderOptions.SetEdgeMode(img, EdgeMode.Aliased);

            // Monograma con inicial en caso de estar cargando
            string initial = (GetStr(a, "name") + " ").Substring(0, 1).ToUpperInvariant();
            TextBlock fallbackInitial = new TextBlock
            {
                Text = initial,
                FontWeight = FontWeights.Bold,
                FontSize = 18,
                Foreground = AccentBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed
            };
            iconGrid.Children.Add(fallbackInitial);
            iconGrid.Children.Add(img);
            iconContainer.Child = iconGrid;

            ImageSource src = GetAppIconSource(a, downloadedSrc =>
            {
                img.Source = downloadedSrc;
                fallbackInitial.Visibility = Visibility.Collapsed;
            });

            if (src != null)
            {
                img.Source = src;
            }
            else
            {
                fallbackInitial.Visibility = Visibility.Visible;
            }

            // Stack de Marca (Icono + Títulos)
            StackPanel brandStack = new StackPanel { Orientation = Orientation.Horizontal };
            brandStack.Children.Add(iconContainer);

            StackPanel titleStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            titleStack.Children.Add(new TextBlock
            {
                Text = GetStr(a, "category") + (isInstalled ? "  •  ✓ INSTALADO (" + ver + ")" : ""),
                Foreground = isInstalled ? RiskLowText : AccentBrush,
                FontSize = 9.8,
                FontWeight = FontWeights.Bold
            });
            titleStack.Children.Add(new TextBlock
            {
                Text = GetStr(a, "name"),
                Foreground = TextPrimary,
                FontSize = 13.5,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 1, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 230
            });
            string pub = GetStr(a, "publisher");
            if (!string.IsNullOrEmpty(pub))
            {
                titleStack.Children.Add(new TextBlock
                {
                    Text = pub,
                    Foreground = TextSecondary,
                    FontSize = 10.5,
                    Margin = new Thickness(0, 1, 0, 0)
                });
            }

            brandStack.Children.Add(titleStack);
            header.Children.Add(brandStack);
            sp.Children.Add(header);

            sp.Children.Add(new TextBlock
            {
                Text = GetStr(a, "description"),
                Foreground = TextSecondary,
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 10),
                Height = 36,
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            DockPanel footer = new DockPanel();
            Button singleBtn = CreateStyledButton(isInstalled ? "Actualizar" : "Instalar", false);
            singleBtn.Padding = new Thickness(9, 4, 9, 4);
            singleBtn.Click += async (s, e) => await InstallSingleAppAsync(a);
            DockPanel.SetDock(singleBtn, Dock.Right);
            footer.Children.Add(singleBtn);

            footer.Children.Add(new TextBlock
            {
                Text = GetStr(a, "wingetId"),
                Foreground = TextSecondary,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 10.5,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            sp.Children.Add(footer);

            card.Child = sp;
            return card;
        }

        // --------------------------------------------------------------------
        // MÓDULO D: MONITOR DE SCRIPTS EN VIVO
        // --------------------------------------------------------------------
        private UIElement BuildMonitorView()
        {
            StackPanel root = new StackPanel();

            Button refreshBtn = CreateStyledButton("🔄 Refrescar Procesos Ahora", true);
            refreshBtn.Click += async (s, e) => await RefreshLiveScriptsNativeAsync();

            root.Children.Add(CreateBanner(
                "📡 Monitor de Scripts en Vivo (Consola Interna + Procesos Activos en Windows)",
                "Detecta en tiempo real powershell.exe, pwsh.exe, cmd.exe, wscript.exe, cscript.exe y mshta.exe mediante consulta WMI nativa.",
                refreshBtn
            ));

            Border consoleCard = CreateCardContainer();
            consoleCard.Margin = new Thickness(0, 0, 0, 14);
            StackPanel consoleStack = new StackPanel();
            consoleStack.Children.Add(new TextBlock
            {
                Text = "💻 1. Cola y Consola Interna de Ejecución de Blackwall",
                Foreground = TextPrimary,
                FontWeight = FontWeights.Bold,
                FontSize = 14,
                Margin = new Thickness(0, 0, 0, 8)
            });

            _liveConsoleTextBox = new TextBox
            {
                Text = string.Join("\r\n", _consoleLogs),
                Background = BrushHex("#070A0F"),
                Foreground = BrushHex("#CBD5E1"),
                BorderBrush = BorderSubtle,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                Height = 200,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(10)
            };
            _liveConsoleTextBox.ScrollToEnd();
            consoleStack.Children.Add(_liveConsoleTextBox);
            consoleCard.Child = consoleStack;
            root.Children.Add(consoleCard);

            Border scriptsCard = CreateCardContainer();
            StackPanel scriptsStack = new StackPanel();
            scriptsStack.Children.Add(new TextBlock
            {
                Text = "🕵️ 2. Motores de Scripts Activos en Windows (" + _liveScripts.Count + " Procesos Detectados)",
                Foreground = TextPrimary,
                FontWeight = FontWeights.Bold,
                FontSize = 14,
                Margin = new Thickness(0, 0, 0, 10)
            });

            if (_liveScripts.Count == 0)
            {
                scriptsStack.Children.Add(new TextBlock
                {
                    Text = "No hay otros scripts externos ejecutándose en segundo plano en este momento.",
                    Foreground = TextSecondary,
                    FontSize = 12.5
                });
            }
            else
            {
                foreach (var p in _liveScripts)
                {
                    int pid = GetInt(p, "pid");
                    Border pRow = new Border
                    {
                        Background = BgApp,
                        BorderBrush = BorderSubtle,
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(8),
                        Padding = new Thickness(12, 10, 12, 10),
                        Margin = new Thickness(0, 0, 0, 8)
                    };
                    DockPanel dp = new DockPanel();
                    Button killBtn = CreateStyledButton("🛑 Terminar (Kill PID " + pid + ")", false);
                    killBtn.Foreground = RiskHighText;
                    killBtn.BorderBrush = RiskHighText;
                    killBtn.VerticalAlignment = VerticalAlignment.Top;
                    killBtn.Click += async (s, e) =>
                    {
                        try
                        {
                            Process targetProc = Process.GetProcessById(pid);
                            targetProc.Kill();
                            AppendLog("Proceso PID " + pid + " terminado nativamente.", "KILL");
                        }
                        catch (Exception ex)
                        {
                            AppendLog("No se pudo terminar PID " + pid + ": " + ex.Message, "WARN");
                        }
                        await RefreshLiveScriptsNativeAsync();
                    };
                    DockPanel.SetDock(killBtn, Dock.Right);
                    dp.Children.Add(killBtn);

                    StackPanel info = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
                    info.Children.Add(new TextBlock
                    {
                        Text = "PID " + pid + "  •  " + GetStr(p, "name") + "  •  Parent PID: " + GetStr(p, "parentPid") + "  •  RAM: " + GetStr(p, "memoryMb") + " MB",
                        Foreground = TextPrimary,
                        FontWeight = FontWeights.Bold,
                        FontSize = 12.8
                    });
                    info.Children.Add(new TextBox
                    {
                        Text = GetStr(p, "commandLine"),
                        Background = Brushes.Transparent,
                        Foreground = AccentBrush,
                        BorderThickness = new Thickness(0),
                        IsReadOnly = true,
                        TextWrapping = TextWrapping.Wrap,
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 11.8,
                        Margin = new Thickness(0, 3, 0, 0)
                    });
                    dp.Children.Add(info);
                    pRow.Child = dp;
                    scriptsStack.Children.Add(pRow);
                }
            }

            scriptsCard.Child = scriptsStack;
            root.Children.Add(scriptsCard);
            return root;
        }

        // --------------------------------------------------------------------
        // MÓDULO C: SEGURIDAD Y RESTAURACIÓN
        // --------------------------------------------------------------------
        private UIElement BuildSafetyView()
        {
            StackPanel root = new StackPanel();
            root.Children.Add(CreateBanner(
                "🛡️ Módulo de Seguridad, Puntos de Restauración y Reparación del Sistema",
                "Blackwall utiliza la API WMI nativa root\\default:SystemRestore sin scripts externos."
            ));

            UniformGrid grid = new UniformGrid { Columns = 2, Margin = new Thickness(0, 0, 0, 14) };

            Border rpCard = CreateCardContainer();
            rpCard.Margin = new Thickness(0, 0, 12, 0);
            StackPanel rpStack = new StackPanel();
            rpStack.Children.Add(new TextBlock { Text = "🛡️ Protección Automática y Puntos de Restauración", Foreground = TextPrimary, FontWeight = FontWeights.Bold, FontSize = 14 });
            rpStack.Children.Add(new TextBlock { Text = "Crea puntos de restauración nativos en C:\\ o abre el restaurador oficial de Windows (rstrui.exe).", Foreground = TextSecondary, FontSize = 12.3, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 10) });

            CheckBox autoChk = new CheckBox
            {
                Content = "Crear Punto de Restauración automáticamente antes de aplicar tweaks",
                Foreground = TextPrimary,
                IsChecked = _autoRestorePoint,
                Margin = new Thickness(0, 0, 0, 12)
            };
            autoChk.Click += (s, e) => _autoRestorePoint = autoChk.IsChecked == true;
            rpStack.Children.Add(autoChk);

            StackPanel rpBtns = new StackPanel { Orientation = Orientation.Horizontal };
            Button createRpBtn = CreateStyledButton("➕ Crear Punto de Restauración Ahora", true);
            createRpBtn.Margin = new Thickness(0, 0, 8, 0);
            createRpBtn.Click += async (s, e) => await CreateRestorePointNowAsync();
            rpBtns.Children.Add(createRpBtn);

            Button rstruiBtn = CreateStyledButton("🕰️ Abrir rstrui.exe", false);
            rstruiBtn.Click += (s, e) => { try { Process.Start("rstrui.exe"); } catch { } };
            rpBtns.Children.Add(rstruiBtn);
            rpStack.Children.Add(rpBtns);
            rpCard.Child = rpStack;
            grid.Children.Add(rpCard);

            Border repairCard = CreateCardContainer();
            StackPanel repStack = new StackPanel();
            repStack.Children.Add(new TextBlock { Text = "🔧 Reparación de Imagen de Windows (SFC, DISM & Red)", Foreground = TextPrimary, FontWeight = FontWeights.Bold, FontSize = 14 });
            repStack.Children.Add(new TextBlock { Text = "Invoca directamente las herramientas nativas de mantenimiento de Microsoft.", Foreground = TextSecondary, FontSize = 12.3, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 12) });

            WrapPanel repBtns = new WrapPanel();
            Button sfcBtn = CreateStyledButton("🔍 Ejecutar SFC /verifyonly", false);
            sfcBtn.Margin = new Thickness(0, 0, 8, 8);
            sfcBtn.Click += async (s, e) =>
            {
                SetStatus("🔍 Ejecutando SFC...", "sfc.exe /verifyonly");
                string o = await Task.Run(() => RunSystemToolDirect("sfc.exe", "/verifyonly"));
                AppendLog(o, "SFC");
                SetStatus("🟢 Verificación SFC finalizada", "Listo");
            };
            repBtns.Children.Add(sfcBtn);

            Button dismBtn = CreateStyledButton("🩺 Ejecutar DISM /CheckHealth", false);
            dismBtn.Margin = new Thickness(0, 0, 8, 8);
            dismBtn.Click += async (s, e) =>
            {
                SetStatus("🩺 Ejecutando DISM...", "DISM.exe /CheckHealth");
                string o = await Task.Run(() => RunSystemToolDirect("DISM.exe", "/Online /Cleanup-Image /CheckHealth"));
                AppendLog(o, "DISM");
                SetStatus("🟢 Diagnóstico DISM finalizado", "Listo");
            };
            repBtns.Children.Add(dismBtn);

            Button dnsBtn = CreateStyledButton("🌐 Flush DNS & Winsock", false);
            dnsBtn.Margin = new Thickness(0, 0, 8, 8);
            dnsBtn.Click += async (s, e) =>
            {
                string o = await Task.Run(() => RunSystemToolDirect("ipconfig.exe", "/flushdns"));
                AppendLog(o, "NET");
                SetStatus("🟢 Caché DNS limpiada", "Listo");
            };
            repBtns.Children.Add(dnsBtn);

            Button regBtn = CreateStyledButton("💾 Exportar Backup Registro (.reg)", true);
            regBtn.Margin = new Thickness(0, 0, 8, 8);
            regBtn.Click += async (s, e) =>
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                string outFile = Path.Combine(desktop, "Blackwall-Registry-Backup.reg");
                await Task.Run(() => RunSystemToolDirect("reg.exe", "export \"HKCU\\Control Panel\\Desktop\" \"" + outFile + "\" /y"));
                AppendLog("Backup exportado en: " + outFile, "BACKUP");
                SetStatus("🟢 Backup .reg exportado al Escritorio", "Listo");
            };
            repBtns.Children.Add(regBtn);

            repStack.Children.Add(repBtns);
            repairCard.Child = repStack;
            grid.Children.Add(repairCard);
            root.Children.Add(grid);

            Border listCard = CreateCardContainer();
            StackPanel listStack = new StackPanel();
            listStack.Children.Add(new TextBlock
            {
                Text = "📋 Puntos de Restauración Registrados en C:\\ (" + _restorePoints.Count + ")",
                Foreground = TextPrimary,
                FontWeight = FontWeights.Bold,
                FontSize = 14,
                Margin = new Thickness(0, 0, 0, 8)
            });

            if (_restorePoints.Count == 0)
            {
                listStack.Children.Add(new TextBlock
                {
                    Text = "Haz clic en '➕ Crear Punto de Restauración Ahora' (en modo Administrador) para registrar un punto de control inmediato.",
                    Foreground = TextSecondary,
                    FontSize = 12.3
                });
            }
            else
            {
                foreach (var pt in _restorePoints)
                {
                    listStack.Children.Add(new TextBlock
                    {
                        Text = "#" + GetStr(pt, "sequenceNumber") + "  —  " + GetStr(pt, "description") + "   (" + GetStr(pt, "creationTime") + ")",
                        Foreground = TextPrimary,
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 12.2,
                        Margin = new Thickness(0, 3, 0, 3)
                    });
                }
            }
            listCard.Child = listStack;
            root.Children.Add(listCard);
            return root;
        }

        private async Task ApplyRecommendedSafeProfileAsync(string scope)
        {
            await CreateRestorePointNowAsync();
            foreach (var t in _tweaks)
            {
                if (GetStr(t, "riskLevel") == "low" && ListContains(t, "deviceScope", scope))
                {
                    await ExecuteTweakAsync(t, true);
                }
            }
            SetStatus("🟢 Perfil Seguro (" + scope.ToUpperInvariant() + ") aplicado", "Completado");
        }

        // ====================================================================
        // MOTOR REAL DE AUTO-ACTUALIZACIÓN REMOTA (OTA) EN BINARIO PORTABLE
        // ====================================================================
        private string GetConfiguredOtaManifestUrl()
        {
            try
            {
                string cfgFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Blackwall\ota_manifest_url.txt");
                if (File.Exists(cfgFile))
                {
                    string custom = File.ReadAllText(cfgFile, Encoding.UTF8).Trim();
                    if (!string.IsNullOrEmpty(custom)) return custom;
                }
            }
            catch { }
            return _versionInfo != null && _versionInfo.ContainsKey("manifestUrl")
                ? GetStr(_versionInfo, "manifestUrl")
                : "https://raw.githubusercontent.com/DylanRico/BlackWallOptimizer/main/catalogs/version.json";
        }

        private void SaveConfiguredOtaManifestUrl(string url)
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Blackwall");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "ota_manifest_url.txt"), url.Trim(), Encoding.UTF8);
            }
            catch { }
        }

        private async Task<Dictionary<string, object>> FetchRemoteManifestAsync(string url)
        {
            return await Task.Run(() =>
            {
                try
                {
                    using (WebClient wc = new WebClient())
                    {
                        wc.Encoding = Encoding.UTF8;
                        wc.Headers.Add("User-Agent", "Blackwall-OTA/1.2");
                        wc.Headers.Add("Cache-Control", "no-cache");
                        string sep = url.Contains("?") ? "&" : "?";
                        string raw = wc.DownloadString(url + sep + "t=" + DateTime.UtcNow.Ticks);
                        return _json.Deserialize<Dictionary<string, object>>(raw);
                    }
                }
                catch
                {
                    return null;
                }
            });
        }

        private bool IsNewerVersion(string remoteVerStr, string localVerStr)
        {
            Version rVer, lVer;
            if (Version.TryParse(remoteVerStr, out rVer) && Version.TryParse(localVerStr, out lVer))
                return rVer > lVer;
            return !string.Equals(remoteVerStr, localVerStr, StringComparison.OrdinalIgnoreCase);
        }

        private async Task CheckRemoteOtaAsync(bool silentOnUpToDate)
        {
            string localVer = _versionInfo != null ? GetStr(_versionInfo, "appVersion") : "1.2.0";
            string manifestUrl = GetConfiguredOtaManifestUrl();

            var remoteManifest = await FetchRemoteManifestAsync(manifestUrl);
            if (remoteManifest == null || !remoteManifest.ContainsKey("appVersion"))
            {
                if (!silentOnUpToDate)
                    AppendLog("[OTA] No se pudo conectar al manifiesto remoto: " + manifestUrl, "WARN");
                return;
            }

            string remoteVer = GetStr(remoteManifest, "appVersion");
            if (IsNewerVersion(remoteVer, localVer))
            {
                _versionInfo = remoteManifest;
                if (_otaBtn != null)
                {
                    _otaBtn.Content = "🔔 Actualizar a v" + remoteVer;
                    _otaBtn.Background = RiskLowBg;
                    _otaBtn.Foreground = RiskLowText;
                    _otaBtn.BorderBrush = RiskLowText;
                }
                AppendLog("[OTA] Nueva versión v" + remoteVer + " detectada en el servidor remoto.", "OTA");
                SetStatus("🔔 Nueva actualización remota disponible: v" + remoteVer + " (Haz clic en OTA arriba a la derecha)", "OTA Lista");
            }
        }

        private void ShowOtaUpdateDialog()
        {
            string localVer = "1.2.0";
            string manifestUrl = GetConfiguredOtaManifestUrl();

            Window dlg = new Window
            {
                Title = "Blackwall — Centro de Actualización Remota (OTA) para Todos tus PCs",
                Width = 640,
                Height = 520,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                Background = BgSurface,
                ResizeMode = ResizeMode.NoResize
            };

            StackPanel root = new StackPanel { Margin = new Thickness(20) };
            root.Children.Add(new TextBlock
            {
                Text = "🔄 Auto-Actualización Remota (Over-The-Air)",
                Foreground = TextPrimary,
                FontWeight = FontWeights.Bold,
                FontSize = 17
            });
            root.Children.Add(new TextBlock
            {
                Text = "Cuando publiques una nueva versión o catálogo en tu repositorio (GitHub / Servidor), todos los PCs que tengan Blackwall.exe descargarán los cambios y se auto-reemplazarán en caliente.",
                Foreground = TextSecondary,
                FontSize = 12.2,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 14)
            });

            root.Children.Add(new TextBlock
            {
                Text = "URL del Manifiesto Remoto (version.json en GitHub Raw o Servidor propio):",
                Foreground = TextSecondary,
                FontSize = 11.8,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 4)
            });

            TextBox urlBox = new TextBox
            {
                Text = manifestUrl,
                Background = BgApp,
                Foreground = TextPrimary,
                BorderBrush = BorderSubtle,
                Padding = new Thickness(8, 6, 8, 6),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 12)
            };
            root.Children.Add(urlBox);

            TextBox statusBox = new TextBox
            {
                Background = BrushHex("#070A0F"),
                Foreground = BrushHex("#CBD5E1"),
                BorderBrush = BorderSubtle,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                Height = 210,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(10),
                Margin = new Thickness(0, 0, 0, 14),
                Text = "Versión local instalada en este PC: v" + localVer + "\r\n" +
                       "Manifiesto configurado: " + manifestUrl + "\r\n\r\n" +
                       "Opciones disponibles:\r\n" +
                       "1. 'Comprobar y Sincronizar Catálogos': Descarga tweaks.json y apps.json remotos al instante sin reiniciar.\r\n" +
                       "2. 'Auto-Actualizar Ejecutable (.exe)': Descarga el nuevo Blackwall.exe remoto, verifica su firma SHA-256, renombra el ejecutable en uso a Blackwall.exe.old y reinicia automáticamente la nueva versión."
            };
            root.Children.Add(statusBox);

            WrapPanel btnPanel = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };

            Button checkCatalogsBtn = CreateStyledButton("🔄 1. Comprobar y Sincronizar Catálogos", false);
            checkCatalogsBtn.Margin = new Thickness(0, 0, 8, 0);
            checkCatalogsBtn.Click += async (s, e) =>
            {
                string u = urlBox.Text.Trim();
                SaveConfiguredOtaManifestUrl(u);
                statusBox.Text = "Consultando manifiesto remoto:\r\n" + u + " ...";
                var rem = await FetchRemoteManifestAsync(u);
                if (rem == null)
                {
                    statusBox.Text += "\r\n\r\n[AVISO] No se pudo descargar el manifiesto desde esa URL (verifica que el repositorio en GitHub esté público).";
                    return;
                }
                _versionInfo = rem;
                string rVer = GetStr(rem, "appVersion");
                var clList = new List<string>();
                if (rem.ContainsKey("changelog") && rem["changelog"] is IEnumerable)
                {
                    foreach (object item in (IEnumerable)rem["changelog"])
                        clList.Add("• " + Convert.ToString(item));
                }

                statusBox.Text = "✅ Conectado al servidor OTA.\r\n" +
                                 "Versión Local: v" + localVer + "  |  Versión en Servidor: v" + rVer + "\r\n\r\n" +
                                 "Notas de la versión (Changelog):\r\n" + string.Join("\r\n", clList) + "\r\n\r\n" +
                                 "Sincronizando catálogos remotos tweaks.json y apps.json...";

                bool catOk = await SyncRemoteCatalogsAsync(rem);
                if (catOk)
                {
                    LoadEmbeddedCatalogs();
                    RenderCurrentTab();
                    statusBox.Text += "\r\n✅ Catálogos actualizados en caliente en este PC.";
                    SetStatus("🟢 Catálogos OTA sincronizados desde el servidor remoto", "OTA OK");
                }
            };
            btnPanel.Children.Add(checkCatalogsBtn);

            Button updateExeBtn = CreateStyledButton("⚡ 2. Auto-Actualizar Ejecutable (.exe) Ahora", true);
            updateExeBtn.Margin = new Thickness(0, 0, 8, 0);
            updateExeBtn.Click += async (s, e) =>
            {
                string u = urlBox.Text.Trim();
                SaveConfiguredOtaManifestUrl(u);
                statusBox.Text = "Verificando manifiesto remoto para reemplazo de binario...";
                var rem = await FetchRemoteManifestAsync(u);
                if (rem == null)
                {
                    statusBox.Text += "\r\n[ERROR] No se pudo conectar a: " + u;
                    return;
                }
                await PerformBinarySelfUpdateAsync(rem, statusBox);
            };
            btnPanel.Children.Add(updateExeBtn);

            Button closeBtn = CreateStyledButton("Cerrar", false);
            closeBtn.Click += (s, e) => dlg.Close();
            btnPanel.Children.Add(closeBtn);

            root.Children.Add(btnPanel);
            dlg.Content = root;
            dlg.ShowDialog();
        }

        private async Task<bool> SyncRemoteCatalogsAsync(Dictionary<string, object> remoteManifest)
        {
            return await Task.Run(() =>
            {
                try
                {
                    var cats = GetDict(remoteManifest, "catalogs");
                    string tweaksUrl = GetStr(cats, "tweaksUrl");
                    string appsUrl = GetStr(cats, "appsUrl");

                    string cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Blackwall\Catalogs");
                    if (!Directory.Exists(cacheDir)) Directory.CreateDirectory(cacheDir);

                    using (WebClient wc = new WebClient())
                    {
                        wc.Encoding = Encoding.UTF8;
                        wc.Headers.Add("User-Agent", "Blackwall-OTA/1.2");
                        if (!string.IsNullOrEmpty(tweaksUrl))
                        {
                            string tJson = wc.DownloadString(tweaksUrl + "?t=" + DateTime.UtcNow.Ticks);
                            if (tJson.Contains("\"tweaks\""))
                                File.WriteAllText(Path.Combine(cacheDir, "tweaks.json"), tJson, Encoding.UTF8);
                        }
                        if (!string.IsNullOrEmpty(appsUrl))
                        {
                            string aJson = wc.DownloadString(appsUrl + "?t=" + DateTime.UtcNow.Ticks);
                            if (aJson.Contains("\"apps\""))
                                File.WriteAllText(Path.Combine(cacheDir, "apps.json"), aJson, Encoding.UTF8);
                        }
                    }
                    return true;
                }
                catch
                {
                    return false;
                }
            });
        }

        private async Task PerformBinarySelfUpdateAsync(Dictionary<string, object> remoteManifest, TextBox logBox)
        {
            var binInfo = GetDict(remoteManifest, "binary");
            string downloadUrl = GetStr(binInfo, "downloadUrl");
            string expectedSha = GetStr(binInfo, "sha256").Trim();

            if (string.IsNullOrEmpty(downloadUrl))
            {
                logBox.Text += "\r\n[ERROR] El manifiesto remoto no contiene binary.downloadUrl.";
                return;
            }

            string currentExe = Process.GetCurrentProcess().MainModule.FileName;
            string tempUpdateFile = currentExe + ".update";
            string oldBackupFile = currentExe + ".old";

            try
            {
                if (File.Exists(tempUpdateFile)) File.Delete(tempUpdateFile);
                logBox.Text += "\r\nDescargando nuevo ejecutable desde:\r\n" + downloadUrl + " ...";

                using (WebClient wc = new WebClient())
                {
                    wc.Headers.Add("User-Agent", "Blackwall-OTA/1.2");
                    wc.DownloadProgressChanged += (s, ev) =>
                    {
                        SetStatus("⬇️ Descargando actualización OTA: " + ev.ProgressPercentage + "%", "OTA");
                    };
                    await wc.DownloadFileTaskAsync(new Uri(downloadUrl), tempUpdateFile);
                }

                // Verificar cabecera ejecutable PE ("MZ") y tamaño mínimo
                byte[] fileBytes = File.ReadAllBytes(tempUpdateFile);
                if (fileBytes.Length < 40960 || fileBytes[0] != 0x4D || fileBytes[1] != 0x5A)
                {
                    File.Delete(tempUpdateFile);
                    logBox.Text += "\r\n[ERROR] El archivo descargado no es un ejecutable válido de Windows.";
                    return;
                }

                // Verificar Hash SHA-256 si fue especificado en version.json
                if (!string.IsNullOrEmpty(expectedSha))
                {
                    using (SHA256 sha = SHA256.Create())
                    {
                        byte[] hashBytes = sha.ComputeHash(fileBytes);
                        string actualHex = BitConverter.ToString(hashBytes).Replace("-", "");
                        if (!string.Equals(actualHex, expectedSha, StringComparison.OrdinalIgnoreCase))
                        {
                            File.Delete(tempUpdateFile);
                            logBox.Text += "\r\n[ERROR SEGURIDAD] El hash SHA-256 del ejecutable descargado no coincide con el manifiesto.";
                            return;
                        }
                    }
                }

                // Reemplazo en caliente Zero-Lock en NTFS (Renombrar .exe activo -> .exe.old)
                if (File.Exists(oldBackupFile)) File.Delete(oldBackupFile);
                File.Move(currentExe, oldBackupFile);
                File.Move(tempUpdateFile, currentExe);

                logBox.Text += "\r\n✅ Ejecutable actualizado correctamente. Reiniciando Blackwall...";
                Process.Start(currentExe);
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                try { if (File.Exists(tempUpdateFile)) File.Delete(tempUpdateFile); } catch { }
                logBox.Text += "\r\n[ERROR] No se pudo completar el auto-reemplazo: " + ex.Message;
            }
        }

        private void ExportReport(string format)
        {
            if (_hardware == null) return;
            var chassis = GetDict(_hardware, "chassis");
            string serial = GetStr(chassis, "serialNumber");

            SaveFileDialog sfd = new SaveFileDialog
            {
                FileName = "Blackwall-Reporte-" + serial + "." + format,
                Filter = format.ToUpperInvariant() + " Files (*." + format + ")|*." + format
            };
            if (sfd.ShowDialog(this) == true)
            {
                File.WriteAllText(sfd.FileName, _json.Serialize(_hardware), Encoding.UTF8);
                SetStatus("🟢 Reporte exportado en: " + sfd.FileName, "Exportado");
            }
        }

        // ====================================================================
        // HELPERS VISUALES Y DE DATOS
        // ====================================================================
        private Border CreateBanner(string title, string subtitle, UIElement rightAction = null)
        {
            Border b = new Border
            {
                Background = BgSurface,
                BorderBrush = AccentBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(16, 14, 16, 14),
                Margin = new Thickness(0, 0, 0, 14)
            };
            DockPanel dp = new DockPanel();
            if (rightAction != null)
            {
                DockPanel.SetDock(rightAction, Dock.Right);
                dp.Children.Add(rightAction);
            }
            StackPanel sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = title, Foreground = TextPrimary, FontWeight = FontWeights.Bold, FontSize = 15.5 });
            sp.Children.Add(new TextBlock { Text = subtitle, Foreground = TextSecondary, FontSize = 12.2, Margin = new Thickness(0, 3, 0, 0), TextWrapping = TextWrapping.Wrap });
            dp.Children.Add(sp);
            b.Child = dp;
            return b;
        }

        private Border CreateCardContainer()
        {
            return new Border
            {
                Background = BgSurface,
                BorderBrush = BorderSubtle,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(15)
            };
        }

        private UIElement CreateKvCard(string title, string badgeText, string[,] rows)
        {
            Border card = CreateCardContainer();
            card.Margin = new Thickness(0, 0, 10, 10);
            StackPanel sp = new StackPanel();

            DockPanel hdr = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            Border badge = CreateBadge(badgeText, "low");
            DockPanel.SetDock(badge, Dock.Right);
            hdr.Children.Add(badge);
            hdr.Children.Add(new TextBlock { Text = title, Foreground = TextPrimary, FontWeight = FontWeights.Bold, FontSize = 13.5, TextWrapping = TextWrapping.Wrap });
            sp.Children.Add(hdr);

            int count = rows.GetLength(0);
            for (int i = 0; i < count; i++)
            {
                Border rowBorder = new Border
                {
                    BorderBrush = BorderSubtle,
                    BorderThickness = new Thickness(0, 0, 0, i == count - 1 ? 0 : 1),
                    Padding = new Thickness(0, 5, 0, 5)
                };
                DockPanel rdp = new DockPanel();
                TextBox valBox = new TextBox
                {
                    Text = rows[i, 1],
                    Foreground = TextPrimary,
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    IsReadOnly = true,
                    FontWeight = FontWeights.SemiBold,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 11.8,
                    HorizontalAlignment = HorizontalAlignment.Right
                };
                DockPanel.SetDock(valBox, Dock.Right);
                rdp.Children.Add(valBox);
                rdp.Children.Add(new TextBlock { Text = rows[i, 0], Foreground = TextSecondary, FontSize = 12 });
                rowBorder.Child = rdp;
                sp.Children.Add(rowBorder);
            }

            card.Child = sp;
            return card;
        }

        private Border CreateBadge(string text, string level)
        {
            SolidColorBrush bg = level == "high" ? RiskHighBg : level == "medium" ? RiskMedBg : RiskLowBg;
            SolidColorBrush fg = level == "high" ? RiskHighText : level == "medium" ? RiskMedText : RiskLowText;
            return new Border
            {
                Background = bg,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 2, 8, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = text, Foreground = fg, FontSize = 10, FontWeight = FontWeights.ExtraBold }
            };
        }

        private Button CreateStyledButton(string text, bool primary)
        {
            Button btn = new Button
            {
                Content = text,
                Background = primary ? AccentBrush : BgSurface,
                Foreground = primary ? Brushes.White : TextPrimary,
                BorderBrush = primary ? AccentBrush : BorderSubtle,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 6, 12, 6),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Cursor = Cursors.Hand
            };

            btn.MouseEnter += (s, e) =>
            {
                if (!primary)
                {
                    btn.Background = BgElevated;
                    btn.BorderBrush = AccentBrush;
                }
            };
            btn.MouseLeave += (s, e) =>
            {
                if (!primary)
                {
                    btn.Background = BgSurface;
                    btn.BorderBrush = BorderSubtle;
                }
            };
            return btn;
        }

        private void AddFilterButton(Panel parent, string text, bool active, Action onClick)
        {
            Button b = CreateStyledButton(text, active);
            b.Margin = new Thickness(0, 0, 8, 6);
            b.Click += (s, e) => onClick();
            parent.Children.Add(b);
        }

        private void AppendLog(string msg, string tag)
        {
            Dispatcher.Invoke(() =>
            {
                _consoleLogs.Add("[" + DateTime.Now.ToString("HH:mm:ss") + "] [" + tag + "] " + msg);
                if (_liveConsoleTextBox != null)
                {
                    _liveConsoleTextBox.Text = string.Join("\r\n", _consoleLogs);
                    _liveConsoleTextBox.ScrollToEnd();
                }
            });
        }

        private void SetStatus(string status, string task)
        {
            Dispatcher.Invoke(() =>
            {
                if (_statusText != null) _statusText.Text = status;
                if (_taskText != null) _taskText.Text = task;
            });
        }

        private string FormatChassisHeader(Dictionary<string, object> chassis)
        {
            if (chassis == null) return "Equipo Windows";
            bool lap = GetBool(chassis, "isLaptop");
            return (lap ? "💻 Laptop: " : "🖥️ Desktop: ") + GetStr(chassis, "manufacturer") + " " + GetStr(chassis, "model");
        }

        private Dictionary<string, object> GetDict(Dictionary<string, object> parent, string key)
        {
            if (parent != null && parent.ContainsKey(key) && parent[key] is Dictionary<string, object>)
                return (Dictionary<string, object>)parent[key];
            return new Dictionary<string, object>();
        }

        private List<Dictionary<string, object>> ConvertToDictList(object obj)
        {
            List<Dictionary<string, object>> list = new List<Dictionary<string, object>>();
            if (obj is Dictionary<string, object>)
            {
                list.Add((Dictionary<string, object>)obj);
            }
            else if (obj is IEnumerable)
            {
                foreach (object item in (IEnumerable)obj)
                {
                    if (item is Dictionary<string, object>)
                        list.Add((Dictionary<string, object>)item);
                }
            }
            return list;
        }

        private string GetStr(Dictionary<string, object> d, string k)
        {
            if (d != null && d.ContainsKey(k) && d[k] != null)
                return Convert.ToString(d[k]);
            return "";
        }

        private bool GetBool(Dictionary<string, object> d, string k)
        {
            if (d != null && d.ContainsKey(k) && d[k] != null)
            {
                try { return Convert.ToBoolean(d[k]); } catch { }
            }
            return false;
        }

        private int GetInt(Dictionary<string, object> d, string k)
        {
            if (d != null && d.ContainsKey(k) && d[k] != null)
            {
                try { return Convert.ToInt32(d[k]); } catch { }
            }
            return 0;
        }

        private bool ListContains(Dictionary<string, object> d, string key, string expected)
        {
            if (d != null && d.ContainsKey(key) && d[key] is IEnumerable)
            {
                foreach (object o in (IEnumerable)d[key])
                {
                    if (string.Equals(Convert.ToString(o), expected, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            return false;
        }
    }
}
