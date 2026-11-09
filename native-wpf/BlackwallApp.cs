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
using System.Windows.Interop;
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

namespace Blackwall
{
    // Windows' own Authenticode check (the one Explorer uses before running a download): signature intact and
    // chained to a trusted root. Unsigned or tampered files fail.
    internal static class Authenticode
    {
        [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
        private static extern uint WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid action, ref TrustData data);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct TrustFile { public uint cbStruct; public string pcwszFilePath; public IntPtr hFile; public IntPtr pgKnownSubject; }

        [StructLayout(LayoutKind.Sequential)]
        private struct TrustData
        {
            public uint cbStruct; public IntPtr pPolicyCallbackData; public IntPtr pSIPClientData; public uint dwUIChoice; public uint fdwRevocationChecks;
            public uint dwUnionChoice; public IntPtr pFile; public uint dwStateAction; public IntPtr hWVTStateData; public IntPtr pwszURLReference;
            public uint dwProvFlags; public uint dwUIContext; public IntPtr pSignatureSettings;
        }

        public static bool IsTrusted(string path)
        {
            var file = new TrustFile { cbStruct = (uint)Marshal.SizeOf(typeof(TrustFile)), pcwszFilePath = path };
            IntPtr filePtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(TrustFile)));
            try
            {
                Marshal.StructureToPtr(file, filePtr, false);
                var data = new TrustData { cbStruct = (uint)Marshal.SizeOf(typeof(TrustData)), dwUIChoice = 2 /* no UI */, dwUnionChoice = 1 /* file */, pFile = filePtr };
                return WinVerifyTrust(new IntPtr(-1), new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE") /* GENERIC_VERIFY_V2 */, ref data) == 0;
            }
            finally { Marshal.DestroyStructure(filePtr, typeof(TrustFile)); Marshal.FreeHGlobal(filePtr); }
        }
    }

    // srclient's own API: unlike the WMI method it returns the point's sequence number. srrestoreptapi.h packs
    // both structures to 1 byte: STATEMGRSTATUS is 12 bytes with the INT64 at offset 4 (natural packing read it
    // at offset 8 and always got 0).
    internal static class RestorePointApi
    {
        [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Unicode)]
        private struct RestorePointInfo { public uint EventType; public uint RestorePointType; public long SequenceNumber; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Description; }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct StateManagerStatus { public uint Status; public long SequenceNumber; }

        [DllImport("srclient.dll", EntryPoint = "SRSetRestorePointW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SRSetRestorePointW(ref RestorePointInfo info, out StateManagerStatus status);

        // Returns 0 and the point's number, or the Win32 error Windows reported for BEGIN or END.
        public static int Create(string description, out long sequence)
        {
            var info = new RestorePointInfo { EventType = 100 /* BEGIN_SYSTEM_CHANGE */, RestorePointType = 12 /* MODIFY_SETTINGS */, Description = description };
            StateManagerStatus status;
            bool ok = SRSetRestorePointW(ref info, out status);
            sequence = status.SequenceNumber;
            if (!ok) return Failure(status);
            // Microsoft's sample: same structure, END event and the sequence BEGIN returned.
            info.EventType = 101; // END_SYSTEM_CHANGE
            info.SequenceNumber = sequence;
            return SRSetRestorePointW(ref info, out status) ? 0 : Failure(status);
        }

        private static int Failure(StateManagerStatus status)
        {
            int error = (int)status.Status;
            if (error == 0) error = Marshal.GetLastWin32Error();
            return error != 0 ? error : 1627; // ERROR_FUNCTION_FAILED: FALSE must never read as success
        }
    }

    // What Windows itself reports about the hardware, instead of guessing from names. Both layouts were checked
    // against this laptop (AMD Renoir: HwSchSupported=0; powercfg /a: S3 yes, S0 no, hibernation yes).
    internal static class HardwareCaps
    {
        [StructLayout(LayoutKind.Sequential)] private struct AdapterInfo { public uint hAdapter; public uint LuidLow; public int LuidHigh; public uint NumOfSources; public int bPrecisePresentRegionsPreferred; }
        [StructLayout(LayoutKind.Sequential)] private struct EnumAdapters2 { public uint NumAdapters; public IntPtr pAdapters; }
        [StructLayout(LayoutKind.Sequential)] private struct QueryAdapterInfo { public uint hAdapter; public uint Type; public IntPtr pPrivateDriverData; public uint PrivateDriverDataSize; }
        [StructLayout(LayoutKind.Sequential)] private struct CloseAdapter { public uint hAdapter; }
        [DllImport("gdi32.dll")] private static extern int D3DKMTEnumAdapters2(ref EnumAdapters2 adapters);
        [DllImport("gdi32.dll")] private static extern int D3DKMTQueryAdapterInfo(ref QueryAdapterInfo query);
        [DllImport("gdi32.dll")] private static extern int D3DKMTCloseAdapter(ref CloseAdapter close);
        [DllImport("powrprof.dll")] private static extern uint CallNtPowerInformation(int level, IntPtr input, uint inputLength, byte[] output, uint outputLength);

        // True when at least one GPU driver reports hardware-accelerated GPU scheduling (KMTQAITYPE_WDDM_2_7_CAPS,
        // bit 0 HwSchSupported). HwSchMode in the registry does nothing on any other GPU.
        public static bool? HagsSupported()
        {
            try
            {
                var adapters = new EnumAdapters2();
                if (D3DKMTEnumAdapters2(ref adapters) != 0) return null;
                int size = Marshal.SizeOf(typeof(AdapterInfo));
                adapters.pAdapters = Marshal.AllocHGlobal(size * (int)Math.Max(1, adapters.NumAdapters));
                IntPtr caps = Marshal.AllocHGlobal(4);
                try
                {
                    if (D3DKMTEnumAdapters2(ref adapters) != 0) return null;
                    bool supported = false;
                    for (int i = 0; i < adapters.NumAdapters; i++)
                    {
                        var info = (AdapterInfo)Marshal.PtrToStructure(adapters.pAdapters + i * size, typeof(AdapterInfo));
                        Marshal.WriteInt32(caps, 0);
                        var query = new QueryAdapterInfo { hAdapter = info.hAdapter, Type = 70 /* KMTQAITYPE_WDDM_2_7_CAPS */, pPrivateDriverData = caps, PrivateDriverDataSize = 4 };
                        if (D3DKMTQueryAdapterInfo(ref query) == 0 && (Marshal.ReadInt32(caps) & 1) != 0) supported = true;
                        var close = new CloseAdapter { hAdapter = info.hAdapter };
                        D3DKMTCloseAdapter(ref close);
                    }
                    return supported;
                }
                finally { Marshal.FreeHGlobal(caps); Marshal.FreeHGlobal(adapters.pAdapters); }
            }
            catch (EntryPointNotFoundException) { return null; } // pre-WDDM 2.x Windows
            catch (DllNotFoundException) { return null; }
        }

        // SYSTEM_POWER_CAPABILITIES (CallNtPowerInformation level 4): byte 2 LidPresent, 5 SystemS3, 6 SystemS4,
        // 8 HiberFilePresent, 20 AoAc (Modern Standby). Null when Windows does not answer.
        public static byte[] PowerCapabilities()
        {
            var buffer = new byte[76];
            try { return CallNtPowerInformation(4, IntPtr.Zero, 0, buffer, (uint)buffer.Length) == 0 ? buffer : null; }
            catch (DllNotFoundException) { return null; }
        }
    }

    public class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            // Habilitar TLS 1.2 y TLS 1.3 para descargas HTTPS en cualquier instalación de Windows 10/11
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | (SecurityProtocolType)3072;
            if (args.Length > 0 && args[0] == "--ota-install") { Environment.ExitCode = OtaInstaller.Run(args); return; }
            if (args.Length > 0 && args[0] == "--ota-prepared") { Environment.ExitCode = OtaInstaller.RunPrepared(args); return; }
            if (args.Length == 0 || args[0] != "--ota-health")
            {
                string recovery = OtaInstaller.RecoverInterrupted(Process.GetCurrentProcess().MainModule.FileName);
                if (recovery != null)
                {
                    MessageBox.Show(recovery, "Blackwall · actualización", MessageBoxButton.OK, MessageBoxImage.Warning);
                    if (recovery.StartsWith("Se recuperó", StringComparison.Ordinal)) return;
                }
            }
            bool updateOnly = IsUpdateHelper(args);
            if (updateOnly && OtaProtocol.Elevated())
            {
                MessageBox.Show("El actualizador necesita una sesión normal de Windows. Abre el Explorador sin Administrador y vuelve a intentarlo.", "Blackwall", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!OtaProtocol.Elevated() && RequiresElevation(args))
            {
                try { Process.Start(new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName, string.Join(" ", args.Select(OtaInstaller.Quote))) { UseShellExecute = true, Verb = "runas" }); }
                catch (System.ComponentModel.Win32Exception ex) { if (ex.NativeErrorCode != 1223) MessageBox.Show("No se pudo iniciar Blackwall con permisos: " + ex.Message, "Blackwall"); }
                return;
            }

            Application app = new Application();
            InstallCrashReporting(app);
            MainWindow win = new MainWindow();
            win.UpdateOnly = updateOnly;
            if (updateOnly) { win.RequestedUpdateVersion = args[1]; win.RequestedUpdateHash = args[2]; win.Opacity = 0; win.ShowInTaskbar = false; }
            win.Loaded += (s, e) =>
            {
                if (args.Length == 2 && args[0] == "--ota-rollback")
                    try { win.ReportOtaRollback(OtaInstaller.CheckStage(args[1])); } catch { }
            };
            bool reported = false;
            win.ContentRendered += async (s, e) =>
            {
                if (reported) return;
                reported = true;
                try
                {
                    OtaInstaller.ReportHealthy(args);
                    if (args.Length == 3 && args[0] == "--ota-health" && !OtaProtocol.Elevated())
                    {
                        string result = Path.Combine(OtaInstaller.CheckStage(args[1]), "result.txt");
                        for (int attempt = 0; attempt < 300 && !File.Exists(result); attempt++) await Task.Delay(100);
                        if (win.IsVisible && File.Exists(result) && File.ReadAllText(result).StartsWith("OK:", StringComparison.Ordinal))
                        {
                            for (int attempt = 0; attempt < 10; attempt++)
                            {
                                bool retry = false;
                                try { OtaInstaller.CleanStage(args[1]); break; } catch (IOException) { retry = true; } catch { break; }
                                if (retry) await Task.Delay(200);
                            }
                            win.RelaunchAsAdministrator();
                        }
                    }
                }
                catch { }
            };
            app.Run(win);
        }

        // Any error nobody caught is shown and written to errores.log instead of closing the app or vanishing:
        // a failure must be visible ("falla en caliente"), never a silent success.
        internal static readonly string CrashLogPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Blackwall", "errores.log");
        private static DateTime _lastCrashDialog = DateTime.MinValue;

        internal static void InstallCrashReporting(Application app)
        {
            app.DispatcherUnhandledException += (s, e) => { e.Handled = true; ReportCrash(e.Exception, true); };
            AppDomain.CurrentDomain.UnhandledException += (s, e) => ReportCrash(e.ExceptionObject as Exception, true);
            TaskScheduler.UnobservedTaskException += (s, e) => { e.SetObserved(); ReportCrash(e.Exception, false); };
        }

        internal static void ReportCrash(Exception ex, bool showDialog)
        {
            if (ex == null) return;
            Exception root = ex is AggregateException && ex.InnerException != null ? ex.InnerException : ex;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CrashLogPath));
                File.AppendAllText(CrashLogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " · " + Assembly.GetExecutingAssembly().GetName().Version + Environment.NewLine + ex + Environment.NewLine + Environment.NewLine);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            var dispatcher = Application.Current != null ? Application.Current.Dispatcher : null;
            if (dispatcher == null) return;
            dispatcher.BeginInvoke(new Action(() =>
            {
                var window = Application.Current.MainWindow as MainWindow;
                if (window != null) window.ShowUnexpectedError(root.Message);
                // One dialog at a time: a failure repeating every frame must not bury the user in windows.
                if (!showDialog || (DateTime.UtcNow - _lastCrashDialog).TotalSeconds < 5) return;
                _lastCrashDialog = DateTime.UtcNow;
                MessageBox.Show("Blackwall encontró un error inesperado y no completó la última acción:\n\n" + root.Message + "\n\nNo se aplicó nada a medias sin avisar. Detalles guardados en:\n" + CrashLogPath, "Blackwall · error", MessageBoxButton.OK, MessageBoxImage.Error);
            }));
        }

        internal static bool IsUpdateHelper(string[] args)
        {
            Version version;
            return args.Length == 3 && args[0] == "--ota-update" && Version.TryParse(args[1], out version) && Regex.IsMatch(args[1], @"^\d+\.\d+\.\d+$") && Regex.IsMatch(args[2], "^[a-f0-9]{64}$");
        }

        internal static bool RequiresElevation(string[] args)
        {
            return !IsUpdateHelper(args) && !(args.Length == 3 && args[0] == "--ota-health" && Regex.IsMatch(args[2], "^[a-f0-9]{32}$"));
        }

        internal static void LaunchUnelevated(string file, string arguments)
        {
            object windows = null, desktop = null, document = null, shell = null;
            try
            {
                windows = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39")));
                desktop = windows.GetType().InvokeMember("FindWindowSW", BindingFlags.InvokeMethod, null, windows, new object[] { 0, null, 8, 0, 1 });
                if (desktop == null) throw new InvalidOperationException("No se encontró el escritorio de Windows para abrir el actualizador sin elevación.");
                document = desktop.GetType().InvokeMember("Document", BindingFlags.GetProperty, null, desktop, null);
                shell = document.GetType().InvokeMember("Application", BindingFlags.GetProperty, null, document, null);
                shell.GetType().InvokeMember("ShellExecute", BindingFlags.InvokeMethod, null, shell, new object[] { file, arguments, Path.GetDirectoryName(file), "open", 1 });
            }
            finally
            {
                foreach (object item in new[] { shell, document, desktop, windows }) if (item != null && Marshal.IsComObject(item)) Marshal.ReleaseComObject(item);
            }
        }
    }

    public class SmoothScrollViewer : ScrollViewer
    {
        public SmoothScrollViewer() { Unloaded += (s, e) => StopSmoothScroll(); }
        private bool _reduceMotion;
        public bool ReduceMotion
        {
            get { return _reduceMotion; }
            set { _reduceMotion = value; if (value) StopSmoothScroll(); }
        }
        private double _targetOffset;
        private bool _scrolling;
        private bool _directPending;
        private readonly Stopwatch _scrollClock = new Stopwatch();
        private double _lastFrameMilliseconds;
        private double _renderOffset;
        private double _responseMilliseconds = 24.0;
        private TimeSpan _lastRenderingTime = TimeSpan.MinValue;

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            if (e.Handled) return;
            if (e.Delta == 0) { e.Handled = true; return; }
            if (ScrollableHeight <= 0 || Keyboard.Modifiers != ModifierKeys.None)
            {
                StopSmoothScroll();
                base.OnMouseWheel(e);
                return;
            }
            if (!_scrolling && !_directPending && ((VerticalOffset <= 0 && e.Delta > 0) || (VerticalOffset >= ScrollableHeight && e.Delta < 0))) return;
            double step = SystemParameters.WheelScrollLines < 0 ? ViewportHeight : SystemParameters.WheelScrollLines * 16.0;
            if (ReduceMotion || !SystemParameters.ClientAreaAnimation)
            {
                double directOrigin = _directPending ? _targetOffset : VerticalOffset;
                _targetOffset = Math.Max(0, Math.Min(ScrollableHeight, directOrigin - e.Delta / 120.0 * step));
                _directPending = true;
                ScrollToVerticalOffset(_targetOffset);
                e.Handled = true;
                return;
            }
            double from = VerticalOffset;
            _responseMilliseconds = Math.Abs(e.Delta) >= 120 ? 60.0 : 24.0;
            double origin = _scrolling && e.Delta * (_targetOffset - from) <= 0 ? _targetOffset : from;
            if (!_scrolling || origin == from) _renderOffset = from;
            _targetOffset = Math.Max(0, Math.Min(ScrollableHeight, origin - e.Delta / 120.0 * step));
            if (!_scrolling)
            {
                _scrolling = true;
                _lastFrameMilliseconds = 0;
                _lastRenderingTime = TimeSpan.MinValue;
                _scrollClock.Restart();
                CompositionTarget.Rendering += OnScrollFrame;
            }
            e.Handled = true;
        }

        private void OnScrollFrame(object sender, EventArgs e)
        {
            var frame = e as RenderingEventArgs;
            if (frame != null && frame.RenderingTime == _lastRenderingTime) return;
            if (frame != null) _lastRenderingTime = frame.RenderingTime;
            double now = _scrollClock.Elapsed.TotalMilliseconds;
            double elapsed = Math.Min(50, Math.Max(0, now - _lastFrameMilliseconds));
            _lastFrameMilliseconds = now;
            double remaining = _targetOffset - _renderOffset;
            if (Math.Abs(remaining) < 0.25)
            {
                ScrollToVerticalOffset(_targetOffset);
                StopSmoothScroll();
                return;
            }
            // One frame loop follows the accumulated input; event frequency never changes the scroll model.
            _renderOffset += remaining * (1 - Math.Exp(-elapsed / _responseMilliseconds));
            ScrollToVerticalOffset(_renderOffset);
        }

        internal void StopSmoothScroll()
        {
            _scrolling = false;
            _directPending = false;
            _scrollClock.Stop();
            CompositionTarget.Rendering -= OnScrollFrame;
        }

        protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
        {
            StopSmoothScroll();
            base.OnPreviewMouseDown(e);
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            StopSmoothScroll();
            base.OnPreviewKeyDown(e);
        }

        protected override void OnContentChanged(object oldContent, object newContent)
        {
            StopSmoothScroll();
            base.OnContentChanged(oldContent, newContent);
        }

        protected override void OnScrollChanged(ScrollChangedEventArgs e)
        {
            if (_directPending && Math.Abs(VerticalOffset - _targetOffset) < 0.001) _directPending = false;
            base.OnScrollChanged(e);
        }
    }

    public class ActivityLogEntry
    {
        public DateTime Time;
        public string Level;   // info | success | warn | error
        public string Source;  // Optimización | Instalación | Sistema | Seguridad | Actualizaciones | Procesos
        public string Message;
    }

    // Display row for the virtualized log: data + theme brushes resolved when the row is shown.
    public class ActivityLogRow
    {
        public string Time { get; set; }
        public string Source { get; set; }
        public string Message { get; set; }
        public Brush Dot { get; set; }
        public Brush Text { get; set; }
        public Brush Muted { get; set; }
    }

    // One user-visible task (apply a tweak, install an app...). Mutated only on the UI thread.
    public class ActivityOperation
    {
        public string Title;
        public string Source;
        public string Step = "";
        public int Steps;      // 0 = unknown length (indeterminate progress)
        public int Done;
        public bool Finished;
        public bool Ok;
        public string Summary = "";
        public DateTime Started = DateTime.Now;
        public DateTime Ended;
    }

    // Applies a just-written preference to the running session (SystemParametersInfo, documented by Microsoft) or
    // broadcasts it like Windows Settings does, so it shows without signing out.
    public static class SettingsNotify
    {
        [StructLayout(LayoutKind.Sequential)] private struct FlagsOnly { public uint cbSize; public uint dwFlags; }
        [StructLayout(LayoutKind.Sequential)] private struct FilterKeys { public uint cbSize; public uint dwFlags; public uint iWaitMSec; public uint iDelayMSec; public uint iRepeatMSec; public uint iBounceMSec; }
        [DllImport("user32.dll", SetLastError = true)] private static extern bool SystemParametersInfo(uint action, uint param, int[] value, uint ini);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool SystemParametersInfo(uint action, uint param, IntPtr value, uint ini);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool SystemParametersInfo(uint action, uint param, ref FlagsOnly value, uint ini);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool SystemParametersInfo(uint action, uint param, ref FilterKeys value, uint ini);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, UIntPtr wParam, string lParam, uint flags, uint timeout, out UIntPtr result);
        private const uint UpdateAndSend = 0x1 | 0x2; // SPIF_UPDATEINIFILE | SPIF_SENDCHANGE

        private static void Check(bool ok, string what) { if (!ok) throw new InvalidOperationException(what + " (error " + Marshal.GetLastWin32Error() + ")"); }

        public static void Broadcast(string area)
        {
            UIntPtr result;
            SendMessageTimeout((IntPtr)0xFFFF, 0x001A, UIntPtr.Zero, area, 0x0002, 3000, out result); // WM_SETTINGCHANGE, SMTO_ABORTIFHUNG
        }

        public static void Mouse(int threshold1, int threshold2, int speed)
        {
            Check(SystemParametersInfo(0x0004, 0, new[] { threshold1, threshold2, speed }, UpdateAndSend), "SPI_SETMOUSE");
        }

        public static void WindowArranging(bool on)
        {
            Check(SystemParametersInfo(0x0083, 0, (IntPtr)(on ? 1 : 0), UpdateAndSend), "SPI_SETWINARRANGING");
        }

        // Sticky (0x3A/0x3B), Toggle (0x34/0x35) and Filter keys (0x32/0x33): read the live structure, change only the flags.
        public static void AccessibilityFlags(uint sticky, uint toggle, uint filter)
        {
            foreach (var pair in new[] { new[] { 0x3Au, 0x3Bu, sticky }, new[] { 0x34u, 0x35u, toggle } })
            {
                var data = new FlagsOnly { cbSize = (uint)Marshal.SizeOf(typeof(FlagsOnly)) };
                Check(SystemParametersInfo(pair[0], data.cbSize, ref data, 0), "SPI_GET 0x" + pair[0].ToString("X"));
                data.dwFlags = pair[2];
                Check(SystemParametersInfo(pair[1], data.cbSize, ref data, UpdateAndSend), "SPI_SET 0x" + pair[1].ToString("X"));
            }
            var keys = new FilterKeys { cbSize = (uint)Marshal.SizeOf(typeof(FilterKeys)) };
            Check(SystemParametersInfo(0x32, keys.cbSize, ref keys, 0), "SPI_GETFILTERKEYS");
            keys.dwFlags = filter;
            Check(SystemParametersInfo(0x33, keys.cbSize, ref keys, UpdateAndSend), "SPI_SETFILTERKEYS");
        }
    }

    public class MaterialCard : Border
    {
        private static readonly SolidColorBrush HighlightFill = CreateHighlightFill();
        private static readonly Pen HighlightEdge = CreateHighlightEdge();
        private static readonly Pen EmberEdge = CreateEmberEdge();
        private static SolidColorBrush CreateHighlightFill() { var brush = new SolidColorBrush(Color.FromArgb(9, 231, 237, 247)); brush.Freeze(); return brush; }
        private static Pen CreateHighlightEdge() { var pen = new Pen(new SolidColorBrush(Color.FromArgb(170, 231, 237, 247)), 1); pen.Freeze(); return pen; }
        private static Pen CreateEmberEdge() { var pen = new Pen(new SolidColorBrush(Color.FromArgb(145, 226, 70, 77)), 1); pen.Freeze(); return pen; }
        public Func<bool> Animate { get; set; }
        private static readonly DependencyProperty HighlightProperty = DependencyProperty.Register("Highlight", typeof(double), typeof(MaterialCard), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
        protected override void OnMouseEnter(MouseEventArgs e) { base.OnMouseEnter(e); Highlight(1); }
        protected override void OnMouseLeave(MouseEventArgs e) { base.OnMouseLeave(e); Highlight(0); }
        private void Highlight(double value)
        {
            double from = (double)GetValue(HighlightProperty);
            SetValue(HighlightProperty, value);
            BeginAnimation(HighlightProperty, Animate != null && Animate() ? new DoubleAnimation(from, value, TimeSpan.FromMilliseconds(160)) { FillBehavior = FillBehavior.Stop } : null);
        }
        public void StopMotion() { BeginAnimation(HighlightProperty, null); }
        protected override void OnRender(DrawingContext drawing)
        {
            base.OnRender(drawing);
            double highlight = (double)GetValue(HighlightProperty);
            if (highlight <= 0 || ActualWidth < 34 || ActualHeight < 34) return;
            drawing.PushOpacity(highlight);
            drawing.DrawRoundedRectangle(HighlightFill, null, new Rect(1, 1, ActualWidth - 2, ActualHeight - 2), 15, 15);
            drawing.DrawLine(HighlightEdge, new Point(17, 0.5), new Point(Math.Min(70, ActualWidth - 17), 0.5));
            drawing.DrawLine(EmberEdge, new Point(ActualWidth - 50, ActualHeight - 0.5), new Point(ActualWidth - 17, ActualHeight - 0.5));
            drawing.Pop();
        }
    }

    // Reflows existing cards without rebuilding them or losing selection and focus.
    public class AdaptiveCardGrid : Panel
    {
        // An expanded card grows alone: its row neighbours keep the row's normal height.
        public static readonly DependencyProperty ExpandedProperty = DependencyProperty.RegisterAttached(
            "Expanded", typeof(bool), typeof(AdaptiveCardGrid), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsParentMeasure));
        public static void SetExpanded(UIElement element, bool value) { element.SetValue(ExpandedProperty, value); }
        public static bool GetExpanded(UIElement element) { return (bool)element.GetValue(ExpandedProperty); }
        public int Columns { get; set; }
        private double[] _rowHeights = new double[0];
        private double[] _baseHeights = new double[0];
        private readonly List<UIElement> _layoutChildren = new List<UIElement>();
        protected override Size MeasureOverride(Size available)
        {
            double width = double.IsInfinity(available.Width) ? 310 * Math.Max(1, Columns) : available.Width;
            _layoutChildren.Clear();
            foreach (UIElement child in Children) if (child.Visibility != Visibility.Collapsed) _layoutChildren.Add(child);
            Columns = Math.Max(1, Math.Min(Math.Max(1, _layoutChildren.Count), Math.Min(4, (int)(width / 310))));
            int rows = (_layoutChildren.Count + Columns - 1) / Columns;
            if (_rowHeights.Length != rows) { _rowHeights = new double[rows]; _baseHeights = new double[rows]; }
            else { Array.Clear(_rowHeights, 0, rows); Array.Clear(_baseHeights, 0, rows); }
            for (int i = 0; i < _layoutChildren.Count; i++)
            {
                UIElement child = _layoutChildren[i];
                child.Measure(new Size(width / Columns, double.PositiveInfinity));
                _rowHeights[i / Columns] = Math.Max(_rowHeights[i / Columns], child.DesiredSize.Height);
                if (!GetExpanded(child)) _baseHeights[i / Columns] = Math.Max(_baseHeights[i / Columns], child.DesiredSize.Height);
            }
            return new Size(width, _rowHeights.Sum());
        }
        protected override Size ArrangeOverride(Size bounds)
        {
            double y = 0, width = bounds.Width / Math.Max(1, Columns);
            for (int i = 0; i < _layoutChildren.Count; i++)
            {
                UIElement child = _layoutChildren[i];
                int row = i / Columns;
                double height = GetExpanded(child) ? Math.Max(child.DesiredSize.Height, _baseHeights[row])
                    : _baseHeights[row] > 0 ? _baseHeights[row] : _rowHeights[row];
                child.Arrange(new Rect((i % Columns) * width, y, width, height));
                if (i % Columns == Columns - 1) y += _rowHeights[i / Columns];
            }
            return bounds;
        }
    }

    // One bounded drawing surface for the entire background; never participates in hit testing.
    // WPF's render thread tessellates every anti-aliased stroke on the CPU each frame (~40 ms for this mesh), so the
    // mesh is rasterised here, at the screen's real pixel resolution, into one bitmap that WPF only has to upload.
    // Only the area covered by this frame and the previous one is cleared and uploaded.
    public class GraphiteNetwork : FrameworkElement
    {
        private const int Count = 90;
        private const double LinkDistance = 165, PointerReach = 240, PointerPush = 150;
        private readonly Point[] _points = new Point[Count];
        private readonly Vector[] _velocities = new Vector[Count];
        private readonly Point[] _positions = new Point[Count];
        private readonly Stopwatch _clock = new Stopwatch();
        private double _previous;
        private bool _running;
        private bool _skipFrame;
        private bool _dormant; // frame loop detached after IdleSeconds without the cursor; the last frame stays on screen
        private const double IdleSeconds = 12;
        private double _lastPointerMove = -1;
        private TimeSpan _lastRendering = TimeSpan.MinValue;
        private Point? _pointer;
        private Point _visualPointer;
        private double _pointerStrength;
        private bool _dark = true;
        private WriteableBitmap _bitmap;
        private int[] _buffer;
        private int _width, _height;
        private Int32Rect _drawn = Int32Rect.Empty;
        public bool Running { get { return _running; } }
        public bool PointerActive { get { return _pointer.HasValue; } }
        public bool Dark { get { return _dark; } set { _dark = value; Redraw(); } }
        public GraphiteNetwork()
        {
            IsHitTestVisible = false; ClipToBounds = true;
            var random = new Random(7319);
            for (int i = 0; i < Count; i++)
            {
                _points[i] = new Point(random.NextDouble(), random.NextDouble());
                _velocities[i] = new Vector((random.NextDouble() - .5) * .05, (random.NextDouble() - .5) * .05);
            }
            Unloaded += (s, e) => SetRunning(false);
        }
        public void SetPointer(Point? point)
        {
            if (point.HasValue && !_pointer.HasValue && _pointerStrength < .01) _visualPointer = point.Value;
            _pointer = point;
            _lastPointerMove = _clock.Elapsed.TotalSeconds;
            if (_running && _dormant) { _dormant = false; _previous = _clock.Elapsed.TotalSeconds; CompositionTarget.Rendering += OnFrame; }
            if (!Running) { if (point.HasValue) _visualPointer = point.Value; _pointerStrength = point.HasValue ? 1 : 0; Redraw(); }
        }
        public void SetRunning(bool running)
        {
            if (running && !_running)
            {
                _running = true; _dormant = false; _clock.Restart(); _previous = 0; _lastPointerMove = 0; _lastRendering = TimeSpan.MinValue;
                CompositionTarget.Rendering += OnFrame;
            }
            else if (!running && _running)
            {
                _running = false; _dormant = false; CompositionTarget.Rendering -= OnFrame; _clock.Stop();
                _pointer = null; _pointerStrength = 0; Redraw();
            }
        }
        // Runs once per displayed frame (vsync) at render priority: mouse input can no longer starve it.
        private void OnFrame(object sender, EventArgs e)
        {
            var frame = e as RenderingEventArgs;
            if (frame != null) { if (frame.RenderingTime == _lastRendering) return; _lastRendering = frame.RenderingTime; }
            double elapsed = _clock.Elapsed.TotalSeconds;
            // Full display rate while the cursor interacts; half rate for the slow idle drift (indistinguishable, half the work).
            bool interacting = elapsed - _lastPointerMove < .5 || Math.Abs((_pointer.HasValue ? 1 : 0) - _pointerStrength) > .02;
            // An optimizer must not spend CPU on decoration nobody is touching: sleep until the cursor moves again.
            if (!interacting && elapsed - _lastPointerMove > IdleSeconds) { CompositionTarget.Rendering -= OnFrame; _dormant = true; return; }
            _skipFrame = !interacting && !_skipFrame;
            if (_skipFrame) return;
            double dt = Math.Min(.05, Math.Max(0, elapsed - _previous)); _previous = elapsed;
            double blend = 1 - Math.Exp(-dt / .07); // the cursor halo follows within ~0.2 s instead of trailing behind
            _pointerStrength += ((_pointer.HasValue ? 1 : 0) - _pointerStrength) * blend;
            if (_pointer.HasValue) _visualPointer += (_pointer.Value - _visualPointer) * blend;
            for (int i = 0; i < Count; i++)
            {
                var p = _points[i]; var v = _velocities[i]; p.X += v.X * dt; p.Y += v.Y * dt;
                if (p.X < 0 || p.X > 1) { v.X = -v.X; p.X = Math.Max(0, Math.Min(1, p.X)); }
                if (p.Y < 0 || p.Y > 1) { v.Y = -v.Y; p.Y = Math.Max(0, Math.Min(1, p.Y)); }
                _points[i] = p; _velocities[i] = v;
            }
            Redraw();
        }
        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo) { base.OnRenderSizeChanged(sizeInfo); Redraw(); }
        protected override void OnRender(DrawingContext drawing)
        {
            if (_bitmap != null) drawing.DrawImage(_bitmap, new Rect(0, 0, ActualWidth, ActualHeight));
        }

        private void Redraw()
        {
            double width = ActualWidth, height = ActualHeight;
            if (width <= 0 || height <= 0) return;
            var source = PresentationSource.FromVisual(this);
            double scale = source != null && source.CompositionTarget != null ? source.CompositionTarget.TransformToDevice.M11 : 1;
            int bw = Math.Max(1, (int)Math.Round(width * scale)), bh = Math.Max(1, (int)Math.Round(height * scale));
            bool resized = _bitmap == null || bw != _width || bh != _height;
            if (resized)
            {
                _width = bw; _height = bh; _buffer = new int[bw * bh];
                _bitmap = new WriteableBitmap(bw, bh, 96 * scale, 96 * scale, PixelFormats.Pbgra32, null);
                _drawn = new Int32Rect(0, 0, bw, bh);
            }
            double sx = bw / width, sy = bh / height;
            bool pointer = _pointerStrength > .01;
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            for (int i = 0; i < Count; i++)
            {
                Point p = new Point(_points[i].X * width, _points[i].Y * height);
                if (pointer)
                {
                    Vector away = p - _visualPointer; double d = away.Length;
                    if (d > .1 && d < PointerPush) p += away / d * (18 * _pointerStrength * (1 - d / PointerPush));
                }
                p = new Point(p.X * sx, p.Y * sy);
                _positions[i] = p;
                minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y); maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y);
            }
            var cursor = new Point(_visualPointer.X * sx, _visualPointer.Y * sy);
            if (pointer) { minX = Math.Min(minX, cursor.X); minY = Math.Min(minY, cursor.Y); maxX = Math.Max(maxX, cursor.X); maxY = Math.Max(maxY, cursor.Y); }
            // Everything of this frame lies inside the particles' bounding box: clear what the last frame drew plus that box.
            var current = DirtyRect((int)minX - 6, (int)minY - 6, (int)maxX + 7, (int)maxY + 7);
            var dirty = Union(_drawn, current);
            for (int y = dirty.Y; y < dirty.Y + dirty.Height; y++) Array.Clear(_buffer, y * bw + dirty.X, dirty.Width);

            int lr = _dark ? 245 : 138, lg = _dark ? 92 : 40, lb = _dark ? 92 : 48;
            double lineAlpha = _dark ? .78 : .55, dotAlpha = _dark ? .95 : .8;
            double link = LinkDistance * sx, reach = PointerReach * sx;
            for (int i = 0; i < Count; i++)
            {
                Point a = _positions[i];
                for (int j = i + 1; j < Count; j++)
                {
                    double dx = a.X - _positions[j].X, dy = a.Y - _positions[j].Y, distance = Math.Sqrt(dx * dx + dy * dy);
                    if (distance < link) Line(a, _positions[j], lr, lg, lb, lineAlpha * Math.Pow(1 - distance / link, .7)); // softer fade keeps mid-range links readable
                }
                if (pointer)
                {
                    double distance = (a - cursor).Length;
                    if (distance < reach) Line(a, cursor, lr, lg, lb, Math.Min(1, lineAlpha * 1.25) * (1 - distance / reach) * _pointerStrength);
                }
            }
            double radius = 2.3 * sx;
            for (int i = 0; i < Count; i++) Dot(_positions[i], radius, _dark ? 255 : 138, _dark ? 118 : 40, _dark ? 110 : 48, dotAlpha);
            if (dirty.Width > 0 && dirty.Height > 0) _bitmap.WritePixels(dirty, _buffer, bw * 4, dirty.X, dirty.Y);
            _drawn = current;
            if (resized) InvalidateVisual();
        }

        private Int32Rect DirtyRect(int x0, int y0, int x1, int y1)
        {
            x0 = Math.Max(0, x0); y0 = Math.Max(0, y0); x1 = Math.Min(_width, x1); y1 = Math.Min(_height, y1);
            return x1 <= x0 || y1 <= y0 ? Int32Rect.Empty : new Int32Rect(x0, y0, x1 - x0, y1 - y0);
        }
        private static Int32Rect Union(Int32Rect a, Int32Rect b)
        {
            if (a.IsEmpty || a.Width == 0) return b;
            if (b.IsEmpty || b.Width == 0) return a;
            int x0 = Math.Min(a.X, b.X), y0 = Math.Min(a.Y, b.Y);
            return new Int32Rect(x0, y0, Math.Max(a.X + a.Width, b.X + b.Width) - x0, Math.Max(a.Y + a.Height, b.Y + b.Height) - y0);
        }

        // Premultiplied "over" blend of one colour at coverage alpha (0..1); x/255 computed without division.
        private void Blend(int x, int y, int r, int g, int b, double alpha)
        {
            if (x < 0 || y < 0 || x >= _width || y >= _height || alpha <= 0.004) return;
            int a = (int)(alpha * 255 + .5); if (a > 255) a = 255;
            int index = y * _width + x, d = _buffer[index], inv = 255 - a;
            int na = a + ((((d >> 24) & 255) * inv + 128) * 257 >> 16);
            int nr = ((r * a + 128) * 257 >> 16) + ((((d >> 16) & 255) * inv + 128) * 257 >> 16);
            int ng = ((g * a + 128) * 257 >> 16) + ((((d >> 8) & 255) * inv + 128) * 257 >> 16);
            int nb = ((b * a + 128) * 257 >> 16) + (((d & 255) * inv + 128) * 257 >> 16);
            _buffer[index] = (na << 24) | (nr << 16) | (ng << 8) | nb;
        }

        // Xiaolin Wu anti-aliased line: one pixel wide and sharp at device resolution.
        private void Line(Point from, Point to, int r, int g, int b, double alpha)
        {
            if (alpha <= 0.004) return;
            double x0 = from.X, y0 = from.Y, x1 = to.X, y1 = to.Y;
            bool steep = Math.Abs(y1 - y0) > Math.Abs(x1 - x0);
            if (steep) { double t = x0; x0 = y0; y0 = t; t = x1; x1 = y1; y1 = t; }
            if (x0 > x1) { double t = x0; x0 = x1; x1 = t; t = y0; y0 = y1; y1 = t; }
            double dx = x1 - x0, gradient = dx < 1e-6 ? 1 : (y1 - y0) / dx, y = y0 + gradient * (Math.Round(x0) - x0);
            for (int x = (int)Math.Round(x0); x <= (int)Math.Round(x1); x++, y += gradient)
            {
                int iy = (int)Math.Floor(y); double f = y - iy;
                if (steep) { Blend(iy, x, r, g, b, alpha * (1 - f)); Blend(iy + 1, x, r, g, b, alpha * f); }
                else { Blend(x, iy, r, g, b, alpha * (1 - f)); Blend(x, iy + 1, r, g, b, alpha * f); }
            }
        }

        private void Dot(Point centre, double radius, int r, int g, int b, double alpha)
        {
            int x0 = (int)Math.Floor(centre.X - radius - 1), x1 = (int)Math.Ceiling(centre.X + radius + 1);
            int y0 = (int)Math.Floor(centre.Y - radius - 1), y1 = (int)Math.Ceiling(centre.Y + radius + 1);
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    double d = Math.Sqrt((x + .5 - centre.X) * (x + .5 - centre.X) + (y + .5 - centre.Y) * (y + .5 - centre.Y));
                    Blend(x, y, r, g, b, alpha * Math.Max(0, Math.Min(1, radius + .5 - d)));
                }
        }
    }

    // A small isolated drawing surface keeps the moving circuit away from text/layout.
    public class EnergyTrace : FrameworkElement
    {
        private static readonly Pen BasePen = CreatePen(Color.FromArgb(100, 143, 24, 40), 1);
        private static readonly Pen HotPen = CreatePen(Color.FromArgb(210, 239, 104, 120), 1.4);
        private static readonly SolidColorBrush[] ChargeInk = CreateChargeInk();
        private static Pen CreatePen(Color color, double width) { var pen = new Pen(new SolidColorBrush(color), width); pen.Freeze(); return pen; }
        private static SolidColorBrush[] CreateChargeInk()
        {
            var brushes = new SolidColorBrush[8];
            for (int i = 0; i < brushes.Length; i++) { brushes[i] = new SolidColorBrush(Color.FromArgb((byte)(190 - i * 22), 255, 91, 121)); brushes[i].Freeze(); }
            return brushes;
        }
        private readonly DispatcherTimer _timer;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        public bool Vertical { get; set; }
        public bool Running { get { return _timer.IsEnabled; } }
        public EnergyTrace()
        {
            IsHitTestVisible = false;
            ClipToBounds = true;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            _timer.Tick += (s, e) => InvalidateVisual();
            Unloaded += (s, e) => SetRunning(false);
        }
        public void SetRunning(bool running)
        {
            if (running) _timer.Start(); else _timer.Stop();
            InvalidateVisual();
        }
        protected override void OnRender(DrawingContext drawing)
        {
            base.OnRender(drawing);
            double length = Vertical ? ActualHeight : ActualWidth;
            double cross = Vertical ? ActualWidth : ActualHeight;
            if (length <= 0 || cross <= 0) return;
            double time = Running ? _clock.Elapsed.TotalSeconds : 0;
            Func<double, double, Point> point = (along, across) => Vertical ? new Point(across, along) : new Point(along, across);
            Point previous = point(0, cross / 2);
            for (int i = 1; i <= 36; i++)
            {
                double along = length * i / 36;
                double offset = i % 4 == 0 ? cross * 0.26 : i % 4 == 1 ? -cross * 0.26 : 0;
                Point next = point(along, cross / 2 + offset);
                drawing.DrawLine(BasePen, previous, next);
                previous = next;
            }
            if (!Running) return;
            for (int charge = 0; charge < 3; charge++)
            {
                double head = ((time * 0.13 + charge / 3.0) % 1) * length;
                for (int tail = 0; tail < 8; tail++)
                {
                    double position = head - tail * 4;
                    if (position < 0) continue;
                    drawing.DrawEllipse(ChargeInk[tail], null, point(position, cross / 2 + Math.Sin(position / length * Math.PI * 12) * cross * 0.18), tail == 0 ? 2.4 : 1.4, tail == 0 ? 2.4 : 1.4);
                }
                drawing.DrawLine(HotPen, point(Math.Max(0, head - 14), cross / 2), point(head, cross / 2));
            }
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
        private string _lastRenderedTab;
        private bool _lastRenderedReady;
        private string _appCategoryScope = "Todas";
        private string _tweakSearchText = "";
        private string _appSearchText = "";
        private TextBox _sectionSearch;
        private Button _installSelectedButton;
        // Every change Blackwall makes is journaled with the exact prior values, so it can be undone without a restore point.
        private string _changesPath;
        private List<Dictionary<string, object>> _changes = new List<Dictionary<string, object>>();
        private Dictionary<string, object> _restorePointRecord;
        private bool _restorePointDialogOpen;
        private DateTime _lastRestorePointWatch = DateTime.MinValue;
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
        private OtaRelease _availableRelease;
        private string _announcedRelease;
        private Window _otaDialog;
        private Border _otaNotice;
        private bool _otaInProgress;
        internal bool UpdateOnly;
        internal string RequestedUpdateVersion, RequestedUpdateHash;
        private List<Dictionary<string, object>> _liveScripts = new List<Dictionary<string, object>>();
        private bool _monitorProcessesChanged;
        private readonly Dictionary<int, TextBlock> _monitorProcessMemoryLabels = new Dictionary<int, TextBlock>();
        private StackPanel _monitorProcessPanel;
        private TextBlock _monitorProcessTitle;
        private List<Dictionary<string, object>> _restorePoints = new List<Dictionary<string, object>>();
        private const int ActivityLogLimit = 500;
        private const int ActivityRowLimit = 200;
        private readonly List<ActivityLogEntry> _activityLog = new List<ActivityLogEntry>();
        private readonly List<ActivityOperation> _operations = new List<ActivityOperation>();
        private int _operationsCompleted, _operationsFailed;
        private string _activityFilter = "all";
        private ItemsControl _activityLogRows;
        private ScrollViewer _activityLogScroll;
        private TextBlock _activityLogEmpty;
        private StackPanel _activityOpsPanel;
        private TextBlock _activitySummary;
        private ProgressBar _footerProgress;
        private bool _activityRefreshQueued;

        // Controles principales de UI
        private Border _rootBorder;
        private Grid _rootGrid;
        private GraphiteNetwork _network;
        private Border _logoBox;
        private TextBlock _deviceBadgeText;
        private TextBox _omniboxInput;
        private Popup _omniboxPopup;
        private StackPanel _omniboxResultsPanel;
        private readonly List<Button> _omniboxItems = new List<Button>();
        private int _omniboxSelected = -1;
        private string _pendingSearchId;
        private Button _themeBtn;
        private Button _otaBtn;
        private Button _minBtn;
        private Button _maxBtn;
        private Button _closeBtn;
        private ScrollViewer _mainScroll;
        private TextBlock _statusText;
        private TextBlock _taskText;
        private Dictionary<string, Button> _navButtons = new Dictionary<string, Button>();
        private DispatcherTimer _scriptMonitorTimer;
        private JavaScriptSerializer _json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        private readonly Dictionary<string, Task> _readTasks = new Dictionary<string, Task>();
        private readonly HashSet<string> _loadedModules = new HashSet<string>();
        private readonly Dictionary<string, string> _readErrors = new Dictionary<string, string>();
        private readonly Dictionary<string, string> _readLabels = new Dictionary<string, string>();
        private readonly Dictionary<string, int> _readProgress = new Dictionary<string, int>();
        private Border _activityPanel;
        private TextBlock _activityTitle;
        private TextBlock _activityDetail;
        private ProgressBar _activityProgress;
        private Button _activityRetry;
        private Button _activityDismiss;

        private readonly string _preferencesPath;
        private bool _sidebarCollapsed = true;
        private bool _reduceMotion;
        private readonly HashSet<string> _pendingTweaks = new HashSet<string>();
        private readonly HashSet<string> _uiActions = new HashSet<string>();
        private readonly Dictionary<string, string> _tweakActionErrors = new Dictionary<string, string>();
        private Func<Dictionary<string, object>, bool, Task> _executeTweakAction;
        private Func<string, List<Dictionary<string, object>>, Task<List<Dictionary<string, object>>>> _confirmApply;
        private Func<Dictionary<string, object>, bool> _confirmCatalogRevert;
        private Func<List<Dictionary<string, object>>, bool> _confirmLeftoverRevert;
        private Func<List<Dictionary<string, object>>, int, bool> _confirmFullReset;
        private Action<List<string>, List<KeyValuePair<string, string>>> _reportFullReset;
        private Func<string, string> _powercfg;
        private Func<string, string, string> _appxTool;
        private Func<bool> _confirmPowerRepair;
        private Func<string, string, bool> _confirmCleanup;
        private Func<Task<bool>> _ensureRestorePoint;
        private bool _repairPowerOnReset;
        private ColumnDefinition _sidebarColumn;
        private Border _sidebarBorder;
        private Border _sidebarScrim;
        private Button _sidebarToggle;
        private Button _motionToggle;
        private readonly List<FrameworkElement> _expandedSidebarElements = new List<FrameworkElement>();
        private readonly List<SmoothScrollViewer> _scrollViewers = new List<SmoothScrollViewer>();
        private EnergyTrace _headerEnergy;
        private EnergyTrace _sidebarEnergy;
        private readonly List<FrameworkElement> _motionEntrances = new List<FrameworkElement>();
        private readonly HashSet<FrameworkElement> _revealedCards = new HashSet<FrameworkElement>();
        private List<FrameworkElement> _pageMotionCards = new List<FrameworkElement>();
        private FrameworkElement _motionPage;
        private readonly HashSet<Button> _motionButtons = new HashSet<Button>();
        private RotateTransform _dashboardEdgeRotation;
        private readonly RotateTransform _scanEdgeRotation = new RotateTransform(0, 0.5, 0.5);
        private LinearGradientBrush _scanEdgeBrush;
        private System.Windows.Media.Effects.DropShadowEffect _logoHalo;
        private static readonly DependencyProperty MotionWiredProperty = DependencyProperty.RegisterAttached("MotionWired", typeof(bool), typeof(MainWindow), new PropertyMetadata(false));
        private static readonly DependencyProperty SidebarWidthProperty = DependencyProperty.Register("SidebarWidth", typeof(double), typeof(MainWindow), new PropertyMetadata(248.0, (d, e) =>
        {
            var window = (MainWindow)d;
            if (window._sidebarBorder != null) window._sidebarBorder.Width = (double)e.NewValue;
        }));
        private bool MotionAllowed { get { return !_reduceMotion && SystemParameters.ClientAreaAnimation; } }

        public MainWindow() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Blackwall", "ui-preferences.json")) { }

        public MainWindow(string preferencesPath)
        {
            _preferencesPath = preferencesPath;
            _executeTweakAction = ExecuteTweakAsync;
            _confirmApply = ConfirmApplyAsync;
            _confirmCatalogRevert = ConfirmCatalogRevert;
            _confirmLeftoverRevert = ConfirmLeftoverRevert;
            _confirmFullReset = ConfirmFullReset;
            _reportFullReset = ReportFullReset;
            _powercfg = RunPowercfg;
            _appxTool = RunAppxTool;
            _confirmPowerRepair = ConfirmPowerRepair;
            _confirmCleanup = ConfirmCleanup;
            _ensureRestorePoint = EnsureRestorePointCoreAsync;
            _checkRestorePoint = () => Task.Run(() => CheckRecordedRestorePoint());
            _notifyPreference = NotifyPreference;
            _restartExplorer = RestartExplorerAsync;
            _confirmRestart = ConfirmRestart;
            _runShutdown = args => { int code; RunSystemToolDirect("shutdown.exe", args, out code, 15000); return code == 0; };
            _changesPath = Path.Combine(Path.GetDirectoryName(preferencesPath), "cambios.json");
            LoadUiPreferences();
            bool isAdmin = IsAdministrator();
            Title = "Blackwall — Muro Negro" + (isAdmin ? " [ADMINISTRADOR]" : " [Modo Estándar]");
            // Never ask for more than the work area: small laptops or 125–150 % scaling would clip the restored window.
            Rect workArea = SystemParameters.WorkArea;
            Width = Math.Min(1400, workArea.Width);
            Height = Math.Min(880, workArea.Height);
            MinWidth = Math.Min(1080, workArea.Width);
            MinHeight = Math.Min(700, workArea.Height);
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
            SourceInitialized += (s, e) => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle).AddHook(WindowBoundsHook);

            this.StateChanged += (s, e) =>
            {
                if (this.WindowState == WindowState.Maximized)
                {
                    if (_rootBorder != null) _rootBorder.Padding = new Thickness(7);
                }
                else
                {
                    if (_rootBorder != null) _rootBorder.Padding = new Thickness(0);
                }
                UpdateMaximizeButton();
            };

            AppendLog("Blackwall iniciado · herramientas nativas de Windows disponibles.", "info", "Sistema");
            AppendLog(isAdmin ? "Ejecutándose como administrador." : "Ejecutándose sin permisos de administrador: algunos ajustes no se podrán aplicar.", isAdmin ? "info" : "warn", "Sistema");

            LoadEmbeddedCatalogs();
            LoadJournal(); // after the catalog: journal entries are validated against it
            UpdateThemeBrushes();
            BuildShellUI();
            InitializeActivityPanel();
            Activated += (s, e) =>
            {
                UpdateAmbientMotion(); AnnounceAvailableUpdate();
                if ((DateTime.UtcNow - _lastRestorePointWatch).TotalMinutes >= 2) { var watch = WatchRestorePointAsync(); }
            };
            Deactivated += (s, e) => { foreach (var viewer in _scrollViewers) viewer.StopSmoothScroll(); UpdateAmbientMotion(); };
            StateChanged += (s, e) => { if (WindowState == WindowState.Minimized) foreach (var viewer in _scrollViewers) viewer.StopSmoothScroll(); UpdateAmbientMotion(); };
            IsVisibleChanged += (s, e) => UpdateAmbientMotion();
            SystemParameters.StaticPropertyChanged += OnSystemMotionChanged;
            Closed += (s, e) =>
            {
                SystemParameters.StaticPropertyChanged -= OnSystemMotionChanged;
                _scriptMonitorTimer.Stop();
                if (_logoHalo != null) _logoHalo.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.BlurRadiusProperty, null);
                if (_headerEnergy != null) _headerEnergy.SetRunning(false);
                if (_sidebarEnergy != null) _sidebarEnergy.SetRunning(false);
                if (_network != null) _network.SetRunning(false);
                SetDashboardEdgeMotion(false);
                SetCeitoMotion(false);
                _scanEdgeRotation.BeginAnimation(RotateTransform.AngleProperty, null);
                StopEntrances();
            };

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
                    bool searchOpen = _omniboxPopup.IsOpen;
                    _omniboxPopup.IsOpen = false;
                    if (searchOpen) _omniboxInput.Focus();
                    else if (!_sidebarCollapsed) ToggleSidebar();
                    e.Handled = true;
                }
            };

            this.Loaded += async (s, e) =>
            {
                if (UpdateOnly) { ShowOtaUpdateDialog(); Application.Current.Shutdown(); return; }
                TriggerLogoGlitch();
                RenderCurrentTab();
                await Dispatcher.Yield(DispatcherPriority.Background);
                var updateCheck = CheckRemoteOtaAsync(true);
                var restoreWatch = WatchRestorePointAsync();
                await RefreshHardwareNativeAsync();
            };

            _scriptMonitorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _scriptMonitorTimer.Tick += async (s, e) =>
            {
                if (IsVisible && WindowState != WindowState.Minimized && _activeTab == "monitor" && !_readTasks.ContainsKey("monitor") && !_readErrors.ContainsKey("monitor"))
                {
                    await RefreshLiveScriptsNativeAsync(true);
                }
            };
            _scriptMonitorTimer.Start();
        }

        private void LoadUiPreferences()
        {
            try
            {
                if (!File.Exists(_preferencesPath)) return;
                var settings = _json.Deserialize<Dictionary<string, object>>(File.ReadAllText(_preferencesPath));
                if (settings.ContainsKey("sidebarCollapsed")) _sidebarCollapsed = GetBool(settings, "sidebarCollapsed");
                _reduceMotion = GetBool(settings, "reduceMotion");
                if (settings.ContainsKey("dark")) _isDark = GetBool(settings, "dark");
            }
            catch { AppendLog("Preferencias no válidas; se usarán los valores iniciales.", "warn", "Sistema"); }
        }

        private void SaveUiPreferences()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_preferencesPath));
                string temp = _preferencesPath + ".tmp";
                File.WriteAllText(temp, _json.Serialize(new { sidebarCollapsed = _sidebarCollapsed, reduceMotion = _reduceMotion, dark = _isDark }));
                if (File.Exists(_preferencesPath)) File.Replace(temp, _preferencesPath, null);
                else File.Move(temp, _preferencesPath);
            }
            catch (Exception ex) { AppendLog("No se pudo guardar la preferencia visual: " + ex.Message, "INTERFAZ"); }
        }

        internal void ShowUnexpectedError(string message)
        {
            AppendLog("Error inesperado: " + message, "ERROR");
            SetStatus("🔴 Error inesperado: " + message, "Detalles en errores.log");
        }

        private void LoadJournal()
        {
            try
            {
                if (!File.Exists(_changesPath)) { _changes = new List<Dictionary<string, object>>(); _restorePointRecord = null; return; }
                var journal = _json.Deserialize<Dictionary<string, object>>(File.ReadAllText(_changesPath));
                _restorePointRecord = journal.ContainsKey("restorePoint") ? journal["restorePoint"] as Dictionary<string, object> : null;
                var loaded = journal.ContainsKey("changes") ? ConvertToDictList(journal["changes"]) : new List<Dictionary<string, object>>();
                _changes = loaded.Where(IsAllowedChange).ToList();
                if (_changes.Count < loaded.Count)
                    AppendLog((loaded.Count - _changes.Count) + " entradas del registro de cambios se descartaron: piden cambios que el catálogo de Blackwall no hace (archivo dañado o manipulado).", "warn", "Seguridad");
            }
            catch (Exception ex) { AppendLog("No se pudo leer el registro de cambios (" + ex.Message + "); Revertir todo no podrá usarlo.", "warn", "Seguridad"); }
        }

        // cambios.json lives in the user's profile but its ops run as administrator. Only ops that put back something
        // the catalog itself touches are accepted, so a tampered file cannot smuggle arbitrary changes in.
        private bool IsAllowedChange(Dictionary<string, object> change)
        {
            object raw;
            if (!change.TryGetValue("restoreOps", out raw) || !(raw is IEnumerable) || raw is string) return false;
            return ((IEnumerable)raw).Cast<object>().All(item => item is Dictionary<string, object> && IsAllowedRestoreOp((Dictionary<string, object>)item));
        }

        internal bool IsAllowedRestoreOp(Dictionary<string, object> op)
        {
            var catalog = _tweaks.SelectMany(t => ConvertToDictList(t.ContainsKey("applyOps") ? t["applyOps"] : null).Concat(ConvertToDictList(t.ContainsKey("undoOps") ? t["undoOps"] : null))).ToList();
            var ignore = StringComparison.OrdinalIgnoreCase;
            string type = GetStr(op, "type");
            if (type == "appx_install") return IsAllowedAppxPackage(GetStr(op, "name"));
            if (type == "reg_value" || type == "reg_delete")
            {
                string hive = GetStr(op, "hive"), key = GetStr(op, "key"), name = GetStr(op, "name");
                if (hive != "HKLM" && hive != "HKCU") return false;
                // A startup program's on/off switch: harmless to put back, whatever the program is called.
                if (IsStartupApprovedKey(key) && (type == "reg_delete" || GetStr(op, "kind") == "Binary")) return true;
                return catalog.Any(c => GetStr(c, "hive") == hive && (GetStr(c, "type") == "reg_delete_tree"
                    ? key.Equals(GetStr(c, "key"), ignore) || key.StartsWith(GetStr(c, "key") + "\\", ignore)
                    : key.Equals(GetStr(c, "key"), ignore) && name.Equals(GetStr(c, "name"), ignore)));
            }
            if (type == "service_start") return catalog.Any(c => GetStr(c, "type") == "service_stop" && GetStr(c, "name").Equals(GetStr(op, "name"), ignore));
            if (type != "powercfg") return false;
            string args = GetStr(op, "args");
            const string guid = "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}";
            if (Regex.IsMatch(args, "^/setactive (scheme_current|" + guid + ")$", RegexOptions.IgnoreCase)) return true;
            if (Regex.IsMatch(args, "^/hibernate (on|off|/size [0-9]{1,3})$", RegexOptions.IgnoreCase)) return true;
            Match delete = Regex.Match(args, "^/delete (" + guid + ")$", RegexOptions.IgnoreCase);
            if (delete.Success && delete.Groups[1].Value.Equals(RepairSchemeGuid, ignore)) return true;
            if (delete.Success) return catalog.Any(c => GetStr(c, "args").StartsWith("/duplicatescheme ", ignore) && GetStr(c, "args").EndsWith(" " + delete.Groups[1].Value, ignore));
            Match index = Regex.Match(args, "^/set(ac|dc)valueindex " + guid + " (\\S+) (\\S+) [0-9]+$", RegexOptions.IgnoreCase);
            return index.Success && catalog.Any(c =>
            {
                string[] p = GetStr(c, "args").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                return p.Length == 5 && p[2].Equals(index.Groups[2].Value, ignore) && p[3].Equals(index.Groups[3].Value, ignore);
            });
        }

        private FileStream _systemChangeLock;

        // One system change at a time, also across two open copies of Blackwall: the lock file sits next to the
        // journal, and the journal is re-read under it so neither copy overwrites the other's history.
        private string _changeOrigin, _changeSession;

        private bool TryBeginSystemChange(string origin)
        {
            if (_systemChangeLock != null) { SetStatus("Espera a que termine el cambio en curso", "Blackwall hace un cambio a la vez"); return false; }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_changesPath));
                _systemChangeLock = new FileStream(_changesPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (Exception ex)
            {
                if (!(ex is IOException) && !(ex is UnauthorizedAccessException)) throw;
                SetStatus("Otra ventana de Blackwall está cambiando el sistema", "Espera a que termine");
                return false;
            }
            LoadJournal();
            _changeOrigin = origin;
            _changeSession = DateTime.Now.ToString("HHmmss");
            WriteChangeLog("==== INICIO #" + _changeSession + " · origen: " + origin + " · Blackwall " + Assembly.GetExecutingAssembly().GetName().Version.ToString(3)
                + " · Windows " + Convert.ToString(Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "CurrentBuildNumber", "?"))
                + " · " + (IsAdministrator() ? "administrador" : "SIN permisos de administrador") + " · " + _changes.Count + " cambios guardados ====");
            return true;
        }

        private void EndSystemChange()
        {
            if (_systemChangeLock == null) return;
            WriteChangeLog("==== FIN #" + _changeSession + " · " + _changeOrigin + " · " + _changes.Count + " cambios guardados ====");
            _changeOrigin = _changeSession = null;
            _systemChangeLock.Dispose();
            _systemChangeLock = null;
        }

        // Throws on failure: a change must never run unless its prior values are safely on disk.
        private void SaveJournal()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_changesPath));
            string temp = _changesPath + ".tmp";
            File.WriteAllText(temp, _json.Serialize(new Dictionary<string, object> { { "restorePoint", _restorePointRecord }, { "changes", _changes } }));
            if (File.Exists(_changesPath)) File.Replace(temp, _changesPath, null);
            else File.Move(temp, _changesPath);
        }

        private void OnSystemMotionChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "ClientAreaAnimation") Dispatcher.BeginInvoke(new Action(UpdateMotionPreference));
        }

        private SmoothScrollViewer CreateSmoothScroller()
        {
            var viewer = new SmoothScrollViewer { ReduceMotion = !MotionAllowed };
            _scrollViewers.Add(viewer);
            return viewer;
        }

        private void UpdateMotionPreference()
        {
            foreach (var viewer in _scrollViewers) viewer.ReduceMotion = !MotionAllowed;
            if (_motionToggle != null)
            {
                _motionToggle.Content = NavContent(_reduceMotion ? "" : "", _sidebarCollapsed ? (_reduceMotion ? "Reducido" : "Animado") : (_reduceMotion ? "Movimiento reducido" : "Animaciones activas"), _sidebarCollapsed);
                _motionToggle.HorizontalContentAlignment = _sidebarCollapsed ? HorizontalAlignment.Center : HorizontalAlignment.Left;
                _motionToggle.Padding = _sidebarCollapsed ? new Thickness(0, 8, 0, 7) : new Thickness(14, 10, 10, 10);
                _motionToggle.ToolTip = _reduceMotion ? "Activar animaciones" : "Reducir movimiento";
                System.Windows.Automation.AutomationProperties.SetName(_motionToggle, Convert.ToString(_motionToggle.ToolTip));
            }
            if (!MotionAllowed && _mainScroll != null)
            {
                StopEntrances();
                BeginAnimation(SidebarWidthProperty, null);
                _sidebarBorder.Width = _sidebarCollapsed ? 72 : 248;
                _mainScroll.BeginAnimation(OpacityProperty, null);
                _mainScroll.RenderTransform = Transform.Identity;
                FrameworkElement page = _mainScroll.Content as FrameworkElement;
                if (page != null)
                {
                    page.BeginAnimation(OpacityProperty, null); page.RenderTransform = Transform.Identity;
                    foreach (var card in MotionCards(page).OfType<MaterialCard>()) card.StopMotion();
                }
                _logoBox.BeginAnimation(OpacityProperty, null);
                foreach (var button in _motionButtons)
                {
                    var frame = button.Template.FindName("Frame", button) as Border;
                    var sheen = button.Template.FindName("Sheen", button) as Border;
                    if (frame != null) frame.RenderTransform = Transform.Identity;
                    if (sheen != null) { sheen.Background = null; sheen.BorderBrush = null; }
                    var scale = button.RenderTransform as ScaleTransform;
                    if (scale != null) { scale.BeginAnimation(ScaleTransform.ScaleXProperty, null); scale.BeginAnimation(ScaleTransform.ScaleYProperty, null); }
                }
            }
            UpdateAmbientMotion();
            UpdateActivityPanel();
        }

        private void UpdateAmbientMotion()
        {
            if (_network != null) _network.SetRunning(MotionAllowed && IsVisible && IsActive && WindowState != WindowState.Minimized);
            SetDashboardEdgeMotion(MotionAllowed && IsVisible && IsActive && WindowState != WindowState.Minimized && _activeTab == "hardware");
            SetScanEdgeMotion(MotionAllowed && IsVisible && IsActive && WindowState != WindowState.Minimized && _activeTab == "hardware" && _readTasks.ContainsKey("hardware"));
            SetCeitoMotion(MotionAllowed && IsVisible && IsActive && WindowState != WindowState.Minimized && _activeTab == "ceito" && !_ceitoUnlocked);
            if (_headerEnergy != null) _headerEnergy.SetRunning(false);
            if (_sidebarEnergy != null) _sidebarEnergy.SetRunning(false);
            if (_logoHalo != null)
            {
                _logoHalo.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.BlurRadiusProperty, null);
            }
        }

        // A slow decorative turn needs no more than 30 fps; at 60 it kept the whole window recomposing at full rate.
        private static DoubleAnimation SlowSpin(double seconds)
        {
            var spin = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(seconds)) { RepeatBehavior = RepeatBehavior.Forever };
            Timeline.SetDesiredFrameRate(spin, 30);
            return spin;
        }

        private void SetDashboardEdgeMotion(bool running)
        {
            if (_dashboardEdgeRotation == null) return;
            if (running && !_dashboardEdgeRotation.HasAnimatedProperties)
                _dashboardEdgeRotation.BeginAnimation(RotateTransform.AngleProperty, SlowSpin(9));
            else if (!running && _dashboardEdgeRotation.HasAnimatedProperties)
                _dashboardEdgeRotation.BeginAnimation(RotateTransform.AngleProperty, null);
        }

        private void SetScanEdgeMotion(bool running)
        {
            if (running && !_scanEdgeRotation.HasAnimatedProperties)
                _scanEdgeRotation.BeginAnimation(RotateTransform.AngleProperty, SlowSpin(4));
            else if (!running && _scanEdgeRotation.HasAnimatedProperties)
                _scanEdgeRotation.BeginAnimation(RotateTransform.AngleProperty, null);
        }

        private void AnimateEntrance(FrameworkElement element, double distance, int milliseconds)
        {
            if (!MotionAllowed) return;
            var translate = new TranslateTransform();
            element.RenderTransform = translate;
            translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(distance, 0, TimeSpan.FromMilliseconds(milliseconds)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
            element.BeginAnimation(OpacityProperty, new DoubleAnimation(0.65, 1, TimeSpan.FromMilliseconds(milliseconds)) { FillBehavior = FillBehavior.Stop });
        }

        private void StopEntrances()
        {
            foreach (var element in _motionEntrances)
            {
                element.BeginAnimation(OpacityProperty, null);
                TransformGroup group = element.RenderTransform as TransformGroup;
                if (group != null) foreach (var transform in group.Children)
                {
                    var translate = transform as TranslateTransform;
                    var scale = transform as ScaleTransform;
                    if (translate != null) translate.BeginAnimation(TranslateTransform.YProperty, null);
                    if (scale != null) scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                }
                element.RenderTransform = Transform.Identity;
            }
            _motionEntrances.Clear();
            _revealedCards.Clear();
        }

        private IEnumerable<FrameworkElement> MotionCards(DependencyObject node)
        {
            FrameworkElement element = node as FrameworkElement;
            if (element != null && element.Uid == "motion-card") { yield return element; yield break; }
            foreach (object child in LogicalTreeHelper.GetChildren(node))
            {
                DependencyObject dependency = child as DependencyObject;
                if (dependency != null) foreach (var card in MotionCards(dependency)) yield return card;
            }
        }

        private void AnimatePage(FrameworkElement page)
        {
            if (_mainScroll.Content != page) return;
            StopEntrances();
            if (!MotionAllowed) return;
            _motionPage = page;
            _pageMotionCards = MotionCards(page).ToList();
            RevealVisibleCards(page);
        }

        private void RevealVisibleCards(FrameworkElement page)
        {
            if (!MotionAllowed || page != _mainScroll.Content || page != _motionPage) return;
            int index = 0;
            foreach (var card in _pageMotionCards)
            {
                if (card.Visibility == Visibility.Collapsed || _revealedCards.Contains(card) || card.ActualHeight <= 0) continue;
                Point position;
                try { position = card.TransformToAncestor(_mainScroll).Transform(new Point(0, 0)); }
                catch (InvalidOperationException) { continue; }
                if (position.Y + card.ActualHeight <= 0 || position.Y >= _mainScroll.ActualHeight) continue;
                _revealedCards.Add(card);
                double delay = Math.Min(index++ * 12, 36);
                var translate = new TranslateTransform();
                var scale = new ScaleTransform(1, 1);
                var transforms = new TransformGroup();
                transforms.Children.Add(scale);
                transforms.Children.Add(translate);
                card.RenderTransformOrigin = new Point(0.5, 0.5);
                card.RenderTransform = transforms;
                var slide = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
                slide.KeyFrames.Add(new LinearDoubleKeyFrame(3, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                slide.KeyFrames.Add(new LinearDoubleKeyFrame(3, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delay))));
                slide.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delay + 135)), new CubicEase { EasingMode = EasingMode.EaseOut }));
                translate.BeginAnimation(TranslateTransform.YProperty, slide);
                var reveal = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
                reveal.KeyFrames.Add(new LinearDoubleKeyFrame(0.82, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                reveal.KeyFrames.Add(new LinearDoubleKeyFrame(0.82, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delay))));
                reveal.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delay + 135)), new CubicEase { EasingMode = EasingMode.EaseOut }));
                card.BeginAnimation(OpacityProperty, reveal);
                _motionEntrances.Add(card);
            }
        }

        private void OnMotionButtonLoaded(object sender, RoutedEventArgs args)
        {
            var button = sender as Button;
            if (button == null) return;
            _motionButtons.Add(button);
            if ((bool)button.GetValue(MotionWiredProperty)) return;
            button.SetValue(MotionWiredProperty, true);
            button.Unloaded += (s, e) => _motionButtons.Remove(button);
            button.MouseEnter += (s, e) => PlayButtonSheen(button);
            button.GotKeyboardFocus += (s, e) => PlayButtonSheen(button);
            button.LostKeyboardFocus += (s, e) =>
            {
                var frame = button.Template.FindName("Frame", button) as Border;
                if (frame != null) frame.RenderTransform = Transform.Identity;
            };
            button.MouseLeave += (s, e) =>
            {
                var frame = button.Template.FindName("Frame", button) as Border;
                var move = frame == null ? null : frame.RenderTransform as TranslateTransform;
                if (move != null) move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(move.Y, 0, TimeSpan.FromMilliseconds(MotionAllowed ? 180 : 0)));
            };
        }

        private void PlayButtonSheen(Button button)
        {
            if (!MotionAllowed || !button.IsEnabled) return;
            var sheen = button.Template.FindName("Sheen", button) as Border;
            var frame = button.Template.FindName("Frame", button) as Border;
            if (sheen == null || frame == null) return;
            var brush = new LinearGradientBrush();
            brush.StartPoint = new Point(0, 0);
            brush.EndPoint = new Point(1, 0.4);
            brush.GradientStops.Add(new GradientStop(Colors.Transparent, 0.40));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(_isDark ? (byte)230 : (byte)90, 231, 237, 247), 0.5));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(_isDark ? (byte)145 : (byte)75, 245, 86, 78), 0.55));
            brush.GradientStops.Add(new GradientStop(Colors.Transparent, 0.60));
            var sweep = new TranslateTransform();
            brush.RelativeTransform = sweep;
            sheen.Background = null;
            sheen.BorderBrush = brush;
            var reflection = new DoubleAnimation(-1.3, 1.3, TimeSpan.FromMilliseconds(520)) { FillBehavior = FillBehavior.Stop };
            reflection.Completed += (s, e) => { if (sheen.BorderBrush == brush) sheen.BorderBrush = null; };
            sweep.BeginAnimation(TranslateTransform.XProperty, reflection);
            var lift = frame.RenderTransform as TranslateTransform;
            if (lift == null) { lift = new TranslateTransform(); frame.RenderTransform = lift; }
            lift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(lift.Y, -1, TimeSpan.FromMilliseconds(160)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }

        private void PlaySearchSheen(Border edge)
        {
            if (!MotionAllowed) return;
            var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, .25) };
            brush.GradientStops.Add(new GradientStop(Colors.Transparent, .38));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(_isDark ? (byte)220 : (byte)130, 225, 67, 73), .5));
            brush.GradientStops.Add(new GradientStop(Colors.Transparent, .62));
            var sweep = new TranslateTransform(); brush.RelativeTransform = sweep; edge.BorderBrush = brush;
            var animation = new DoubleAnimation(-1.3, 1.3, TimeSpan.FromMilliseconds(650)) { FillBehavior = FillBehavior.Stop };
            animation.Completed += (s, e) => { if (edge.BorderBrush == brush) edge.BorderBrush = Brushes.Transparent; };
            sweep.BeginAnimation(TranslateTransform.XProperty, animation);
        }

        private void ToggleSidebar()
        {
            double from = _sidebarBorder.Width;
            _sidebarCollapsed = !_sidebarCollapsed;
            ApplySidebarState();
            SaveUiPreferences();
            double target = _sidebarCollapsed ? 72 : 248;
            SetValue(SidebarWidthProperty, target);
            if (MotionAllowed)
                BeginAnimation(SidebarWidthProperty, new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(210)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
        }

        private void ApplySidebarState()
        {
            _sidebarColumn.Width = new GridLength(72);
            _sidebarBorder.Width = _sidebarCollapsed ? 72 : 248;
            if (_sidebarScrim != null) _sidebarScrim.Visibility = _sidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;
            _sidebarBorder.Padding = new Thickness(_sidebarCollapsed ? 6 : 14, 16, _sidebarCollapsed ? 6 : 14, 16);
            foreach (var element in _expandedSidebarElements) element.Visibility = _sidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;
            foreach (var button in _navButtons.Values)
            {
                var item = (string[])button.Tag;
                button.Content = NavContent(item[0], item[1], _sidebarCollapsed);
                button.HorizontalContentAlignment = _sidebarCollapsed ? HorizontalAlignment.Center : HorizontalAlignment.Left;
                button.Padding = _sidebarCollapsed ? new Thickness(0, 8, 0, 7) : new Thickness(14, 10, 10, 10);
            }
            _sidebarToggle.Content = NavContent("", _sidebarCollapsed ? null : "Contraer menú", _sidebarCollapsed);
            _sidebarToggle.HorizontalContentAlignment = _sidebarCollapsed ? HorizontalAlignment.Center : HorizontalAlignment.Left;
            _sidebarToggle.Padding = _sidebarCollapsed ? new Thickness(0, 10, 0, 10) : new Thickness(14, 10, 10, 10);
            _sidebarToggle.ToolTip = _sidebarCollapsed ? "Expandir menú" : "Contraer menú";
            System.Windows.Automation.AutomationProperties.SetName(_sidebarToggle, Convert.ToString(_sidebarToggle.ToolTip));
            UpdateMotionPreference();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        private struct MinMaxInfo
        {
            public NativePoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo
        {
            public int Size;
            public NativeRect Monitor, Work;
            public int Flags;
        }
        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

        private static Rect CalculateMaximizedBounds(Rect monitor, Rect work)
        {
            return new Rect(work.Left - monitor.Left, work.Top - monitor.Top, work.Width, work.Height);
        }

        private IntPtr WindowBoundsHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_GETMINMAXINFO = 0x0024;
            if (message != WM_GETMINMAXINFO) return IntPtr.Zero;
            MonitorInfo monitor = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
            if (!GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref monitor)) return IntPtr.Zero;
            Rect bounds = CalculateMaximizedBounds(
                new Rect(monitor.Monitor.Left, monitor.Monitor.Top, monitor.Monitor.Right - monitor.Monitor.Left, monitor.Monitor.Bottom - monitor.Monitor.Top),
                new Rect(monitor.Work.Left, monitor.Work.Top, monitor.Work.Right - monitor.Work.Left, monitor.Work.Bottom - monitor.Work.Top));
            MinMaxInfo info = (MinMaxInfo)Marshal.PtrToStructure(lParam, typeof(MinMaxInfo));
            info.MaxPosition.X = (int)bounds.X;
            info.MaxPosition.Y = (int)bounds.Y;
            info.MaxSize.X = (int)bounds.Width;
            info.MaxSize.Y = (int)bounds.Height;
            Marshal.StructureToPtr(info, lParam, false);
            handled = true;
            return IntPtr.Zero;
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

        internal void RelaunchAsAdministrator()
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
        private string ReadEmbeddedResource(string resourceName)
        {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
            {
                if (stream == null) throw new InvalidDataException("Falta el catálogo embebido " + resourceName);
                using (var reader = new StreamReader(stream, Encoding.UTF8)) return reader.ReadToEnd();
            }
        }

        private void LoadEmbeddedCatalogs()
        {
            try
            {
                string tweaksRaw = ReadEmbeddedResource("tweaks.json");
                var tweaksRoot = _json.Deserialize<Dictionary<string, object>>(tweaksRaw);
                _tweaks = ConvertToDictList(tweaksRoot["tweaks"]);

                string appsRaw = ReadEmbeddedResource("apps.json");
                var appsRoot = _json.Deserialize<Dictionary<string, object>>(appsRaw);
                _apps = ConvertToDictList(appsRoot["apps"]);

                _appCategories.Clear();
                _appCategories.Add("Todas");
                if (appsRoot.ContainsKey("categories") && appsRoot["categories"] is IEnumerable)
                {
                    foreach (object c in (IEnumerable)appsRoot["categories"])
                        _appCategories.Add(Convert.ToString(c));
                }

                string verRaw = ReadEmbeddedResource("version.json");
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
        private void InitializeActivityPanel()
        {
            _scanEdgeBrush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1), RelativeTransform = _scanEdgeRotation };
            _scanEdgeBrush.GradientStops.Add(new GradientStop(ColorHex("#4E101C"), 0));
            _scanEdgeBrush.GradientStops.Add(new GradientStop(ColorHex("#FF665D"), 0.25));
            _scanEdgeBrush.GradientStops.Add(new GradientStop(ColorHex("#8D1529"), 0.53));
            _scanEdgeBrush.GradientStops.Add(new GradientStop(ColorHex("#FFC0A4"), 0.78));
            _scanEdgeBrush.GradientStops.Add(new GradientStop(ColorHex("#4E101C"), 1));
            StackPanel body = new StackPanel();
            _activityTitle = new TextBlock { Foreground = TextPrimary, FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
            _activityDetail = new TextBlock { Foreground = TextSecondary, FontSize = 12, Margin = new Thickness(0, 8, 0, 12), TextWrapping = TextWrapping.Wrap, MaxHeight = 100 };
            _activityProgress = new ProgressBar { Height = 4, Maximum = 100, Foreground = CyberCyanBrush, Background = BgElevated, BorderThickness = new Thickness(0) };
            StackPanel actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
            _activityRetry = CreateStyledButton("Intentar de nuevo", true);
            _activityDismiss = CreateStyledButton("Cerrar", false);
            _activityDismiss.Margin = new Thickness(8, 0, 0, 0);
            _activityRetry.Click += async (s, e) =>
            {
                string key = Convert.ToString(_activityRetry.Tag);
                _readErrors.Remove(key);
                await LoadModuleAsync(key);
            };
            _activityDismiss.Click += (s, e) =>
            {
                _readErrors.Remove(Convert.ToString(_activityDismiss.Tag));
                UpdateActivityPanel();
            };
            actions.Children.Add(_activityRetry);
            actions.Children.Add(_activityDismiss);
            body.Children.Add(_activityTitle);
            body.Children.Add(_activityDetail);
            body.Children.Add(_activityProgress);
            body.Children.Add(actions);
            _activityPanel = new Border
            {
                Width = 370, Padding = new Thickness(20), Margin = new Thickness(24),
                Background = BgSidebar, BorderBrush = CyberCyanBrush, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(16),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
                Visibility = Visibility.Collapsed, Child = body
            };
            System.Windows.Automation.AutomationProperties.SetLiveSetting(_activityPanel, System.Windows.Automation.AutomationLiveSetting.Polite);
            Grid.SetRow(_activityPanel, 1);
            Panel.SetZIndex(_activityPanel, 10);
            _rootGrid.Children.Add(_activityPanel);
        }

        private Task LoadModuleAsync(string key)
        {
            switch (key)
            {
                case "hardware": return RefreshHardwareNativeAsync();
                case "tweaks": return RefreshTweaksStateNativeAsync();
                case "apps": return RefreshInstalledAppsNativeAsync();
                case "monitor": return RefreshLiveScriptsNativeAsync();
                case "safety": return RefreshRestorePointsNativeAsync();
                default: return Task.FromResult(0);
            }
        }

        private string CurrentModuleKey()
        {
            return _activeTab == "winutil" || _activeTab == "ceito" ? "tweaks" : _activeTab;
        }

        private Task RunReadAsync(string key, string label, Func<Task> read, bool background = false)
        {
            Task pending;
            if (_readTasks.TryGetValue(key, out pending)) return pending;
            _readErrors.Remove(key);
            _readLabels[key] = label;
            _readProgress.Remove(key);
            if (key == "hardware") { _readProgress[key] = 0; _readLabels[key] = "Consultando identidad del equipo y otras piezas en paralelo"; }
            Task task = CompleteReadAsync(key, read, background);
            _readTasks[key] = task;
            if (!background) UpdateActivityPanel();
            return task;
        }

        private async Task CompleteReadAsync(string key, Func<Task> read, bool background)
        {
            await Task.Yield();
            try
            {
                await read();
                _loadedModules.Add(key);
                if (key != "hardware" && !background) SetStatus("Lectura completada: " + _readLabels[key], "Listo");
                if (key == "hardware" && _activeTab == "hardware" && _activityPanel.Visibility == Visibility.Visible && MotionAllowed && IsVisible)
                {
                    _activityPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(180)) { FillBehavior = FillBehavior.Stop });
                    await Task.Delay(180);
                }
            }
            catch (Exception ex)
            {
                background = false;
                _readErrors[key] = ex.Message;
                AppendLog(_readLabels[key] + ": " + ex.Message, "ERROR");
                SetStatus("No se pudo completar " + _readLabels[key].ToLowerInvariant(), "Reintento disponible");
            }
            finally
            {
                _readTasks.Remove(key);
                // A changed process list only rebuilds its own card: the activity log keeps its rows and scroll position.
                if (CurrentModuleKey() == key && key == "monitor" && _monitorProcessPanel != null) { if (!background || _monitorProcessesChanged) RebuildProcessList(); }
                else if (CurrentModuleKey() == key && (!background || key != "monitor" || _monitorProcessesChanged)) RenderCurrentTab();
                if (!background) UpdateActivityPanel();
            }
        }

        private void ReportReadProgress(string key, int completed, int total, string detail)
        {
            RunOnUi(() =>
            {
                _readProgress[key] = total > 0 ? Math.Min(100, completed * 100 / total) : -1;
                string[] nextHardware = { "placa base y seguridad", "procesador", "memoria y sistema", "almacenamiento", "gráficos", "batería", "red e inventario" };
                _readLabels[key] = key == "hardware" ? "Etapa completada: " + detail + (completed > 0 && completed <= nextHardware.Length ? "\nSiguiente etapa del informe: " + nextHardware[completed - 1] : "") : detail;
                UpdateActivityPanel();
            });
        }

        private void UpdateActivityPanel()
        {
            if (_activityPanel == null) return;
            string key = CurrentModuleKey();
            if (!_readTasks.ContainsKey(key) && !_readErrors.ContainsKey(key))
                key = _readTasks.Keys.FirstOrDefault();
            bool failed = key != null && _readErrors.ContainsKey(key);
            bool busy = key != null && _readTasks.ContainsKey(key);
            bool scan = key == "hardware" && _activeTab == "hardware" && busy && !failed;
            bool entering = _activityPanel.Visibility != Visibility.Visible && (busy || failed);
            _activityPanel.Visibility = busy || failed ? Visibility.Visible : Visibility.Collapsed;
            _activityPanel.HorizontalAlignment = scan ? HorizontalAlignment.Center : HorizontalAlignment.Right;
            _activityPanel.VerticalAlignment = scan ? VerticalAlignment.Center : VerticalAlignment.Bottom;
            _activityPanel.BorderThickness = new Thickness(scan ? 2.5 : 1);
            _activityPanel.Width = scan ? 440 : 370;
            if (entering && scan && MotionAllowed) _activityPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)) { FillBehavior = FillBehavior.Stop });
            SetScanEdgeMotion(scan && MotionAllowed && IsVisible && IsActive && WindowState != WindowState.Minimized);
            if (!busy && !failed) return;
            _activityTitle.Text = failed ? "No se pudo completar la lectura" : scan ? "ESCANEANDO TU EQUIPO" : "LECTURA EN CURSO";
            _activityDetail.Text = failed ? _readLabels[key] + "\n" + _readErrors[key] : _readLabels[key];
            _activityPanel.BorderBrush = failed ? RiskHighText : scan ? (Brush)_scanEdgeBrush : CyberCyanBrush;
            _activityProgress.Visibility = failed ? Visibility.Collapsed : Visibility.Visible;
            int progress;
            bool measured = _readProgress.TryGetValue(key, out progress) && progress >= 0;
            _activityProgress.IsIndeterminate = !measured && MotionAllowed;
            _activityProgress.Value = measured ? progress : 0;
            if (!failed && measured) _activityDetail.Text += " · " + progress + "% de etapas completadas";
            _activityRetry.Visibility = failed ? Visibility.Visible : Visibility.Collapsed;
            _activityDismiss.Visibility = failed ? Visibility.Visible : Visibility.Collapsed;
            _activityRetry.Tag = key;
            _activityDismiss.Tag = key;
        }

        private Task RefreshHardwareNativeAsync()
        {
            return RunReadAsync("hardware", "Auditoría de hardware", RefreshHardwareCoreAsync);
        }

        private async Task RefreshHardwareCoreAsync()
        {
            Stopwatch sw = Stopwatch.StartNew();
            SetStatus("Escaneando hardware por canal nativo WMI/SMBIOS...", "Auditoría en curso");

            var report = await Task.Run(() =>
            {
                var result = new Dictionary<string, object>();
                // Las consultas independientes se solapan para no sumar sus latencias al arranque.
                var cpuRead = Task.Run(() => QueryFirstWmi("root\\CIMV2", "SELECT Name, Architecture, SocketDesignation, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, CurrentClockSpeed, L2CacheSize, L3CacheSize, VirtualizationFirmwareEnabled, LoadPercentage FROM Win32_Processor", true));
                var tpmRead = Task.Run(() => QueryFirstWmi("root\\CIMV2\\Security\\MicrosoftTpm", "SELECT SpecVersion FROM Win32_Tpm"));
                var diskRead = Task.Run(() => QueryAllWmi("root\\Microsoft\\Windows\\Storage", "SELECT DeviceId, FriendlyName, SerialNumber, MediaType, BusType, Size, HealthStatus FROM MSFT_PhysicalDisk", false, 15));
                // Which physical disk holds Windows: SSD-only tweaks must look at that disk, not at "any" disk.
                var systemDiskRead = Task.Run(() => QueryAllWmi("root\\Microsoft\\Windows\\Storage", "SELECT DiskNumber FROM MSFT_Partition WHERE DriveLetter = '" + Path.GetPathRoot(Environment.SystemDirectory).Substring(0, 1) + "'", false, 15));

                var enclosure = QueryFirstWmi("root\\CIMV2", "SELECT ChassisTypes, Manufacturer, SerialNumber FROM Win32_SystemEnclosure");
                var bios = QueryFirstWmi("root\\CIMV2", "SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate, SerialNumber FROM Win32_BIOS");
                var csProd = QueryFirstWmi("root\\CIMV2", "SELECT IdentifyingNumber, UUID, Version FROM Win32_ComputerSystemProduct");
                var cs = QueryFirstWmi("root\\CIMV2", "SELECT Manufacturer, Model, SystemSKUNumber, TotalPhysicalMemory FROM Win32_ComputerSystem", true);
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
                if (cs.Count == 0) throw new InvalidOperationException("Windows no devolvió la identidad del equipo. Comprueba el servicio WMI y vuelve a intentar.");
                ReportReadProgress("hardware", 1, 8, "Identidad y chasis · 1/8");

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
                string secureBootCerts = ReadSecureBootCerts(secureBoot.StartsWith("Activo"));

                string tpmVersion = "No Detectado";
                try
                {
                    var tpmWmi = tpmRead.GetAwaiter().GetResult();
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
                    { "secureBootCerts", secureBootCerts },
                    { "tpmVersion", tpmVersion }
                };

                var cpu = cpuRead.GetAwaiter().GetResult();
                if (cpu.Count == 0) throw new InvalidOperationException("No se pudo leer el procesador mediante WMI. Comprueba el servicio WMI y vuelve a intentar.");
                ReportReadProgress("hardware", 2, 8, "Placa base y seguridad · 2/8");
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
                ReportReadProgress("hardware", 3, 8, "Procesador · 3/8");
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
                ReportReadProgress("hardware", 4, 8, "Memoria y sistema · 4/8");
                try
                {
                    DriveInfo cDrive = new DriveInfo("C");
                    if (cDrive.IsReady) freeCGb = Math.Round(cDrive.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0), 2);
                }
                catch { }

                var storageList = new List<Dictionary<string, object>>();
                var msftDisks = diskRead.GetAwaiter().GetResult();
                var systemDisk = systemDiskRead.GetAwaiter().GetResult();
                string systemDiskId = systemDisk.Count > 0 ? GetWmiStr(systemDisk[0], "DiskNumber", "") : "";
                if (msftDisks.Count > 0)
                {
                    foreach (var md in msftDisks)
                    {
                        // Windows' own enumerations; an unreported value stays unknown instead of becoming "SSD"/"NVMe".
                        int mType = GetWmiInt(md, "MediaType", 0);
                        int bType = GetWmiInt(md, "BusType", 0);
                        int hStat = GetWmiInt(md, "HealthStatus", 5);
                        string mediaStr = mType == 4 ? "SSD" : mType == 3 ? "HDD" : mType == 5 ? "SCM" : "Sin especificar";
                        string busStr = bType == 17 ? "NVMe" : bType == 11 ? "SATA" : bType == 7 ? "USB" : bType == 8 ? "RAID" : bType == 10 ? "SAS" : bType == 3 ? "ATA" : bType == 12 ? "SD" : bType == 15 ? "Virtual" : bType == 16 ? "Espacios de almacenamiento" : bType == 19 ? "UFS" : "Otro (" + bType + ")";
                        string healthStr = hStat == 0 ? "Correcto" : hStat == 1 ? "Advertencia" : hStat == 2 ? "Con fallos" : "Desconocido";
                        bool isSystem = systemDiskId.Length > 0 && GetWmiStr(md, "DeviceId", "") == systemDiskId;

                        storageList.Add(new Dictionary<string, object>
                        {
                            { "friendlyName", GetWmiStr(md, "FriendlyName", "Disco") },
                            { "serialNumber", GetWmiStr(md, "SerialNumber", "N/A").Trim() },
                            { "mediaType", mediaStr },
                            { "busType", busStr },
                            { "isSystemDisk", isSystem },
                            { "partitionStyle", bootMode == "UEFI" ? "GPT" : "MBR" },
                            { "sizeGb", Math.Round(GetWmiLong(md, "Size", 0) / (1024.0 * 1024.0 * 1024.0), 2) },
                            { "freeGb", isSystem ? (object)freeCGb : "—" },
                            { "healthStatus", healthStr },
                            { "healthPercent", "—" }
                        });
                    }
                    // The Windows disk is listed first: the dashboard shows it as the main unit.
                    storageList = storageList.OrderBy(d => GetBool(d, "isSystemDisk") ? 0 : 1).ToList();
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
                            { "mediaType", "Sin especificar" },
                            { "busType", GetWmiStr(wd, "InterfaceType", "Sin especificar") },
                            { "partitionStyle", bootMode == "UEFI" ? "GPT" : "MBR" },
                            { "sizeGb", Math.Round(GetWmiLong(wd, "Size", 0) / (1024.0 * 1024.0 * 1024.0), 2) },
                            { "freeGb", freeCGb },
                            { "healthStatus", GetWmiStr(wd, "Status", "Desconocido") },
                            { "healthPercent", "—" }
                        });
                    }
                }
                result["storage"] = storageList;
                ReportReadProgress("hardware", 5, 8, "Almacenamiento · 5/8");

                result["gpus"] = ReadGpus();
                ReportReadProgress("hardware", 6, 8, "Gráficos · 6/8");

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
                ReportReadProgress("hardware", 7, 8, "Batería · 7/8");

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
                    { "caption", GetWmiStr(osInfo, "Caption", "No detectado") },
                    { "displayVersion", displayVer },
                    { "buildNumber", GetWmiStr(osInfo, "BuildNumber", Convert.ToString(Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "CurrentBuildNumber", "No detectado"))) },
                    { "installDate", FormatWmiDate(GetWmiStr(osInfo, "InstallDate", "")) },
                    { "uptime", uptimeStr },
                    { "partialProductKey", partialKey },
                    { "adapters", netAdapters }
                };

                result["capabilities"] = BuildCapabilities(result);
                ReportReadProgress("hardware", 8, 8, "Red e inventario completados · 8/8");
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

        }

        private Task RefreshTweaksStateNativeAsync()
        {
            return RunReadAsync("tweaks", "Estado de optimizaciones", RefreshTweaksStateCoreAsync);
        }

        private async Task RefreshTweaksStateCoreAsync()
        {
            if (_hardware == null)
            {
                await RefreshHardwareNativeAsync();
                if (_hardware == null) throw new InvalidOperationException("Primero debe completarse la identificación del equipo para seleccionar optimizaciones compatibles.");
            }
            await Task.Run(() =>
            {
                var map = new Dictionary<string, bool>();
                var partial = new HashSet<string>();
                string scheme = null; // powercfg runs once per refresh, only if a tweak needs it
                Func<string> activeScheme = () => scheme ?? (scheme = RunSystemToolDirect("powercfg.exe", "/getactivescheme"));
                foreach (var t in _tweaks)
                {
                    string id = GetStr(t, "id");
                    if (string.IsNullOrEmpty(id)) continue;
                    if (IsPreference(t)) { map[id] = PreferenceOn(t, activeScheme); continue; }
                    bool full = IsTweakActive(t, activeScheme);
                    map[id] = full || IsTweakTouched(t, activeScheme);
                    if (map[id] && !full) partial.Add(id);
                }
                _tweakStates = map;
                _tweakPartial = partial;
            });

        }

        private Task RefreshInstalledAppsNativeAsync()
        {
            return RunReadAsync("apps", "Inventario de aplicaciones", RefreshInstalledAppsCoreAsync);
        }

        private async Task RefreshInstalledAppsCoreAsync()
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

        }

        private Task RefreshLiveScriptsNativeAsync(bool background = false)
        {
            return RunReadAsync("monitor", "Monitor de procesos", RefreshLiveScriptsCoreAsync, background);
        }

        private async Task RefreshLiveScriptsCoreAsync()
        {
            await Task.Run(() =>
            {
                int myPid = Process.GetCurrentProcess().Id;
                var list = new List<Dictionary<string, object>>();
                var rows = QueryAllWmi(
                    "root\\CIMV2",
                    "SELECT ProcessId, ParentProcessId, Name, ExecutablePath, CommandLine, WorkingSetSize FROM Win32_Process WHERE Name='powershell.exe' OR Name='pwsh.exe' OR Name='cmd.exe' OR Name='wscript.exe' OR Name='cscript.exe' OR Name='mshta.exe'", true
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
                string previous = string.Join("\n", _liveScripts.OrderBy(p => GetInt(p, "pid")).Select(p => GetStr(p, "pid") + "|" + GetStr(p, "name") + "|" + GetStr(p, "commandLine")));
                string current = string.Join("\n", list.OrderBy(p => GetInt(p, "pid")).Select(p => GetStr(p, "pid") + "|" + GetStr(p, "name") + "|" + GetStr(p, "commandLine")));
                _monitorProcessesChanged = previous != current;
                _liveScripts = list;
            });
            if (!_monitorProcessesChanged) UpdateMonitorProcessMemoryLabels();

        }

        private void UpdateMonitorProcessMemoryLabels()
        {
            foreach (var process in _liveScripts)
            {
                TextBlock label;
                if (_monitorProcessMemoryLabels.TryGetValue(GetInt(process, "pid"), out label))
                    label.Text = ProcessMetaText(process);
            }
        }

        private string ProcessMetaText(Dictionary<string, object> process)
        {
            return "PID " + GetStr(process, "pid") + "  ·  proceso padre " + GetStr(process, "parentPid") + "  ·  " + GetStr(process, "memoryMb") + " MB";
        }

        private Task RefreshRestorePointsNativeAsync()
        {
            return RunReadAsync("safety", "Puntos de restauración", RefreshRestorePointsCoreAsync);
        }

        private async Task RefreshRestorePointsCoreAsync()
        {
            await Task.Run(() =>
            {
                var list = new List<Dictionary<string, object>>();
                var rows = ListRestorePoints();
                foreach (var r in rows)
                {
                    list.Add(new Dictionary<string, object>
                    {
                        { "sequenceNumber", GetWmiLong(r, "SequenceNumber", 1) },
                        { "description", GetWmiStr(r, "Description", "Punto de Restauración") },
                        { "creationTime", FormatWmiDate(GetWmiStr(r, "CreationTime", "")) }
                    });
                }
                _restorePoints = list;
                if (_restorePointRecord != null) CheckRecordedRestorePoint(); // the status line below must not trust the list alone
            });

        }

        // ====================================================================
        // EJECUTOR NATIVO DE TWEAKS (REGISTRO / SERVICIOS / POWERCFG SIN PS1)
        // ====================================================================
        // Fixed package identities and verified Store IDs: neither the catalog nor an OTA can expand this set.
        private static readonly Dictionary<string, string> AppxStoreIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Microsoft.WindowsFeedbackHub", "9NBLGGH4R32N" },
            { "Microsoft.GetHelp", "9PKDZBMV1H3T" },
            { "Microsoft.MicrosoftOfficeHub", "9WZDNCRD29V9" },
            { "Clipchamp.Clipchamp", "9P1J8S7CCWWT" },
            { "Microsoft.WindowsAlarms", "9WZDNCRFJ3PR" },
            { "MicrosoftCorporationII.QuickAssist", "9P7BP5VNWKX5" },
            { "Microsoft.WindowsSoundRecorder", "9WZDNCRFHWKN" },
            { "Microsoft.MicrosoftStickyNotes", "9NBLGGH4QGHW" },
            { "Microsoft.Todos", "9NBLGGH5R558" },
            { "Microsoft.MicrosoftSolitaireCollection", "9WZDNCRFHWD2" },
            { "Microsoft.PowerAutomateDesktop", "9NFTCH6J7FHV" },
            { "Microsoft.Windows.DevHome", "9N8MHTPHNGVV" },
            { "Microsoft.BingWeather", "9WZDNCRFJ3Q2" },
            { "Microsoft.StartExperiencesApp", "9PC1H9VN18CM" },
            { "Microsoft.BingNews", "9WZDNCRFHVFW" },
            { "Microsoft.Copilot", "9NHT9RB2F4HD" },
            { "Microsoft.BingSearch", "9NZBF4GT040C" }
        };

        internal static bool IsAllowedAppxPackage(string name)
        {
            if (string.IsNullOrEmpty(name) || !Regex.IsMatch(name, @"\A[A-Za-z0-9]+(\.[A-Za-z0-9]+)+\z")) return false;
            var denied = new[] { "Microsoft.WindowsStore", "Microsoft.DesktopAppInstaller", "Microsoft.StorePurchaseApp", "Microsoft.WindowsCalculator", "Microsoft.GamingApp", "Microsoft.SecHealthUI", "Microsoft.Windows.ShellExperienceHost", "Microsoft.Windows.StartMenuExperienceHost", "Microsoft.Windows.Photos", "Microsoft.WindowsNotepad", "Microsoft.Paint", "Microsoft.ScreenSketch", "Microsoft.WindowsTerminal" };
            var prefixes = new[] { "Microsoft.Xbox", "MicrosoftWindows.Client.", "Microsoft.UI.Xaml.", "Microsoft.VCLibs.", "Microsoft.NET.", "Microsoft.WindowsAppRuntime." };
            return !denied.Contains(name, StringComparer.OrdinalIgnoreCase) && !prefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)) && AppxStoreIds.ContainsKey(name);
        }

        private string RunAppxTool(string tool, string command)
        {
            string exe = tool == "powershell" ? Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe") : ResolveWingetExecutablePath();
            if (string.IsNullOrEmpty(exe)) throw new InvalidOperationException("WinGet no está disponible; reinstálala desde Microsoft Store o instala el Instalador de aplicaciones y reintenta.");
            string args = tool == "powershell" ? "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(command)) : command;
            int code;
            string output = RunSystemToolDirect(exe, args, out code, 300000);
            return tool != "powershell" && code == -1978335189 ? output : RequireSuccessfulToolResult(exe, code, output);
        }

        private Dictionary<string, object> ReadAppxPackage(string name, bool remove)
        {
            if (!IsAllowedAppxPackage(name)) throw new InvalidOperationException("Paquete APPX protegido o no permitido: " + name);
            string script = "$ErrorActionPreference = 'Stop'; $ProgressPreference = 'SilentlyContinue'; $name = '" + name + "'; "
                + "$pkgs = @(Get-AppxPackage -Name $name -AllUsers -PackageTypeFilter Bundle,Main -ErrorAction Stop | Where-Object { $_.Name -eq $name } | Sort-Object -Property PackageFullName -Unique); "
                + "$provs = @(Get-AppxProvisionedPackage -Online -ErrorAction Stop | Where-Object { $_.DisplayName -eq $name } | Sort-Object -Property PackageName -Unique); ";
            script += remove ? @"
$removed = $false; $errors = @();
foreach ($pkg in ($pkgs | Sort-Object -Property IsBundle -Descending)) {
    try {
        if (@(Get-AppxPackage -Name $name -AllUsers -PackageTypeFilter Bundle,Main -ErrorAction Stop | Where-Object { $_.PackageFullName -eq $pkg.PackageFullName }).Count -gt 0) {
            Remove-AppxPackage -Package $pkg.PackageFullName -AllUsers -ErrorAction Stop;
            $removed = $true;
        }
    } catch { $errors += $_.Exception.Message }
}
foreach ($prov in $provs) {
    try {
        Remove-AppxProvisionedPackage -Online -PackageName $prov.PackageName -ErrorAction Stop | Out-Null;
        $removed = $true;
    } catch { $errors += $_.Exception.Message }
}
@{ removed = $removed; error = ($errors -join '; ') } | ConvertTo-Json -Compress
" : @"
$inventory = @(
    foreach ($pkg in $pkgs) {
        'package:' + $pkg.PackageFullName
        foreach ($user in $pkg.PackageUserInformation) {
            if ($user.InstallState -eq 'Installed') { 'registered:' + $pkg.PackageFullName + ':' + [string]$user.UserSecurityId }
        }
    }
    foreach ($prov in $provs) { 'provisioned:' + $prov.PackageName }
)
@{ installed = ($pkgs.Count -gt 0); provisioned = ($provs.Count -gt 0); inventory = (($inventory | Sort-Object) -join ';') } | ConvertTo-Json -Compress
";
            var result = _json.Deserialize<Dictionary<string, object>>(_appxTool("powershell", script));
            if (result == null || (remove ? !result.ContainsKey("removed") || !result.ContainsKey("error") : !result.ContainsKey("installed") || !result.ContainsKey("provisioned") || !result.ContainsKey("inventory")))
                throw new InvalidOperationException("Windows no informó el estado del paquete " + name);
            return result;
        }

        private void FinishAppxRemoval(string name, bool removed)
        {
            // The candidate was saved before the write. Keep only confirmed removals; pending candidates let an
            // interrupted process compare its saved presence with Windows before deciding whether to reinstall.
            var entry = _changes.LastOrDefault();
            if (entry == null) return;
            var restore = ConvertToDictList(entry["restoreOps"]);
            var candidate = restore.FirstOrDefault(o => GetStr(o, "type") == "appx_install" && GetStr(o, "name").Equals(name, StringComparison.OrdinalIgnoreCase) && GetBool(o, "pending"));
            if (candidate == null) return;
            if (removed) candidate["pending"] = false;
            else restore.Remove(candidate);
            entry["restoreOps"] = restore;
            SaveJournal();
        }

        private void RunAppxOperation(Dictionary<string, object> op)
        {
            string name = GetStr(op, "name"), type = GetStr(op, "type");
            if (!IsAllowedAppxPackage(name)) throw new InvalidOperationException("Paquete APPX protegido o no permitido: " + name);
            if (type == "appx_remove")
            {
                var before = ReadAppxPackage(name, false);
                if (!GetBool(before, "installed") && !GetBool(before, "provisioned")) { FinishAppxRemoval(name, false); return; }
                var entry = _changes.LastOrDefault();
                if (entry == null || !ConvertToDictList(entry["restoreOps"]).Any(o => GetStr(o, "type") == "appx_install" && GetStr(o, "name").Equals(name, StringComparison.OrdinalIgnoreCase) && GetBool(o, "pending")))
                    throw new InvalidOperationException("No se guardó la recuperación de " + name + "; no se quitó la app. Reintenta el ajuste.");
                var result = ReadAppxPackage(name, true);
                bool removed = GetBool(result, "removed");
                if (!removed && GetStr(result, "error").Length > 0)
                    removed = GetStr(before, "inventory").Split(';').Where(s => s.Length > 0).Except(GetStr(ReadAppxPackage(name, false), "inventory").Split(';')).Any();
                FinishAppxRemoval(name, removed);
                if (GetStr(result, "error").Length > 0) throw new InvalidOperationException(GetStr(result, "error"));
                var after = ReadAppxPackage(name, false);
                if (GetBool(after, "installed") || GetBool(after, "provisioned")) throw new InvalidOperationException("Windows todavía registra o aprovisiona " + name);
                return;
            }
            if (GetBool(op, "pending"))
            {
                var now = ReadAppxPackage(name, false);
                if (op.ContainsKey("inventory") && !GetStr(op, "inventory").Split(';').Where(s => s.Length > 0).Except(GetStr(now, "inventory").Split(';')).Any()) return;
            }
            string storeId = AppxStoreIds[name];
            if (string.IsNullOrEmpty(storeId)) { AppendLog(name + ": reinstálala desde Microsoft Store (sin ID confirmado).", "warn", "Optimización"); return; }
            _appxTool("winget", "install --id \"" + storeId + "\" --source msstore --exact --silent --accept-package-agreements --accept-source-agreements --disable-interactivity");
            if (!GetBool(ReadAppxPackage(name, false), "installed")) throw new InvalidOperationException("Microsoft Store no confirmó la reinstalación de " + name);
        }

        private async Task ExecuteTweakAsync(Dictionary<string, object> tweak, bool apply)
        {
            string id = GetStr(tweak, "id");
            string title = GetStr(tweak, "title");
            string shortTitle = GetStr(tweak, "shortTitle");
            if (string.IsNullOrEmpty(shortTitle)) shortTitle = title;
            string opsKey = apply ? "applyOps" : "undoOps";
            var opsList = tweak.ContainsKey(opsKey) ? ConvertToDictList(tweak[opsKey]) : new List<Dictionary<string, object>>();
            // Reverting prefers the values recorded before this tweak ran; the catalog defaults are only a fallback.
            var recorded = apply ? new List<Dictionary<string, object>>() : _changes.Where(c => GetStr(c, "id") == id).ToList();
            string unmet = apply && !id.StartsWith(StartupChangePrefix) ? UnmetRequirement(tweak) : null;
            if (unmet != null) throw new InvalidOperationException("«" + shortTitle + "» no es compatible con este equipo: " + unmet + ". No se cambió nada.");
            if (apply && IsFrozen(tweak)) throw new InvalidOperationException("Ceito está en pausa: sus ajustes solo se pueden revertir. No se cambió nada.");
            var transfers = new List<Action>();
            if (recorded.Count > 0) opsList = StartServicesLast(RestoreOpsFor(recorded, out transfers));
            // Without saved values, "Revertir" writes the catalog's values: those overwrite the user's own, so
            // they are journaled like any change and can be undone with Revertir todo.
            bool journal = apply || recorded.Count == 0;
            // Reverting too: an undo may delete a plan, and only a backup can bring it back later.
            bool backupPlans = _powerPlansBackup == null && TouchesPowerPlan(tweak);
            var activity = BeginOperation((apply ? "Aplicar · " : "Revertir · ") + shortTitle, "Optimización", opsList.Count + (journal ? 1 : 0) + (backupPlans ? 1 : 0));

            if (backupPlans)
            {
                StepOperation(activity, "Guardando copia completa de tus planes de energía");
                try { await Task.Run(() => BackupPowerPlansOnce()); }
                catch (Exception ex)
                {
                    string error = "No se cambió nada: no se pudo guardar la copia de tus planes de energía (" + ex.Message + ")";
                    CompleteOperation(activity, false, shortTitle + ": " + error);
                    throw new InvalidOperationException(error);
                }
            }

            if (journal)
            {
                StepOperation(activity, "Guardando los valores actuales para poder revertirlo");
                Dictionary<string, object> entry;
                try
                {
                    var restoreOps = await Task.Run(() =>
                    {
                        var captured = new List<Dictionary<string, object>>();
                        foreach (var op in opsList) captured.InsertRange(0, CaptureRestoreOps(op));
                        return StartServicesLast(captured);
                    });
                    entry = new Dictionary<string, object> { { "id", apply ? id : id + "#restablecer" }, { "title", apply ? shortTitle : "Restablecer «" + shortTitle + "» (valores del catálogo)" }, { "at", DateTime.Now.ToString("yyyy-MM-dd HH:mm") }, { "restoreOps", restoreOps } };
                    _changes.Add(entry);
                    try { SaveJournal(); }
                    catch { _changes.Remove(entry); throw; }
                }
                catch (Exception ex)
                {
                    string error = "No se cambió nada: no se pudo guardar el estado anterior (" + ex.Message + ")";
                    CompleteOperation(activity, false, shortTitle + ": " + error);
                    throw new InvalidOperationException(error);
                }
            }

            SetStatus((apply ? "⚡ Aplicando nativamente: " : "↩️ Revirtiendo nativamente: ") + title, id);

            var failures = new List<string>();
            await Task.Run(() =>
            {
                foreach (var op in opsList)
                {
                    StepOperation(activity, DescribeTweakOperation(op));
                    try { RunTweakOperation(op); LogOperation(apply ? "APLICAR" : recorded.Count > 0 ? "REVERTIR (valor guardado)" : "REVERTIR (valor de Windows)", id, op, null); }
                    catch (Exception ex)
                    {
                        LogOperation(apply ? "APLICAR" : "REVERTIR", id, op, ex);
                        bool tolerated = GetBool(op, "allowFailure");
                        string reason = ex.Message.Split(new[] { (char)13, (char)10 }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? ex.GetType().Name;
                        AppendLog(DescribeTweakOperation(op) + (tolerated ? " (ya estaba hecho): " : " falló: ") + reason, tolerated ? "info" : "warn", "Optimización");
                        if (!tolerated) failures.Add(DescribeTweakOperation(op) + ": " + reason);
                    }
                }
            });

            if (failures.Count > 0)
            {
                // Never report success for a change Windows rejected: re-read the real state and surface the error.
                _tweakStates[id] = await Task.Run(() => IsTweakActive(tweak));
                if (opsList.Any(o => GetStr(o, "type") == "appx_remove" || GetStr(o, "type") == "appx_install") && await Task.Run(() => IsTweakTouched(tweak)))
                {
                    _tweakStates[id] = true;
                    _tweakPartial.Add(id);
                }
                string error = failures.Count + " de " + opsList.Count + " cambios no se aplicaron (" + failures[0] + ")";
                CompleteOperation(activity, false, shortTitle + ": " + error);
                throw new InvalidOperationException(error);
            }
            if (apply && opsList.Any(o => GetStr(o, "type") == "appx_remove"))
            {
                _changes.Last(c => GetStr(c, "id") == id)["appxComplete"] = true;
                SaveJournal();
            }
            if (recorded.Count > 0)
            {
                foreach (var transfer in transfers) transfer();
                foreach (var change in recorded) _changes.Remove(change);
                try { SaveJournal(); } catch (Exception ex) { AppendLog("No se pudo actualizar el registro de cambios: " + ex.Message, "warn", "Seguridad"); }
            }
            CompleteOperation(activity, true, shortTitle + (apply ? ": aplicada" : recorded.Count > 0 ? ": valores anteriores restaurados" : ": revertida") + " (" + opsList.Count + " cambios)");
            _tweakStates[id] = apply || (recorded.Count > 0 && await Task.Run(() => IsTweakActive(tweak)));
            _tweakPartial.Remove(id);
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

        // Human-readable step: "Registro · Services\DiagTrack › Start = 4", "Energía · powercfg /setactive …".
        private string DescribeTweakOperation(Dictionary<string, object> op)
        {
            string type = GetStr(op, "type");
            string[] parts = GetStr(op, "key").Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
            string key = string.Join("\\", parts.Skip(Math.Max(0, parts.Length - 2)));
            string name = GetStr(op, "name");
            if (type == "reg_dword" || type == "reg_sz") return "Registro · " + key + " › " + (name.Length == 0 ? "(predeterminado)" : name) + " = " + GetStr(op, "value");
            if (type == "reg_delete") return "Registro · borrar " + key + " › " + name;
            if (type == "reg_delete_tree") return "Registro · borrar la clave " + key;
            if (type == "reg_value") return "Registro · " + key + " › " + (name.Length == 0 ? "(predeterminado)" : name) + " = valor anterior";
            if (type == "powercfg") return "Energía · powercfg " + GetStr(op, "args");
            if (type == "service_stop") return "Servicio · detener " + name;
            if (type == "service_start") return "Servicio · iniciar " + name;
            if (type == "appx_remove") return "Quitar app preinstalada · " + name;
            if (type == "appx_install") return "Microsoft Store · reinstalar " + name;
            if (type == "appx_restore") return "Microsoft Store · solo las apps quitadas por Blackwall";
            return type;
        }

        private RegistryKey TweakHive(Dictionary<string, object> op)
        {
            return GetStr(op, "hive") == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser;
        }

        // REG_DWORD is 32-bit unsigned on disk; catalog values above int.MaxValue (0x80000001...) must wrap, not become 0.
        internal static int ToRegistryDword(object value)
        {
            long raw = Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
            if (raw < int.MinValue || raw > uint.MaxValue) throw new ArgumentOutOfRangeException("value", raw, "Fuera del rango de REG_DWORD.");
            return raw > int.MaxValue ? unchecked((int)(uint)raw) : (int)raw;
        }

        // Some keys refuse writes even to an administrator (a system-protected policy, an organisation's policy or
        // an antivirus): .NET only says "Intento de realizar una operación no válida", so name the key and the cause.
        private void RunTweakOperation(Dictionary<string, object> op)
        {
            try { RunTweakOperationCore(op); }
            catch (UnauthorizedAccessException ex) { throw AccessRefused(op, ex); }
            catch (System.Security.SecurityException ex) { throw AccessRefused(op, ex); }
        }

        private Exception AccessRefused(Dictionary<string, object> op, Exception inner)
        {
            return new InvalidOperationException("Windows denegó el acceso a " + GetStr(op, "hive") + "\\" + GetStr(op, "key")
                + " aun con permisos de administrador: la protege el sistema, una directiva de la organización o un antivirus", inner);
        }

        // True when the value already holds exactly this data and type: nothing to write, and no write access needed.
        private bool RegistryValueIs(Dictionary<string, object> op, object data, RegistryValueKind kind)
        {
            using (var k = TweakHive(op).OpenSubKey(GetStr(op, "key")))
            {
                if (k == null || !k.GetValueNames().Contains(GetStr(op, "name"), StringComparer.OrdinalIgnoreCase) || k.GetValueKind(GetStr(op, "name")) != kind) return false;
                object now = k.GetValue(GetStr(op, "name"), null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                if (now is string[] && data is string[]) return ((string[])now).SequenceEqual((string[])data);
                if (now is byte[] && data is byte[]) return ((byte[])now).SequenceEqual((byte[])data);
                return Equals(now, data);
            }
        }

        private void RunTweakOperationCore(Dictionary<string, object> op)
        {
            string type = GetStr(op, "type");
            if (type == "appx_remove" || type == "appx_install") { RunAppxOperation(op); return; }
            if (type == "appx_restore") return; // Actual reinstalls come exclusively from the saved journal.
            if ((type == "reg_dword" || type == "reg_sz") && RegistryValueIs(op, type == "reg_dword" ? (object)ToRegistryDword(op["value"]) : GetStr(op, "value"), type == "reg_dword" ? RegistryValueKind.DWord : RegistryValueKind.String)) return;
            if (type == "reg_dword" || type == "reg_sz")
            {
                using (var k = TweakHive(op).CreateSubKey(GetStr(op, "key"), true))
                {
                    if (k == null) throw new InvalidOperationException("No se pudo abrir la clave " + GetStr(op, "key"));
                    if (type == "reg_dword") k.SetValue(GetStr(op, "name"), ToRegistryDword(op["value"]), RegistryValueKind.DWord);
                    else k.SetValue(GetStr(op, "name"), GetStr(op, "value"), RegistryValueKind.String);
                }
            }
            else if (type == "reg_delete")
            {
                using (var probe = TweakHive(op).OpenSubKey(GetStr(op, "key")))
                    if (probe == null || !probe.GetValueNames().Contains(GetStr(op, "name"), StringComparer.OrdinalIgnoreCase)) return;
                using (var k = TweakHive(op).OpenSubKey(GetStr(op, "key"), true))
                {
                    if (k != null) k.DeleteValue(GetStr(op, "name"), false);
                }
            }
            else if (type == "reg_delete_tree")
            {
                TweakHive(op).DeleteSubKeyTree(GetStr(op, "key"), false);
            }
            else if (type == "powercfg")
            {
                string[] p = GetStr(op, "args").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length == 2 && p[0].Equals("/setactive", StringComparison.OrdinalIgnoreCase) && SchemeGuidPattern.IsMatch(p[1])) ImportSchemeIfMissing(p[1]);
                _powercfg(GetStr(op, "args"));
            }
            else if (type == "service_stop")
            {
                ControlServiceViaWmi(GetStr(op, "name"), "StopService"); // best effort: persistence comes from the Start value in the same tweak
            }
            else if (type == "service_start")
            {
                // Restoring a running service must really start it; a failure keeps the change journaled.
                uint started = ControlServiceViaWmi(GetStr(op, "name"), "StartService");
                if (started != 0 && started != 10) throw new InvalidOperationException("el servicio " + GetStr(op, "name") + " no se pudo iniciar (código WMI " + started + ")");
            }
            else if (type == "reg_value")
            {
                var kind = (RegistryValueKind)Enum.Parse(typeof(RegistryValueKind), GetStr(op, "kind"));
                object raw = op["value"];
                object data = kind == RegistryValueKind.DWord ? (object)ToRegistryDword(raw)
                    : kind == RegistryValueKind.QWord ? (object)Convert.ToInt64(raw, System.Globalization.CultureInfo.InvariantCulture)
                    : kind == RegistryValueKind.MultiString ? (object)((IEnumerable)raw).Cast<object>().Select(v => Convert.ToString(v)).ToArray()
                    : kind == RegistryValueKind.String || kind == RegistryValueKind.ExpandString ? (object)Convert.ToString(raw)
                    : Convert.FromBase64String(Convert.ToString(raw));
                if (RegistryValueIs(op, data, kind)) return;
                using (var k = TweakHive(op).CreateSubKey(GetStr(op, "key"), true))
                {
                    if (k == null) throw new InvalidOperationException("No se pudo abrir la clave " + GetStr(op, "key"));
                    k.SetValue(GetStr(op, "name"), data, kind);
                }
            }
            else throw new NotSupportedException("Tipo de operación desconocido: " + type);
        }

        // Undoing an older tweak must not clobber a newer change to the same value: the newer change inherits
        // the original value (restored when it is reverted) and only untouched values are restored now.
        // The transfers are returned, not applied: the journal only changes once the revert really succeeded.
        private List<Dictionary<string, object>> RestoreOpsFor(List<Dictionary<string, object>> recorded, out List<Action> transfers)
        {
            var ops = new List<Dictionary<string, object>>();
            transfers = new List<Action>();
            foreach (var change in recorded.AsEnumerable().Reverse())
            {
                var later = _changes.Skip(_changes.IndexOf(change) + 1).Where(c => !recorded.Contains(c)).SelectMany(c => ConvertToDictList(c["restoreOps"])).ToList();
                foreach (var op in ConvertToDictList(change["restoreOps"]))
                {
                    string target = RestoreTarget(op);
                    var newer = target == null ? null : later.FirstOrDefault(o => RestoreTarget(o) == target);
                    if (newer == null) { ops.Add(op); continue; }
                    var original = op;
                    transfers.Add(() => { newer.Clear(); foreach (var pair in original) newer[pair.Key] = pair.Value; });
                }
            }
            return ops;
        }

        // A service can only start once its startup type is restored, so restarts always run last (also for
        // journals written before this ordering existed).
        private List<Dictionary<string, object>> StartServicesLast(List<Dictionary<string, object>> ops)
        {
            return ops.Where(o => GetStr(o, "type") != "service_start").Concat(ops.Where(o => GetStr(o, "type") == "service_start")).ToList();
        }

        private string RestoreTarget(Dictionary<string, object> op)
        {
            string type = GetStr(op, "type");
            if (type == "reg_value" || type == "reg_delete") return ("reg|" + GetStr(op, "hive") + "|" + GetStr(op, "key") + "|" + GetStr(op, "name")).ToLowerInvariant();
            if (type != "powercfg") return null;
            string[] p = GetStr(op, "args").ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length == 5 && (p[0] == "/setacvalueindex" || p[0] == "/setdcvalueindex")) return string.Join(" ", p.Take(4));
            if (p.Length == 2 && p[0] == "/setactive" && p[1] != "scheme_current") return "/setactive";
            if (p.Length >= 2 && p[0] == "/hibernate") return p[1] == "/size" ? "/hibernate /size" : "/hibernate";
            return null;
        }

        // What Windows has right now for everything an op touches, as ops that put it back exactly.
        // Anything that cannot be captured stops the tweak before it changes the system.
        internal List<Dictionary<string, object>> CaptureRestoreOps(Dictionary<string, object> op)
        {
            string type = GetStr(op, "type");
            var restore = new List<Dictionary<string, object>>();
            if (type == "appx_remove")
            {
                var before = ReadAppxPackage(GetStr(op, "name"), false);
                if (GetBool(before, "installed") || GetBool(before, "provisioned"))
                    restore.Add(new Dictionary<string, object> { { "type", "appx_install" }, { "name", GetStr(op, "name") }, { "pending", true }, { "inventory", GetStr(before, "inventory") } });
            }
            else if (type == "appx_restore") { }
            else if (type == "reg_dword" || type == "reg_sz" || type == "reg_delete" || type == "reg_value") restore.Add(CaptureRegistryValue(GetStr(op, "hive"), GetStr(op, "key"), GetStr(op, "name")));
            else if (type == "service_stop") { if (ServiceRunning(GetStr(op, "name"))) restore.Add(new Dictionary<string, object> { { "type", "service_start" }, { "name", GetStr(op, "name") } }); }
            else if (type == "powercfg") restore.AddRange(CapturePowerState(GetStr(op, "args")));
            else if (type == "reg_delete_tree") restore.AddRange(CaptureRegistryTree(GetStr(op, "hive"), GetStr(op, "key")));
            else throw new NotSupportedException("no se puede guardar el estado previo de «" + DescribeTweakOperation(op) + "»");
            return restore;
        }

        // Every value under a key that is about to be deleted, so the whole branch can be written back.
        private List<Dictionary<string, object>> CaptureRegistryTree(string hive, string key)
        {
            var restore = new List<Dictionary<string, object>>();
            using (var k = (hive == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser).OpenSubKey(key))
            {
                if (k == null) return restore;
                foreach (string name in k.GetValueNames()) restore.Add(CaptureRegistryValue(hive, key, name));
                foreach (string sub in k.GetSubKeyNames()) restore.AddRange(CaptureRegistryTree(hive, key + "\\" + sub));
                // An empty key still matters (the classic menu's InprocServer32 is one): a default value recreates it.
                if (k.ValueCount == 0 && k.SubKeyCount == 0) restore.Add(new Dictionary<string, object> { { "type", "reg_value" }, { "hive", hive }, { "key", key }, { "name", "" }, { "kind", "String" }, { "value", "" } });
            }
            return restore;
        }

        private Dictionary<string, object> CaptureRegistryValue(string hive, string key, string name)
        {
            var restore = new Dictionary<string, object> { { "type", "reg_delete" }, { "hive", hive }, { "key", key }, { "name", name } };
            using (var k = (hive == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser).OpenSubKey(key))
            {
                object value = k == null ? null : k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                if (value == null) return restore;
                RegistryValueKind kind = k.GetValueKind(name);
                restore["type"] = "reg_value";
                restore["kind"] = kind.ToString();
                if (kind == RegistryValueKind.DWord) restore["value"] = (long)unchecked((uint)(int)value);
                else if (kind == RegistryValueKind.QWord) restore["value"] = Convert.ToString((long)value, System.Globalization.CultureInfo.InvariantCulture);
                else if (kind == RegistryValueKind.MultiString) restore["value"] = (string[])value;
                else if (kind == RegistryValueKind.String || kind == RegistryValueKind.ExpandString) restore["value"] = (string)value;
                else if (value is byte[] && kind != RegistryValueKind.Unknown && kind != RegistryValueKind.None) restore["value"] = Convert.ToBase64String((byte[])value);
                else throw new NotSupportedException("el valor " + name + " tiene un tipo de registro que Blackwall no sabe restaurar");
            }
            return restore;
        }

        private static readonly Regex SchemeGuidPattern = new Regex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");

        private string ActiveSchemeGuid()
        {
            Match match = SchemeGuidPattern.Match(RunSystemToolDirect("powercfg.exe", "/getactivescheme"));
            if (!match.Success) throw new InvalidOperationException("powercfg no informó el plan de energía activo");
            return match.Value;
        }

        // /qh also lists hidden settings and ends each one with its current AC and DC index, in that order.
        private long ReadPowerIndex(string scheme, string subgroup, string setting, bool ac)
        {
            MatchCollection values = Regex.Matches(RunSystemToolDirect("powercfg.exe", "/qh " + scheme + " " + subgroup + " " + setting), "0x[0-9a-fA-F]{8}");
            if (values.Count < 2) throw new InvalidOperationException("powercfg no informó el valor actual de " + setting);
            return Convert.ToInt64(values[values.Count - (ac ? 2 : 1)].Value, 16);
        }

        private static int ReadPowerDword(string name)
        {
            using (var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Power"))
            {
                object value = k == null ? null : k.GetValue(name);
                return value is int ? (int)value : -1;
            }
        }

        // Only the powercfg forms the catalog uses are accepted; a new form must teach this how to undo it first.
        private List<Dictionary<string, object>> CapturePowerState(string args)
        {
            string[] p = args.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string verb = p.Length > 0 ? p[0].ToLowerInvariant() : "";
            var restore = new List<Dictionary<string, object>>();
            Action<string, bool> power = (arguments, tolerate) => restore.Add(new Dictionary<string, object> { { "type", "powercfg" }, { "args", arguments }, { "allowFailure", tolerate } });
            if (verb == "/setactive" && p.Length == 2)
            {
                if (!p[1].Equals("scheme_current", StringComparison.OrdinalIgnoreCase)) power("/setactive " + ActiveSchemeGuid(), false);
            }
            else if (verb == "/duplicatescheme" && p.Length == 3)
            {
                if (RunSystemToolDirect("powercfg.exe", "/list").IndexOf(p[2], StringComparison.OrdinalIgnoreCase) < 0) power("/delete " + p[2], true);
            }
            else if ((verb == "/setacvalueindex" || verb == "/setdcvalueindex") && p.Length == 5)
            {
                string scheme = p[1].Equals("scheme_current", StringComparison.OrdinalIgnoreCase) ? ActiveSchemeGuid() : p[1];
                power(verb + " " + scheme + " " + p[2] + " " + p[3] + " " + ReadPowerIndex(scheme, p[2], p[3], verb == "/setacvalueindex"), false);
                power("/setactive scheme_current", false);
            }
            else if (verb == "/hibernate" && p.Length == 2)
            {
                var hibernate = HibernateRestoreOp(ReadPowerDword("HibernateEnabled"));
                if (hibernate != null) restore.Add(hibernate);
            }
            else if (verb == "/hibernate" && p.Length == 3 && p[1].Equals("/size", StringComparison.OrdinalIgnoreCase))
            {
                int size = ReadPowerDword("HiberFileSizePercent");
                if (size > 0) power("/hibernate /size " + size, true);
            }
            else if (verb == "/delete" && p.Length == 2 && _tweaks.Any(t => ConvertToDictList(t.ContainsKey("applyOps") ? t["applyOps"] : null).Any(o => GetStr(o, "args").StartsWith("/duplicatescheme ", StringComparison.OrdinalIgnoreCase) && GetStr(o, "args").EndsWith(" " + p[1], StringComparison.OrdinalIgnoreCase))))
            {
                // Only Blackwall's own copy of a built-in plan: re-applying the tweak recreates it, nothing to keep.
            }
            else throw new NotSupportedException("no se sabe guardar el estado previo de powercfg " + args);
            return restore;
        }

        // Putting hibernation back must really succeed: a failure keeps the journal entry so it can be retried.
        internal static Dictionary<string, object> HibernateRestoreOp(int enabled)
        {
            if (enabled < 0) return null;
            return new Dictionary<string, object> { { "type", "powercfg" }, { "args", "/hibernate " + (enabled == 1 ? "on" : "off") }, { "allowFailure", false } };
        }

        private bool ServiceRunning(string serviceName)
        {
            using (var searcher = new ManagementObjectSearcher("SELECT State FROM Win32_Service WHERE Name='" + serviceName.Replace("'", "") + "'"))
            using (var results = searcher.Get())
                foreach (ManagementObject svc in results)
                    using (svc) return Convert.ToString(svc["State"]) == "Running";
            return false;
        }

        private readonly object _changeLogLock = new object();
        internal string ChangeLogPath { get { return Path.Combine(Path.GetDirectoryName(_changesPath), "registro-cambios.log"); } }

        // Every system write, its outcome, a re-read of the value afterwards and the Microsoft page that documents it,
        // appended to registro-cambios.log under the session (#hhmmss) and origin that started it: what was changed,
        // from where, what failed, what is still there and whether Windows documents it can always be answered.
        private void LogOperation(string action, string tweakId, Dictionary<string, object> op, Exception error)
        {
            string result = error != null ? (GetBool(op, "allowFailure") ? "TOLERADO: " : "FALLO: ") + (error.Message.Split('\r', '\n').FirstOrDefault(l => l.Trim().Length > 0) ?? error.GetType().Name)
                : "OK · " + VerifyOperation(op);
            var tweak = _tweaks.FirstOrDefault(t => GetStr(t, "id") == tweakId.Split('#')[0]);
            WriteChangeLog(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " | #" + (_changeSession ?? "-") + " | " + action + " | " + tweakId + " | " + FullOperation(op) + " | " + result
                + " | fuente: " + (tweak != null ? DescribeSource(tweak, op) : tweakId == PowerRepairId ? "powercfg · " + PowercfgDocs : tweakId.StartsWith(StartupChangePrefix) ? "SIN DOCUMENTACIÓN OFICIAL · mismo mecanismo que el Administrador de tareas" : "no está en el catálogo"));
        }

        private void WriteChangeLog(string line)
        {
            try { lock (_changeLogLock) { Directory.CreateDirectory(Path.GetDirectoryName(ChangeLogPath)); File.AppendAllText(ChangeLogPath, line + Environment.NewLine); } }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        // The catalog's official source whose cover matches this operation (cover = text found in the logged
        // operation, optionally "|" and the term the page must contain; scripts/check-official-sources.mjs checks
        // both against the live pages). Null when Microsoft documents none of it.
        internal Dictionary<string, object> OfficialSourceFor(Dictionary<string, object> tweak, Dictionary<string, object> op)
        {
            string text = FullOperation(op);
            foreach (var s in ConvertToDictList(tweak.ContainsKey("sources") ? tweak["sources"] : null))
                foreach (object cover in s.ContainsKey("covers") && s["covers"] is System.Collections.IEnumerable ? (System.Collections.IEnumerable)s["covers"] : new object[0])
                    if (text.IndexOf(Convert.ToString(cover).Split('|')[0], StringComparison.OrdinalIgnoreCase) >= 0) return s;
            return null;
        }

        internal string DescribeSource(Dictionary<string, object> tweak, Dictionary<string, object> op)
        {
            var s = OfficialSourceFor(tweak, op);
            if (s == null) return "SIN DOCUMENTACIÓN OFICIAL";
            return (GetBool(s, "deprecated") ? "OBSOLETO según Microsoft · " : GetStr(s, "url").Contains("/previous-versions/") ? "documentación archivada · " : "")
                + GetStr(s, "title") + " · " + GetStr(s, "url");
        }

        // "oficial", "parcial (2 de 3)" or "sin documentación oficial": how much of a tweak Microsoft documents.
        internal string DocumentationStatus(Dictionary<string, object> tweak)
        {
            var ops = ConvertToDictList(tweak.ContainsKey("applyOps") ? tweak["applyOps"] : null);
            int documented = ops.Count(op => { var s = OfficialSourceFor(tweak, op); return s != null && !GetBool(s, "deprecated"); });
            return documented == 0 ? "sin documentación oficial" : documented == ops.Count ? "oficial" : "parcial (" + documented + " de " + ops.Count + ")";
        }

        private string FullOperation(Dictionary<string, object> op)
        {
            string type = GetStr(op, "type");
            if (type == "powercfg") return "powercfg " + GetStr(op, "args");
            if (type == "service_stop" || type == "service_start") return type + " " + GetStr(op, "name");
            if (type == "appx_remove") return "Remove-AppxPackage -AllUsers + Remove-AppxProvisionedPackage -Online · " + GetStr(op, "name");
            if (type == "appx_install") return "winget install --source msstore · " + GetStr(op, "name");
            if (type == "appx_restore") return "Microsoft Store · apps del diario";
            string target = GetStr(op, "hive") + "\\" + GetStr(op, "key") + " › " + (GetStr(op, "name").Length > 0 ? GetStr(op, "name") : "(predeterminado)");
            return type == "reg_delete" ? "borrar " + target : type == "reg_delete_tree" ? "borrar árbol " + GetStr(op, "hive") + "\\" + GetStr(op, "key") : target + " = " + GetStr(op, "value");
        }

        // What the system holds right now for the place an op writes; "" when there is nothing to read back.
        internal string CurrentValue(Dictionary<string, object> op)
        {
            string type = GetStr(op, "type");
            if (type.StartsWith("reg_") && type != "reg_delete_tree")
                using (var k = TweakHive(op).OpenSubKey(GetStr(op, "key")))
                {
                    object v = k == null ? null : k.GetValue(GetStr(op, "name"), null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    return v == null ? "(no existe)" : v is string[] ? string.Join(";", (string[])v) : Convert.ToString(v);
                }
            if (type != "powercfg") return "";
            string[] p = GetStr(op, "args").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length == 5 && (p[0].Equals("/setacvalueindex", StringComparison.OrdinalIgnoreCase) || p[0].Equals("/setdcvalueindex", StringComparison.OrdinalIgnoreCase)))
            {
                string scheme = p[1].Equals("scheme_current", StringComparison.OrdinalIgnoreCase) ? ActiveSchemeGuid() : p[1];
                return Convert.ToString(ReadPowerIndex(scheme, p[2], p[3], p[0].StartsWith("/setac", StringComparison.OrdinalIgnoreCase)));
            }
            if (p.Length == 2 && p[0].Equals("/setactive", StringComparison.OrdinalIgnoreCase)) return "plan activo " + ActiveSchemeGuid();
            return "";
        }

        private string VerifyOperation(Dictionary<string, object> op)
        {
            try
            {
                string type = GetStr(op, "type");
                if (type == "appx_remove" || type == "appx_install")
                {
                    string name = GetStr(op, "name");
                    if (!IsAllowedAppxPackage(name)) return "paquete no permitido";
                    if (type == "appx_install" && string.IsNullOrEmpty(AppxStoreIds[name])) return name + ": reinstálala desde Microsoft Store";
                    var state = ReadAppxPackage(name, false);
                    bool matches = type == "appx_remove" ? !GetBool(state, "installed") && !GetBool(state, "provisioned") : GetBool(state, "installed");
                    return matches ? "verificado" : "NO COINCIDE: " + name;
                }
                string now = CurrentValue(op);
                if (now.Length == 0) return "sin relectura";
                if (type == "reg_delete") return now == "(no existe)" ? "verificado: ya no existe" : "SIGUE EXISTIENDO: " + now;
                string expected = GetStr(op, "value");
                if (type == "reg_dword") expected = Convert.ToString(ToRegistryDword(op["value"]));
                if (type == "powercfg")
                {
                    string[] p = GetStr(op, "args").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    expected = p.Length == 5 ? p[4] : p.Length == 2 && !p[1].Equals("scheme_current", StringComparison.OrdinalIgnoreCase) ? p[1] : null;
                    if (expected == null) return "sin relectura";
                    bool same = p.Length == 5 ? PowerIndexMatches(now, expected) : now.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0;
                    return same ? "verificado" : "NO COINCIDE: ahora " + now;
                }
                return now == expected ? "verificado" : "NO COINCIDE: ahora " + now;
            }
            catch (Exception ex) { return "no se pudo releer: " + ex.Message; }
        }

        internal static bool PowerIndexMatches(string now, string expected)
        {
            long a, b;
            return long.TryParse(now, out a) && long.TryParse(expected, out b) && a == b;
        }

        // Every catalog tweak, value by value: what the PC holds now, Blackwall's value and Windows' value, plus the
        // journal. One file that answers what is still applied and what a revert left behind.
        internal string BuildTweakStateReport()
        {
            var text = new StringBuilder();
            text.AppendLine("Estado de los ajustes de Blackwall · " + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + " · " + Environment.MachineName);
            text.AppendLine("Equipo: " + HardwareSummary());
            text.AppendLine("Registro de operaciones: " + ChangeLogPath);
            text.AppendLine();
            foreach (var t in _tweaks)
            {
                bool full = false, part = false;
                if (IsPreference(t))
                {
                    // A preference is the user's choice either way: its state is reported, never as something Blackwall applied.
                    bool on = false;
                    try { on = PreferenceOn(t); } catch (Exception) { }
                    text.AppendLine("[preferencia: " + (on ? "activada" : "desactivada") + "] " + GetStr(t, "id") + " — " + TweakName(t));
                    continue;
                }
                try { full = IsTweakActive(t); part = !full && IsTweakTouched(t); } catch (Exception) { }
                string unmet = UnmetRequirement(t);
                text.AppendLine("[" + (full ? "APLICADO" : part ? "APLICADO EN PARTE" : "no aplicado") + "] " + GetStr(t, "id") + " — " + (GetStr(t, "shortTitle").Length > 0 ? GetStr(t, "shortTitle") : GetStr(t, "title")) + (unmet != null ? " (no corresponde: " + unmet + ")" : "") + " · documentación: " + DocumentationStatus(t));
                if (!full && !part) continue;
                foreach (var s in ConvertToDictList(t.ContainsKey("sources") ? t["sources"] : null).Where(s => GetStr(s, "note").Length > 0))
                    text.AppendLine("    Nota: " + GetStr(s, "note") + " (" + GetStr(s, "url") + ")");
                var undo = t.ContainsKey("undoOps") ? ConvertToDictList(t["undoOps"]) : new List<Dictionary<string, object>>();
                foreach (var op in ConvertToDictList(t.ContainsKey("applyOps") ? t["applyOps"] : null))
                {
                    string now;
                    try { now = CurrentValue(op); } catch (Exception ex) { now = "no se pudo leer: " + ex.Message; }
                    if (now.Length == 0) continue;
                    var windows = undo.FirstOrDefault(u => GetStr(u, "type") == GetStr(op, "type") ? FullOperation(u).Split('=')[0] == FullOperation(op).Split('=')[0] : GetStr(u, "type") == "reg_delete" && GetStr(u, "key").Equals(GetStr(op, "key"), StringComparison.OrdinalIgnoreCase) && GetStr(u, "name") == GetStr(op, "name"));
                    text.AppendLine("    " + FullOperation(op) + "  →  ahora: " + now + "  ·  Windows: " + (windows == null ? "?" : GetStr(windows, "type") == "reg_delete" ? "(no existe)" : GetStr(windows, "type") == "powercfg" ? GetStr(windows, "args").Split(' ').Last() : GetStr(windows, "value")));
                    text.AppendLine("        fuente: " + DescribeSource(t, op));
                }
            }
            text.AppendLine();
            text.AppendLine("Cambios guardados por Blackwall (" + _changes.Count + "):");
            foreach (var c in _changes) text.AppendLine("    " + GetStr(c, "at") + " · " + GetStr(c, "id") + " · " + ConvertToDictList(c["restoreOps"]).Count + " valores");
            return text.ToString();
        }

        // Read only: Windows' own record of the 2011 → 2023 Secure Boot certificate rollout. Blackwall never writes
        // Secure Boot, BCD or firmware. https://support.microsoft.com/en-us/servicing/os/secure-boot/2025/09/registry-key-updates-for-secure-boot-windows-devices-with-it-managed-updates
        private static string ReadSecureBootCerts(bool secureBootOn)
        {
            string status = null;
            int error = 0;
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\Servicing"))
                {
                    if (key != null)
                    {
                        status = Convert.ToString(key.GetValue("UEFICA2023Status"));
                        object raw = key.GetValue("UEFICA2023Error");
                        if (raw is int) error = (int)raw;
                    }
                }
            }
            catch { }
            return DescribeSecureBootCerts(secureBootOn, status, error);
        }

        internal static string DescribeSecureBootCerts(bool secureBootOn, string status, int error)
        {
            string text;
            if (!secureBootOn) text = "No aplica: Secure Boot desactivado";
            else if (string.Equals(status, "Updated", StringComparison.OrdinalIgnoreCase)) text = "Actualizados (2023)";
            else if (string.Equals(status, "InProgress", StringComparison.OrdinalIgnoreCase)) text = "En curso: deja el cargador conectado al reiniciar";
            else if (string.Equals(status, "NotStarted", StringComparison.OrdinalIgnoreCase)) text = "Pendientes: actualiza la BIOS desde la web del fabricante antes de Windows Update";
            else text = "Sin datos de Windows todavía";
            return error != 0 ? text + " · error 0x" + error.ToString("X") : text;
        }

        internal const string RepairSchemeGuid ="b1ac0a11-5e7f-4d2a-9c3e-0000000002ba";
        private const string BalancedSchemeGuid = "381b4222-f694-41f0-9685-ff5bb260df2e";
        internal const string PowerRepairId = "power_plan_repair";
        private const string PowercfgDocs = "https://learn.microsoft.com/en-us/windows-hardware/design/device-experiences/powercfg-command-line-options";
        internal string PowerPlansFolder { get { return Path.Combine(Path.GetDirectoryName(_changesPath), "planes"); } }

        private string RunPowercfg(string args)
        {
            int exitCode;
            string output = RunSystemToolDirect("powercfg.exe", args, out exitCode);
            if (exitCode != 0) throw new InvalidOperationException("powercfg " + args.Split(' ')[0] + " devolvió " + exitCode + (string.IsNullOrWhiteSpace(output) ? "" : ": " + output.Trim()));
            return output;
        }

        private async Task RepairPowerPlanAsync()
        {
            if (!TryBeginSystemChange("Reparar plan de energía")) return;
            try
            {
                if (!_confirmPowerRepair() || !await EnsureRestorePointAsync()) return;
                string folder = await RepairPowerPlanCoreAsync();
                SetStatus("🟢 Plan Equilibrado nuevo activo. Copia de tus planes en " + folder, "Reinicia si notas el equipo lento");
                RenderCurrentTab();
            }
            catch (Exception ex) { SetStatus("🔴 No se pudo reparar el plan de energía: " + ex.Message, "Consulta Actividad"); AppendLog("Reparar plan de energía: " + ex.Message, "ERROR"); }
            finally { EndSystemChange(); }
        }

        private bool ConfirmPowerRepair()
        {
            StackPanel body;
            WrapPanel actions;
            Window dialog = CreateAppDialog("Reparar plan de energía", 540, out body, out actions);
            body.Children.Add(DialogText("Un plan de energía puede guardar valores ocultos que frenan el procesador y que no se ven en Configuración. Esto lo arregla sin borrar nada:", TextPrimary, 12.5));
            body.Children.Add(DialogText("1. Comprueba el punto de restauración; si no existe o se borró, te pide crearlo y no sigue sin él.", TextSecondary, 12));
            body.Children.Add(DialogText("2. Guarda una copia de todos tus planes en " + PowerPlansFolder + ". Si una copia falla, se detiene sin cambiar nada.", TextSecondary, 12));
            body.Children.Add(DialogText("3. Crea «Equilibrado (Blackwall)», una copia del plan Equilibrado de Windows con el estado máximo del procesador al 100 %, lo comprueba y lo activa.", TextSecondary, 12));
            body.Children.Add(DialogText("4. Tus planes anteriores se quedan como estaban. «Revertir todo» vuelve a activar el que usabas.", TextSecondary, 12));
            bool confirmed = false;
            AddDialogButton(actions, "Cancelar", false).Click += (s, e) => dialog.Close();
            AddDialogButton(actions, "Reparar", true).Click += (s, e) => { confirmed = true; dialog.Close(); };
            ShowAppDialog(dialog);
            return confirmed;
        }

        // Exports every plan first (a failed export changes nothing), journals how to go back, then activates a fresh
        // copy of Windows' Balanced plan under Blackwall's own GUID. No user plan is ever deleted. Returns the backup folder.
        // A full copy of every plan, checked file by file: individual values are journaled anyway, but a plan that already
        // carried hidden values (another tool, an older Blackwall) can only be brought back whole from this copy.
        private void ExportPowerPlans(string folder, IEnumerable<string> plans)
        {
            Directory.CreateDirectory(folder);
            foreach (string guid in plans)
            {
                string file = Path.Combine(folder, guid + ".pow");
                _powercfg("/export \"" + file + "\" " + guid);
                if (!File.Exists(file) || new FileInfo(file).Length == 0) throw new InvalidOperationException("no se pudo guardar la copia del plan " + guid + "; no se cambió nada");
            }
        }

        private string _powerPlansBackup;

        // Names sort by time (ImportSchemeIfMissing takes the newest); a second backup in the same millisecond gets a suffix.
        private string NewPlansBackupFolder()
        {
            string stamp = Path.Combine(PowerPlansFolder, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")), folder = stamp;
            for (int n = 2; Directory.Exists(folder); n++) folder = stamp + "-" + n;
            return folder;
        }

        // A plan the journal must reactivate may be gone (deleted by a later change or by the user): it comes back from
        // the newest backup that holds it. powercfg /import file GUID keeps the GUID (powercfg-command-line-options).
        private void ImportSchemeIfMissing(string guid)
        {
            if (_powercfg("/list").IndexOf(guid, StringComparison.OrdinalIgnoreCase) >= 0) return;
            string file = !Directory.Exists(PowerPlansFolder) ? null : Directory.GetDirectories(PowerPlansFolder).OrderByDescending(d => d, StringComparer.Ordinal)
                .Select(d => Path.Combine(d, guid.ToLowerInvariant() + ".pow")).FirstOrDefault(File.Exists);
            if (file == null) throw new InvalidOperationException("el plan de energía " + guid + " ya no existe y no hay copia para recuperarlo");
            _powercfg("/import \"" + file + "\" " + guid);
            AppendLog("Plan de energía " + guid + " recuperado de la copia " + file, "info", "Seguridad");
        }

        internal bool TouchesPowerPlan(Dictionary<string, object> tweak)
        {
            return ConvertToDictList(tweak.ContainsKey("applyOps") ? tweak["applyOps"] : null).Any(op => GetStr(op, "type") == "powercfg");
        }

        // Once per session, before the first optimization that changes a power plan.
        private string BackupPowerPlansOnce()
        {
            if (_powerPlansBackup != null) return _powerPlansBackup;
            var plans = SchemeGuidPattern.Matches(_powercfg("/list")).Cast<Match>().Select(m => m.Value.ToLowerInvariant()).Distinct().ToList();
            if (plans.Count == 0) throw new InvalidOperationException("powercfg no listó ningún plan de energía");
            string folder = NewPlansBackupFolder();
            ExportPowerPlans(folder, plans);
            AppendLog("Copia de tus planes de energía guardada en " + folder, "info", "Seguridad");
            return _powerPlansBackup = folder;
        }

        internal async Task<string> RepairPowerPlanCoreAsync()
        {
            var activity = BeginOperation("Reparar plan de energía", "Optimización", 3);
            try
            {
                string folder = NewPlansBackupFolder();
                StepOperation(activity, "Copia de seguridad de todos los planes");
                var state = await Task.Run(() =>
                {
                    var plans = SchemeGuidPattern.Matches(_powercfg("/list")).Cast<Match>().Select(m => m.Value.ToLowerInvariant()).Distinct().ToList();
                    string active = SchemeGuidPattern.Match(_powercfg("/getactivescheme")).Value.ToLowerInvariant();
                    if (active.Length == 0) throw new InvalidOperationException("powercfg no informó el plan activo; no se cambió nada");
                    if (!plans.Contains(BalancedSchemeGuid)) throw new InvalidOperationException("este equipo no tiene el plan Equilibrado de Windows; no se cambió nada");
                    ExportPowerPlans(folder, plans);
                    return new { plans, active };
                });

                // Repairing again keeps the first repair's way back and its place in the journal: changes made after it
                // (Máximo rendimiento, say) still unwind before it, and Revertir todo ends on the user's original plan.
                var earlier = _changes.Where(c => GetStr(c, "id") == PowerRepairId).ToList();
                string previous = earlier.SelectMany(c => ConvertToDictList(c["restoreOps"])).Select(o => GetStr(o, "args")).Where(a => a.StartsWith("/setactive ")).Select(a => a.Substring(11)).FirstOrDefault()
                    ?? (state.active != RepairSchemeGuid ? state.active : BalancedSchemeGuid);
                var entry = new Dictionary<string, object> { { "id", PowerRepairId }, { "title", "Reparar plan de energía" }, { "at", DateTime.Now.ToString("yyyy-MM-dd HH:mm") }, { "restoreOps", new List<Dictionary<string, object>> {
                    new Dictionary<string, object> { { "type", "powercfg" }, { "args", "/setactive " + previous } },
                    new Dictionary<string, object> { { "type", "powercfg" }, { "args", "/delete " + RepairSchemeGuid }, { "allowFailure", true } } } } };
                int at = earlier.Count > 0 ? _changes.IndexOf(earlier[0]) : _changes.Count;
                foreach (var e in earlier) _changes.Remove(e);
                _changes.Insert(Math.Min(at, _changes.Count), entry);
                try { SaveJournal(); }
                catch { _changes.Remove(entry); _changes.InsertRange(Math.Min(at, _changes.Count), earlier); throw; }

                StepOperation(activity, "Crear «Equilibrado (Blackwall)» con el procesador al 100 %");
                await Task.Run(() =>
                {
                    Action<string> run = args =>
                    {
                        var op = new Dictionary<string, object> { { "type", "powercfg" }, { "args", args } };
                        try { _powercfg(args); LogOperation("REPARAR ENERGÍA", PowerRepairId, op, null); }
                        catch (Exception ex) { LogOperation("REPARAR ENERGÍA", PowerRepairId, op, ex); throw; }
                    };
                    bool replaced = state.plans.Contains(RepairSchemeGuid), created = false;
                    try
                    {
                        if (replaced)
                        {
                            if (state.active == RepairSchemeGuid) run("/setactive " + BalancedSchemeGuid);
                            run("/delete " + RepairSchemeGuid);
                        }
                        run("/duplicatescheme " + BalancedSchemeGuid + " " + RepairSchemeGuid);
                        created = true;
                        run("/changename " + RepairSchemeGuid + " \"Equilibrado (Blackwall)\" \"Copia del plan Equilibrado de Windows con el procesador al 100 %, creada por Blackwall\"");
                        // The copy inherits whatever Balanced carries; a capped maximum processor state is what keeps the GHz low.
                        // PROCTHROTTLEMAX ranges 0-100 %: https://learn.microsoft.com/en-us/windows-hardware/customize/power-settings/options-for-perf-state-engine-maxperformance
                        run("/setacvalueindex " + RepairSchemeGuid + " SUB_PROCESSOR PROCTHROTTLEMAX 100");
                        run("/setdcvalueindex " + RepairSchemeGuid + " SUB_PROCESSOR PROCTHROTTLEMAX 100");
                        var maxState = Regex.Matches(_powercfg("/qh " + RepairSchemeGuid + " SUB_PROCESSOR PROCTHROTTLEMAX"), "0x[0-9a-fA-F]{8}").Cast<Match>().Select(m => Convert.ToInt64(m.Value, 16)).ToList();
                        if (maxState.Count < 2 || maxState.Skip(maxState.Count - 2).Any(v => v != 100))
                            throw new InvalidOperationException("Windows no dejó el procesador al 100 % en el plan nuevo");
                        run("/setactive " + RepairSchemeGuid);
                        if (SchemeGuidPattern.Match(_powercfg("/getactivescheme")).Value.ToLowerInvariant() != RepairSchemeGuid)
                            throw new InvalidOperationException("Windows no activó el plan nuevo");
                    }
                    catch
                    {
                        // Half way: drop the unfinished copy, put back the one it replaced (exported above) and the plan that was active.
                        try
                        {
                            if (created) { run("/setactive " + BalancedSchemeGuid); run("/delete " + RepairSchemeGuid); }
                            if (replaced && _powercfg("/list").IndexOf(RepairSchemeGuid, StringComparison.OrdinalIgnoreCase) < 0)
                                run("/import \"" + Path.Combine(folder, RepairSchemeGuid + ".pow") + "\" " + RepairSchemeGuid);
                            ImportSchemeIfMissing(state.active);
                            run("/setactive " + state.active);
                        }
                        catch (Exception rollback) { AppendLog("Reparar plan de energía: no se pudo volver al plan anterior (" + rollback.Message + "); tus planes están en " + folder, "ERROR"); }
                        throw;
                    }
                });
                StepOperation(activity, "Comprobado: «Equilibrado (Blackwall)» es el plan activo");
                CompleteOperation(activity, true, "Plan Equilibrado nuevo activo · copia de tus planes en " + folder);
                return folder;
            }
            catch (Exception ex)
            {
                CompleteOperation(activity, false, "Reparar plan de energía: " + ex.Message);
                throw;
            }
        }

        // «Restablecer todo» undoes optimizations only: a repaired power plan or a startup program the user switched
        // off are decisions, not tweaks, and stay (Revertir todo can still undo them).
        private static bool IsOptimizationChange(Dictionary<string, object> change)
        {
            string id = Convert.ToString(change.ContainsKey("id") ? change["id"] : "");
            return id != PowerRepairId && !id.StartsWith(StartupChangePrefix) && !id.EndsWith("#on") && !id.EndsWith("#off");
        }

        // ── Programas de inicio ──
        // The same switch Task Manager uses: StartupApproved\{Run,Run32,StartupFolder} holds 12 bytes per program, the
        // first one even (02) when it starts with Windows and odd (03) when it is disabled, then the FILETIME of the change.
        // Microsoft does not document the format; a program switched here shows the same way in Task Manager.
        internal const string StartupChangePrefix = "startup:";
        private const string StartupApprovedRoot = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

        internal static bool IsStartupApprovedKey(string key)
        {
            return new[] { "Run", "Run32", "StartupFolder" }.Any(sub => key.Equals(StartupApprovedRoot + sub, StringComparison.OrdinalIgnoreCase));
        }

        internal static bool StartupEnabled(byte[] approved)
        {
            return approved == null || approved.Length == 0 || (approved[0] & 1) == 0;
        }

        internal List<Dictionary<string, object>> ReadStartupEntries()
        {
            var entries = new List<Dictionary<string, object>>();
            Action<string, string, string, string, string> add = (hive, sub, name, command, scope) =>
            {
                RegistryKey root = hive == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser;
                byte[] approved;
                using (var k = root.OpenSubKey(StartupApprovedRoot + sub)) approved = k == null ? null : k.GetValue(name) as byte[];
                entries.Add(new Dictionary<string, object> { { "name", name }, { "command", command }, { "scope", scope }, { "hive", hive }, { "key", StartupApprovedRoot + sub }, { "enabled", StartupEnabled(approved) } });
            };
            Action<RegistryKey, string, string, string, string> fromRun = (root, hive, path, sub, scope) =>
            {
                using (var k = root.OpenSubKey(path))
                    if (k != null) foreach (string n in k.GetValueNames().Where(n => n.Length > 0)) add(hive, sub, n, Convert.ToString(k.GetValue(n)), scope);
            };
            fromRun(Registry.CurrentUser, "HKCU", @"Software\Microsoft\Windows\CurrentVersion\Run", "Run", "Tu usuario");
            fromRun(Registry.LocalMachine, "HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "Run", "Todos los usuarios");
            fromRun(Registry.LocalMachine, "HKLM", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", "Run32", "Todos los usuarios");
            foreach (var folder in new[] { new { path = Environment.GetFolderPath(Environment.SpecialFolder.Startup), hive = "HKCU", scope = "Tu usuario" }, new { path = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), hive = "HKLM", scope = "Todos los usuarios" } })
                if (Directory.Exists(folder.path))
                    foreach (string file in Directory.GetFiles(folder.path).Where(p => !Path.GetFileName(p).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)))
                        add(folder.hive, "StartupFolder", Path.GetFileName(file), file, folder.scope + " · carpeta Inicio");
            return entries.OrderBy(e => GetStr(e, "name"), StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        // Journaled like any change: «Revertir todo» puts back the exact earlier switch.
        internal async Task SetStartupEnabledAsync(Dictionary<string, object> entry, bool enable)
        {
            string name = GetStr(entry, "name");
            if (!TryBeginSystemChange("Programas de inicio · " + name + (enable ? " (activar)" : " (desactivar)"))) return;
            try
            {
                if (!await EnsureRestorePointAsync()) return;
                var data = new byte[12];
                data[0] = enable ? (byte)2 : (byte)3;
                if (!enable) BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);
                string title = (enable ? "Iniciar con Windows: " : "No iniciar con Windows: ") + name;
                var change = new Dictionary<string, object> { { "id", StartupChangePrefix + GetStr(entry, "hive") + "\\" + GetStr(entry, "key") + "\\" + name }, { "title", title }, { "shortTitle", title },
                    { "applyOps", new List<Dictionary<string, object>> { new Dictionary<string, object> { { "type", "reg_value" }, { "hive", GetStr(entry, "hive") }, { "key", GetStr(entry, "key") }, { "name", name }, { "kind", "Binary" }, { "value", Convert.ToBase64String(data) } } } } };
                await _executeTweakAction(change, true);
                SetStatus("🟢 " + title, "Se aplica en el próximo inicio de sesión");
            }
            catch (Exception ex) { SetStatus("🔴 No se pudo cambiar «" + name + "»: " + ex.Message, "Consulta Actividad"); AppendLog(name + ": " + ex.Message, "ERROR"); }
            finally { EndSystemChange(); RenderCurrentTab(); }
        }

        private UIElement BuildStartupCard()
        {
            Border card = CreateCardContainer();
            card.Margin = new Thickness(0, 0, 0, 14);
            var stack = new StackPanel();
            List<Dictionary<string, object>> entries;
            try { entries = ReadStartupEntries(); }
            catch (Exception ex) { entries = new List<Dictionary<string, object>>(); AppendLog("No se pudieron leer los programas de inicio: " + ex.Message, "warn", "Sistema"); }
            stack.Children.Add(new TextBlock { Text = "Programas de inicio · " + entries.Count(e => GetBool(e, "enabled")) + " de " + entries.Count + " se abren con Windows", Foreground = TextPrimary, FontWeight = FontWeights.Bold, FontSize = 14 });
            stack.Children.Add(new TextBlock { Text = "Usa el mismo interruptor que el Administrador de tareas (Microsoft no documenta su formato): no borra nada y se puede revertir con «Revertir todo». Antes se comprueba el punto de restauración.", Foreground = TextSecondary, FontSize = 12.3, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 8) });
            if (entries.Count == 0) stack.Children.Add(new TextBlock { Text = "No hay programas configurados para iniciarse con Windows.", Foreground = TextSecondary, FontSize = 12.3 });
            foreach (var e in entries)
            {
                var entry = e;
                bool on = GetBool(entry, "enabled");
                var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
                Button toggle = CreateStyledButton(on ? "Desactivar" : "Activar", !on);
                toggle.Margin = new Thickness(8, 0, 0, 0);
                toggle.Click += async (s, ev) => await RunUiActionAsync("startup", toggle, () => SetStartupEnabledAsync(entry, !on));
                DockPanel.SetDock(toggle, Dock.Right);
                row.Children.Add(toggle);
                var state = new TextBlock { Text = on ? "Se abre al iniciar" : "Desactivado", Foreground = on ? TextPrimary : TextSecondary, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
                DockPanel.SetDock(state, Dock.Right);
                row.Children.Add(state);
                var text = new StackPanel();
                text.Children.Add(new TextBlock { Text = GetStr(entry, "name"), Foreground = TextPrimary, FontWeight = FontWeights.SemiBold, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis });
                text.Children.Add(new TextBlock { Text = GetStr(entry, "scope") + " · " + GetStr(entry, "command"), ToolTip = GetStr(entry, "command"), Foreground = TextSecondary, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis });
                row.Children.Add(text);
                stack.Children.Add(row);
            }
            card.Child = stack;
            return card;
        }

        // ── Liberar espacio ──
        // Temporary files older than a day (anything in use or newer is left alone) and Microsoft's documented component
        // store cleanup without /ResetBase, so installed updates can still be uninstalled. Neither is journaled: deleted
        // temporary files cannot come back, which the confirmation says.
        internal static long DeleteOldTempFiles(string folder, DateTime olderThan, bool dryRun)
        {
            long bytes = 0;
            if (!Directory.Exists(folder)) return 0;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).ToList(); }
            catch (Exception) { return 0; }
            foreach (string file in files)
            {
                try
                {
                    var info = new FileInfo(file);
                    if (info.LastWriteTimeUtc > olderThan.ToUniversalTime() || (info.Attributes & (FileAttributes.System | FileAttributes.ReparsePoint)) != 0) continue;
                    long size = info.Length;
                    if (!dryRun) info.Delete();
                    bytes += size;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return bytes;
        }

        private IEnumerable<string> TempFolders()
        {
            return new[] { Path.GetTempPath(), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp") }.Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private long _tempBytes = -1;

        private UIElement BuildCleanupCard()
        {
            Border card = CreateCardContainer();
            card.Margin = new Thickness(0, 0, 0, 14);
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock { Text = "Liberar espacio", Foreground = TextPrimary, FontWeight = FontWeights.Bold, FontSize = 14 });
            stack.Children.Add(new TextBlock { Text = "Archivos temporales de más de un día (los que están en uso no se tocan) y la limpieza de componentes de Windows Update que documenta Microsoft, sin /ResetBase: las actualizaciones instaladas se pueden seguir desinstalando. Lo borrado no se puede recuperar.", Foreground = TextSecondary, FontSize = 12.3, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 8) });
            var actions = new WrapPanel();
            Button temp = CreateStyledButton(_tempBytes < 0 ? "Calcular temporales" : "Borrar temporales (" + FormatBytes(_tempBytes) + ")", _tempBytes > 0);
            temp.Margin = new Thickness(0, 0, 8, 0);
            temp.Click += async (s, e) => await RunUiActionAsync("cleanup", temp, CleanTempAsync);
            actions.Children.Add(temp);
            Button dism = CreateStyledButton("Limpiar componentes de Windows Update", false);
            dism.ToolTip = "DISM /Online /Cleanup-Image /StartComponentCleanup · learn.microsoft.com/windows-hardware/manufacture/desktop/clean-up-the-winsxs-folder";
            dism.Click += async (s, e) => await RunUiActionAsync("cleanup", dism, CleanComponentStoreAsync);
            actions.Children.Add(dism);
            stack.Children.Add(actions);
            card.Child = stack;
            return card;
        }

        private static string FormatBytes(long bytes)
        {
            return bytes >= 1L << 30 ? (bytes / (double)(1L << 30)).ToString("0.0") + " GB" : (bytes / (double)(1L << 20)).ToString("0") + " MB";
        }

        private async Task CleanTempAsync()
        {
            DateTime cutoff = DateTime.Now.AddDays(-1);
            if (_tempBytes < 0)
            {
                _tempBytes = await Task.Run(() => TempFolders().Sum(f => DeleteOldTempFiles(f, cutoff, true)));
                SetStatus("Temporales de más de un día: " + FormatBytes(_tempBytes), "Pulsa de nuevo para borrarlos");
                RenderCurrentTab();
                return;
            }
            if (!_confirmCleanup("Borrar archivos temporales", "Se borran " + FormatBytes(_tempBytes) + " de archivos temporales de más de un día en " + string.Join(" y ", TempFolders()) + ". Los que están en uso se omiten. No se pueden recuperar.")) return;
            var activity = BeginOperation("Borrar archivos temporales", "Sistema", 1);
            long freed = await Task.Run(() => TempFolders().Sum(f => DeleteOldTempFiles(f, cutoff, false)));
            CompleteOperation(activity, true, FormatBytes(freed) + " liberados");
            SetStatus("🟢 " + FormatBytes(freed) + " de temporales liberados", "Listo");
            _tempBytes = -1;
            RenderCurrentTab();
        }

        private async Task CleanComponentStoreAsync()
        {
            if (!TryBeginSystemChange("Limpiar componentes de Windows Update")) return;
            try
            {
                if (!_confirmCleanup("Limpiar componentes de Windows Update", "Windows borra versiones antiguas de componentes que ya fueron sustituidas por actualizaciones (DISM /StartComponentCleanup, sin /ResetBase). Puede tardar varios minutos. Las actualizaciones instaladas se podrán seguir desinstalando.")) return;
                if (!await EnsureRestorePointAsync()) return;
                var activity = BeginOperation("Limpiar componentes de Windows Update", "Sistema", 1);
                StepOperation(activity, "DISM /Online /Cleanup-Image /StartComponentCleanup");
                int code = -1;
                string output = await Task.Run(() => RunSystemToolDirect("dism.exe", "/Online /Cleanup-Image /StartComponentCleanup", out code, 60 * 60 * 1000));
                bool ok = code == 0 || code == 3010;
                string last = (output ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
                WriteChangeLog(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " | #" + _changeSession + " | LIMPIEZA | dism | /Online /Cleanup-Image /StartComponentCleanup | " + (ok ? "OK" : "FALLO: código " + code) + " · " + last.Trim() + " | fuente: https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/clean-up-the-winsxs-folder");
                CompleteOperation(activity, ok, ok ? "Componentes antiguos eliminados" : "DISM devolvió " + code + ": " + last.Trim());
                SetStatus(ok ? "🟢 Limpieza de componentes completada" : "🔴 DISM devolvió " + code, ok ? "Listo" : last.Trim());
            }
            finally { EndSystemChange(); }
        }

        private bool ConfirmCleanup(string title, string text)
        {
            StackPanel body;
            WrapPanel actions;
            Window dialog = CreateAppDialog(title, 520, out body, out actions);
            body.Children.Add(DialogText(text, TextPrimary, 12.5));
            bool confirmed = false;
            AddDialogButton(actions, "Cancelar", false).Click += (s, e) => dialog.Close();
            AddDialogButton(actions, "Borrar", true).Click += (s, e) => { confirmed = true; dialog.Close(); };
            ShowAppDialog(dialog);
            return confirmed;
        }

        private async Task ExportTweakStateAsync()
        {
            await RefreshTweaksStateNativeAsync();
            string file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "blackwall-estado-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".txt");
            string report = await Task.Run(() => BuildTweakStateReport());
            File.WriteAllText(file, report, new UTF8Encoding(true));
            AppendLog("Estado de los ajustes guardado en " + file, "SAFETY");
            SetStatus("🟢 Estado de los ajustes guardado en el Escritorio", Path.GetFileName(file));
        }

        // A tweak an older version applied only in part (one step failed) must still be revertible: it counts as
        // applied when any of its values holds Blackwall's value and that value differs from Windows' own (the
        // undo's), so a value that merely coincides with the default on a clean PC never flags anything.
        internal bool IsTweakTouched(Dictionary<string, object> t, Func<string> activeScheme = null)
        {
            if (_changes.Any(c => GetStr(c, "id") == GetStr(t, "id") && ConvertToDictList(c["restoreOps"]).Any(o => GetStr(o, "type") == "appx_install"))) return true;
            var undo = t.ContainsKey("undoOps") ? ConvertToDictList(t["undoOps"]) : new List<Dictionary<string, object>>();
            foreach (var op in t.ContainsKey("applyOps") ? ConvertToDictList(t["applyOps"]) : new List<Dictionary<string, object>>())
            {
                string type = GetStr(op, "type");
                try
                {
                    if (type == "powercfg")
                    {
                        string[] p = GetStr(op, "args").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (p.Length == 2 && p[0].Equals("/setactive", StringComparison.OrdinalIgnoreCase) && !p[1].Equals("scheme_current", StringComparison.OrdinalIgnoreCase)
                            && (activeScheme ?? (() => RunSystemToolDirect("powercfg.exe", "/getactivescheme")))().IndexOf(p[1], StringComparison.OrdinalIgnoreCase) >= 0) return true;
                        // A plan value left at Blackwall's number (and Windows' own number is different) still counts.
                        if (p.Length == 5 && (p[0].Equals("/setacvalueindex", StringComparison.OrdinalIgnoreCase) || p[0].Equals("/setdcvalueindex", StringComparison.OrdinalIgnoreCase)))
                        {
                            var windows = undo.FirstOrDefault(u => GetStr(u, "type") == "powercfg" && GetStr(u, "args").StartsWith(p[0] + " ", StringComparison.OrdinalIgnoreCase) && GetStr(u, "args").IndexOf(p[3], StringComparison.OrdinalIgnoreCase) >= 0);
                            if (windows == null || GetStr(windows, "args").EndsWith(" " + p[4])) continue;
                            string scheme = p[1].Equals("scheme_current", StringComparison.OrdinalIgnoreCase) ? SchemeGuidPattern.Match((activeScheme ?? (() => RunSystemToolDirect("powercfg.exe", "/getactivescheme")))()).Value : p[1];
                            if (ReadPowerIndex(scheme, p[2], p[3], p[0].StartsWith("/setac", StringComparison.OrdinalIgnoreCase)) == Convert.ToInt64(p[4], System.Globalization.CultureInfo.InvariantCulture)) return true;
                        }
                        continue;
                    }
                    if (type != "reg_dword" && type != "reg_sz") continue;
                    var restore = undo.FirstOrDefault(u => GetStr(u, "hive") == GetStr(op, "hive") && GetStr(u, "key").Equals(GetStr(op, "key"), StringComparison.OrdinalIgnoreCase) && GetStr(u, "name").Equals(GetStr(op, "name"), StringComparison.OrdinalIgnoreCase));
                    if (restore == null || (GetStr(restore, "type") != "reg_delete" && GetStr(restore, "value") == GetStr(op, "value"))) continue;
                    bool holds = type == "reg_dword" ? CheckRegDword(TweakHive(op), GetStr(op, "key"), GetStr(op, "name"), ToRegistryDword(op["value"]))
                        : CheckRegString(TweakHive(op), GetStr(op, "key"), GetStr(op, "name"), GetStr(op, "value"));
                    if (holds) return true;
                }
                catch (Exception) { } // unreadable means "not shown as Blackwall's", never as applied
            }
            return false;
        }

        // Active only when every verifiable change of applyOps is present, so a half-applied tweak is not shown as active.
        private bool IsTweakActive(Dictionary<string, object> t, Func<string> activeScheme = null)
        {
            if (ConvertToDictList(t.ContainsKey("applyOps") ? t["applyOps"] : null).Any(o => GetStr(o, "type") == "appx_remove"))
                return _changes.Any(c => GetStr(c, "id") == GetStr(t, "id") && GetBool(c, "appxComplete"));
            int checkedOps = 0;
            foreach (var op in t.ContainsKey("applyOps") ? ConvertToDictList(t["applyOps"]) : new List<Dictionary<string, object>>())
            {
                string type = GetStr(op, "type");
                bool? matches = null;
                try
                {
                if (type == "reg_dword") matches = CheckRegDword(TweakHive(op), GetStr(op, "key"), GetStr(op, "name"), ToRegistryDword(op["value"]));
                else if (type == "reg_sz")
                {
                    string name = GetStr(op, "name");
                    matches = string.IsNullOrEmpty(name) ? CheckRegKeyExists(TweakHive(op), GetStr(op, "key")) : CheckRegString(TweakHive(op), GetStr(op, "key"), name, GetStr(op, "value"));
                }
                else if (type == "reg_delete")
                {
                    using (var k = TweakHive(op).OpenSubKey(GetStr(op, "key"))) matches = k == null || k.GetValue(GetStr(op, "name")) == null;
                }
                else if (type == "powercfg")
                {
                    string[] parts = GetStr(op, "args").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    Func<string> schemeText = activeScheme ?? (() => RunSystemToolDirect("powercfg.exe", "/getactivescheme"));
                    if (parts.Length == 2 && parts[0].Equals("/setactive", StringComparison.OrdinalIgnoreCase) && !parts[1].Equals("scheme_current", StringComparison.OrdinalIgnoreCase))
                        matches = schemeText().IndexOf(parts[1], StringComparison.OrdinalIgnoreCase) >= 0;
                    else if (parts.Length == 5 && (parts[0].Equals("/setacvalueindex", StringComparison.OrdinalIgnoreCase) || parts[0].Equals("/setdcvalueindex", StringComparison.OrdinalIgnoreCase)))
                    {
                        string scheme = parts[1].Equals("scheme_current", StringComparison.OrdinalIgnoreCase) ? SchemeGuidPattern.Match(schemeText()).Value : parts[1];
                        matches = ReadPowerIndex(scheme, parts[2], parts[3], parts[0].Equals("/setacvalueindex", StringComparison.OrdinalIgnoreCase)) == Convert.ToInt64(parts[4], System.Globalization.CultureInfo.InvariantCulture);
                    }
                }
                }
                catch (Exception) { matches = false; } // unreadable or invalid means "not verified as active"
                if (matches == null) continue;
                checkedOps++;
                if (!matches.Value) return false;
            }
            return checkedOps > 0;
        }

        // Returns the WMI result (0 ok, 5/6 already stopped, 10 already running; uint.MaxValue when unreachable).
        private uint ControlServiceViaWmi(string serviceName, string method)
        {
            uint result = uint.MaxValue;
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_Service WHERE Name='" + serviceName.Replace("'", "") + "'"))
                {
                    foreach (ManagementObject svc in searcher.Get())
                    {
                        uint code = result = Convert.ToUInt32(svc.InvokeMethod(method, null));
                        // 5/6: already stopped, 10: already running; anything else is a real failure worth showing.
                        if (code != 0 && code != 5 && code != 6 && code != 10)
                            AppendLog("Servicio " + serviceName + ": " + (method == "StartService" ? "no se pudo iniciar" : "no se pudo detener") + " (código WMI " + code + "); el cambio de inicio se aplicará al reiniciar.", "warn", "Optimización");
                    }
                }
            }
            catch (Exception ex) { AppendLog("Servicio " + serviceName + ": " + ex.Message, "warn", "Optimización"); }
            return result;
        }

        private string RunSystemToolDirect(string exeName, string arguments)
        {
            int code;
            string output = RunSystemToolDirect(exeName, arguments, out code);
            return RequireSuccessfulToolResult(exeName, code, output);
        }

        internal static string RequireSuccessfulToolResult(string tool, int exitCode, string output)
        {
            if (exitCode != 0) throw new InvalidOperationException(tool + " devolvió el código " + exitCode + (string.IsNullOrWhiteSpace(output) ? "" : ": " + LastMeaningfulLines(output, 2)));
            return output;
        }

        // Bare tool names resolve to System32 only: otherwise Windows looks next to Blackwall.exe first and a planted
        // powercfg.exe in Downloads would run as administrator. A timed-out tool is killed, never left running.
        private string RunSystemToolDirect(string exeName, string arguments, out int exitCode, int timeoutMs = 180000)
        {
            exitCode = -1;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = SystemToolPath(exeName),
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
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { RunSystemToolDirect("taskkill.exe", "/PID " + p.Id + " /T /F"); } catch (InvalidOperationException) { }
                        return "Tiempo de espera agotado; se detuvo " + Path.GetFileName(exeName);
                    }
                    string sOut = outTask.Result;
                    string sErr = errTask.Result;
                    exitCode = p.ExitCode;
                    return string.IsNullOrWhiteSpace(sOut) ? sErr.Trim() : sOut.Trim();
                }
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        internal static string SystemToolPath(string exeName)
        {
            return Path.IsPathRooted(exeName) ? exeName : Path.Combine(Environment.SystemDirectory, exeName);
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

                // Never another user's folder: anyone can drop a winget.exe there and Blackwall would run it as administrator.
                // 2. Buscar en C:\Program Files\WindowsApps\Microsoft.DesktopAppInstaller_*_x64__8wekyb3d8bbwe
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
            var activity = BeginOperation("Instalar · " + name, "Instalación", 3);

            // Paso 1: Intentar con WinGet directo si no está forzado el modo HTTPS directo
            if (!_preferDirectHttpsDownload)
            {
                string wingetPath = ResolveWingetExecutablePath();
                if (!string.IsNullOrEmpty(wingetPath))
                {
                    SetStatus("📦 Instalando " + name + " mediante WinGet nativo...", wgId);
                    StepOperation(activity, "WinGet · instalando " + wgId);

                    int exitCode = await Task.Run(() =>
                    {
                        int code;
                        string sOut = RunSystemToolDirect(wingetPath, "install --id \"" + wgId + "\" --exact --silent --accept-package-agreements --accept-source-agreements --disable-interactivity", out code, 300000);
                        if (!string.IsNullOrWhiteSpace(sOut)) AppendLog(LastMeaningfulLines(sOut, 3), "WINGET");
                        return code;
                    });

                    // ExitCode 0 = instalado; -1978335189 (0x8A15002B) = ya instalado en última versión
                    if (exitCode == 0 || exitCode == -1978335189)
                    {
                        installedOk = true;
                        StepOperation(activity, "WinGet terminó correctamente", true, false);
                    }
                    else
                    {
                        AppendLog("WinGet devolvió el código " + exitCode + "; se usa la descarga directa del fabricante.", "FALLBACK");
                    }
                }
                else
                {
                    AppendLog("WinGet no está disponible; se usa la descarga directa del fabricante.", "FALLBACK");
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
                    StepOperation(activity, "Descargando desde " + new Uri(fbUrl).Host);

                    using (WebClient wc = new WebClient())
                    {
                        wc.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Blackwall/1.2");
                        int lastPercent = -1; // the event fires per received chunk: update the UI only when the percentage moves
                        wc.DownloadProgressChanged += (s, ev) =>
                        {
                            if (ev.ProgressPercentage == lastPercent) return;
                            lastPercent = ev.ProgressPercentage;
                            double mbRecv = Math.Round(ev.BytesReceived / (1024.0 * 1024.0), 1);
                            SetStatus("⬇️ Descargando " + name + ": " + ev.ProgressPercentage + "% (" + mbRecv + " MB)", ev.ProgressPercentage + "%");
                            StepOperation(activity, "Descargando " + ev.ProgressPercentage + " % (" + mbRecv + " MB)", false, false);
                        };
                        await wc.DownloadFileTaskAsync(new Uri(fbUrl), targetFile);
                    }

                    // A download is only run as administrator when Windows trusts its publisher signature.
                    StepOperation(activity, "Comprobando la firma digital del instalador");
                    if (!await Task.Run(() => Authenticode.IsTrusted(targetFile)))
                    {
                        try { File.Delete(targetFile); } catch (IOException) { }
                        throw new InvalidDataException("el instalador descargado no tiene una firma digital válida y no se ejecutó");
                    }

                    SetStatus("⚙️ Ejecutando instalador silencioso de " + name + "...", silentArgs);
                    StepOperation(activity, "Ejecutando el instalador oficial en modo silencioso");

                    int installerCode = await Task.Run(() =>
                    {
                        bool msi = ext.Equals(".msi", StringComparison.OrdinalIgnoreCase);
                        // ShellExecute so an installer that needs elevation can still ask for it.
                        using (Process p = Process.Start(new ProcessStartInfo(msi ? SystemToolPath("msiexec.exe") : targetFile, msi ? "/i \"" + targetFile + "\" " + silentArgs : silentArgs) { UseShellExecute = true }))
                        {
                            if (!p.WaitForExit(600000)) { try { p.Kill(); } catch (InvalidOperationException) { } throw new TimeoutException("el instalador no terminó en 10 minutos y se detuvo"); }
                            int code = p.ExitCode;
                            try { File.Delete(targetFile); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                            return code;
                        }
                    });
                    // 3010 / 1641: installed, restart required or started.
                    if (installerCode != 0 && installerCode != 3010 && installerCode != 1641) throw new InvalidOperationException("el instalador terminó con el código " + installerCode);
                    if (installerCode != 0) AppendLog(name + " necesita reiniciar Windows para terminar la instalación.", "warn", "Instalación");
                    installedOk = true;
                }
                catch (Exception ex)
                {
                    AppendLog("Error en descarga/instalación de " + name + ": " + ex.Message, "error", "Instalación");
                    SetStatus("🔴 Error al instalar " + name, "Error");
                }
            }

            CompleteOperation(activity, installedOk, name + (installedOk ? ": instalado" : ": no se pudo instalar"));
            await RefreshInstalledAppsNativeAsync();
            if (installedOk)
            {
                SetStatus("🟢 " + name + " instalado correctamente", "Listo");
            }
        }

        private async Task InstallSelectedAppsBatchAsync()
        {
            var chosen = _apps.Where(a => _selectedApps.Contains(GetStr(a, "id"))).ToList();
            var activity = BeginOperation("Instalar " + chosen.Count + " aplicaciones", "Instalación", chosen.Count);
            for (int i = 0; i < chosen.Count; i++)
            {
                StepOperation(activity, GetStr(chosen[i], "name"), true, false);
                await InstallSingleAppAsync(chosen[i]);
            }
            CompleteOperation(activity, true, "Instalación de " + chosen.Count + " aplicaciones terminada");
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
            bool updated = await RunOperationAsync("Actualizar todas las aplicaciones", "Instalación", op => Task.Run(() =>
            {
                StepOperation(op, "WinGet · upgrade --all");
                int code;
                string res = RunSystemToolDirect(wingetPath, "upgrade --all --include-unknown --silent --accept-package-agreements --accept-source-agreements --disable-interactivity", out code, 1800000);
                if (!string.IsNullOrWhiteSpace(res)) AppendLog(LastMeaningfulLines(res, 5), "WINGET");
                if (code != 0) throw new InvalidOperationException("WinGet devolvió el código " + code);
                return "Aplicaciones actualizadas";
            }));
            await RefreshInstalledAppsNativeAsync();
            if (updated) SetStatus("🟢 Todas las aplicaciones actualizadas", "Listo");
        }

        // ====================================================================
        // PUNTOS DE RESTAURACIÓN NATIVOS WMI (SIN POWERSHELL)
        // ====================================================================
        private uint InvokeSystemRestore(string method, string argument)
        {
            try
            {
                using (var sr = new ManagementClass(new ManagementScope("\\\\localhost\\root\\default"), new ManagementPath("SystemRestore"), new ObjectGetOptions()))
                {
                    ManagementBaseObject inParams = sr.GetMethodParameters(method);
                    if (method == "Enable") inParams["Drive"] = argument;
                    else
                    {
                        inParams["Description"] = argument;
                        inParams["RestorePointType"] = 12; // MODIFY_SETTINGS
                        inParams["EventType"] = 100;       // BEGIN_SYSTEM_CHANGE
                    }
                    return Convert.ToUInt32(sr.InvokeMethod(method, inParams, null)["ReturnValue"]);
                }
            }
            catch (Exception) { return uint.MaxValue; }
        }


        // Instance enumeration (not a projected WQL query), as Microsoft's own samples list restore points.
        // VSS can take well over 5 s to answer right after a snapshot.
        private List<Dictionary<string, object>> ListRestorePoints()
        {
            var list = new List<Dictionary<string, object>>();
            var options = new EnumerationOptions { Timeout = TimeSpan.FromSeconds(90) };
            using (var restore = new ManagementClass(new ManagementScope("\\\\.\\root\\default"), new ManagementPath("SystemRestore"), null))
            using (var instances = restore.GetInstances(options))
            {
                foreach (ManagementObject point in instances)
                    using (point)
                    {
                        var row = new Dictionary<string, object>();
                        foreach (string name in new[] { "SequenceNumber", "Description", "CreationTime" })
                            if (point[name] != null) row[name] = point[name];
                        list.Add(row);
                    }
            }
            return list;
        }

        // Every restore point is a VSS shadow copy of the system volume: a second, independent proof that the
        // point exists, used because the SystemRestore list has been seen to lag behind what rstrui shows.
        private List<KeyValuePair<string, DateTime>> SystemShadowCopies()
        {
            string drive = Path.GetPathRoot(Environment.SystemDirectory).TrimEnd('\\');
            string volume = GetWmiStr(QueryFirstWmi("root\\CIMV2", "SELECT DeviceID FROM Win32_Volume WHERE DriveLetter='" + drive + "'", true), "DeviceID", "");
            var shadows = new List<KeyValuePair<string, DateTime>>();
            foreach (var shadow in QueryAllWmi("root\\CIMV2", "SELECT ID, InstallDate, VolumeName FROM Win32_ShadowCopy", true, 60))
            {
                if (volume.Length > 0 && !string.Equals(GetWmiStr(shadow, "VolumeName", ""), volume, StringComparison.OrdinalIgnoreCase)) continue;
                string installed = GetWmiStr(shadow, "InstallDate", "");
                if (installed.Length == 0) continue;
                shadows.Add(new KeyValuePair<string, DateTime>(GetWmiStr(shadow, "ID", ""), ManagementDateTimeConverter.ToDateTime(installed)));
            }
            return shadows;
        }

        private string _restorePointStatus = "none";

        // "ok" only while Windows still has the point Blackwall verified (in its list or as its shadow copy);
        // "deleted" when both are gone.
        private string CheckRecordedRestorePoint()
        {
            if (!IsAdministrator()) return _restorePointStatus = "unavailable";
            if (_restorePointRecord == null) return _restorePointStatus = "none";
            // An unreadable list is not "needs admin": offering to create one surfaces the real error.
            try
            {
                long sequence = Convert.ToInt64(_restorePointRecord["sequence"]);
                string shadow = GetStr(_restorePointRecord, "shadowId");
                bool listed = ListRestorePoints().Any(p => GetWmiLong(p, "SequenceNumber", -1) == sequence);
                bool snapshot = shadow.Length > 0 && SystemShadowCopies().Any(s => string.Equals(s.Key, shadow, StringComparison.OrdinalIgnoreCase));
                return _restorePointStatus = listed || snapshot ? "ok" : "deleted";
            }
            catch (Exception) { return _restorePointStatus = "none"; }
        }

        private async Task<string> CreateVerifiedRestorePointAsync()
        {
            string error = await Task.Run(() =>
            {
                try { return CreateVerifiedRestorePointCore(); }
                catch (Exception ex) { return "No se pudo crear o comprobar el punto de restauración: " + ex.Message; }
            });
            if (error != null) { AppendLog(error, "warn", "Seguridad"); return error; }
            try { SaveJournal(); }
            catch (Exception ex) { return "El punto de restauración se creó, pero no se pudo guardar su registro: " + ex.Message; }
            AppendLog("Punto de restauración " + RestorePointSummary() + " creado y verificado en Windows.", "SAFETY");
            return null;
        }

        // The 24 h creation limit is lifted just for this call and restored afterwards with its original type.
        // Success is never Windows' word alone: the new point must show up in its list (by our unique description)
        // or as a new shadow copy of the system volume. Returns null on success, otherwise the reason shown.
        private string CreateVerifiedRestorePointCore()
        {
            if (!IsAdministrator()) return "Abre Blackwall como administrador para crear puntos de restauración.";
            string drive = Path.GetPathRoot(Environment.SystemDirectory);
            string description = "Blackwall · antes de optimizar · " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " · " + Guid.NewGuid().ToString("N").Substring(0, 6);
            var listedBefore = new HashSet<long>(ListRestorePoints().Select(p => GetWmiLong(p, "SequenceNumber", -1)));
            var shadowsBefore = new HashSet<string>(SystemShadowCopies().Select(s => s.Key), StringComparer.OrdinalIgnoreCase);
            DateTime started = DateTime.Now.AddSeconds(-5);
            const string limitKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore";
            object limit = null;
            RegistryValueKind limitKind = RegistryValueKind.DWord;
            using (var k = Registry.LocalMachine.OpenSubKey(limitKey))
                if (k != null && (limit = k.GetValue("SystemRestorePointCreationFrequency")) != null) limitKind = k.GetValueKind("SystemRestorePointCreationFrequency");
            int code;
            long sequence;
            try
            {
                using (var k = Registry.LocalMachine.CreateSubKey(limitKey)) k.SetValue("SystemRestorePointCreationFrequency", 0, RegistryValueKind.DWord);
                code = RestorePointApi.Create(description, out sequence);
                // 1058: System Protection is off on the Windows drive, which the dialog said Blackwall turns on.
                if (code == 1058 && InvokeSystemRestore("Enable", drive) == 0) code = RestorePointApi.Create(description, out sequence);
            }
            finally
            {
                using (var k = Registry.LocalMachine.CreateSubKey(limitKey))
                {
                    if (limit == null) k.DeleteValue("SystemRestorePointCreationFrequency", false);
                    else k.SetValue("SystemRestorePointCreationFrequency", limit, limitKind);
                }
            }
            if (code != 0) return "Windows no creó el punto de restauración (" + new System.ComponentModel.Win32Exception(code).Message + ", código " + code + "). Comprueba que la protección del sistema esté activada en " + drive.TrimEnd('\\') + " y que haya espacio libre.";

            // Windows may publish the point a little later: look again for up to 30 s, never create another one.
            for (int attempt = 0; attempt < 15; attempt++)
            {
                if (attempt > 0) System.Threading.Thread.Sleep(2000);
                var listed = ListRestorePoints().FirstOrDefault(p => GetWmiStr(p, "Description", "") == description
                    || (GetWmiLong(p, "SequenceNumber", -1) == sequence && !listedBefore.Contains(sequence)));
                var shadow = SystemShadowCopies().Where(s => !shadowsBefore.Contains(s.Key) && s.Value >= started).OrderByDescending(s => s.Value).FirstOrDefault();
                if (listed == null && shadow.Key == null) continue;
                _restorePointRecord = new Dictionary<string, object>
                {
                    { "sequence", listed != null ? GetWmiLong(listed, "SequenceNumber", sequence) : sequence },
                    { "description", description },
                    { "created", listed != null ? FormatWmiDate(GetWmiStr(listed, "CreationTime", "")) : shadow.Value.ToString("dd/MM/yyyy HH:mm") },
                    { "shadowId", shadow.Key ?? "" }
                };
                if (shadow.Key == null)
                {
                    // Listed but its shadow copy not identified yet: take the newest one created since we started.
                    var any = SystemShadowCopies().Where(s => s.Value >= started).OrderByDescending(s => s.Value).FirstOrDefault();
                    _restorePointRecord["shadowId"] = any.Key ?? "";
                }
                _restorePointStatus = "ok";
                return null;
            }
            return "Windows aceptó crear el punto #" + sequence + ", pero no aparece en su lista ni como copia de seguridad del disco tras 30 segundos. No se aplicará ningún cambio; vuelve a intentarlo o revisa la protección del sistema.";
        }

        private string RestorePointSummary()
        {
            return _restorePointRecord == null ? "Sin punto verificado" : "#" + GetStr(_restorePointRecord, "sequence") + " · " + GetStr(_restorePointRecord, "created");
        }

        private async Task CreateRestorePointNowAsync()
        {
            if (!TryBeginSystemChange("Crear punto de restauración")) return;
            try { await CreateRestorePointNowCoreAsync(); }
            finally { EndSystemChange(); }
        }

        private async Task CreateRestorePointNowCoreAsync()
        {
            SetStatus("🛡️ Creando punto de restauración…", "Puede tardar hasta un minuto");
            var activity = BeginOperation("Crear punto de restauración", "Seguridad", 0);
            string error = await CreateVerifiedRestorePointAsync();
            CompleteOperation(activity, error == null, error ?? "Punto de restauración " + RestorePointSummary() + " verificado");
            if (error == null) SetStatus("🟢 Punto de restauración verificado", RestorePointSummary());
            else { AppendLog(error, "WARN"); SetStatus("🟡 " + error, "Aviso"); }
            await RefreshRestorePointsNativeAsync();
        }

        // Changes only start once the user saw what will change and Windows lists a verified restore point.
        private async Task<List<Dictionary<string, object>>> ConfirmApplyAsync(string label, List<Dictionary<string, object>> tweaks)
        {
            bool needsPreview = tweaks.Count > 1 || tweaks.Any(IsRiskyTweak);
            var chosen = needsPreview ? ShowApplyPreviewDialog(label, tweaks) : tweaks;
            if (chosen == null || chosen.Count == 0) return null;
            return await EnsureRestorePointAsync() ? chosen : null;
        }

        // "Activo" does not mean Blackwall applied it: with no saved values, reverting writes the catalog's values
        // over whatever the user has, so it is spelled out first (and journaled, so it can still be undone).
        private bool ConfirmCatalogRevert(Dictionary<string, object> t)
        {
            string title = GetStr(t, "shortTitle"); if (string.IsNullOrEmpty(title)) title = GetStr(t, "title");
            StackPanel body;
            WrapPanel actions;
            Window dialog = CreateAppDialog("Restablecer «" + title + "»", 520, out body, out actions);
            body.Children.Add(DialogText("Blackwall no tiene guardados los valores que había antes de este ajuste (se aplicó con otra herramienta o con una versión anterior). Revertirlo escribe los valores del catálogo, que pueden no ser los tuyos:", TextPrimary, 12.5));
            var list = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            foreach (var op in ConvertToDictList(t.ContainsKey("undoOps") ? t["undoOps"] : null))
                list.Children.Add(new TextBlock { Text = "• " + DescribeTweakOperation(op), Foreground = TextSecondary, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1) });
            body.Children.Add(list);
            body.Children.Add(DialogText("Antes se guardarán los valores actuales, así que podrás deshacerlo con «Revertir todo».", TextSecondary, 12));
            bool confirmed = false;
            AddDialogButton(actions, "Cancelar", false).Click += (s, e) => dialog.Close();
            AddDialogButton(actions, "Restablecer", true).Click += (s, e) => { confirmed = true; dialog.Close(); };
            ShowAppDialog(dialog);
            return confirmed;
        }

        private bool IsRiskyTweak(Dictionary<string, object> t)
        {
            string risk = GetStr(t, "riskLevel");
            return risk == "medium" || risk == "high";
        }

        private Task<bool> EnsureRestorePointAsync() { return _ensureRestorePoint(); }

        private async Task<bool> EnsureRestorePointCoreAsync()
        {
            string status = await Task.Run(() => CheckRecordedRestorePoint());
            return status == "ok" || ShowRestorePointDialog(status);
        }

        // A point that disappeared (System Protection off, disk cleanup, quota) is demanded again right away.
        private async Task WatchRestorePointAsync()
        {
            if (_restorePointRecord == null || UpdateOnly || _restorePointDialogOpen) return;
            _lastRestorePointWatch = DateTime.UtcNow;
            string status = await Task.Run(() => CheckRecordedRestorePoint());
            if (status == "deleted" && IsVisible && _otaDialog == null) ShowRestorePointDialog(status);
        }

        private Window CreateAppDialog(string title, double width, out StackPanel body, out WrapPanel actions)
        {
            var dialog = new Window { Title = title, Width = Math.Min(width, SystemParameters.WorkArea.Width - 32), SizeToContent = SizeToContent.Height, MaxHeight = SystemParameters.WorkArea.Height - 40, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Owner = IsLoaded ? this : null, WindowStartupLocation = IsLoaded ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen, ShowInTaskbar = false, Background = BgApp, Foreground = TextPrimary, FontFamily = FontFamily, Icon = Icon };
            WindowChrome.SetWindowChrome(dialog, new WindowChrome { CaptionHeight = 0, CornerRadius = new CornerRadius(18), GlassFrameThickness = new Thickness(0), ResizeBorderThickness = new Thickness(0) });
            dialog.Resources = Resources;
            KeyboardNavigation.SetTabNavigation(dialog, KeyboardNavigationMode.Cycle);
            dialog.PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape) { dialog.Close(); e.Handled = true; } };
            body = new StackPanel { Margin = new Thickness(28, 24, 28, 16) };
            body.Children.Add(new TextBlock { Text = title, Foreground = TextPrimary, FontSize = 18, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
            actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
            var layout = new DockPanel();
            var footer = new Border { Background = BgSidebar, BorderBrush = BorderSubtle, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(28, 14, 28, 14), Child = actions };
            DockPanel.SetDock(footer, Dock.Bottom);
            layout.Children.Add(footer);
            layout.Children.Add(body);
            var frame = new Border { Background = BgApp, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(18), Child = layout };
            frame.SetResourceReference(Border.BorderBrushProperty, "MaterialEdge");
            dialog.Content = frame;
            return dialog;
        }

        private Button AddDialogButton(WrapPanel actions, string text, bool primary)
        {
            Button button = CreateStyledButton(text, primary);
            button.Margin = new Thickness(8, 0, 0, 0);
            actions.Children.Add(button);
            return button;
        }

        private void ShowAppDialog(Window dialog)
        {
            IInputElement previousFocus = Keyboard.FocusedElement;
            SetModalBackdrop(true);
            try { dialog.ShowDialog(); }
            finally { SetModalBackdrop(false); if (previousFocus != null) Keyboard.Focus(previousFocus); }
        }

        private TextBlock DialogText(string text, Brush color, double size = 13)
        {
            return new TextBlock { Text = text, Foreground = color, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
        }

        // Returns true once Windows lists a new, verified point; nothing is applied otherwise.
        private bool ShowRestorePointDialog(string status)
        {
            if (_restorePointDialogOpen) return false;
            StackPanel body;
            WrapPanel actions;
            Window dialog = CreateAppDialog(status == "deleted" ? "Se borró el punto de restauración" : status == "unavailable" ? "Se necesitan permisos de administrador" : "Primero, un punto de restauración", 480, out body, out actions);
            body.Children.Add(DialogText(status == "deleted"
                ? "El punto de restauración que Blackwall verificó ya no está en Windows. Crea uno nuevo antes de seguir cambiando el sistema."
                : status == "unavailable"
                    ? "Blackwall solo cambia el sistema con un punto de restauración verificado, y para crearlo y comprobarlo necesita abrirse como administrador."
                    : "Antes de cambiar el sistema, Blackwall crea un punto de restauración y comprueba que Windows lo guardó.", TextPrimary));
            if (status != "unavailable") body.Children.Add(DialogText("Si la protección del sistema está desactivada en C:, Blackwall la activará para poder crearlo.", TextSecondary, 12));
            TextBlock message = DialogText("", TextSecondary, 12);
            message.Visibility = Visibility.Collapsed;
            body.Children.Add(message);
            var bar = new ProgressBar { Height = 5, IsIndeterminate = true, Foreground = CyberCyanBrush, Background = BgElevated, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 6) };
            body.Children.Add(bar);
            Button later = AddDialogButton(actions, "Ahora no", false);
            Button create = AddDialogButton(actions, status == "unavailable" ? "Abrir como administrador" : "Crear y verificar", true);
            bool busy = false, ready = false;
            dialog.Closing += (s, e) => e.Cancel = busy;
            later.Click += (s, e) => dialog.Close();
            create.Click += async (s, e) =>
            {
                if (status == "unavailable") { dialog.Close(); RelaunchAsAdministrator(); return; }
                busy = true; create.IsEnabled = later.IsEnabled = false;
                bar.Visibility = Visibility.Visible; message.Visibility = Visibility.Visible; message.Foreground = TextSecondary;
                message.Text = "Creando el punto de restauración… puede tardar hasta un minuto.";
                string error = await CreateVerifiedRestorePointAsync();
                busy = false; create.IsEnabled = later.IsEnabled = true; bar.Visibility = Visibility.Collapsed;
                if (error != null) { message.Text = error; message.Foreground = RiskHighText; create.Content = "Reintentar"; return; }
                ready = true;
                dialog.Close();
            };
            dialog.Loaded += (s, e) => create.Focus();
            _restorePointDialogOpen = true;
            try { ShowAppDialog(dialog); }
            finally { _restorePointDialogOpen = false; }
            if (ready) { SetStatus("🟢 Punto de restauración verificado", RestorePointSummary()); var refresh = RefreshRestorePointsNativeAsync(); }
            return ready;
        }

        // Lists exactly what will change. Risky items start unticked and need an explicit acknowledgement.
        internal Window CreateApplyPreviewDialog(string label, List<Dictionary<string, object>> tweaks, Action<List<Dictionary<string, object>>> onApply)
        {
            StackPanel body;
            WrapPanel actions;
            Window dialog = CreateAppDialog(label, 580, out body, out actions);
            var pending = tweaks.Where(t => !(_tweakStates.ContainsKey(GetStr(t, "id")) && _tweakStates[GetStr(t, "id")])).ToList();
            int already = tweaks.Count - pending.Count;
            body.Children.Add(DialogText("Revisa qué se va a cambiar; no se aplica nada hasta que confirmes. Antes se comprobará el punto de restauración y Blackwall guardará tus valores actuales para poder revertirlo todo." + (already > 0 ? " " + already + (already == 1 ? " ajuste ya estaba aplicado y se omite." : " ajustes ya estaban aplicados y se omiten.") : ""), TextSecondary, 12.5));
            var list = new StackPanel();
            var boxes = new List<KeyValuePair<CheckBox, Dictionary<string, object>>>();
            foreach (var t in pending)
            {
                string risk = GetStr(t, "riskLevel");
                string title = GetStr(t, "shortTitle"); if (string.IsNullOrEmpty(title)) title = GetStr(t, "title");
                string summary = GetStr(t, "summary"); if (string.IsNullOrEmpty(summary)) summary = GetStr(t, "description");
                string warning = GetStr(t, "riskWarning");
                var row = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
                var box = new CheckBox { IsChecked = !IsRiskyTweak(t), Foreground = TextPrimary, Content = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap } };
                System.Windows.Automation.AutomationProperties.SetName(box, title);
                row.Children.Add(box);
                var detail = new StackPanel { Margin = new Thickness(22, 2, 0, 0) };
                detail.Children.Add(new TextBlock { Text = summary, Foreground = TextSecondary, FontSize = 12, TextWrapping = TextWrapping.Wrap });
                if (TouchesPowerPlan(t))
                    detail.Children.Add(new TextBlock { Text = "⚡ Cambia el plan de energía: antes se guarda una copia completa de tus planes", Foreground = TextSecondary, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });
                if (IsRiskyTweak(t) || !string.IsNullOrEmpty(warning))
                    detail.Children.Add(new TextBlock { Text = (risk == "high" ? "⚠ Riesgo alto: " : "⚠ Precaución: ") + (string.IsNullOrEmpty(warning) ? "puede cambiar cómo funcionan algunas partes de Windows." : warning), Foreground = risk == "high" ? RiskHighText : RiskMedText, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });
                if ((GetStr(t, "affects") + " " + warning).IndexOf("reinici", StringComparison.OrdinalIgnoreCase) >= 0)
                    detail.Children.Add(new TextBlock { Text = "↻ Requiere reiniciar", Foreground = RiskMedText, FontSize = 12, Margin = new Thickness(0, 3, 0, 0) });
                row.Children.Add(detail);
                list.Children.Add(row);
                boxes.Add(new KeyValuePair<CheckBox, Dictionary<string, object>>(box, t));
            }
            if (pending.Count == 0) list.Children.Add(DialogText("Todos los ajustes de este perfil ya están aplicados.", TextPrimary));
            body.Children.Add(new ScrollViewer { Content = list, MaxHeight = Math.Max(220, SystemParameters.WorkArea.Height - 360), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
            var acknowledge = new CheckBox { Foreground = TextPrimary, Content = new TextBlock { Text = "Entiendo los riesgos de los cambios marcados con ⚠ y quiero aplicarlos", TextWrapping = TextWrapping.Wrap } };
            var riskBox = new Border { Background = RiskHighBg, CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 6, 0, 0), Child = acknowledge, Visibility = Visibility.Collapsed };
            body.Children.Add(riskBox);
            Button cancel = AddDialogButton(actions, "Cancelar", false);
            Button apply = AddDialogButton(actions, "Aplicar", true);
            Action refresh = () =>
            {
                int count = boxes.Count(b => b.Key.IsChecked == true);
                bool risky = boxes.Any(b => b.Key.IsChecked == true && IsRiskyTweak(b.Value));
                riskBox.Visibility = risky ? Visibility.Visible : Visibility.Collapsed;
                apply.Content = count == 1 ? "Aplicar 1 cambio" : "Aplicar " + count + " cambios";
                apply.IsEnabled = count > 0 && (!risky || acknowledge.IsChecked == true);
            };
            foreach (var b in boxes) { b.Key.Checked += (s, e) => refresh(); b.Key.Unchecked += (s, e) => refresh(); }
            acknowledge.Checked += (s, e) => refresh(); acknowledge.Unchecked += (s, e) => refresh();
            cancel.Click += (s, e) => dialog.Close();
            apply.Click += (s, e) => { onApply(boxes.Where(b => b.Key.IsChecked == true).Select(b => b.Value).ToList()); dialog.Close(); };
            dialog.Loaded += (s, e) => (apply.IsEnabled ? apply : cancel).Focus();
            refresh();
            return dialog;
        }

        private List<Dictionary<string, object>> ShowApplyPreviewDialog(string label, List<Dictionary<string, object>> tweaks)
        {
            List<Dictionary<string, object>> chosen = null;
            ShowAppDialog(CreateApplyPreviewDialog(label, tweaks, selection => chosen = selection));
            return chosen;
        }

        // Undoes every journaled change, newest first, back to the values Windows had before Blackwall touched them.
        private async Task RevertAllChangesAsync()
        {
            if (!TryBeginSystemChange("Revertir todo (valores guardados)")) return;
            try { await ConfirmAndRevertAllAsync(); }
            finally { EndSystemChange(); }
        }

        private async Task ConfirmAndRevertAllAsync()
        {
            if (_changes.Count == 0) { SetStatus("No hay cambios de Blackwall que revertir", "Listo"); return; }
            StackPanel body;
            WrapPanel actions;
            Window dialog = CreateAppDialog("Revertir todos los cambios", 520, out body, out actions);
            body.Children.Add(DialogText("Se restaurarán los valores que Windows tenía antes de cada cambio de Blackwall, del más reciente al más antiguo. No hace falta el punto de restauración. Algunos cambios terminan de deshacerse al reiniciar.", TextSecondary, 12.5));
            var list = new StackPanel();
            foreach (var change in _changes.AsEnumerable().Reverse())
                list.Children.Add(new TextBlock { Text = GetStr(change, "at") + "  ·  " + GetStr(change, "title"), Foreground = TextPrimary, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) });
            body.Children.Add(new ScrollViewer { Content = list, MaxHeight = Math.Max(200, SystemParameters.WorkArea.Height - 380), VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            bool confirmed = false;
            AddDialogButton(actions, "Cancelar", false).Click += (s, e) => dialog.Close();
            Button revert = AddDialogButton(actions, "Revertir " + _changes.Count + (_changes.Count == 1 ? " cambio" : " cambios"), true);
            revert.Click += (s, e) => { confirmed = true; dialog.Close(); };
            ShowAppDialog(dialog);
            if (!confirmed) return;
            await RevertAllRecordedChangesAsync();
        }

        internal Task RevertAllRecordedChangesAsync() { return RevertRecordedChangesAsync(c => true); }

        internal async Task RevertRecordedChangesAsync(Func<Dictionary<string, object>, bool> include)
        {
            var ordered = _changes.AsEnumerable().Reverse().Where(include).ToList();
            var activity = BeginOperation("Revertir todos los cambios", "Optimización", ordered.Count);
            var failed = new List<string>();
            var blocked = new HashSet<string>();
            int reverted = 0, held = 0;
            foreach (var change in ordered)
            {
                string title = GetStr(change, "title");
                var targets = ConvertToDictList(change["restoreOps"]).Select(RestoreTarget).Where(t => t != null).ToList();
                // An older entry holds the values from *before* a change that could not be undone: replaying it on
                // top would lose the original for good, so it waits in the journal. Unrelated entries still revert.
                if (targets.Any(blocked.Contains)) { held++; AppendLog(title + ": en espera, toca un valor que no se pudo revertir", "warn", "Optimización"); continue; }
                StepOperation(activity, title, true, false);
                string error = await Task.Run(() =>
                {
                    string first = null;
                    foreach (var op in StartServicesLast(ConvertToDictList(change["restoreOps"])))
                    {
                        try { RunTweakOperation(op); LogOperation("REVERTIR TODO", GetStr(change, "id"), op, null); }
                        catch (Exception ex) { LogOperation("REVERTIR TODO", GetStr(change, "id"), op, ex); if (!GetBool(op, "allowFailure") && first == null) first = DescribeTweakOperation(op) + ": " + ex.Message; }
                    }
                    return first;
                });
                if (error != null) { failed.Add(title); targets.ForEach(t => blocked.Add(t)); AppendLog(title + ": " + error, "ERROR"); continue; }
                _changes.Remove(change);
                reverted++;
                // Saved after every entry: a crash halfway never replays an already reverted change.
                try { SaveJournal(); } catch (Exception ex) { AppendLog("No se pudo actualizar el registro de cambios: " + ex.Message, "warn", "Seguridad"); }
            }
            string summary = reverted + " de " + ordered.Count + " cambios revertidos" + (failed.Count > 0 ? " · no se pudo revertir: " + string.Join(", ", failed) + (held > 0 ? " · " + held + " en espera para no perder sus valores originales" : "") + "; revisa Actividad" : "");
            CompleteOperation(activity, failed.Count == 0, summary);
            SetStatus((failed.Count == 0 ? "🟢 " : "🟡 ") + summary, failed.Count == 0 ? "Listo" : "Consulta la consola");
            await RefreshTweaksStateNativeAsync();
            RenderCurrentTab();
        }

        // ====================================================================
        // HELPERS NATIVOS WMI Y REGISTRO
        // ====================================================================
        private Dictionary<string, object> QueryFirstWmi(string ns, string query, bool required = false)
        {
            var all = QueryAllWmi(ns, query, required);
            return all.Count > 0 ? all[0] : new Dictionary<string, object>();
        }

        private List<Dictionary<string, object>> QueryAllWmi(string ns, string query, bool required = false, int timeoutSeconds = 0)
        {
            var list = new List<Dictionary<string, object>>();
            try
            {
                var opts = new EnumerationOptions { ReturnImmediately = true, Rewindable = false, Timeout = TimeSpan.FromSeconds(timeoutSeconds > 0 ? timeoutSeconds : required ? 5 : 2) };
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
            catch (Exception ex)
            {
                if (required) throw new InvalidOperationException("No se pudo consultar WMI (" + ns + "). " + ex.Message, ex);
            }
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

        // The facts every tweak's "requires" list is checked against. Only what Windows reports counts: an unknown
        // fact is "no", so an option is offered only when the hardware is known to support it.
        internal Dictionary<string, object> BuildCapabilities(Dictionary<string, object> hw)
        {
            bool laptop = GetBool(GetDict(hw, "chassis"), "isLaptop");
            var gpus = ConvertToDictList(hw.ContainsKey("gpus") ? hw["gpus"] : null).Where(g => GetStr(g, "kind") == "dedicated" || GetStr(g, "kind") == "integrated").ToList();
            var systemDisk = ConvertToDictList(hw.ContainsKey("storage") ? hw["storage"] : null).FirstOrDefault(d => GetBool(d, "isSystemDisk"));
            byte[] power = HardwareCaps.PowerCapabilities();
            int build;
            int.TryParse(GetStr(GetDict(hw, "osNetwork"), "buildNumber"), out build);
            // Update build revision: Microsoft gates some policies on a cumulative update, e.g. 26100.3915.
            object ubrValue = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "UBR", null);
            int ubr = ubrValue is int ? (int)ubrValue : 0;
            return new Dictionary<string, object>
            {
                { "laptop", laptop },
                { "desktop", !laptop },
                { "battery", GetBool(GetDict(hw, "battery"), "present") },
                { "dedicatedGpu", gpus.Any(g => GetStr(g, "kind") == "dedicated") },
                { "integratedOnly", gpus.Count > 0 && gpus.All(g => GetStr(g, "kind") == "integrated") },
                { "nvidia", gpus.Any(g => GetStr(g, "vendor") == "NVIDIA") },
                { "amdGpu", gpus.Any(g => GetStr(g, "vendor") == "AMD") },
                { "intelGpu", gpus.Any(g => GetStr(g, "vendor") == "Intel") },
                { "hags", HardwareCaps.HagsSupported() == true },
                { "modernStandby", power != null && power[20] != 0 },
                { "s3", power != null && power[5] != 0 },
                { "hibernation", power != null && power[6] != 0 },
                { "systemSsd", systemDisk != null && GetStr(systemDisk, "mediaType") == "SSD" },
                { "systemHdd", systemDisk != null && GetStr(systemDisk, "mediaType") == "HDD" },
                { "win11", build >= 22000 },
                { "win10", build >= 10240 && build < 22000 },
                { "recallPolicy", build > 26100 || (build == 26100 && ubr >= 3915) },
                { "edge", AppInstalled("msedge.exe") },
                { "brave", AppInstalled("brave.exe") }
            };
        }

        // Browsers register their executable under App Paths; an uninstalled one leaves no file behind.
        private static bool AppInstalled(string exe)
        {
            string path = Convert.ToString(Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exe, "", null));
            return !string.IsNullOrEmpty(path) && File.Exists(Environment.ExpandEnvironmentVariables(path.Trim('"')));
        }

        private static readonly Dictionary<string, string> RequirementNames = new Dictionary<string, string>
        {
            { "laptop", "un portátil" }, { "desktop", "un PC de escritorio" }, { "battery", "batería" },
            { "dedicatedGpu", "una GPU dedicada" }, { "integratedOnly", "solo gráficos integrados" }, { "nvidia", "una GPU NVIDIA" },
            { "amdGpu", "una GPU AMD" }, { "intelGpu", "una GPU Intel" }, { "hags", "una GPU con programación acelerada por hardware (HAGS)" },
            { "modernStandby", "Modern Standby (S0)" }, { "s3", "suspensión clásica (S3)" }, { "hibernation", "hibernación" },
            { "systemSsd", "Windows instalado en un SSD" }, { "systemHdd", "Windows instalado en un disco duro" },
            { "win11", "Windows 11" }, { "win10", "Windows 10" },
            { "recallPolicy", "Windows 11 24H2 con la actualización KB5055627 (26100.3915) o posterior" },
            { "edge", "Microsoft Edge instalado" },
            { "brave", "el navegador Brave instalado" }
        };

        // Why this PC cannot take a tweak, or null when it can. A tweak lists the facts it needs in "requires"
        // ("!fact" negates one); deviceScope must include the detected device. Unknown hardware offers nothing
        // hardware-dependent.
        internal string UnmetRequirement(Dictionary<string, object> t)
        {
            if (GetBool(t, "retired")) return "se retiró: " + GetStr(t, "retiredReason");
            var caps = _hardware == null ? null : GetDict(_hardware, "capabilities");
            if (caps == null || caps.Count == 0) return "aún no se ha identificado el equipo";
            string device = GetBool(caps, "laptop") ? "laptop" : "desktop";
            if (t.ContainsKey("deviceScope") && !ListContains(t, "deviceScope", device)) return "es solo para " + (device == "laptop" ? "PC de escritorio" : "portátiles");
            if (t.ContainsKey("osCompatibility") && !(GetBool(caps, "win11") && ListContains(t, "osCompatibility", "win11")) && !(GetBool(caps, "win10") && ListContains(t, "osCompatibility", "win10")))
                return "no es compatible con tu versión de Windows";
            object raw;
            if (!t.TryGetValue("requires", out raw) || !(raw is IEnumerable) || raw is string) return null;
            foreach (object item in (IEnumerable)raw)
            {
                string requirement = Convert.ToString(item);
                bool negate = requirement.StartsWith("!");
                string key = negate ? requirement.Substring(1) : requirement;
                string name;
                if (!RequirementNames.TryGetValue(key, out name)) return "pide una comprobación desconocida (" + key + ")";
                if (GetBool(caps, key) == negate) return (negate ? "no se aplica con " : "necesita ") + name;
            }
            return null;
        }

        internal bool TweakAvailable(Dictionary<string, object> t) { return UnmetRequirement(t) == null; }

        private bool IsTweakOn(Dictionary<string, object> t)
        {
            bool on;
            return !IsPreference(t) && _tweakStates.TryGetValue(GetStr(t, "id"), out on) && on;
        }

        internal static bool IsPreference(Dictionary<string, object> t) { return t.ContainsKey("preference") && Equals(t["preference"], true); }
        internal static bool IsFrozen(Dictionary<string, object> t) { return t.ContainsKey("section") && Convert.ToString(t["section"]) == "ceito"; }

        // A preference is on when its "on" values are what the system holds; with none of its values present Windows
        // uses its own default, which the catalog states (defaultOn) instead of guessing "off".
        internal bool PreferenceOn(Dictionary<string, object> t, Func<string> activeScheme = null)
        {
            bool present = ConvertToDictList(t["applyOps"]).Concat(ConvertToDictList(t["undoOps"])).Any(op => CurrentValue(op) != "(no existe)");
            return present ? IsTweakActive(t, activeScheme) : GetBool(t, "defaultOn");
        }

        // Undoes, in one go, every active tweak this PC should not have. With no saved values the catalog's are
        // written, and each one is journaled first so Revertir todo can still undo it.
        internal async Task RevertLeftoversAsync(List<Dictionary<string, object>> leftovers)
        {
            if (!TryBeginSystemChange("Aviso: revertir ajustes que no corresponden al equipo")) return;
            try
            {
                if (!_confirmLeftoverRevert(leftovers)) return;
                var failed = new List<string>();
                foreach (var t in leftovers)
                {
                    try { await _executeTweakAction(t, false); }
                    catch (Exception ex) { failed.Add(GetStr(t, "shortTitle")); AppendLog(GetStr(t, "shortTitle") + ": " + ex.Message, "ERROR"); }
                }
                await RefreshTweaksStateNativeAsync();
                SetStatus(failed.Count == 0 ? "🟢 Revertidos " + leftovers.Count + " ajustes que no correspondían a este equipo" : "🟡 No se pudieron revertir: " + string.Join(", ", failed), failed.Count == 0 ? "Reinicia para completar" : "Consulta la consola");
            }
            finally { EndSystemChange(); }
        }

        // The problem fixer: undoes everything Blackwall left on this PC, from any version. Journaled changes go
        // back to their exact earlier values first; whatever is still detected afterwards (1.4.7 and older kept no
        // journal, or only part of a tweak applied) goes back to Windows' values. Each catalog reset is journaled.
        internal async Task ResetEverythingAsync()
        {
            if (!TryBeginSystemChange("Restablecer todo lo de Blackwall")) return;
            try
            {
                if (_hardware == null || !_loadedModules.Contains("tweaks")) await RefreshTweaksStateNativeAsync();
                var detected = _tweaks.Where(IsTweakOn).ToList();
                // The power repair and startup choices are not optimizations: resetting keeps them (Revertir todo undoes them).
                Func<Dictionary<string, object>, bool> blackwallChange = IsOptimizationChange;
                int journaled = _changes.Count(blackwallChange);
                if (journaled == 0 && detected.Count == 0) { SetStatus("🟢 No hay ajustes de Blackwall aplicados en este equipo", "Nada que restablecer"); return; }
                if (!_confirmFullReset(detected, journaled)) return;
                var activity = BeginOperation("Restablecer todo lo de Blackwall", "Optimización", 2);
                StepOperation(activity, "Valores guardados por Blackwall (" + journaled + ")");
                if (journaled > 0) await RevertRecordedChangesAsync(blackwallChange);
                // Whatever stays journaled (a value Windows refused) is retried below through its own tweak; the
                // rest of the PC is still reset instead of stopping at the first refusal.
                await RefreshTweaksStateNativeAsync();
                var remaining = _tweaks.Where(IsTweakOn).ToList();
                StepOperation(activity, "Ajustes detectados sin valores guardados (" + remaining.Count + ")");
                var errors = new Dictionary<string, string>();
                var journalBefore = new HashSet<Dictionary<string, object>>(_changes);
                foreach (var t in remaining)
                {
                    try { await _executeTweakAction(t, false); }
                    catch (Exception ex) { errors[GetStr(t, "id")] = ex.Message; AppendLog(GetStr(t, "shortTitle") + ": " + ex.Message, "ERROR"); }
                }
                await RefreshTweaksStateNativeAsync();
                // Those catalog resets were journaled only so a crash halfway could be undone. Once a tweak is back
                // on Windows' values its entry would keep the red link counting it (and a second click would replay
                // Blackwall's values first), so it goes; an entry whose reset failed stays to keep the old values.
                _changes.RemoveAll(c => !journalBefore.Contains(c) && GetStr(c, "id").EndsWith("#restablecer")
                    && !_tweaks.Any(t => GetStr(t, "id") + "#restablecer" == GetStr(c, "id") && IsTweakOn(t)));
                try { SaveJournal(); } catch (Exception ex) { AppendLog("No se pudo actualizar el registro de cambios: " + ex.Message, "warn", "Seguridad"); }

                Func<Dictionary<string, object>, string> name = t => GetStr(t, "shortTitle").Length > 0 ? GetStr(t, "shortTitle") : GetStr(t, "title");
                var done = detected.Where(t => !IsTweakOn(t)).Select(name).ToList();
                string powerError = null;
                if (_repairPowerOnReset)
                {
                    _repairPowerOnReset = false;
                    if (!await EnsureRestorePointAsync()) powerError = "no se creó el punto de restauración";
                    else try { done.Add("Plan de energía: Equilibrado nuevo activo; copia de tus planes en " + await RepairPowerPlanCoreAsync()); }
                        catch (Exception ex) { powerError = ex.Message; }
                }
                var notDone = _tweaks.Where(IsTweakOn).Select(t => new KeyValuePair<string, string>(name(t),
                    errors.ContainsKey(GetStr(t, "id")) ? errors[GetStr(t, "id")] : "Sigue detectado. Reinicia Windows y pulsa «Revisar estados»; si continúa, revisa registro-cambios.log")).ToList();
                notDone.AddRange(_changes.Where(blackwallChange).Where(c => !_tweaks.Any(t => GetStr(t, "id") == GetStr(c, "id").Split('#')[0] && IsTweakOn(t)))
                    .Select(c => new KeyValuePair<string, string>(GetStr(c, "title"), "Windows no dejó restaurar el valor guardado; sigue en el registro de cambios para reintentarlo")));
                if (powerError != null) notDone.Add(new KeyValuePair<string, string>("Reparar plan de energía", powerError));
                string summary = notDone.Count == 0 ? "Todo lo de Blackwall está restablecido. Reinicia Windows para completarlo." : done.Count + " restablecidos · " + notDone.Count + " no se pudieron restablecer: " + string.Join(", ", notDone.Select(p => p.Key));
                CompleteOperation(activity, notDone.Count == 0, summary);
                SetStatus((notDone.Count == 0 ? "🟢 " : "🟡 ") + summary, notDone.Count == 0 ? "Reinicia para completar" : "Consulta Actividad");
                RenderCurrentTab();
                _reportFullReset(done, notDone);
            }
            finally { EndSystemChange(); }
        }

        private bool ConfirmFullReset(List<Dictionary<string, object>> detected, int journaled)
        {
            StackPanel body;
            WrapPanel actions;
            Window dialog = CreateAppDialog("Restablecer todo lo de Blackwall", 560, out body, out actions);
            body.Children.Add(DialogText("Se deshacen todos los ajustes de Blackwall que hay en este equipo, de cualquier versión:", TextPrimary, 12.5));
            if (journaled > 0) body.Children.Add(DialogText("• " + journaled + (journaled == 1 ? " cambio guardado vuelve" : " cambios guardados vuelven") + " a su valor anterior exacto.", TextSecondary, 12));
            if (detected.Count > 0)
            {
                body.Children.Add(DialogText("• Estos ajustes detectados vuelven a los valores de Windows:", TextSecondary, 12));
                var list = new StackPanel { Margin = new Thickness(14, 0, 0, 8) };
                foreach (var t in detected) list.Children.Add(new TextBlock { Text = GetStr(t, "shortTitle").Length > 0 ? GetStr(t, "shortTitle") : GetStr(t, "title"), Foreground = TextSecondary, FontSize = 12, TextWrapping = TextWrapping.Wrap });
                body.Children.Add(new ScrollViewer { Content = list, MaxHeight = Math.Max(160, SystemParameters.WorkArea.Height - 420), VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            }
            body.Children.Add(DialogText("Para volver atrás después, usa el punto de restauración. Al terminar verás qué se restableció y qué no. Reinicia Windows: algunos servicios y ajustes de energía se completan al arrancar.", TextSecondary, 12));
            var repairPower = new CheckBox { Content = new TextBlock { Text = "Reparar también el plan de energía: se crea una copia del plan Equilibrado con el procesador al 100 % y se guarda copia de tus planes (no se borra ninguno)", TextWrapping = TextWrapping.Wrap }, Foreground = TextPrimary, FontSize = 12, Margin = new Thickness(0, 4, 0, 0) };
            body.Children.Add(repairPower);
            bool confirmed = false;
            AddDialogButton(actions, "Cancelar", false).Click += (s, e) => dialog.Close();
            AddDialogButton(actions, "Restablecer todo", true).Click += (s, e) => { confirmed = true; _repairPowerOnReset = repairPower.IsChecked == true; dialog.Close(); };
            ShowAppDialog(dialog);
            return confirmed;
        }

        // What «Restablecer todo» really left: re-read from the system afterwards, not assumed from the clicks.
        private void ReportFullReset(List<string> done, List<KeyValuePair<string, string>> notDone)
        {
            StackPanel body;
            WrapPanel actions;
            Window dialog = CreateAppDialog(notDone.Count == 0 ? "Todo restablecido" : "Restablecer todo: resultado", 560, out body, out actions);
            var list = new StackPanel();
            if (done.Count > 0)
            {
                list.Children.Add(DialogText("Restablecidos (" + done.Count + "), comprobado leyendo el sistema:", TextPrimary, 12.5));
                foreach (string t in done) list.Children.Add(DialogText("✓ " + t, TextSecondary, 12));
            }
            if (notDone.Count > 0)
            {
                list.Children.Add(DialogText("No se pudieron restablecer (" + notDone.Count + "):", TextPrimary, 12.5));
                foreach (var p in notDone) list.Children.Add(DialogText("✗ " + p.Key + " — " + p.Value, TextSecondary, 12));
            }
            body.Children.Add(new ScrollViewer { Content = list, MaxHeight = Math.Max(200, SystemParameters.WorkArea.Height - 380), VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            body.Children.Add(DialogText("Reinicia Windows para completarlo. Cada operación y su comprobación están en " + ChangeLogPath + ".", TextSecondary, 12));
            AddDialogButton(actions, "Abrir registro", false).Click += (s, e) => { try { Process.Start(new ProcessStartInfo(ChangeLogPath) { UseShellExecute = true }); } catch (Exception ex) { AppendLog("No se pudo abrir el registro: " + ex.Message, "warn", "Seguridad"); } };
            AddDialogButton(actions, "Cerrar", true).Click += (s, e) => dialog.Close();
            ShowAppDialog(dialog);
        }

        private bool ConfirmLeftoverRevert(List<Dictionary<string, object>> leftovers)
        {
            StackPanel body;
            WrapPanel actions;
            Window dialog = CreateAppDialog("Revertir ajustes que no corresponden a este equipo", 540, out body, out actions);
            body.Children.Add(DialogText("Estos ajustes están aplicados pero no son para este equipo. Se volverá a los valores de Windows (o a los que había, si Blackwall los guardó), y antes se guardan los actuales para poder deshacerlo:", TextPrimary, 12.5));
            foreach (var t in leftovers)
                body.Children.Add(new TextBlock { Text = "• " + (GetStr(t, "shortTitle").Length > 0 ? GetStr(t, "shortTitle") : GetStr(t, "title")) + " — " + UnmetRequirement(t), Foreground = TextSecondary, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1) });
            body.Children.Add(DialogText("Algunos cambios terminan de deshacerse al reiniciar.", TextSecondary, 12));
            bool confirmed = false;
            AddDialogButton(actions, "Cancelar", false).Click += (s, e) => dialog.Close();
            AddDialogButton(actions, "Revertir", true).Click += (s, e) => { confirmed = true; dialog.Close(); };
            ShowAppDialog(dialog);
            return confirmed;
        }

        // "Portátil · AMD Radeon(TM) Graphics (integrada) · SSD · Windows 11": what the options below are tailored to.
        private string HardwareSummary()
        {
            var caps = _hardware == null ? null : GetDict(_hardware, "capabilities");
            if (caps == null || caps.Count == 0) return "Identificando el equipo…";
            var gpu = ConvertToDictList(_hardware.ContainsKey("gpus") ? _hardware["gpus"] : null).FirstOrDefault();
            var parts = new List<string> { GetBool(caps, "laptop") ? "Portátil" : "PC de escritorio" };
            if (gpu != null) parts.Add(GetStr(gpu, "name") + " (" + GetStr(gpu, "kindLabel").ToLowerInvariant() + ")");
            if (GetBool(caps, "systemSsd")) parts.Add("SSD"); else if (GetBool(caps, "systemHdd")) parts.Add("disco duro");
            parts.Add(GetBool(caps, "win11") ? "Windows 11" : GetBool(caps, "win10") ? "Windows 10" : "Windows");
            return string.Join(" · ", parts);
        }

        private const string DisplayClass =@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

        // Every display adapter, read the way Ceito does: from the driver's own registry entry (vendor by PCI ID,
        // real 64-bit memory size, driver version/date/provider). WMI lists only present adapters and adds the
        // live resolution; when WMI is slow or broken the driver class alone still answers. Dedicated GPUs come
        // first so a desktop's NVIDIA card is never hidden behind the CPU's integrated graphics. Unknown values
        // stay empty: nothing is invented.
        internal List<Dictionary<string, object>> ReadGpus()
        {
            var gpus = new List<Dictionary<string, object>>();
            foreach (var w in QueryAllWmi("root\\CIMV2", "SELECT Name, PNPDeviceID, AdapterRAM, DriverVersion, DriverDate, CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate FROM Win32_VideoController", false, 20))
            {
                string pnp = GetWmiStr(w, "PNPDeviceID", "");
                gpus.Add(BuildGpu(GetWmiStr(w, "Name", ""), pnp, DisplayDriverKeyFor(pnp), w));
            }
            if (gpus.Count == 0)
                using (var cls = Registry.LocalMachine.OpenSubKey(DisplayClass))
                    if (cls != null)
                        foreach (string sub in cls.GetSubKeyNames().Where(n => Regex.IsMatch(n, "^[0-9]{4}$")))
                        {
                            var reg = ReadDisplayDriverKey(DisplayClass + "\\" + sub);
                            if (reg != null && GetStr(reg, "MatchingDeviceId").StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase))
                                gpus.Add(BuildGpu(GetStr(reg, "DriverDesc"), GetStr(reg, "MatchingDeviceId"), reg, null));
                        }
            return gpus.OrderBy(g => GetStr(g, "kind") == "dedicated" ? 0 : GetStr(g, "kind") == "integrated" ? 1 : 2).ToList();
        }

        private Dictionary<string, object> DisplayDriverKeyFor(string pnpDeviceId)
        {
            if (string.IsNullOrEmpty(pnpDeviceId)) return null;
            try
            {
                using (var device = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\" + pnpDeviceId))
                {
                    string driver = device == null ? null : device.GetValue("Driver") as string; // "{4d36e968-…}\0000"
                    return string.IsNullOrEmpty(driver) ? null : ReadDisplayDriverKey(@"SYSTEM\CurrentControlSet\Control\Class\" + driver);
                }
            }
            catch (System.Security.SecurityException) { return null; }
        }

        private static Dictionary<string, object> ReadDisplayDriverKey(string path)
        {
            using (var k = Registry.LocalMachine.OpenSubKey(path))
            {
                if (k == null) return null;
                var values = new Dictionary<string, object>();
                foreach (string name in new[] { "DriverDesc", "ProviderName", "DriverVersion", "DriverDate", "MatchingDeviceId", "HardwareInformation.qwMemorySize", "HardwareInformation.MemorySize" })
                {
                    object v = k.GetValue(name);
                    if (v != null) values[name] = v;
                }
                return values;
            }
        }

        // Integrated vs dedicated from the PCI vendor and the product name: NVIDIA ships no PC iGPU; Intel is
        // integrated except Arc A/B desktop cards; AMD is dedicated only for RX/Pro/FirePro boards ("Radeon(TM)
        // Graphics", "780M" and "RX Vega 11 Graphics" are APUs).
        internal static string ClassifyGpu(string vendorId, string name)
        {
            string n = (name ?? "").ToUpperInvariant();
            if (vendorId == "10DE") return "dedicated";
            if (vendorId == "8086") return Regex.IsMatch(n, @"ARC(\(TM\))?\s*[AB][0-9]{3}") ? "dedicated" : "integrated";
            if (vendorId == "1002" || vendorId == "1022")
            {
                bool apuName = n.Contains("GRAPHICS") && !Regex.IsMatch(n, @"\bRX\s*[0-9]{3,4}");
                return !apuName && Regex.IsMatch(n, @"\bRX\b|RADEON\s+PRO|FIREPRO|\bR[579]\s+[0-9]{3}|RADEON\s+VII|\bVEGA\s*(56|64)\b") ? "dedicated" : "integrated";
            }
            if (vendorId == "1414" || vendorId == "15AD" || vendorId == "80EE" || vendorId == "1AB8" || vendorId == "1234") return "virtual";
            return "unknown";
        }

        private Dictionary<string, object> BuildGpu(string name, string pnp, Dictionary<string, object> reg, Dictionary<string, object> wmi)
        {
            Match ven = Regex.Match(pnp ?? "", @"VEN_([0-9A-Fa-f]{4})");
            string vendorId = ven.Success ? ven.Groups[1].Value.ToUpperInvariant() : "";
            if (string.IsNullOrEmpty(name)) name = GetStr(reg, "DriverDesc");
            string vendor = vendorId == "10DE" ? "NVIDIA" : vendorId == "1002" || vendorId == "1022" ? "AMD" : vendorId == "8086" ? "Intel" : vendorId == "1414" ? "Microsoft" : GetStr(reg, "ProviderName");
            string kind = pnp != null && !pnp.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase) && pnp.Length > 0 ? "virtual" : ClassifyGpu(vendorId, name);
            // The 32-bit AdapterRAM wraps above 4 GB; the driver's QWORD is the real figure.
            long memory = RegistryBytes(reg, "HardwareInformation.qwMemorySize");
            if (memory <= 0) memory = RegistryBytes(reg, "HardwareInformation.MemorySize");
            if (memory <= 0 && wmi != null) { long adapterRam = GetWmiLong(wmi, "AdapterRAM", 0); if (adapterRam > 0 && adapterRam < uint.MaxValue) memory = adapterRam; }
            string version = GetStr(reg, "DriverVersion"); if (version.Length == 0 && wmi != null) version = GetWmiStr(wmi, "DriverVersion", "");
            string date = ParseDriverDate(GetStr(reg, "DriverDate")); if (date.Length == 0 && wmi != null) { date = FormatWmiDate(GetWmiStr(wmi, "DriverDate", "")); if (date == "N/A") date = ""; }
            int width = wmi == null ? 0 : GetWmiInt(wmi, "CurrentHorizontalResolution", 0), height = wmi == null ? 0 : GetWmiInt(wmi, "CurrentVerticalResolution", 0), hz = wmi == null ? 0 : GetWmiInt(wmi, "CurrentRefreshRate", 0);
            var gpu = new Dictionary<string, object>
            {
                { "name", name.Length > 0 ? name : "GPU sin nombre" },
                { "vendor", vendor },
                { "vendorId", vendorId },
                { "kind", kind },
                { "kindLabel", kind == "dedicated" ? "Dedicada" : kind == "integrated" ? "Integrada" : kind == "virtual" ? "Virtual / remota" : "Sin clasificar" },
                { "vramGb", memory > 0 ? (object)Math.Round(memory / 1073741824.0, 2) : "No detectado" },
                { "driverVersion", version.Length > 0 ? version : "No detectado" },
                { "driverDate", date.Length > 0 ? date : "No detectado" },
                { "driverProvider", GetStr(reg, "ProviderName").Length > 0 ? GetStr(reg, "ProviderName") : "No detectado" },
                { "resolution", width > 0 && height > 0 ? width + "x" + height : "Sin pantalla activa" },
                { "refreshRateHz", hz > 1 ? (object)hz : "—" }
            };
            // NVIDIA's own version number (e.g. 31.0.15.6094 → 560.94) is what its driver pages use.
            string digits = version.Replace(".", "");
            if (vendor == "NVIDIA" && digits.Length >= 5) gpu["nvidiaDriver"] = digits.Substring(digits.Length - 5, 3) + "." + digits.Substring(digits.Length - 2);
            return gpu;
        }

        private static long RegistryBytes(Dictionary<string, object> reg, string name)
        {
            object v;
            if (reg == null || !reg.TryGetValue(name, out v)) return 0;
            if (v is long) return (long)v;
            if (v is int) return unchecked((uint)(int)v);
            var bytes = v as byte[];
            if (bytes != null && bytes.Length >= 8) return BitConverter.ToInt64(bytes, 0);
            if (bytes != null && bytes.Length >= 4) return BitConverter.ToUInt32(bytes, 0);
            return 0;
        }

        // Display drivers store DriverDate as "M-d-yyyy".
        private static string ParseDriverDate(string raw)
        {
            DateTime date;
            return DateTime.TryParseExact(raw ?? "", new[] { "M-d-yyyy", "MM-dd-yyyy" }, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out date) ? date.ToString("yyyy-MM-dd") : "";
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
            if (_network != null) _network.Dark = _isDark;
            if (BgApp == null)
            {
                // Primera inicialización de pinceles
                BgApp = BrushHex(_isDark ? "#090B10" : "#D1D6DE");
                BgSidebar = BrushHex(_isDark ? "#0D1016" : "#DADEE4");
                BgSurface = BrushHex(_isDark ? "#141820" : "#DFE3E8");
                BgElevated = BrushHex(_isDark ? "#1C222D" : "#C2CAD4");
                BorderSubtle = BrushHex(_isDark ? "#424C5D" : "#748294");
                TextPrimary = BrushHex(_isDark ? "#EEF2F7" : "#24303E");
                TextSecondary = BrushHex(_isDark ? "#A7B0BF" : "#465464");
                AccentBrush = BrushHex(_isDark ? "#8F1828" : "#8F1828");
                CyberCyanBrush = BrushHex(_isDark ? "#FF8B80" : "#873A36");
                CyberYellowBrush = BrushHex(_isDark ? "#FF8B80" : "#873A36");
                RiskLowBg = BrushHex(_isDark ? "#0B1E19" : "#CCDCD2");
                RiskLowText = BrushHex(_isDark ? "#8BD8B5" : "#235C3C");
                RiskMedBg = BrushHex(_isDark ? "#2A2207" : "#DCD2BC");
                RiskMedText = BrushHex(_isDark ? "#E8C080" : "#6B4C15");
                RiskHighBg = BrushHex(_isDark ? "#2B0A11" : "#E5CDD1");
                RiskHighText = BrushHex(_isDark ? "#FF8998" : "#9F283D");
            }
            else
            {
                // Mutación reactiva en caliente sin destruir el árbol visual
                BgApp.Color = ColorHex(_isDark ? "#090B10" : "#D1D6DE");
                BgSidebar.Color = ColorHex(_isDark ? "#0D1016" : "#DADEE4");
                BgSurface.Color = ColorHex(_isDark ? "#141820" : "#DFE3E8");
                BgElevated.Color = ColorHex(_isDark ? "#1C222D" : "#C2CAD4");
                BorderSubtle.Color = ColorHex(_isDark ? "#424C5D" : "#748294");
                TextPrimary.Color = ColorHex(_isDark ? "#EEF2F7" : "#24303E");
                TextSecondary.Color = ColorHex(_isDark ? "#A7B0BF" : "#465464");
                AccentBrush.Color = ColorHex(_isDark ? "#8F1828" : "#8F1828");
                CyberCyanBrush.Color = ColorHex(_isDark ? "#FF8B80" : "#873A36");
                CyberYellowBrush.Color = ColorHex(_isDark ? "#FF8B80" : "#873A36");
                RiskLowBg.Color = ColorHex(_isDark ? "#0B1E19" : "#CCDCD2");
                RiskLowText.Color = ColorHex(_isDark ? "#8BD8B5" : "#235C3C");
                RiskMedBg.Color = ColorHex(_isDark ? "#2A2207" : "#DCD2BC");
                RiskMedText.Color = ColorHex(_isDark ? "#E8C080" : "#6B4C15");
                RiskHighBg.Color = ColorHex(_isDark ? "#2B0A11" : "#E5CDD1");
                RiskHighText.Color = ColorHex(_isDark ? "#FF8998" : "#9F283D");
            }
            // WPF may freeze template resources; keep the mutable palette outside that dictionary.
            Resources["CyberFocusBrush"] = CyberCyanBrush.Clone();
            Resources["CyberTrackBrush"] = BorderSubtle.Clone();
            Resources[SystemColors.HighlightBrushKey] = CyberCyanBrush.Clone();
            Resources[SystemColors.HighlightTextBrushKey] = BgSidebar.Clone();
            Resources["MaterialSurface"] = MaterialGradient(_isDark ? "#1B1D27" : "#E3E6EB", _isDark ? "#10121A" : "#D4DAE2");
            Resources["MaterialButtonSurface"] = MaterialGradient(_isDark ? "#303947" : "#D9DEE6", _isDark ? "#141920" : "#BCC6D3");
            Resources["DashboardButtonSurface"] = MaterialGradient(_isDark ? "#252A32" : "#D9DEE6", _isDark ? "#090B0E" : "#BCC6D3");
            Resources["MaterialSilver"] = MaterialGradient(_isDark ? "#F4F7FC" : "#CBD3DE", _isDark ? "#AAB6C7" : "#AAB7C8");
            var edge = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
            edge.GradientStops.Add(new GradientStop(ColorHex(_isDark ? "#B3BECE" : "#B8C2CE"), 0));
            edge.GradientStops.Add(new GradientStop(ColorHex(_isDark ? "#424C5D" : "#8A96A5"), 0.19));
            edge.GradientStops.Add(new GradientStop(ColorHex(_isDark ? "#252D39" : "#C5CCD5"), 0.55));
            edge.GradientStops.Add(new GradientStop(ColorHex(_isDark ? "#8E9AAC" : "#8E9AAC"), 0.88));
            edge.GradientStops.Add(new GradientStop(ColorHex(_isDark ? "#303A49" : "#BCC7D6"), 1));
            Resources["MaterialEdge"] = edge;
            var dashboardEdge = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
            SetDashboardEdgeMotion(false);
            _dashboardEdgeRotation = new RotateTransform(0, 0.5, 0.5);
            dashboardEdge.RelativeTransform = _dashboardEdgeRotation;
            dashboardEdge.GradientStops.Add(new GradientStop(ColorHex(_isDark ? "#F2F4F8" : "#788594"), 0));
            dashboardEdge.GradientStops.Add(new GradientStop(ColorHex(_isDark ? "#C9CED7" : "#96A2AF"), 0.08));
            dashboardEdge.GradientStops.Add(new GradientStop(ColorHex(_isDark ? "#7D8797" : "#ADB7C2"), 0.22));
            dashboardEdge.GradientStops.Add(new GradientStop(ColorHex(_isDark ? "#353B49" : "#CFD5DC"), 0.56));
            dashboardEdge.GradientStops.Add(new GradientStop(ColorHex(_isDark ? "#A2AAB8" : "#A3AFBC"), 0.73));
            dashboardEdge.GradientStops.Add(new GradientStop(ColorHex(_isDark ? "#E4E8EF" : "#8996A5"), 0.84));
            dashboardEdge.GradientStops.Add(new GradientStop(ColorHex(_isDark ? "#858FA2" : "#BCC5CF"), 1));
            Resources["DashboardButtonEdge"] = dashboardEdge;
            UpdateAmbientMotion();
        }

        private LinearGradientBrush MaterialGradient(string top, string bottom)
        {
            return new LinearGradientBrush(ColorHex(top), ColorHex(bottom), new Point(0, 0), new Point(0, 1));
        }

        private void TriggerLogoGlitch()
        {
            if (_logoBox == null || !MotionAllowed) return;
            try
            {
                DoubleAnimation anim = new DoubleAnimation
                {
                    From = 0.72,
                    To = 1.0,
                    Duration = TimeSpan.FromMilliseconds(180),
                    FillBehavior = FillBehavior.Stop
                };
                _logoBox.BeginAnimation(UIElement.OpacityProperty, anim);
            }
            catch { }
        }

        private const string GlyphMinimize = "", GlyphMaximize = "", GlyphRestore = "", GlyphClose = "";
        private static readonly ControlTemplate WindowControlTemplate = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(@"
            <ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Button'>
                <Border Background='{TemplateBinding Background}' CornerRadius='8'>
                    <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
                </Border>
            </ControlTemplate>");

        private Button CreateWindowControlButton(string glyph, string name, Action onClick, bool isClose = false)
        {
            TextBlock txt = new TextBlock
            {
                Text = glyph,
                FontFamily = NavIconFont,
                FontSize = 10.5,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = TextPrimary
            };

            Button btn = new Button
            {
                Width = 42,
                Height = 32,
                MinHeight = 0,
                Margin = new Thickness(1, 0, 1, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Content = txt,
                Template = WindowControlTemplate,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                ToolTip = name
            };
            System.Windows.Automation.AutomationProperties.SetName(btn, name);

            WindowChrome.SetIsHitTestVisibleInChrome(btn, true);

            Action highlight = () =>
            {
                btn.Background = isClose ? BrushHex("#C42B1C") : BgElevated;
                txt.Foreground = isClose ? Brushes.White : TextPrimary;
            };
            Action reset = () =>
            {
                if (btn.IsMouseOver || btn.IsKeyboardFocused) return;
                btn.Background = Brushes.Transparent;
                txt.Foreground = TextPrimary;
            };
            btn.MouseEnter += (s, e) => highlight();
            btn.GotKeyboardFocus += (s, e) => highlight();
            btn.MouseLeave += (s, e) => reset();
            btn.LostKeyboardFocus += (s, e) => reset();

            btn.Click += (s, e) => onClick();
            return btn;
        }

        private void UpdateMaximizeButton()
        {
            if (_maxBtn == null) return;
            bool maximized = WindowState == WindowState.Maximized;
            ((TextBlock)_maxBtn.Content).Text = maximized ? GlyphRestore : GlyphMaximize;
            string name = maximized ? "Restaurar" : "Maximizar";
            _maxBtn.ToolTip = name;
            System.Windows.Automation.AutomationProperties.SetName(_maxBtn, name);
        }

        // ====================================================================
        // CONSTRUCCIÓN DE LA INTERFAZ NATIVA WPF
        // ====================================================================
        private void BuildShellUI()
        {
            Background = BgApp;
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI");
            FontSize = 13;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            Resources[typeof(Button)] = (Style)System.Windows.Markup.XamlReader.Parse(@"
                <Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                       xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Button'>
                    <Setter Property='MinHeight' Value='38'/>
                    <Setter Property='BorderBrush' Value='{DynamicResource MaterialEdge}'/>
                    <Setter Property='BorderThickness' Value='1'/>
                    <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
                    <Setter Property='Template'>
                        <Setter.Value><ControlTemplate TargetType='Button'>
                            <Grid>
                            <Border x:Name='Frame'
                                BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}'
                                CornerRadius='20' SnapsToDevicePixels='True'>
                                <Grid>
                                    <Border Background='{TemplateBinding Background}' BorderBrush='#26FFFFFF' BorderThickness='0,1,0,0' CornerRadius='19' Padding='{TemplateBinding Padding}'>
                                        <ContentPresenter HorizontalAlignment='{TemplateBinding HorizontalContentAlignment}'
                                            VerticalAlignment='Center' RecognizesAccessKey='True'/>
                                    </Border>
                                    <Border x:Name='Hover' Background='#0EFFFFFF' Opacity='0' CornerRadius='19' IsHitTestVisible='False'/>
                                    <Border x:Name='Sheen' BorderThickness='1.4' Margin='-1' CornerRadius='20' IsHitTestVisible='False'/>
                                </Grid>
                            </Border>
                            <!-- Windows-style keyboard focus: 2px ring outside the pill, never a rectangle over the rounded shape. -->
                            <Border x:Name='FocusRing' BorderBrush='{DynamicResource CyberFocusBrush}' BorderThickness='2' Margin='-3'
                                CornerRadius='23' IsHitTestVisible='False' Visibility='Hidden'/>
                            </Grid>
                            <ControlTemplate.Triggers>
                                <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Hover' Property='Opacity' Value='1'/></Trigger>
                                <Trigger Property='IsPressed' Value='True'><Setter TargetName='Frame' Property='Opacity' Value='0.72'/></Trigger>
                                <Trigger Property='IsKeyboardFocused' Value='True'>
                                    <Setter TargetName='Frame' Property='BorderBrush' Value='{DynamicResource CyberFocusBrush}'/>
                                    <Setter TargetName='FocusRing' Property='Visibility' Value='Visible'/>
                                </Trigger>
                                <Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.4'/></Trigger>
                            </ControlTemplate.Triggers>
                        </ControlTemplate></Setter.Value>
                    </Setter>
                </Style>");
            ((Style)Resources[typeof(Button)]).Setters.Add(new EventSetter(FrameworkElement.LoadedEvent, new RoutedEventHandler(OnMotionButtonLoaded)));
            // Navigation rail item (Windows 11 style): rounded hover, short accent indicator on the left, 2px focus ring.
            Resources["NavButton"] = (Style)System.Windows.Markup.XamlReader.Parse(@"
                <Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                       xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Button'>
                    <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
                    <Setter Property='Background' Value='Transparent'/>
                    <Setter Property='BorderBrush' Value='Transparent'/>
                    <Setter Property='Template'>
                        <Setter.Value><ControlTemplate TargetType='Button'>
                            <Grid>
                                <Border x:Name='Bg' Background='{TemplateBinding Background}' CornerRadius='10'/>
                                <Border x:Name='Hover' Background='{DynamicResource MaterialButtonSurface}' CornerRadius='10' Opacity='0' IsHitTestVisible='False'/>
                                <Border Width='3' Height='18' CornerRadius='1.5' HorizontalAlignment='Left' VerticalAlignment='Center' Background='{TemplateBinding BorderBrush}' IsHitTestVisible='False'/>
                                <ContentPresenter Margin='{TemplateBinding Padding}' HorizontalAlignment='{TemplateBinding HorizontalContentAlignment}' VerticalAlignment='Center' RecognizesAccessKey='True'/>
                                <Border x:Name='FocusRing' BorderBrush='{DynamicResource CyberFocusBrush}' BorderThickness='2' Margin='-2' CornerRadius='12' IsHitTestVisible='False' Visibility='Hidden'/>
                            </Grid>
                            <ControlTemplate.Triggers>
                                <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Hover' Property='Opacity' Value='0.55'/></Trigger>
                                <Trigger Property='IsPressed' Value='True'><Setter TargetName='Hover' Property='Opacity' Value='0.85'/></Trigger>
                                <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='FocusRing' Property='Visibility' Value='Visible'/></Trigger>
                            </ControlTemplate.Triggers>
                        </ControlTemplate></Setter.Value>
                    </Setter>
                </Style>");
            // Text-only actions (Detalles, Cambiar, Mostrar): no pill frame, same 2px keyboard focus ring.
            Resources["FlatButton"] = (Style)System.Windows.Markup.XamlReader.Parse(@"
                <Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                       xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Button'>
                    <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
                    <Setter Property='Background' Value='Transparent'/>
                    <Setter Property='Template'>
                        <Setter.Value><ControlTemplate TargetType='Button'>
                            <Grid>
                                <Border x:Name='Bg' Background='{TemplateBinding Background}' Padding='{TemplateBinding Padding}' CornerRadius='6'>
                                    <ContentPresenter HorizontalAlignment='{TemplateBinding HorizontalContentAlignment}' VerticalAlignment='Center' RecognizesAccessKey='True'/>
                                </Border>
                                <Border x:Name='FocusRing' BorderBrush='{DynamicResource CyberFocusBrush}' BorderThickness='2' Margin='-3' CornerRadius='8' IsHitTestVisible='False' Visibility='Hidden'/>
                            </Grid>
                            <ControlTemplate.Triggers>
                                <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Bg' Property='Opacity' Value='0.75'/></Trigger>
                                <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='FocusRing' Property='Visibility' Value='Visible'/></Trigger>
                                <Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.4'/></Trigger>
                            </ControlTemplate.Triggers>
                        </ControlTemplate></Setter.Value>
                    </Setter>
                </Style>");
            Resources[typeof(ScrollBar)] = (Style)System.Windows.Markup.XamlReader.Parse(@"
                <Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                       xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='ScrollBar'>
                    <Setter Property='Width' Value='14'/>
                    <Setter Property='Background' Value='Transparent'/>
                    <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ScrollBar'>
                        <Grid><Border Background='{DynamicResource CyberTrackBrush}' Opacity='0.22' CornerRadius='7' Margin='4,1' IsHitTestVisible='False'/>
                        <Track x:Name='PART_Track' Orientation='{TemplateBinding Orientation}' IsDirectionReversed='True'
                            Minimum='{TemplateBinding Minimum}' Maximum='{TemplateBinding Maximum}'
                            ViewportSize='{TemplateBinding ViewportSize}' Value='{Binding Value, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}'>
                            <Track.DecreaseRepeatButton><RepeatButton x:Name='PageBefore' Command='ScrollBar.PageUpCommand' Background='Transparent' Focusable='False'>
                                <RepeatButton.Template><ControlTemplate TargetType='RepeatButton'><Border Background='Transparent'/></ControlTemplate></RepeatButton.Template>
                            </RepeatButton></Track.DecreaseRepeatButton>
                            <Track.Thumb><Thumb Background='{DynamicResource CyberTrackBrush}'>
                                <Thumb.Template><ControlTemplate TargetType='Thumb'><Border x:Name='Grip' Background='{TemplateBinding Background}' BorderBrush='{DynamicResource CyberFocusBrush}' BorderThickness='0,0,1,0' CornerRadius='5' Margin='3,2'/>
                                    <ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Grip' Property='Background' Value='{DynamicResource CyberFocusBrush}'/></Trigger></ControlTemplate.Triggers>
                                </ControlTemplate></Thumb.Template>
                            </Thumb></Track.Thumb>
                            <Track.IncreaseRepeatButton><RepeatButton x:Name='PageAfter' Command='ScrollBar.PageDownCommand' Background='Transparent' Focusable='False'>
                                <RepeatButton.Template><ControlTemplate TargetType='RepeatButton'><Border Background='Transparent'/></ControlTemplate></RepeatButton.Template>
                            </RepeatButton></Track.IncreaseRepeatButton>
                        </Track></Grid>
                        <ControlTemplate.Triggers><Trigger Property='Orientation' Value='Horizontal'>
                            <Setter TargetName='PART_Track' Property='IsDirectionReversed' Value='False'/>
                            <Setter TargetName='PageBefore' Property='Command' Value='ScrollBar.PageLeftCommand'/>
                            <Setter TargetName='PageAfter' Property='Command' Value='ScrollBar.PageRightCommand'/>
                        </Trigger></ControlTemplate.Triggers>
                    </ControlTemplate></Setter.Value></Setter>
                    <Style.Triggers><Trigger Property='Orientation' Value='Horizontal'>
                        <Setter Property='Width' Value='Auto'/><Setter Property='Height' Value='14'/>
                    </Trigger></Style.Triggers>
                </Style>");
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
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(172) });
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Col 0: Logo Cyberpunk Blackwall ICE + Marca
            StackPanel brandStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            WindowChrome.SetIsHitTestVisibleInChrome(brandStack, true);

            _logoBox = new Border
            {
                Width = 34,
                Height = 34,
                CornerRadius = new CornerRadius(0, 0, 9, 0),
                Background = Brushes.Transparent,
                Margin = new Thickness(0, 0, 10, 0),
                Cursor = Cursors.Hand
            };
            using (Stream logoStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("blackwall-mark.png"))
            {
                if (logoStream != null)
                {
                    var logo = new BitmapImage();
                    logo.BeginInit();
                    logo.CacheOption = BitmapCacheOption.OnLoad;
                    logo.StreamSource = logoStream;
                    logo.EndInit();
                    logo.Freeze();
                    Icon = logo;
                    _logoBox.Child = new Image { Source = logo, Stretch = Stretch.Uniform };
                }
            }
            _logoBox.MouseLeftButtonUp += (s, e) => TriggerLogoGlitch();
            _logoHalo = new System.Windows.Media.Effects.DropShadowEffect { Color = ColorHex("#EF4055"), BlurRadius = 3, ShadowDepth = 0, Opacity = 0.8 };
            _logoBox.Effect = _logoHalo;
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
                Text = " / MN",
                Foreground = CyberCyanBrush,
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
                    : "SYSTEM INTERFACE // Inicializando",
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
                CornerRadius = new CornerRadius(18),
                Height = 35,
                Padding = new Thickness(10, 0, 10, 0)
            };
            WindowChrome.SetIsHitTestVisibleInChrome(omniBorder, true);

            DockPanel omniDock = new DockPanel { LastChildFill = true };
            TextBlock searchIcon = new TextBlock
            {
                Text = "⌕",
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
            _omniboxInput.PreviewKeyDown += OnOmniboxKeyDown;
            _omniboxInput.ToolTip = "Buscar hardware, ajustes y software · Ctrl+K";
            System.Windows.Automation.AutomationProperties.SetName(_omniboxInput, "Buscar en Blackwall");
            omniDock.Children.Add(_omniboxInput);
            omniBorder.Child = omniDock;
            omniboxContainer.Children.Add(omniBorder);
            var searchHint = new TextBlock { Text = "Buscar en Blackwall", Foreground = TextSecondary, FontSize = 12, Margin = new Thickness(34, 0, 76, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, IsHitTestVisible = false };
            omniboxContainer.Children.Add(searchHint);
            _omniboxInput.TextChanged += (s, e) => searchHint.Visibility = string.IsNullOrEmpty(_omniboxInput.Text) ? Visibility.Visible : Visibility.Collapsed;
            var searchSheen = new Border { BorderBrush = Brushes.Transparent, BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(18), Height = 35, IsHitTestVisible = false };
            omniboxContainer.Children.Add(searchSheen);
            omniBorder.MouseEnter += (s, e) => PlaySearchSheen(searchSheen);
            _omniboxInput.GotKeyboardFocus += (s, e) => PlaySearchSheen(searchSheen);

            _omniboxPopup = new Popup
            {
                PlacementTarget = omniBorder,
                Placement = PlacementMode.Bottom,
                StaysOpen = false,
                AllowsTransparency = true,
                Width = 520
            };
            _omniboxPopup.Resources.MergedDictionaries.Add(Resources);
            omniBorder.SizeChanged += (s, e) => _omniboxPopup.Width = Math.Max(300, omniBorder.ActualWidth);
            Border popupBorder = new Border
            {
                Background = BgSurface,
                BorderBrush = AccentBrush,
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(8),
                Margin = new Thickness(0, 4, 0, 0)
            };
            _omniboxResultsPanel = new StackPanel();
            var searchScroll = CreateSmoothScroller();
            searchScroll.MaxHeight = 390;
            searchScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            searchScroll.Content = _omniboxResultsPanel;
            popupBorder.Child = searchScroll;
            _omniboxPopup.Child = popupBorder;

            Grid.SetColumn(omniboxContainer, 1);
            topGrid.Children.Add(omniboxContainer);

            // Col 2: Botones de Acción + Separador + Controles de Ventana (Min, Max/Restore, Close)
            StackPanel rightContainer = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Stretch };

            StackPanel actionsStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            WindowChrome.SetIsHitTestVisibleInChrome(actionsStack, true);

            Button rpBtn = CreateStyledButton("Restauración", false);
            rpBtn.ToolTip = "Crear un punto de restauración de Windows";
            rpBtn.Margin = new Thickness(0, 0, 6, 0);
            rpBtn.Click += async (s, e) => await RunUiActionAsync("restore", rpBtn, () => CreateRestorePointNowAsync());
            actionsStack.Children.Add(rpBtn);

            _otaBtn = CreateStyledButton("Actualizaciones", false);
            _otaBtn.Margin = new Thickness(0, 0, 6, 0);
            _otaBtn.Click += (s, e) => ShowOtaUpdateDialog();
            actionsStack.Children.Add(_otaBtn);

            _themeBtn = CreateStyledButton(_isDark ? "Modo claro" : "Modo oscuro", false);
            _themeBtn.Margin = new Thickness(0, 0, 8, 0);
            _themeBtn.Click += (s, e) =>
            {
                _isDark = !_isDark;
                SaveUiPreferences();
                UpdateThemeBrushes();
                _themeBtn.Content = _isDark ? "Modo claro" : "Modo oscuro";
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
            _minBtn = CreateWindowControlButton(GlyphMinimize, "Minimizar", () => this.WindowState = WindowState.Minimized);
            _maxBtn = CreateWindowControlButton(GlyphMaximize, "Maximizar", () =>
            {
                this.WindowState = (this.WindowState == WindowState.Maximized) ? WindowState.Normal : WindowState.Maximized;
            });
            UpdateMaximizeButton();
            _closeBtn = CreateWindowControlButton(GlyphClose, "Cerrar", () => this.Close(), true);

            winControlsStack.Children.Add(_minBtn);
            winControlsStack.Children.Add(_maxBtn);
            winControlsStack.Children.Add(_closeBtn);
            rightContainer.Children.Add(winControlsStack);

            Grid.SetColumn(rightContainer, 2);
            topGrid.Children.Add(rightContainer);
            topBar.Child = topGrid;
            Grid.SetRow(topBar, 0);
            _rootGrid.Children.Add(topBar);
            _headerEnergy = new EnergyTrace { Height = 12, VerticalAlignment = VerticalAlignment.Bottom };
            _headerEnergy.Visibility = Visibility.Collapsed;
            _rootGrid.Children.Add(_headerEnergy);

            // 2. CUERPO CENTRAL
            Grid bodyGrid = new Grid();
            _network = new GraphiteNetwork { Dark = _isDark };
            Grid.SetColumnSpan(_network, 2);
            bodyGrid.Children.Add(_network);
            bodyGrid.MouseMove += (s, e) => { if (MotionAllowed && _network.Running) _network.SetPointer(e.GetPosition(_network)); };
            bodyGrid.MouseLeave += (s, e) => _network.SetPointer(null);
            _sidebarScrim = new Border { Background = Brushes.Black, Opacity = 0.28, Visibility = Visibility.Collapsed };
            Grid.SetColumnSpan(_sidebarScrim, 2);
            Panel.SetZIndex(_sidebarScrim, 15);
            _sidebarScrim.MouseLeftButtonUp += (s, e) => { if (!_sidebarCollapsed) ToggleSidebar(); };
            bodyGrid.Children.Add(_sidebarScrim);
            _sidebarColumn = new ColumnDefinition { Width = new GridLength(248) };
            bodyGrid.ColumnDefinitions.Add(_sidebarColumn);
            bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            Border sidebarBorder = new Border
            {
                Background = BgSidebar,
                BorderBrush = BorderSubtle,
                BorderThickness = new Thickness(0, 0, 1, 0),
                Padding = new Thickness(14, 24, 14, 18)
            };

            _sidebarBorder = sidebarBorder;
            DockPanel sidebarDock = new DockPanel { LastChildFill = true };
            _sidebarToggle = new Button { Cursor = Cursors.Hand, Foreground = TextSecondary, Margin = new Thickness(0, 0, 0, 12) };
            _sidebarToggle.SetResourceReference(FrameworkElement.StyleProperty, "NavButton");
            _sidebarToggle.Click += (s, e) => ToggleSidebar();
            DockPanel.SetDock(_sidebarToggle, Dock.Top);
            sidebarDock.Children.Add(_sidebarToggle);
            _motionToggle = new Button { Cursor = Cursors.Hand, Foreground = TextSecondary, Margin = new Thickness(0, 12, 0, 0) };
            _motionToggle.SetResourceReference(FrameworkElement.StyleProperty, "NavButton");
            _motionToggle.Click += (s, e) => { _reduceMotion = !_reduceMotion; UpdateMotionPreference(); SaveUiPreferences(); };
            DockPanel.SetDock(_motionToggle, Dock.Bottom);
            sidebarDock.Children.Add(_motionToggle);
            StackPanel navStack = new StackPanel();
            _navButtons.Clear();

            AddNavGroupLabel(navStack, "DIAGNÓSTICO");
            AddNavButton(navStack, "hardware", "Hardware");

            AddNavGroupLabel(navStack, "CONFIGURACIÓN");
            AddNavButton(navStack, "winutil", "WinUtil");
            AddNavButton(navStack, "ceito", "Ceito");

            AddNavGroupLabel(navStack, "HERRAMIENTAS");
            AddNavButton(navStack, "apps", "Software");
            AddNavButton(navStack, "monitor", "Actividad");

            AddNavGroupLabel(navStack, "RECUPERACIÓN");
            AddNavButton(navStack, "safety", "Seguridad");

            Border infoBox = new Border
            {
                Background = BgSurface,
                BorderBrush = BorderSubtle,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(13)
            };
            StackPanel infoStack = new StackPanel();
            infoStack.Children.Add(new TextBlock
            {
                Text = "MURO NEGRO",
                Foreground = CyberCyanBrush,
                FontWeight = FontWeights.Bold,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 10.5
            });
            infoStack.Children.Add(new TextBlock
            {
                Text = "Revisa el impacto antes de aplicar cambios.",
                Foreground = TextSecondary,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
                Margin = new Thickness(0, 6, 0, 0)
            });
            infoBox.Child = infoStack;
            _expandedSidebarElements.Add(infoBox);
            DockPanel.SetDock(infoBox, Dock.Bottom);
            sidebarDock.Children.Add(infoBox);
            var navScroll = CreateSmoothScroller();
            navScroll.Content = navStack;
            navScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            navScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            navScroll.Margin = new Thickness(0, 0, 0, 16);
            sidebarDock.Children.Add(navScroll);

            sidebarBorder.Child = sidebarDock;
            Grid.SetColumn(sidebarBorder, 0);
            Grid.SetColumnSpan(sidebarBorder, 2);
            sidebarBorder.HorizontalAlignment = HorizontalAlignment.Left;
            Panel.SetZIndex(sidebarBorder, 20);
            bodyGrid.Children.Add(sidebarBorder);
            _sidebarEnergy = new EnergyTrace { Width = 8, Vertical = true, HorizontalAlignment = HorizontalAlignment.Left };
            _sidebarEnergy.Visibility = Visibility.Collapsed;
            bodyGrid.Children.Add(_sidebarEnergy);

            _mainScroll = CreateSmoothScroller();
            _mainScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _mainScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            _mainScroll.Padding = new Thickness(28, 24, 24, 24);
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
                Text = "○  AUDITORÍA PENDIENTE",
                Foreground = TextSecondary,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 11.5,
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
            _footerProgress = new ProgressBar { Width = 90, Height = 4, Maximum = 100, Foreground = CyberCyanBrush, Background = BgElevated, BorderThickness = new Thickness(0), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0), Visibility = Visibility.Collapsed };
            footerRight.Children.Add(_footerProgress);

            Button consoleJumpBtn = CreateStyledButton("Actividad ↗", false);
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
            ApplySidebarState();
            UpdateNavSelection();
        }

        private void AddNavGroupLabel(StackPanel parent, string text)
        {
            var label = new TextBlock
            {
                Text = text,
                Foreground = TextSecondary,
                FontSize = 9.2,
                FontFamily = new FontFamily("Consolas"),
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(10, 10, 6, 6)
            };
            _expandedSidebarElements.Add(label);
            parent.Children.Add(label);
        }

        private static readonly FontFamily NavIconFont = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        private static readonly Dictionary<string, string> NavGlyphs = new Dictionary<string, string>
        {
            { "hardware", "" },     // System (PC)
            { "winutil", "" }, // LightningBolt
            { "ceito", "" },      // Lock
            { "apps", "" },         // Download
            { "monitor", "" },      // Diagnostic (activity)
            { "safety", "" }        // UpdateRestore
        };

        // Collapsed rail: icon centred over a short label. Expanded: icon and label in a row.
        private UIElement NavContent(string glyph, string text, bool collapsed)
        {
            var icon = new TextBlock { Text = glyph, FontFamily = NavIconFont, FontSize = collapsed ? 18 : 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var panel = new StackPanel { Orientation = collapsed ? Orientation.Vertical : Orientation.Horizontal, HorizontalAlignment = collapsed ? HorizontalAlignment.Center : HorizontalAlignment.Left };
            panel.Children.Add(icon);
            if (string.IsNullOrEmpty(text)) return panel;
            var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            if (collapsed) { label.FontSize = 10; label.FontWeight = FontWeights.Normal; label.Margin = new Thickness(0, 4, 0, 0); label.HorizontalAlignment = HorizontalAlignment.Center; label.MaxWidth = 60; } // regular weight: the active tab must not truncate
            else { label.FontSize = 12.5; label.Margin = new Thickness(12, 0, 0, 0); icon.Width = 22; }
            panel.Children.Add(label);
            return panel;
        }

        private void AddNavButton(StackPanel parent, string tabKey, string label)
        {
            string title = Regex.Replace(label, @"^[^\p{L}]+", "");
            Button btn = new Button
            {
                Tag = new[] { NavGlyphs[tabKey], title },
                Margin = new Thickness(0, 2, 0, 2),
                Cursor = Cursors.Hand,
                Foreground = TextSecondary
            };
            btn.SetResourceReference(FrameworkElement.StyleProperty, "NavButton");
            System.Windows.Automation.AutomationProperties.SetName(btn, title);
            ToolTip navigationTip = new ToolTip { Content = title, PlacementTarget = btn, Placement = PlacementMode.Right };
            ToolTipService.SetInitialShowDelay(btn, 350);
            ToolTipService.SetShowDuration(btn, 1000);
            ToolTipService.SetBetweenShowDelay(btn, 350);
            btn.ToolTip = navigationTip;
            btn.Click += (s, e) => { navigationTip.IsOpen = false; if (_activeTab != tabKey) SwitchTab(tabKey); if (!_sidebarCollapsed) ToggleSidebar(); };
            _navButtons[tabKey] = btn;
            parent.Children.Add(btn);
        }

        private void UpdateNavSelection()
        {
            foreach (var kv in _navButtons)
            {
                bool active = kv.Key == _activeTab;
                kv.Value.Background = active ? BgElevated : Brushes.Transparent;
                kv.Value.Foreground = active ? CyberCyanBrush : TextSecondary;
                kv.Value.BorderBrush = active ? AccentBrush : Brushes.Transparent;
                kv.Value.FontWeight = active ? FontWeights.Bold : FontWeights.Normal;
            }
        }

        private void SwitchTab(string tabKey)
        {
            bool changed = _activeTab != tabKey;
            // Ceito's warning is shown every time the section is entered, not once per session.
            if (tabKey != "ceito") { _ceitoUnlocked = false; _ceitoStep = 0; _ceitoRestoreStatus = null; _ceitoConfirmed = false; SetCeitoEdgeMotion(false); }
            _activeTab = tabKey;
            UpdateAmbientMotion();
            UpdateNavSelection();
            RenderCurrentTab();
            if (changed) _mainScroll.ScrollToTop();
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
            _omniboxItems.Clear();
            _omniboxSelected = -1;
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

            foreach (var t in _tweaks.Where(TweakAvailable))
            {
                if (count >= 10) break;
                string title = GetStr(t, "title");
                string desc = GetStr(t, "description");
                string sub = GetStr(t, "subcategory");
                if (title.ToLowerInvariant().Contains(q) || desc.ToLowerInvariant().Contains(q) || sub.ToLowerInvariant().Contains(q))
                {
                    string cat = IsFrozen(t) ? "ceito" : "winutil";
                    string risk = GetStr(t, "riskLevel").ToUpperInvariant();
                    string tweakId = GetStr(t, "id");
                    AddOmniboxResultItem(title, sub + " — " + desc, "Riesgo " + risk, () => OpenSearchTarget(cat, tweakId));
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
                    AddOmniboxResultItem(name, GetStr(a, "category") + " • " + wg, "Software", () =>
                    {
                        OpenSearchTarget("apps", appId);
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
            SelectOmniboxResult(_omniboxItems.Count > 0 ? 0 : -1);
        }

        private void OnOmniboxKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Down || e.Key == Key.Up)
            {
                if (!_omniboxPopup.IsOpen) OnOmniboxTextChanged(_omniboxInput, null);
                if (_omniboxItems.Count > 0) SelectOmniboxResult((_omniboxSelected + (e.Key == Key.Down ? 1 : -1) + _omniboxItems.Count) % _omniboxItems.Count);
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && _omniboxPopup.IsOpen && _omniboxSelected >= 0)
            {
                _omniboxItems[_omniboxSelected].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                e.Handled = true;
            }
        }

        private void SelectOmniboxResult(int index)
        {
            _omniboxSelected = index;
            for (int i = 0; i < _omniboxItems.Count; i++)
            {
                _omniboxItems[i].Background = i == index ? BgElevated : Brushes.Transparent;
                _omniboxItems[i].BorderBrush = i == index ? CyberCyanBrush : Brushes.Transparent;
            }
            if (index >= 0) _omniboxItems[index].BringIntoView();
        }

        private void OpenSearchTarget(string tab, string id)
        {
            _pendingSearchId = id;
            if (tab == "apps") { _appCategoryScope = "Todas"; _appSearchText = ""; }
            else _tweakSearchText = "";
            SwitchTab(tab);
        }

        private void AddOmniboxResultItem(string title, string subtitle, string badge, Action onClick)
        {
            Button itemBorder = new Button
            {
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(1),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(10, 7, 10, 7),
                Cursor = Cursors.Hand
            };
            int index = _omniboxItems.Count;
            itemBorder.MouseEnter += (s, e) => SelectOmniboxResult(index);
            itemBorder.GotKeyboardFocus += (s, e) => SelectOmniboxResult(index);
            System.Windows.Automation.AutomationProperties.SetName(itemBorder, title + ". " + badge);

            DockPanel dp = new DockPanel();
            Border pill = CreateBadge(badge, "low");
            pill.Background = BgElevated;
            pill.BorderBrush = BorderSubtle;
            ((TextBlock)pill.Child).Foreground = TextSecondary;
            DockPanel.SetDock(pill, Dock.Right);
            dp.Children.Add(pill);

            StackPanel sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = title, Foreground = TextPrimary, FontWeight = FontWeights.SemiBold, FontSize = 12.8, TextWrapping = TextWrapping.Wrap });
            sp.Children.Add(new TextBlock { Text = subtitle, Foreground = TextSecondary, FontSize = 11.2, TextTrimming = TextTrimming.CharacterEllipsis });
            dp.Children.Add(sp);

            itemBorder.Content = dp;
            itemBorder.Click += (s, e) =>
            {
                _omniboxPopup.IsOpen = false;
                _omniboxInput.Text = "";
                onClick();
                _omniboxInput.Focus();
            };
            _omniboxItems.Add(itemBorder);
            _omniboxResultsPanel.Children.Add(itemBorder);
        }

        // ====================================================================
        // RENDERIZADO CENTRAL DE CADA MÓDULO
        // ====================================================================
        private void RenderCurrentTab()
        {
            if (_mainScroll == null) return;
            SetCeitoMotion(false);
            _ceitoEmblemScale = null; _ceitoWaitRotation = null;
            double previousOffset = _mainScroll.VerticalOffset;
            bool sameSection = _lastRenderedTab == _activeTab;
            bool restoreSearch = sameSection && _sectionSearch != null && _sectionSearch.IsKeyboardFocusWithin;
            int selectionStart = restoreSearch ? _sectionSearch.SelectionStart : 0;
            int selectionLength = restoreSearch ? _sectionSearch.SelectionLength : 0;
            _tweakCardUpdaters.Clear();
            _sectionSearch = null;
            _installSelectedButton = null;
            _activityLogRows = null; _activityLogScroll = null; _activityLogEmpty = null; _activityOpsPanel = null; _activitySummary = null;
            _monitorProcessPanel = null; _monitorProcessTitle = null;
            _monitorProcessMemoryLabels.Clear();

            UIElement content = null;
            string moduleKey = CurrentModuleKey();
            // Refreshes of the same section and jumps to a search result keep every card in place (scroll position, BringIntoView).
            if (!sameSection && _pendingSearchId == null) BeginProgressivePage();
            else { _deferredCards.Clear(); _deferredGeneration++; _pageCardBudget = int.MaxValue; }
            if (moduleKey != "hardware" && !_loadedModules.Contains(moduleKey))
                content = CreateBanner(_readErrors.ContainsKey(moduleKey) ? "Lectura interrumpida" : "Preparando módulo", "El contenido estará disponible al completar la lectura. Puedes seguir navegando.");
            else switch (_activeTab)
            {
                case "hardware":
                    content = BuildHardwareView();
                    break;
                case "winutil":
                    content = BuildWinUtilView();
                    break;
                case "ceito":
                    content = BuildCeitoView();
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

            _pageCardBudget = int.MaxValue;
            if (_deferredCards.Count > 0)
            {
                int generation = _deferredGeneration;
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => FlushDeferredCards(generation)));
            }
            if (content != null)
            {
                Grid centeredWrap = new Grid
                {
                    MaxWidth = 1360,
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                centeredWrap.Children.Add(content);
                StopEntrances();
                _motionPage = null;
                _pageMotionCards.Clear();
                _mainScroll.Content = centeredWrap;
                if (sameSection) _mainScroll.ScrollToVerticalOffset(previousOffset);
                if (restoreSearch && _sectionSearch != null)
                {
                    TextBox search = _sectionSearch;
                    Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
                    {
                        if (_mainScroll.Content != centeredWrap) return;
                        search.Focus(); search.Select(Math.Min(selectionStart, search.Text.Length), Math.Min(selectionLength, search.Text.Length - Math.Min(selectionStart, search.Text.Length)));
                    }));
                }
                bool ready = moduleKey == "hardware" ? _hardware != null : _loadedModules.Contains(moduleKey);
                if (_lastRenderedTab != _activeTab || (!_lastRenderedReady && ready))
                {
                    Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => AnimatePage(centeredWrap)));
                }
                _lastRenderedTab = _activeTab;
                _lastRenderedReady = ready;
                string target = _pendingSearchId;
                if (target != null) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
                {
                    if (_mainScroll.Content != centeredWrap) return;
                    var card = MotionCards(centeredWrap).FirstOrDefault(c => Convert.ToString(c.Tag) == target);
                    if (card != null) { card.BringIntoView(); _pendingSearchId = null; }
                }));
            }
            if (!_loadedModules.Contains(moduleKey) && !_readErrors.ContainsKey(moduleKey))
                LoadModuleAsync(moduleKey);
            UpdateActivityPanel();
        }

        // --------------------------------------------------------------------
        // MÓDULO A: AUDITORÍA PROFUNDA DE HARDWARE
        // --------------------------------------------------------------------
        private UIElement BuildHardwareView()
        {
            StackPanel root = new StackPanel();
            root.Children.Add(BuildDashboardSummary());

            if (_hardware == null)
            {
                root.Children.Add(CreateBanner(
                    _readErrors.ContainsKey("hardware") ? "Lectura de hardware interrumpida" : "Inicializando auditoría de hardware",
                    _readErrors.ContainsKey("hardware") ? "No se completó la auditoría. Puedes reintentar desde el aviso de error o cambiar de módulo." : "Leyendo tablas SMBIOS, slots físicos de RAM, unidades NVMe/SSD y batería..."
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

            WrapPanel headerActions = new WrapPanel();
            Button btnRescan = CreateStyledButton("🔄 Re-escanear", false);
            btnRescan.Margin = new Thickness(0, 0, 8, 0);
            btnRescan.Click += async (s, e) => await RefreshHardwareNativeAsync();
            headerActions.Children.Add(btnRescan);

            headerActions.Children.Add(CreateExportReportButton());
            foreach (Button button in headerActions.Children) StyleDashboardButtonEdge(button);

            root.Children.Add(CreateBanner(
                (GetBool(chassis, "isLaptop") ? "💻 " : "🖥️ ") + GetStr(chassis, "manufacturer") + " " + GetStr(chassis, "model") + " (" + GetStr(chassis, "deviceType") + ")",
                "Serial BIOS: " + GetStr(chassis, "serialNumber") + "  •  SKU: " + GetStr(chassis, "sku") + "  •  Tiempo de Escaneo: " + GetStr(_hardware, "scannedAt"),
                headerActions
            ));

            AdaptiveCardGrid row1 = new AdaptiveCardGrid { Columns = 3, Margin = new Thickness(0, 0, 0, 14) };

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
                { "Certificados 2023", GetStr(mb, "secureBootCerts") },
                { "Módulo TPM", GetStr(mb, "tpmVersion") }
            }));

            row1.Children.Add(CreateKvCard("🧠 3. Procesador (CPU)", "Uso: " + GetStr(cpu, "loadPercent") + "%", new[,]
            {
                { "Nombre Exacto", GetStr(cpu, "name") },
                { "Arquitectura / Socket", GetStr(cpu, "architecture") + " • " + GetStr(cpu, "socket") },
                { "Núcleos / Hilos", GetStr(cpu, "physicalCores") + " Físicos / " + GetStr(cpu, "logicalThreads") + " Hilos" },
                { "Frecuencia Base / Actual", GetStr(cpu, "baseClockMhz") + " MHz / " + GetStr(cpu, "currentClockMhz") + " MHz" },
                { "Caché L2 / Caché L3", FormatCpuCacheMb(GetStr(cpu, "l2CacheKb")) + " / " + FormatCpuCacheMb(GetStr(cpu, "l3CacheKb")) },
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
                FontSize = 14.5,
                TextWrapping = TextWrapping.Wrap
            });
            ramStack.Children.Add(ramHeader);

            AdaptiveCardGrid slotsGrid = new AdaptiveCardGrid { Columns = 2 };
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

            AdaptiveCardGrid row3 = new AdaptiveCardGrid { Columns = 3, Margin = new Thickness(0, 0, 0, 14) };

            var d0 = storageList.Count > 0 ? storageList[0] : new Dictionary<string, object>();
            row3.Children.Add(CreateKvCard("💿 5. Almacenamiento & S.M.A.R.T.", storageList.Count + " Unidad(es)", new[,]
            {
                { "Modelo Principal", GetStr(d0, "friendlyName") },
                { "Interfaz / Partición", GetStr(d0, "busType") + " (" + GetStr(d0, "mediaType") + ") • " + GetStr(d0, "partitionStyle") },
                { "Capacidad Total / Libre C:", GetStr(d0, "sizeGb") + " GB / " + GetStr(d0, "freeGb") + " GB Libres" },
                { "Número de Serie Disco", GetStr(d0, "serialNumber") },
                { "Estado de salud (Windows)", GetStr(d0, "healthStatus") },
                { "Unidades Adicionales", storageList.Count > 1 ? GetStr(storageList[1], "friendlyName") + " (" + GetStr(storageList[1], "sizeGb") + " GB)" : "Ninguna" }
            }));

            // The list is sorted dedicated-first, so this is the card games actually use.
            var g0 = gpuList.Count > 0 ? gpuList[0] : new Dictionary<string, object>();
            string others = string.Join(" · ", gpuList.Skip(1).Select(g => GetStr(g, "name") + " (" + GetStr(g, "kindLabel").ToLowerInvariant() + ")"));
            string driver = GetStr(g0, "driverVersion") + (GetStr(g0, "nvidiaDriver").Length > 0 ? "  (NVIDIA " + GetStr(g0, "nvidiaDriver") + ")" : "");
            row3.Children.Add(CreateKvCard("🎮 6. Gráficos (GPU) y Monitor", gpuList.Count == 0 ? "No detectada" : GetStr(g0, "vendor") + " · " + GetStr(g0, "kindLabel"), new[,]
            {
                { "Modelo GPU", gpuList.Count == 0 ? "No detectada" : GetStr(g0, "name") },
                { "Memoria de vídeo", g0.ContainsKey("vramGb") && g0["vramGb"] is double ? GetStr(g0, "vramGb") + " GB" : GetStr(g0, "vramGb") },
                { "Driver", driver + " · " + GetStr(g0, "driverProvider") },
                { "Fecha de driver", GetStr(g0, "driverDate") },
                { "Pantalla", GetStr(g0, "resolution") + (g0.ContainsKey("refreshRateHz") && g0["refreshRateHz"] is int ? " · " + GetStr(g0, "refreshRateHz") + " Hz" : "") },
                { "Otras GPU", others.Length > 0 ? others : "Ninguna" }
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

        private UIElement BuildDashboardSummary()
        {
            var card = CreateCardContainer();
            card.Margin = new Thickness(0, 0, 0, 20);
            var content = new StackPanel();
            content.Children.Add(new TextBlock { Text = "TU EQUIPO · TU CONTROL", Foreground = CyberCyanBrush, FontSize = 11, FontWeight = FontWeights.SemiBold });
            content.Children.Add(new TextBlock { Text = "Centro de control", Foreground = TextPrimary, FontSize = 30, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 8) });
            content.Children.Add(new TextBlock { Text = _hardware == null ? "La auditoría está pendiente. Puedes explorar las herramientas mientras termina la lectura." : "Auditoría de hardware disponible · " + GetStr(_hardware, "scannedAt") + ". Captura puntual del equipo, sin telemetría continua.", Foreground = TextSecondary, TextWrapping = TextWrapping.Wrap, FontSize = 13 });
            if (_hardware != null)
            {
            var metrics = new AdaptiveCardGrid { Margin = new Thickness(0, 20, 0, 8) };
            metrics.Children.Add(CreateKvCard("Comprobación de ajustes", "CATÁLOGO", new[,] { { "Estados consultados", _loadedModules.Contains("tweaks") ? _tweakStates.Count + " de " + _tweaks.Count : "Pendiente de consulta" }, { "Criterio", "Detección del catálogo; no mide salud ni rendimiento" } }));
            metrics.Children.Add(CreateKvCard("Hardware", "AUDITORÍA", new[,] { { "Procesador", _hardware == null ? "Pendiente" : GetStr(GetDict(_hardware, "cpu"), "name") }, { "Memoria instalada", _hardware == null ? "Pendiente" : GetStr(GetDict(_hardware, "memory"), "totalGb") + " GB" } }));
            metrics.Children.Add(CreateKvCard("Recuperación", "WINDOWS", new[,] { { "Puntos consultados", _loadedModules.Contains("safety") ? _restorePoints.Count.ToString() : "Pendiente de consulta" }, { "Punto verificado", RestorePointSummary() } }));
            content.Children.Add(metrics);
            }
            else content.Children.Add(new TextBlock { Text = "Datos de hardware: pendientes de lectura · Estados de ajustes y recuperación: consulta cada sección.", Foreground = TextSecondary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 16) });
            var shortcuts = new WrapPanel();
            foreach (var destination in new[] { new[] { "winutil", "Revisar ajustes" }, new[] { "apps", "Software" }, new[] { "safety", "Restauración" }, new[] { "monitor", "Logs y tareas" } })
            {
                string tab = destination[0];
                var button = CreateStyledButton(destination[1], tab == "winutil");
                StyleDashboardButtonEdge(button);
                button.Margin = new Thickness(0, 0, 8, 8);
                button.Click += (s, e) => SwitchTab(tab);
                shortcuts.Children.Add(button);
            }
            content.Children.Add(shortcuts);
            card.Child = content;
            return card;
        }

        private UIElement CreateSectionSearch(string value, Action<string> changed)
        {
            var field = new TextBox { Text = value, Background = BgSurface, Foreground = TextPrimary, CaretBrush = TextPrimary, BorderBrush = BorderSubtle, Padding = new Thickness(14, 10, 14, 10), FontSize = 13, Margin = new Thickness(0, 0, 0, 12), ToolTip = "Filtrar esta sección por nombre o descripción" };
            _sectionSearch = field;
            System.Windows.Automation.AutomationProperties.SetName(field, "Filtrar esta sección");
            field.Margin = new Thickness(0);
            field.BorderThickness = new Thickness(0);
            field.Background = Brushes.Transparent;
            var container = new Grid();
            container.Children.Add(field);
            var hint = new TextBlock { Text = "Filtrar por nombre o descripción…", Foreground = TextSecondary, FontSize = 13, Margin = new Thickness(15, 0, 15, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false, Visibility = string.IsNullOrEmpty(value) ? Visibility.Visible : Visibility.Collapsed };
            container.Children.Add(hint);
            field.TextChanged += (s, e) => { hint.Visibility = string.IsNullOrEmpty(field.Text) ? Visibility.Visible : Visibility.Collapsed; changed(field.Text); };
            var frame = new Border { Child = container, Background = BgSurface, BorderBrush = BorderSubtle, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(18), Margin = new Thickness(0, 0, 0, 12) };
            return frame;
        }

        private int _pageCardBudget = int.MaxValue;
        private readonly Queue<KeyValuePair<Panel, UIElement>> _deferredCards = new Queue<KeyValuePair<Panel, UIElement>>();
        private int _deferredGeneration;

        // ponytail: fixed budget of 4 cards (first row); the rest joins within the next frames. Measure the viewport if pop-in is visible on tall screens.
        private void BeginProgressivePage() { _deferredCards.Clear(); _deferredGeneration++; _pageCardBudget = 4; }

        private void FlushDeferredCards(int generation)
        {
            if (generation != _deferredGeneration) return; // a newer page replaced this one
            for (int i = 0; i < 4 && _deferredCards.Count > 0; i++) // ~10 ms per idle frame
            {
                var pending = _deferredCards.Dequeue();
                pending.Key.Children.Add(pending.Value);
            }
            if (_deferredCards.Count > 0) Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => FlushDeferredCards(generation)));
        }

        // Filter existing controls so keyboard composition, focus and action handlers survive each keystroke.
        private Func<string, int> CreateCardFilter(Panel panel, IEnumerable<Dictionary<string, object>> items, Func<Dictionary<string, object>, UIElement> build, params string[] fields)
        {
            var entries = new List<KeyValuePair<UIElement, string>>();
            foreach (var item in items)
            {
                var card = build(item);
                // The first screenful is laid out in the click's frame; the rest joins on the next idle frames.
                if (_pageCardBudget-- > 0) panel.Children.Add(card);
                else _deferredCards.Enqueue(new KeyValuePair<Panel, UIElement>(panel, card));
                entries.Add(new KeyValuePair<UIElement, string>(card, string.Join(" ", fields.Select(field => GetStr(item, field)))));
            }
            return query =>
            {
                int count = 0;
                foreach (var entry in entries)
                {
                    bool visible = entry.Value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
                    entry.Key.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                    if (visible) count++;
                }
                return count;
            };
        }

        // WinUtil's presets (preset.json) minus what Blackwall does not ship: no restore-point card (Blackwall always
        // makes and verifies one), no Appx removals, disk wipes, BitLocker, WPBT, UTC or IPv6 changes.
        internal static readonly Dictionary<string, string[]> WinUtilProfiles = BuildWinUtilProfiles();

        private static Dictionary<string, string[]> BuildWinUtilProfiles()
        {
            var minimal = new[] { "debloat_consumer_features", "winutil_services", "debloat_telemetry_diagtrack" };
            var standard = minimal.Concat(new[] { "debloat_activity_history", "debloat_location_tracking", "debloat_delivery_opt_p2p", "winutil_end_task", "winutil_folder_discovery", "privacy_device_metadata", "winutil_widgets" }).ToArray();
            var full = standard.Concat(new[] { "debloat_win11_classic_context_menu", "winutil_background_apps", "winutil_visual_effects", "privacy_edge_quiet", "debloat_copilot_recall_ai" }).ToArray();
            return new Dictionary<string, string[]> { { "Mínimo", minimal }, { "Recomendado", standard }, { "Completo", full } };
        }

        private static readonly Dictionary<string, string> ProfileHints = new Dictionary<string, string>
        {
            { "Mínimo", "Lo justo (preset Minimal de WinUtil): telemetría, funciones de consumidor y servicios en manual." },
            { "Recomendado", "Para la mayoría (preset Standard): lo mínimo más historial de actividad, ubicación, Widgets, descubrimiento de carpetas y más." },
            { "Completo", "Recomendado más menú clásico, apps en segundo plano, efectos visuales, Edge y Recall (preset Advanced, sin desinstalar nada)." }
        };

        // Only what this PC supports and what is not paused or a preference.
        internal List<Dictionary<string, object>> ProfileTweaks(string name)
        {
            return WinUtilProfiles[name].Select(id => _tweaks.FirstOrDefault(t => GetStr(t, "id") == id))
                .Where(t => t != null && !IsFrozen(t) && !IsPreference(t) && TweakAvailable(t)).ToList();
        }

        private readonly HashSet<string> _profileSelection = new HashSet<string>();
        private string _profileSelectionName;
        private HashSet<string> _tweakPartial = new HashSet<string>();

        // Choosing a profile only marks the cards it would change; nothing runs until «Revisar y aplicar».
        internal void SelectProfile(string name)
        {
            _profileSelection.Clear();
            _profileSelectionName = _profileSelectionName == name ? null : name;
            if (_profileSelectionName != null) foreach (var t in ProfileTweaks(name).Where(t => !IsTweakOn(t))) _profileSelection.Add(GetStr(t, "id"));
            RenderCurrentTab();
        }

        private async Task ApplySelectedProfileAsync()
        {
            string name = _profileSelectionName;
            if (name == null) return;
            await ApplyProfileAsync("Perfil " + name, ProfileTweaks(name));
            _profileSelection.Clear();
            _profileSelectionName = null;
            RenderCurrentTab();
        }

        private async Task ApplyProfileAsync(string label, List<Dictionary<string, object>> tweaks)
        {
            if (!TryBeginSystemChange(label)) return;
            try
            {
                // The preview must know what is really applied right now, not what a stale read said.
                await RefreshTweaksStateNativeAsync();
                var chosen = await _confirmApply(label, tweaks);
                if (chosen == null) return;
                await ApplyTweakBatchAsync(chosen, label);
                await RefreshTweaksStateNativeAsync();
            }
            finally { EndSystemChange(); }
        }

        // One failing tweak no longer aborts the batch or hides behind a success message.
        private async Task ApplyTweakBatchAsync(List<Dictionary<string, object>> batch, string label)
        {
            var failed = new List<string>();
            var activity = BeginOperation(label, "Optimización", batch.Count);
            foreach (var t in batch)
            {
                StepOperation(activity, GetStr(t, "shortTitle"), true, false);
                try { await ExecuteTweakAsync(t, true); }
                catch (Exception ex)
                {
                    _tweakActionErrors[GetStr(t, "id")] = ex.Message;
                    failed.Add(GetStr(t, "shortTitle"));
                    AppendLog(GetStr(t, "shortTitle") + ": " + ex.Message, "ERROR");
                }
            }
            CompleteOperation(activity, failed.Count == 0, label + ": " + (batch.Count - failed.Count) + " de " + batch.Count + " aplicadas" + (failed.Count > 0 ? " · fallaron " + string.Join(", ", failed) : ""));
            if (failed.Count > 0) SetStatus(label + ": " + failed.Count + " no se aplicaron (" + string.Join(", ", failed) + ")", "Consulta la consola");
            else SetStatus("🟢 " + label + ": " + batch.Count + " aplicadas", "Completado");
        }

        private bool _tweakDevicePickerOpen;

        private Button CreateLinkButton(string text)
        {
            var link = new Button
            {
                Content = text,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(4, 2, 4, 2),
                MinHeight = 0,
                Foreground = CyberCyanBrush,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center
            };
            link.SetResourceReference(FrameworkElement.StyleProperty, "FlatButton");
            return link;
        }

        // Common header: title, detected PC, why options are hidden, re-check, reset everything and leftovers.
        private Border BuildTweaksHeader(string title, List<Dictionary<string, object>> section, bool showLeftovers, out StackPanel headStack)
        {
            // Until the PC is identified nothing is "not for this PC": it is just not known yet.
            var hidden = _hardware == null ? new List<Dictionary<string, object>>() : section.Where(t => !GetBool(t, "retired") && !TweakAvailable(t) && !IsTweakOn(t)).ToList();
            var leftovers = showLeftovers ? section.Where(t => !IsPreference(t) && !TweakAvailable(t) && IsTweakOn(t)).ToList() : new List<Dictionary<string, object>>();
            Border head = CreateCardContainer();
            head.Margin = new Thickness(0, 0, 0, 14);
            head.Padding = new Thickness(20, 16, 20, 16);
            headStack = new StackPanel();
            headStack.Children.Add(new TextBlock { Text = title, Foreground = TextPrimary, FontSize = 22, FontWeight = FontWeights.SemiBold });
            WrapPanel headActions = new WrapPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
            Action dot = () => headActions.Children.Add(new TextBlock { Text = "·", Foreground = TextSecondary, Margin = new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center });
            headActions.Children.Add(new TextBlock { Text = HardwareSummary(), Foreground = TextSecondary, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            StackPanel reasons = null;
            if (hidden.Count > 0)
            {
                dot();
                string closedLabel = hidden.Count + (hidden.Count == 1 ? " opción no aplica" : " opciones no aplican") + " a tu equipo";
                Button whyHidden = CreateLinkButton(_tweakDevicePickerOpen ? "Ocultar motivos" : closedLabel);
                System.Windows.Automation.AutomationProperties.SetName(whyHidden, "Ver por qué hay opciones ocultas");
                reasons = new StackPanel { Margin = new Thickness(0, 10, 0, 0), ClipToBounds = true, Visibility = _tweakDevicePickerOpen ? Visibility.Visible : Visibility.Collapsed };
                foreach (var t in hidden)
                    reasons.Children.Add(new TextBlock { Text = "• " + TweakName(t) + ": " + UnmetRequirement(t), Foreground = TextSecondary, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1) });
                var panel = reasons;
                whyHidden.Click += (s, e) =>
                {
                    _tweakDevicePickerOpen = !_tweakDevicePickerOpen;
                    whyHidden.Content = _tweakDevicePickerOpen ? "Ocultar motivos" : closedLabel;
                    AnimateFold(panel, _tweakDevicePickerOpen);
                };
                headActions.Children.Add(whyHidden);
            }
            dot();
            Button recheckBtn = CreateLinkButton("↻ Revisar estados");
            recheckBtn.Click += async (s, e) => await RefreshTweaksStateNativeAsync();
            headActions.Children.Add(recheckBtn);
            int blackwallChanges = _tweaks.Count(IsTweakOn) + _changes.Count(c => IsOptimizationChange(c) && !_tweaks.Any(t => GetStr(t, "id") == GetStr(c, "id").Split('#')[0] && IsTweakOn(t)));
            if (blackwallChanges > 0)
            {
                dot();
                Button resetAllLink = CreateLinkButton("↩ Restablecer todo (" + blackwallChanges + ")");
                resetAllLink.ToolTip = "Deshace todos los ajustes de Blackwall de este equipo (WinUtil y Ceito, de cualquier versión). No toca tus preferencias.";
                resetAllLink.Click += async (s, e) => await RunUiActionAsync("tweaks-batch", resetAllLink, ResetEverythingAsync);
                headActions.Children.Add(resetAllLink);
            }
            headStack.Children.Add(headActions);
            if (reasons != null) headStack.Children.Add(reasons);
            if (leftovers.Count > 0)
            {
                // Typically changes from an older version or another tool that this PC should not have.
                var banner = new DockPanel();
                Button revertLeftovers = CreateStyledButton("Revertir " + leftovers.Count + (leftovers.Count == 1 ? " ajuste" : " ajustes"), true);
                revertLeftovers.Margin = new Thickness(12, 0, 0, 0);
                revertLeftovers.Click += async (s, e) => await RunUiActionAsync("tweaks-batch", revertLeftovers, () => RevertLeftoversAsync(leftovers));
                DockPanel.SetDock(revertLeftovers, Dock.Right);
                banner.Children.Add(revertLeftovers);
                banner.Children.Add(new TextBlock { Text = "⚠ " + leftovers.Count + (leftovers.Count == 1 ? " ajuste aplicado no corresponde" : " ajustes aplicados no corresponden") + " a este equipo: " + string.Join(", ", leftovers.Select(TweakName)) + ".", Foreground = RiskHighText, FontSize = 12.5, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
                headStack.Children.Add(new Border { Background = RiskHighBg, CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 10, 12, 10), Child = banner, Margin = new Thickness(0, 12, 0, 0) });
            }
            head.Child = headStack;
            return head;
        }

        // Opens or closes a panel where it is: height and opacity glide, nothing else on the page moves or rebuilds.
        private void AnimateFold(FrameworkElement panel, bool open)
        {
            panel.BeginAnimation(FrameworkElement.HeightProperty, null);
            panel.BeginAnimation(UIElement.OpacityProperty, null);
            panel.Height = double.NaN;
            if (!MotionAllowed) { panel.Visibility = open ? Visibility.Visible : Visibility.Collapsed; return; }
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            if (open)
            {
                panel.Visibility = Visibility.Visible;
                panel.Measure(new Size(Math.Max(200, ((FrameworkElement)panel.Parent).ActualWidth), double.PositiveInfinity));
                panel.BeginAnimation(FrameworkElement.HeightProperty, new DoubleAnimation(0, panel.DesiredSize.Height, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
                panel.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240)) { FillBehavior = FillBehavior.Stop });
            }
            else
            {
                var close = new DoubleAnimation(panel.ActualHeight, 0, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
                close.Completed += (s, e) => { if (!_tweakDevicePickerOpen) panel.Visibility = Visibility.Collapsed; };
                panel.BeginAnimation(FrameworkElement.HeightProperty, close);
                panel.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200)) { FillBehavior = FillBehavior.Stop });
            }
        }

        private string TweakName(Dictionary<string, object> t)
        {
            return GetStr(t, "shortTitle").Length > 0 ? GetStr(t, "shortTitle") : GetStr(t, "title");
        }

        // One titled group of cards that the section search can filter.
        private void AddTweakGroup(StackPanel target, List<Func<string, int>> filters, string title, string detail, Brush accent, List<Dictionary<string, object>> list)
        {
            if (list.Count == 0) return;
            var group = new StackPanel { Margin = new Thickness(0, 4, 0, 16) };
            var line = new DockPanel();
            var count = new TextBlock { Foreground = TextSecondary, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(count, Dock.Right);
            line.Children.Add(count);
            var name = new StackPanel { Orientation = Orientation.Horizontal };
            name.Children.Add(new Border { Width = 4, Height = 18, CornerRadius = new CornerRadius(2), Background = accent, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
            name.Children.Add(new TextBlock { Text = title, Foreground = CyberCyanBrush, FontWeight = FontWeights.Bold, FontSize = 15.5, VerticalAlignment = VerticalAlignment.Center });
            name.Children.Add(new TextBlock { Text = "  " + detail, Foreground = TextSecondary, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            line.Children.Add(name);
            group.Children.Add(new Border { BorderBrush = BorderSubtle, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 4, 0, 10), Margin = new Thickness(0, 0, 0, 12), Child = line });
            var grid = new AdaptiveCardGrid { Columns = 2 };
            var filter = CreateCardFilter(grid, list, BuildTweakCard, "title", "shortTitle", "description");
            group.Children.Add(grid);
            target.Children.Add(group);
            filters.Add(q => { int c = filter(q); group.Visibility = c == 0 ? Visibility.Collapsed : Visibility.Visible; count.Text = c.ToString(); return c; });
        }

        // Search card + groups; returns the panel the groups go into.
        private StackPanel AddSearchableGroups(StackPanel root, Action<StackPanel, List<Func<string, int>>> fill)
        {
            var filters = new List<Func<string, int>>();
            var groups = new StackPanel();
            fill(groups, filters);
            var empty = _hardware == null ? CreateBanner("Identificando el equipo…", "Blackwall muestra solo los ajustes que corresponden a tu hardware y versión de Windows.") : CreateBanner("Sin resultados", "Prueba con otro texto.");
            groups.Children.Add(empty);
            Action<string> applyFilter = query => { int c = 0; foreach (var filter in filters) c += filter(query); empty.Visibility = c == 0 ? Visibility.Visible : Visibility.Collapsed; };
            var searchCard = CreateCardContainer();
            searchCard.Margin = new Thickness(0, 0, 0, 14);
            var searchBody = new StackPanel();
            searchBody.Children.Add(new TextBlock { Text = "Buscar ajustes", Foreground = TextPrimary, FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
            searchBody.Children.Add(CreateSectionSearch(_tweakSearchText, value => { _tweakSearchText = value; applyFilter(value); }));
            searchCard.Child = searchBody;
            root.Children.Add(searchCard);
            applyFilter(_tweakSearchText);
            root.Children.Add(groups);
            return groups;
        }

        private UIElement BuildWinUtilView()
        {
            var root = new StackPanel();
            var section = _tweaks.Where(t => GetStr(t, "section") == "winutil").ToList();
            StackPanel headStack;
            root.Children.Add(BuildTweaksHeader("WinUtil", section, true, out headStack));
            headStack.Children.Add(new TextBlock { Text = "Perfiles de WinUtil: elige uno, mira qué tarjetas se marcan y aplícalo", Foreground = TextSecondary, FontSize = 12, Margin = new Thickness(0, 14, 0, 8) });
            var profiles = new WrapPanel();
            foreach (string profileName in WinUtilProfiles.Keys)
            {
                string name = profileName;
                Button profile = CreateStyledButton(name, _profileSelectionName == name);
                profile.Margin = new Thickness(0, 0, 8, 6);
                profile.ToolTip = ProfileHints[name];
                profile.Click += (s, e) => SelectProfile(name);
                profiles.Children.Add(profile);
            }
            headStack.Children.Add(profiles);
            if (_profileSelectionName != null)
            {
                var bar = new DockPanel();
                Button apply = CreateStyledButton(_profileSelection.Count == 0 ? "Ya está todo aplicado" : "Revisar y aplicar " + _profileSelection.Count, true);
                apply.IsEnabled = _profileSelection.Count > 0;
                apply.Margin = new Thickness(8, 0, 0, 0);
                apply.Click += async (s, e) => await RunUiActionAsync("tweaks-batch", apply, ApplySelectedProfileAsync);
                DockPanel.SetDock(apply, Dock.Right);
                bar.Children.Add(apply);
                Button cancel = CreateStyledButton("Cancelar", false);
                cancel.Click += (s, e) => SelectProfile(_profileSelectionName);
                DockPanel.SetDock(cancel, Dock.Right);
                bar.Children.Add(cancel);
                bar.Children.Add(new TextBlock { Text = "Perfil «" + _profileSelectionName + "»: " + (_profileSelection.Count == 0 ? "todo lo que incluye ya está activo." : _profileSelection.Count + (_profileSelection.Count == 1 ? " tarjeta marcada" : " tarjetas marcadas") + " con «Se aplicará». Lo ya activo no cambia."), Foreground = TextPrimary, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
                headStack.Children.Add(new Border { BorderBrush = CyberCyanBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 8, 0, 0), Child = bar });
            }
            Func<string, List<Dictionary<string, object>>> inGroup = g => section.Where(t => GetStr(t, "group") == g && (TweakAvailable(t) || IsTweakOn(t))).ToList();
            AddSearchableGroups(root, (groups, filters) =>
            {
                AddTweakGroup(groups, filters, "Esenciales", "Essential Tweaks de WinUtil", RiskLowText, inGroup("esenciales"));
                AddTweakGroup(groups, filters, "Avanzados", "precaución · Advanced Tweaks", RiskMedText, inGroup("avanzados"));
                AddTweakGroup(groups, filters, "Planes de energía", "solo PC de escritorio", CyberCyanBrush, inGroup("energia"));
            });
            root.Children.Add(BuildPreferencesCard(section.Where(t => IsPreference(t) && TweakAvailable(t)).ToList()));
            root.Children.Add(BuildCleanupCard());
            return root;
        }

        // ── Preferencias: interruptores que se acumulan y se aplican juntos con un botón.
        // applyMode says when Windows shows each one: "instant" (Blackwall tells Windows right away), "explorer"
        // (Blackwall restarts Explorer after applying) or "restart" (only after restarting the PC).
        private readonly Dictionary<string, bool> _pendingPreferences = new Dictionary<string, bool>();
        private bool _restartScheduled;

        private static readonly string[][] PreferenceGroups =
        {
            new[] { "instant", "Al instante", "Blackwall avisa a Windows en cuanto se aplican, como hace Configuración." },
            new[] { "explorer", "Al reiniciar el Explorador", "Blackwall reinicia el Explorador al aplicarlas: la barra de tareas parpadea un segundo y se cierran las carpetas abiertas (tus programas no)." },
            new[] { "restart", "Al reiniciar el equipo", "Windows las lee al arrancar. Usa «Aplicar y reiniciar ahora»." }
        };

        private Button CreateSwitch(bool on, bool enabled, string name)
        {
            var knob = new System.Windows.Shapes.Ellipse { Width = 16, Height = 16, Fill = on ? Brushes.White : TextSecondary, HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 2, 0) };
            var track = new Border { Width = 42, Height = 22, CornerRadius = new CornerRadius(11), Background = on ? RiskLowText : BgElevated, BorderBrush = on ? RiskLowText : BorderSubtle, BorderThickness = new Thickness(1), Child = knob };
            var button = new Button { Content = track, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0), MinHeight = 0, Cursor = Cursors.Hand, IsEnabled = enabled, VerticalAlignment = VerticalAlignment.Center, Opacity = enabled ? 1 : 0.5 };
            button.SetResourceReference(FrameworkElement.StyleProperty, "FlatButton");
            System.Windows.Automation.AutomationProperties.SetName(button, name + (on ? ": activado" : ": desactivado"));
            return button;
        }

        private UIElement BuildPreferencesCard(List<Dictionary<string, object>> prefs)
        {
            Border card = CreateCardContainer();
            card.Margin = new Thickness(0, 0, 0, 14);
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock { Text = "Preferencias", Foreground = CyberCyanBrush, FontWeight = FontWeights.Bold, FontSize = 15.5 });
            stack.Children.Add(new TextBlock { Text = "Customize Preferences de WinUtil. Cambia los interruptores que quieras y léelos con calma: no se aplica nada hasta que pulses el botón. Cada interruptor muestra el estado real de Windows y cada cambio se guarda para poder revertirlo.", Foreground = TextSecondary, FontSize = 12.3, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 6) });
            bool known = _hardware != null && _loadedModules.Contains("tweaks");
            if (prefs.Count == 0) stack.Children.Add(new TextBlock { Text = _hardware == null ? "Se mostrarán en cuanto se identifique el equipo." : "No hay preferencias disponibles para este equipo.", Foreground = TextSecondary, FontSize = 12.3 });
            foreach (var group in PreferenceGroups)
            {
                var inGroup = prefs.Where(p => GetStr(p, "applyMode") == group[0]).ToList();
                if (inGroup.Count == 0) continue;
                var header = new StackPanel { Margin = new Thickness(0, 12, 0, 4) };
                header.Children.Add(new TextBlock { Text = group[1], Foreground = TextPrimary, FontWeight = FontWeights.Bold, FontSize = 13.5 });
                header.Children.Add(new TextBlock { Text = group[2], Foreground = TextSecondary, FontSize = 11.8, TextWrapping = TextWrapping.Wrap });
                stack.Children.Add(new Border { BorderBrush = BorderSubtle, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 0, 0, 6), Child = header });
                var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2 };
                foreach (var p in inGroup)
                {
                    var pref = p;
                    string id = GetStr(pref, "id");
                    bool current = _tweakStates.ContainsKey(id) && _tweakStates[id];
                    bool pending = _pendingPreferences.ContainsKey(id);
                    bool shown = pending ? _pendingPreferences[id] : current;
                    var row = new DockPanel { Margin = new Thickness(0, 6, 16, 6), Tag = id };
                    Button toggle = CreateSwitch(shown, known && !_restartScheduled, GetStr(pref, "title"));
                    toggle.Margin = new Thickness(10, 0, 0, 0);
                    toggle.Click += (s, e) =>
                    {
                        bool wanted = !shown;
                        if (wanted == current) _pendingPreferences.Remove(id); else _pendingPreferences[id] = wanted;
                        RenderCurrentTab();
                    };
                    DockPanel.SetDock(toggle, Dock.Right);
                    row.Children.Add(toggle);
                    var text = new StackPanel();
                    text.Children.Add(new TextBlock { Text = GetStr(pref, "title") + (pending ? "  ● cambio pendiente" : ""), Foreground = pending ? CyberCyanBrush : TextPrimary, FontWeight = FontWeights.SemiBold, FontSize = 12.8, TextWrapping = TextWrapping.Wrap });
                    text.Children.Add(new TextBlock { Text = GetStr(pref, "summary"), Foreground = TextSecondary, FontSize = 11.5, TextWrapping = TextWrapping.Wrap });
                    row.Children.Add(text);
                    grid.Children.Add(row);
                }
                stack.Children.Add(grid);
            }
            if (_restartScheduled)
            {
                var bar = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
                Button cancel = CreateStyledButton("Cancelar el reinicio", true);
                cancel.Margin = new Thickness(8, 0, 0, 0);
                cancel.Click += (s, e) => CancelRestart();
                DockPanel.SetDock(cancel, Dock.Right);
                bar.Children.Add(cancel);
                bar.Children.Add(new TextBlock { Text = "El equipo se reiniciará en unos segundos para aplicar las preferencias. Guarda tu trabajo.", Foreground = RiskMedText, FontSize = 12.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
                stack.Children.Add(bar);
            }
            else if (_pendingPreferences.Count > 0)
            {
                bool needsRestart = _pendingPreferences.Keys.Any(id => prefs.Any(p => GetStr(p, "id") == id && GetStr(p, "applyMode") == "restart"));
                var bar = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
                Button apply = CreateStyledButton(needsRestart ? "Aplicar y reiniciar ahora" : "Aplicar preferencias (" + _pendingPreferences.Count + ")", true);
                apply.Margin = new Thickness(8, 0, 0, 0);
                apply.Click += async (s, e) => await RunUiActionAsync("tweaks-batch", apply, () => ApplyPendingPreferencesAsync(needsRestart));
                DockPanel.SetDock(apply, Dock.Right);
                bar.Children.Add(apply);
                if (needsRestart)
                {
                    Button later = CreateStyledButton("Aplicar sin reiniciar", false);
                    later.Margin = new Thickness(8, 0, 0, 0);
                    later.Click += async (s, e) => await RunUiActionAsync("tweaks-batch", later, () => ApplyPendingPreferencesAsync(false));
                    DockPanel.SetDock(later, Dock.Right);
                    bar.Children.Add(later);
                }
                Button discard = CreateStyledButton("Descartar", false);
                discard.Click += (s, e) => { _pendingPreferences.Clear(); RenderCurrentTab(); };
                DockPanel.SetDock(discard, Dock.Right);
                bar.Children.Add(discard);
                bar.Children.Add(new TextBlock { Text = _pendingPreferences.Count + (_pendingPreferences.Count == 1 ? " cambio pendiente" : " cambios pendientes") + (needsRestart ? ". Se aplican, se comprueban y, si todo está bien, el equipo se reinicia." : ". Antes se comprueba el punto de restauración."), Foreground = TextPrimary, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
                stack.Children.Add(bar);
            }
            card.Child = stack;
            return card;
        }

        // Each preference runs as its own journaled change (id#on / id#off) with the exact earlier values saved.
        internal Task SetPreferenceAsync(Dictionary<string, object> pref, bool on)
        {
            string title = GetStr(pref, "title") + (on ? ": activar" : ": desactivar");
            var change = new Dictionary<string, object> { { "id", GetStr(pref, "id") + (on ? "#on" : "#off") }, { "title", title }, { "shortTitle", title }, { "applyOps", pref[on ? "applyOps" : "undoOps"] } };
            foreach (string key in new[] { "requires", "osCompatibility", "deviceScope" }) if (pref.ContainsKey(key)) change[key] = pref[key];
            return _executeTweakAction(change, true);
        }

        // Makes an "instant" preference visible now, from the values just written to the registry.
        private void NotifyPreference(string id)
        {
            Func<string, string, int> read = (key, name) => { int v; int.TryParse(Convert.ToString(Registry.GetValue(@"HKEY_CURRENT_USER\" + key, name, "0")), out v); return v; };
            switch (id)
            {
                case "pref_dark_mode": SettingsNotify.Broadcast("ImmersiveColorSet"); break; // what Settings sends; not documented by Microsoft
                case "pref_mouse_accel": SettingsNotify.Mouse(read(@"Control Panel\Mouse", "MouseThreshold1"), read(@"Control Panel\Mouse", "MouseThreshold2"), read(@"Control Panel\Mouse", "MouseSpeed")); break;
                case "pref_window_snapping": SettingsNotify.WindowArranging(read(@"Control Panel\Desktop", "WindowArrangementActive") == 1); break;
                case "pref_sticky_keys": SettingsNotify.AccessibilityFlags((uint)read(@"Control Panel\Accessibility\StickyKeys", "Flags"), (uint)read(@"Control Panel\Accessibility\ToggleKeys", "Flags"), (uint)read(@"Control Panel\Accessibility\Keyboard Response", "Flags")); break;
            }
        }

        // Applies, re-reads every value and only then restarts Explorer or (if asked and everything checked out) the PC.
        internal async Task ApplyPendingPreferencesAsync(bool restartAfter)
        {
            if (_pendingPreferences.Count == 0) return;
            if (!TryBeginSystemChange("Preferencias (" + _pendingPreferences.Count + ")" + (restartAfter ? " y reinicio" : ""))) return;
            var problems = new List<string>();
            bool explorer = false;
            try
            {
                if (!await EnsureRestorePointAsync()) return;
                var activity = BeginOperation("Aplicar preferencias", "Optimización", _pendingPreferences.Count + 1);
                var applied = new List<KeyValuePair<Dictionary<string, object>, bool>>();
                foreach (var pair in _pendingPreferences.ToList())
                {
                    var pref = _tweaks.FirstOrDefault(t => GetStr(t, "id") == pair.Key);
                    if (pref == null) { _pendingPreferences.Remove(pair.Key); continue; }
                    StepOperation(activity, GetStr(pref, "title") + (pair.Value ? ": activar" : ": desactivar"), true, false);
                    try
                    {
                        await SetPreferenceAsync(pref, pair.Value);
                        _pendingPreferences.Remove(pair.Key);
                        applied.Add(new KeyValuePair<Dictionary<string, object>, bool>(pref, pair.Value));
                        if (GetStr(pref, "applyMode") == "instant")
                            try { _notifyPreference(pair.Key); } catch (Exception ex) { AppendLog(GetStr(pref, "title") + ": se guardó, pero Windows no lo aplicó al instante (" + ex.Message + "); se verá al cerrar sesión", "warn", "Optimización"); }
                        if (GetStr(pref, "applyMode") == "explorer") explorer = true;
                    }
                    catch (Exception ex) { problems.Add(GetStr(pref, "title") + ": " + ex.Message); AppendLog(GetStr(pref, "title") + ": " + ex.Message, "ERROR"); }
                }
                StepOperation(activity, "Comprobando cada preferencia en el registro", true, false);
                await RefreshTweaksStateNativeAsync();
                foreach (var pair in applied)
                {
                    bool now = _tweakStates.ContainsKey(GetStr(pair.Key, "id")) && _tweakStates[GetStr(pair.Key, "id")];
                    if (now != pair.Value) problems.Add(GetStr(pair.Key, "title") + ": Windows no conservó el cambio");
                }
                CompleteOperation(activity, problems.Count == 0, problems.Count == 0 ? "Preferencias aplicadas y comprobadas" : "Con problemas: " + string.Join("; ", problems));
            }
            finally { EndSystemChange(); }
            if (explorer && problems.Count == 0) await _restartExplorer();
            if (restartAfter && problems.Count == 0)
            {
                if (_confirmRestart() && _runShutdown("/r /t 20 /c \"Blackwall reinicia el equipo para aplicar las preferencias. Guarda tu trabajo.\""))
                {
                    _restartScheduled = true;
                    SetStatus("🔄 Preferencias comprobadas: el equipo se reinicia en 20 segundos", "Guarda tu trabajo");
                }
                else SetStatus("🟢 Preferencias aplicadas y comprobadas", "Se verán al reiniciar el equipo");
            }
            else if (problems.Count > 0) SetStatus("🔴 " + problems.Count + " preferencias no se aplicaron; no se reinicia", string.Join("; ", problems));
            else SetStatus("🟢 Preferencias aplicadas y comprobadas", "Listo");
            RenderCurrentTab();
        }

        private bool ConfirmRestart()
        {
            StackPanel body;
            WrapPanel actions;
            Window dialog = CreateAppDialog("Reiniciar el equipo", 500, out body, out actions);
            body.Children.Add(DialogText("Las preferencias ya están aplicadas y comprobadas. Para verlas, Windows tiene que reiniciarse.", TextPrimary, 12.5));
            body.Children.Add(DialogText("El equipo se reiniciará en 20 segundos. Guarda tu trabajo antes. Podrás cancelarlo desde Blackwall.", TextSecondary, 12));
            bool confirmed = false;
            AddDialogButton(actions, "Reiniciar más tarde", false).Click += (s, e) => dialog.Close();
            AddDialogButton(actions, "Reiniciar ahora", true).Click += (s, e) => { confirmed = true; dialog.Close(); };
            ShowAppDialog(dialog);
            return confirmed;
        }

        private void CancelRestart()
        {
            _restartScheduled = !_runShutdown("/a") && _restartScheduled;
            SetStatus(_restartScheduled ? "🔴 No se pudo cancelar el reinicio" : "🟢 Reinicio cancelado", "Las preferencias se verán al reiniciar el equipo");
            RenderCurrentTab();
        }

        // Windows restarts its shell by itself (non-elevated) after explorer.exe ends; Blackwall never starts an
        // elevated Explorer of its own.
        private async Task RestartExplorerAsync()
        {
            await Task.Run(() => { int code; RunSystemToolDirect("taskkill.exe", "/f /im explorer.exe", out code, 15000); });
            for (int i = 0; i < 20 && Process.GetProcessesByName("explorer").Length == 0; i++) await Task.Delay(250);
            if (Process.GetProcessesByName("explorer").Length == 0) SetStatus("🟡 El Explorador no volvió solo: cierra sesión y vuelve a entrar", "Preferencias");
        }

        // ── Ceito: en pausa, solo para revertir; al entrar siempre pasa por un asistente.
        private bool _ceitoUnlocked;
        private int _ceitoStep;
        private string _ceitoRestoreStatus;
        private bool _ceitoChecking;
        private bool _ceitoConfirmed;
        private Func<Task<string>> _checkRestorePoint;
        private Action<string> _notifyPreference;
        private Func<Task> _restartExplorer;
        private Func<bool> _confirmRestart;
        private Func<string, bool> _runShutdown;
        private readonly RotateTransform _ceitoEdgeRotation = new RotateTransform(0, 0.5, 0.5);
        private ScaleTransform _ceitoEmblemScale;
        private RotateTransform _ceitoWaitRotation;
        private int _ceitoDirection = 1;

        private void SetCeitoEdgeMotion(bool running)
        {
            if (running && MotionAllowed && !_ceitoEdgeRotation.HasAnimatedProperties) _ceitoEdgeRotation.BeginAnimation(RotateTransform.AngleProperty, SlowSpin(6));
            else if (!running && _ceitoEdgeRotation.HasAnimatedProperties) _ceitoEdgeRotation.BeginAnimation(RotateTransform.AngleProperty, null);
        }

        private void SetCeitoMotion(bool running)
        {
            SetCeitoEdgeMotion(running);
            if (_ceitoEmblemScale != null)
            {
                var scale = _ceitoEmblemScale;
                if (!running)
                {
                    scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                    scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                }
                else if (!scale.HasAnimatedProperties)
                {
                    var pop = new DoubleAnimation(0.55, 1, TimeSpan.FromMilliseconds(460)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 } };
                    pop.Completed += (s, e) =>
                    {
                        if (_ceitoEmblemScale != scale || !MotionAllowed || !IsVisible || !IsActive || WindowState == WindowState.Minimized || _activeTab != "ceito" || _ceitoUnlocked) return;
                        var breathe = new DoubleAnimation(1, 1.05, TimeSpan.FromSeconds(1.6)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
                        scale.BeginAnimation(ScaleTransform.ScaleXProperty, breathe);
                        scale.BeginAnimation(ScaleTransform.ScaleYProperty, breathe);
                    };
                    scale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
                    scale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
                }
            }
            if (_ceitoWaitRotation != null)
            {
                if (running && !_ceitoWaitRotation.HasAnimatedProperties)
                    _ceitoWaitRotation.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever });
                else if (!running && _ceitoWaitRotation.HasAnimatedProperties)
                    _ceitoWaitRotation.BeginAnimation(RotateTransform.AngleProperty, null);
            }
        }

        // Round emblem for each step, with a slow breathing pulse.
        private FrameworkElement CeitoEmblem(string glyph, Color tint)
        {
            var fill = new RadialGradientBrush(Color.FromArgb(150, tint.R, tint.G, tint.B), Color.FromArgb(18, tint.R, tint.G, tint.B)) { GradientOrigin = new Point(0.35, 0.3) };
            var ring = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1), RelativeTransform = _ceitoEdgeRotation };
            ring.GradientStops.Add(new GradientStop(ColorHex("#4E101C"), 0));
            ring.GradientStops.Add(new GradientStop(ColorHex("#FF665D"), 0.3));
            ring.GradientStops.Add(new GradientStop(ColorHex("#FFC0A4"), 0.7));
            ring.GradientStops.Add(new GradientStop(ColorHex("#8D1529"), 1));
            var emblem = new Border { Width = 84, Height = 84, CornerRadius = new CornerRadius(42), Background = fill, BorderBrush = ring, BorderThickness = new Thickness(2.5), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 14),
                Child = new TextBlock { Text = glyph, FontSize = 38, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = tint, BlurRadius = 26, ShadowDepth = 0, Opacity = 0.55 } };
            var scale = new ScaleTransform(1, 1);
            _ceitoEmblemScale = scale;
            emblem.RenderTransformOrigin = new Point(0.5, 0.5);
            emblem.RenderTransform = scale;
            return emblem;
        }

        private UIElement BuildCeitoView()
        {
            if (!_ceitoUnlocked) return BuildCeitoWizard();
            var root = new StackPanel();
            var section = _tweaks.Where(IsFrozen).ToList();
            StackPanel headStack;
            root.Children.Add(BuildTweaksHeader("Ceito · en pausa", section, false, out headStack));
            headStack.Children.Add(new Border { Background = RiskMedBg, CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 12, 0, 0),
                Child = new TextBlock { Text = "⚠ Sección experimental en pausa: no se pueden aplicar ajustes nuevos. Lo que se aplicó con Ceito, en este o en otro equipo y con cualquier versión de Blackwall, aparece como activo y se puede revertir.", Foreground = RiskMedText, FontSize = 12.5, TextWrapping = TextWrapping.Wrap } });
            AddSearchableGroups(root, (groups, filters) =>
            {
                AddTweakGroup(groups, filters, "Ceito", "solo para revertir", RiskMedText, section.Where(t => !GetBool(t, "retired")).ToList());
                AddTweakGroup(groups, filters, "Retirados", "aparecen solo mientras están aplicados", RiskHighText, section.Where(t => GetBool(t, "retired") && IsTweakOn(t)).ToList());
            });
            return root;
        }

        private UIElement BuildCeitoWizard()
        {
            var outer = new Grid { MinHeight = Math.Max(560, _mainScroll.ViewportHeight - 40) };
            Border card = CreateCardContainer();
            card.Width = 660;
            card.HorizontalAlignment = HorizontalAlignment.Center;
            card.VerticalAlignment = VerticalAlignment.Center;
            card.Padding = new Thickness(40, 30, 40, 26);
            card.CornerRadius = new CornerRadius(22);
            // The red liquid-silver bevel of the scan edge, turning slowly, with a soft ember glow.
            var bevel = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1), RelativeTransform = _ceitoEdgeRotation };
            bevel.GradientStops.Add(new GradientStop(ColorHex("#4E101C"), 0));
            bevel.GradientStops.Add(new GradientStop(ColorHex("#FF665D"), 0.25));
            bevel.GradientStops.Add(new GradientStop(ColorHex("#8D1529"), 0.53));
            bevel.GradientStops.Add(new GradientStop(ColorHex("#FFC0A4"), 0.78));
            bevel.GradientStops.Add(new GradientStop(ColorHex("#4E101C"), 1));
            card.BorderBrush = bevel;
            card.BorderThickness = new Thickness(2.5);
            card.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = ColorHex("#E0303F"), BlurRadius = 34, ShadowDepth = 0, Opacity = 0.32 };
            var stack = new StackPanel();
            var steps = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 18) };
            for (int i = 0; i < 3; i++)
            {
                var pill = new Border { Width = i == _ceitoStep ? 30 : 8, Height = 8, CornerRadius = new CornerRadius(4), Margin = new Thickness(4, 0, 4, 0), Background = i <= _ceitoStep ? CyberCyanBrush : BorderSubtle };
                if (i == _ceitoStep && MotionAllowed) pill.BeginAnimation(FrameworkElement.WidthProperty, new DoubleAnimation(8, 30, TimeSpan.FromMilliseconds(320)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
                steps.Children.Add(pill);
            }
            stack.Children.Add(steps);
            var body = new StackPanel();
            Button back = CreateStyledButton("Atrás", false);
            Button next = CreateStyledButton("Siguiente", true);
            back.IsEnabled = _ceitoStep > 0;
            Func<string, double, Brush, TextBlock> text = (value, size, color) => new TextBlock { Text = value, FontSize = size, Foreground = color, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 0, 0, 10) };
            if (_ceitoStep == 0)
            {
                body.Children.Add(CeitoEmblem("⚠", ColorHex("#FF5A4E")));
                body.Children.Add(text("Ceito es experimental", 22, TextPrimary));
                body.Children.Add(text("Esta parte de Blackwall todavía es totalmente experimental y no se recomienda usarla. Reúne ajustes de rendimiento que vinieron de Ceito Tweaks: muchos no tienen documentación oficial de Microsoft y algunos ya se retiraron porque no mejoraban nada o podían perjudicar al equipo.", 13, TextSecondary));
                body.Children.Add(text("Por ahora está en pausa: solo sirve para ver y revertir lo que ya se aplicó.", 13, TextPrimary));
            }
            else if (_ceitoStep == 1)
            {
                body.Children.Add(text("Punto de restauración", 22, TextPrimary));
                if (_ceitoRestoreStatus == null)
                {
                    if (!_ceitoChecking)
                    {
                        _ceitoChecking = true;
                        Func<Task> check = async () =>
                        {
                            string status;
                            try { status = await _checkRestorePoint(); } catch (Exception) { status = "none"; }
                            _ceitoRestoreStatus = status;
                            _ceitoChecking = false;
                            if (_activeTab == "ceito" && !_ceitoUnlocked) RenderCurrentTab();
                        };
                        var started = check();
                    }
                    var arc = new System.Windows.Shapes.Path { Data = Geometry.Parse("M 22,3 A 19,19 0 1 1 3,22"), Stroke = CyberCyanBrush, StrokeThickness = 3.5, Width = 44, Height = 44, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, RenderTransformOrigin = new Point(0.5, 0.5), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 12) };
                    var spin = new RotateTransform();
                    _ceitoWaitRotation = spin;
                    arc.RenderTransform = spin;
                    body.Children.Add(arc);
                    body.Children.Add(text("Comprobando que exista un punto de restauración verificado…", 13, TextSecondary));
                }
                else if (_ceitoRestoreStatus == "ok")
                {
                    var check = text("✓", 46, RiskLowText);
                    var grow = new ScaleTransform(1, 1);
                    check.RenderTransformOrigin = new Point(0.5, 0.5);
                    check.RenderTransform = grow;
                    if (MotionAllowed)
                    {
                        var pop = new DoubleAnimation(0.3, 1, TimeSpan.FromMilliseconds(420)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 } };
                        grow.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
                        grow.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
                    }
                    body.Children.Add(check);
                    body.Children.Add(text(_restorePointRecord == null ? "Punto de restauración verificado." : "Punto de restauración verificado: " + RestorePointSummary() + ".", 13, TextPrimary));
                }
                else
                {
                    body.Children.Add(text("✗", 40, RiskHighText));
                    body.Children.Add(text(_ceitoRestoreStatus == "unavailable" ? "Abre Blackwall como administrador para comprobar y crear puntos de restauración." : _ceitoRestoreStatus == "deleted" ? "El punto de restauración que Blackwall verificó ya no existe." : "No hay un punto de restauración verificado.", 13, TextPrimary));
                    Button create = CreateStyledButton("Crear punto de restauración ahora", true);
                    create.HorizontalAlignment = HorizontalAlignment.Center;
                    create.IsEnabled = _ceitoRestoreStatus != "unavailable";
                    create.Click += async (s, e) => await RunUiActionAsync("restore", create, async () => { await CreateRestorePointNowAsync(); _ceitoRestoreStatus = null; RenderCurrentTab(); });
                    body.Children.Add(create);
                }
                var confirm = new CheckBox { IsChecked = _ceitoConfirmed, Foreground = TextPrimary, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 0), Content = new TextBlock { Text = "Confirmo que tengo un punto de restauración y que entiendo que Ceito es experimental", TextWrapping = TextWrapping.Wrap } };
                confirm.IsEnabled = _ceitoRestoreStatus == "ok";
                confirm.Click += (s, e) => { _ceitoConfirmed = confirm.IsChecked == true; next.IsEnabled = _ceitoConfirmed && _ceitoRestoreStatus == "ok"; };
                body.Children.Add(confirm);
                next.IsEnabled = _ceitoConfirmed && _ceitoRestoreStatus == "ok";
            }
            else
            {
                body.Children.Add(CeitoEmblem("🛡", ColorHex("#FF7A5C")));
                body.Children.Add(text("Continúas bajo tu propio riesgo", 22, TextPrimary));
                body.Children.Add(text("Te deseamos lo mejor. Antes de seguir, ten en cuenta:", 13, TextSecondary));
                foreach (string tip in new[] { "Revierte de uno en uno y comprueba que el equipo va bien antes del siguiente.", "Si algo va raro, «Restablecer todo» deja Windows con sus valores y el punto de restauración es tu red de seguridad.", "Cada operación queda en el registro de cambios con su comprobación.", "Reinicia el equipo después de revertir ajustes de energía o de servicios." })
                    body.Children.Add(new TextBlock { Text = "• " + tip, FontSize = 12.8, Foreground = TextPrimary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(40, 0, 40, 6) });
                next.Content = "Entrar a Ceito";
            }
            stack.Children.Add(body);
            if (MotionAllowed)
            {
                // Steps slide in from the side they come from.
                var slide = new TranslateTransform();
                body.RenderTransform = slide;
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(40 * _ceitoDirection, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
                body.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320)) { FillBehavior = FillBehavior.Stop });
            }
            var actions = new DockPanel { Margin = new Thickness(0, 18, 0, 0), LastChildFill = false };
            DockPanel.SetDock(next, Dock.Right);
            actions.Children.Add(next);
            DockPanel.SetDock(back, Dock.Left);
            actions.Children.Add(back);
            back.Click += (s, e) => { _ceitoDirection = -1; _ceitoStep = Math.Max(0, _ceitoStep - 1); RenderCurrentTab(); };
            next.Click += (s, e) => { _ceitoDirection = 1; if (_ceitoStep < 2) _ceitoStep++; else { _ceitoUnlocked = true; SetCeitoEdgeMotion(false); } RenderCurrentTab(); };
            stack.Children.Add(actions);
            card.Child = stack;
            outer.Children.Add(card);
            UpdateAmbientMotion();
            return outer;
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
            string unmet = IsTweakOn(t) ? UnmetRequirement(t) : null;
            string warning = GetBool(t, "retired") ? "Retirado: " + GetStr(t, "retiredReason") + " Reviértelo para volver al valor anterior."
                : unmet != null && unmet != "aún no se ha identificado el equipo" ? "Está aplicado pero no corresponde a tu equipo (" + unmet + "). Reviértelo para volver a los valores de Windows."
                : GetStr(t, "riskWarning");
            string subcat = GetStr(t, "subcategory");

            bool isApplied = _tweakStates.ContainsKey(id) && _tweakStates[id];
            bool stateKnown = _hardware != null && _loadedModules.Contains("tweaks") && _tweakStates.ContainsKey(id);

            Border card = CreateCardContainer();
            card.Tag = id;
            card.Margin = new Thickness(0, 0, 12, 12);


            StackPanel sp = new StackPanel();

            Button switchButton = CreateStyledButton(isApplied ? "Revertir" : IsFrozen(t) ? "En pausa" : "Aplicar", !isApplied && !IsFrozen(t));
            switchButton.IsEnabled = stateKnown;
            switchButton.ToolTip = "Cambiar el estado de " + shortTitle;
            System.Windows.Automation.AutomationProperties.SetName(switchButton, (isApplied ? "Revertir " : "Aplicar ") + shortTitle);
            switchButton.IsEnabled = stateKnown && !_pendingTweaks.Contains(id) && (isApplied || !IsFrozen(t));
            if (IsFrozen(t) && !isApplied) switchButton.ToolTip = "Ceito está en pausa: solo se pueden revertir los ajustes ya aplicados";
            if (_pendingTweaks.Contains(id)) switchButton.Content = isApplied ? "Revirtiendo…" : "Aplicando…";

            // 1. TÍTULO + ESTADO: contorno y etiqueta (Activo, En parte, Se aplicará) en lugar del punto de color.
            string riskLabel = risk == "high" ? "Riesgo alto" : risk == "medium" ? "Precaución" : "Riesgo bajo";
            bool frozen = IsFrozen(t);
            DockPanel titleRow = new DockPanel { Margin = new Thickness(0, 0, 0, 3) };
            var stateText = new TextBlock { FontSize = 10.5, FontWeight = FontWeights.Bold };
            var stateChip = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 2, 8, 3), Margin = new Thickness(10, 1, 0, 0), VerticalAlignment = VerticalAlignment.Top, Child = stateText };
            DockPanel.SetDock(stateChip, Dock.Right);
            titleRow.Children.Add(stateChip);
            Action<bool> paintState = applied =>
            {
                string label = null;
                SolidColorBrush fg = null, bg = null;
                if (applied && _tweakPartial.Contains(id)) { label = "◐ En parte"; fg = RiskMedText; bg = RiskMedBg; }
                else if (applied) { label = "✓ Activo"; fg = RiskLowText; bg = RiskLowBg; }
                else if (_profileSelection.Contains(id)) { label = "◆ Se aplicará"; fg = CyberCyanBrush; bg = BgElevated; }
                stateChip.Visibility = label == null ? Visibility.Collapsed : Visibility.Visible;
                stateText.Text = label ?? "";
                if (label == null) { card.SetResourceReference(Border.BorderBrushProperty, "MaterialEdge"); card.BorderThickness = new Thickness(1); }
                else { stateText.Foreground = fg; stateChip.Background = bg; card.BorderBrush = fg; card.BorderThickness = new Thickness(2); }
                System.Windows.Automation.AutomationProperties.SetName(stateChip, label ?? "");
            };
            TextBlock titleText = new TextBlock
            {
                Text = shortTitle,
                Foreground = TextPrimary,
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                TextWrapping = TextWrapping.Wrap
            };
            titleRow.Children.Add(titleText);
            sp.Children.Add(titleRow);

            // 2. UNA LÍNEA CON EL BENEFICIO: el resumen técnico pasa a Detalles.
            string headline = null;
            if (t.ContainsKey("benefits") && t["benefits"] is IEnumerable)
                foreach (object bObj in (IEnumerable)t["benefits"]) { headline = Convert.ToString(bObj); if (!string.IsNullOrEmpty(headline)) break; }
            if (string.IsNullOrEmpty(headline)) headline = summary;
            sp.Children.Add(new TextBlock
            {
                Text = headline,
                Foreground = TextSecondary,
                FontSize = 12.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6)
            });

            // 3. ETIQUETAS SOLO SI HAY ALGO QUE AVISAR (precaución, reinicio).
            string cautionText = (affects ?? "") + " " + (warning ?? "");
            WrapPanel chips = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
            if ((risk == "medium" || risk == "high") && string.IsNullOrEmpty(warning))
            {
                Border riskChip = CreateBadge(risk == "high" ? "⚠ Riesgo alto" : "⚠ Precaución", risk);
                riskChip.ToolTip = string.IsNullOrWhiteSpace(cautionText) ? riskLabel : cautionText.Trim();
                riskChip.Margin = new Thickness(0, 0, 6, 2);
                chips.Children.Add(riskChip);
            }
            if (cautionText.IndexOf("reinici", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Border restartChip = CreateBadge("↻ Requiere reiniciar", "medium");
                restartChip.Margin = new Thickness(0, 0, 6, 2);
                chips.Children.Add(restartChip);
            }
            if (chips.Children.Count > 0) sp.Children.Add(chips);

            // 4. CAJÓN DESPLEGABLE TÉCNICO (DRAWER)
            Button toggleDrawerBtn = new Button
            {
                Content = "Detalles ▾",
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
                Padding = new Thickness(0, 6, 4, 6),
                Foreground = CyberCyanBrush,
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Cursor = Cursors.Hand
            };
            System.Windows.Automation.AutomationProperties.SetName(toggleDrawerBtn, "Detalles de " + shortTitle);
            toggleDrawerBtn.SetResourceReference(FrameworkElement.StyleProperty, "FlatButton");

            StackPanel drawerPanel = new StackPanel
            {
                Visibility = Visibility.Collapsed,
                Margin = new Thickness(0, 4, 0, 6)
            };

            drawerPanel.Children.Add(new TextBlock
            {
                Text = summary,
                Foreground = TextPrimary,
                FontSize = 11.8,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6)
            });

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
                    Text = "Precaución: " + warning,
                    Foreground = RiskMedText,
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap
                };
                sp.Children.Add(warnBox); // safety contract: a risk warning is never hidden behind Detalles
            }
            if (!string.IsNullOrEmpty(affects))
                drawerPanel.Children.Add(new TextBlock { Text = "Afecta: " + affects, Foreground = RiskMedText, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });
            if (!string.IsNullOrEmpty(subcat))
                drawerPanel.Children.Add(new TextBlock { Text = "Categoría: " + subcat, Foreground = TextSecondary, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });

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

            drawerPanel.Children.Add(new TextBlock { Text = "Reversión: " + (t.ContainsKey("undoOps") && ConvertToDictList(t["undoOps"]).Count > 0 ? "Al aplicarlo, Blackwall guarda tus valores actuales y Revertir los restaura; si no hay valores guardados, usa los del catálogo." : "este ajuste no declara una reversión automática."), Foreground = TextSecondary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
            bool detailsOpen = false;
            toggleDrawerBtn.Click += (s, e) =>
            {
                detailsOpen = !detailsOpen;
                if (detailsOpen) AdaptiveCardGrid.SetExpanded(card, true);
                toggleDrawerBtn.Content = detailsOpen ? "Ocultar detalles ▴" : "Detalles ▾";
                drawerPanel.BeginAnimation(FrameworkElement.HeightProperty, null);
                drawerPanel.Height = double.NaN;
                if (detailsOpen)
                {
                    drawerPanel.Visibility = Visibility.Visible;
                    drawerPanel.Measure(new Size(Math.Max(160, card.ActualWidth - 36), double.PositiveInfinity));
                    if (MotionAllowed) drawerPanel.BeginAnimation(FrameworkElement.HeightProperty, new DoubleAnimation(0, drawerPanel.DesiredSize.Height, TimeSpan.FromMilliseconds(180)) { FillBehavior = FillBehavior.Stop, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
                }
                else if (MotionAllowed)
                {
                    var close = new DoubleAnimation(drawerPanel.ActualHeight, 0, TimeSpan.FromMilliseconds(180)) { FillBehavior = FillBehavior.Stop };
                    close.Completed += (sender, args) => { if (!detailsOpen) { drawerPanel.Visibility = Visibility.Collapsed; AdaptiveCardGrid.SetExpanded(card, false); } };
                    drawerPanel.BeginAnimation(FrameworkElement.HeightProperty, close);
                }
                else { drawerPanel.Visibility = Visibility.Collapsed; AdaptiveCardGrid.SetExpanded(card, false); }
            };

            // 4. PIE DE TARJETA: Estado actual + botón Undo si activo
            DockPanel footer = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };

            TextBlock statusText = new TextBlock
            {
                Text = !stateKnown ? "Pendiente de verificar" : isApplied ? "Detectado como activo" : "No detectado como activo",
                ToolTip = "La detección usa los criterios del catálogo; no demuestra que todas las operaciones se hayan aplicado ni que se conserve tu configuración original.",
                Foreground = isApplied ? RiskLowText : TextSecondary,
                FontWeight = isApplied ? FontWeights.Bold : FontWeights.Normal,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            if (_tweakActionErrors.ContainsKey(id)) { statusText.Text = "No se completó: " + _tweakActionErrors[id]; statusText.Foreground = RiskHighText; }
            DockPanel.SetDock(switchButton, Dock.Right);
            switchButton.Margin = new Thickness(10, 0, 0, 0);
            footer.Children.Add(switchButton);
            DockPanel.SetDock(toggleDrawerBtn, Dock.Left);
            footer.Children.Add(toggleDrawerBtn);
            footer.Children.Add(statusText);
            Grid cardLayout = new Grid();
            cardLayout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            cardLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            cardLayout.Children.Add(sp);
            Grid.SetRow(footer, 1);
            cardLayout.Children.Add(footer);
            card.Child = cardLayout;
            paintState(isApplied);

            // Actualizador en caliente registrado para esta tarjeta (Cero saltos de scroll)
            Action<bool> updateCardUi = (bool applied) =>
            {

                switchButton.Content = applied ? "Revertir" : frozen ? "En pausa" : "Aplicar";
                switchButton.IsEnabled = applied || !frozen;
                paintState(applied);
                switchButton.SetResourceReference(Control.BackgroundProperty, applied ? "MaterialButtonSurface" : "MaterialSilver");
                switchButton.Foreground = applied ? TextPrimary : BrushHex("#182232");
                System.Windows.Automation.AutomationProperties.SetName(switchButton, (applied ? "Revertir " : "Aplicar ") + shortTitle);
                AnimateEntrance(statusText, 2, 180);
                statusText.Text = applied ? "Detectado como activo" : "No detectado como activo";
                statusText.Foreground = applied ? RiskLowText : TextSecondary;
                statusText.FontWeight = applied ? FontWeights.Bold : FontWeights.Normal;
            };
            _tweakCardUpdaters[id] = updateCardUi;

            // Manejador del clic en el Switch Toggle
            switchButton.Click += async (s, e) =>
            {
                e.Handled = true;
                if (!stateKnown || (frozen && !(_tweakStates.ContainsKey(id) && _tweakStates[id])) || !_pendingTweaks.Add(id)) return;
                _tweakActionErrors.Remove(id);
                bool current = _tweakStates.ContainsKey(id) && _tweakStates[id];
                switchButton.IsEnabled = false;
                switchButton.Content = current ? "Revirtiendo…" : "Aplicando…";
                bool began = false;
                try
                {
                    if (!(began = TryBeginSystemChange("Interruptor · " + shortTitle + (current ? " (revertir)" : " (aplicar)")))) return;
                    if (!current && await _confirmApply(shortTitle, new List<Dictionary<string, object>> { t }) == null) return;
                    if (current && !_changes.Any(c => GetStr(c, "id") == id) && !_confirmCatalogRevert(t)) return;
                    await _executeTweakAction(t, !current);
                }
                catch (Exception ex)
                {
                    _tweakActionErrors[id] = ex.Message;
                    statusText.Text = "No se completó: " + ex.Message;
                    statusText.Foreground = RiskHighText;
                    SetStatus("No se completó el ajuste", shortTitle);
                    AppendLog(ex.Message, "ERROR");
                }
                finally
                {
                    if (began) EndSystemChange();
                    _pendingTweaks.Remove(id);
                    bool nowApplied = _tweakStates.ContainsKey(id) && _tweakStates[id];
                    switchButton.IsEnabled = nowApplied || !frozen;
                    switchButton.Content = nowApplied ? "Revertir" : frozen ? "En pausa" : "Aplicar";
                    Action<bool> currentUpdater;
                    if (_tweakCardUpdaters.TryGetValue(id, out currentUpdater) && currentUpdater != updateCardUi) RenderCurrentTab();
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

            WrapPanel topBtns = new WrapPanel();
            CheckBox directModeChk = new CheckBox
            {
                Content = "HTTPS directo",
                ToolTip = "Forzar la descarga del instalador oficial por HTTPS en lugar de utilizar WinGet",
                Foreground = TextPrimary,
                IsChecked = _preferDirectHttpsDownload,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            };
            directModeChk.Click += (s, e) => _preferDirectHttpsDownload = directModeChk.IsChecked == true;
            topBtns.Children.Add(directModeChk);

            Button upgradeAllBtn = CreateStyledButton("Actualizar todo", false);
            upgradeAllBtn.ToolTip = "Actualizar todas las aplicaciones mediante WinGet";
            upgradeAllBtn.Margin = new Thickness(0, 0, 8, 0);
            upgradeAllBtn.Click += async (s, e) => await RunUiActionAsync("software-batch", upgradeAllBtn, () => UpgradeAllAppsAsync());
            topBtns.Children.Add(upgradeAllBtn);

            Button installSelectedBtn = CreateStyledButton("Instalar seleccionadas (" + _selectedApps.Count + ")", true);
            _installSelectedButton = installSelectedBtn;
            installSelectedBtn.Click += async (s, e) => await RunUiActionAsync("software-batch", installSelectedBtn, () => InstallSelectedAppsBatchAsync());
            topBtns.Children.Add(installSelectedBtn);

            root.Children.Add(BuildStartupCard());

            root.Children.Add(CreateBanner(
                "Instalador de software",
                "Selecciona aplicaciones y despliega sus instaladores mediante WinGet o descarga HTTPS directa.",
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
            AdaptiveCardGrid grid = new AdaptiveCardGrid { Columns = 3 };
            var filter = CreateCardFilter(grid, _apps.Where(a => _appCategoryScope == "Todas" || GetStr(a, "category") == _appCategoryScope), BuildAppCard, "name", "description", "wingetId");
            var empty = CreateBanner("Sin resultados", "Prueba otro nombre o categoría de software.");
            Action<string> applyFilter = query => empty.Visibility = filter(query) == 0 ? Visibility.Visible : Visibility.Collapsed;
            var filterCard = CreateCardContainer();
            filterCard.Margin = new Thickness(0, 0, 0, 14);
            var filterBody = new StackPanel();
            filterBody.Children.Add(new TextBlock { Text = "Buscar y filtrar aplicaciones", Foreground = TextPrimary, FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
            filterBody.Children.Add(CreateSectionSearch(_appSearchText, value => { _appSearchText = value; applyFilter(value); }));
            filterBody.Children.Add(catBar);
            filterCard.Child = filterBody;
            root.Children.Add(filterCard);
            applyFilter(_appSearchText);
            root.Children.Add(empty);
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
            card.Tag = id;
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
            System.Windows.Automation.AutomationProperties.SetName(chk, "Seleccionar " + GetStr(a, "name"));
            chk.Click += (s, e) =>
            {
                if (chk.IsChecked == true) _selectedApps.Add(id);
                else _selectedApps.Remove(id);
                if (chk.IsChecked == true) card.BorderBrush = AccentBrush;
                else if (isInstalled) card.BorderBrush = RiskLowText;
                else card.SetResourceReference(Border.BorderBrushProperty, "MaterialEdge");
                if (_installSelectedButton != null) _installSelectedButton.Content = "Instalar seleccionadas (" + _selectedApps.Count + ")";
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
                Foreground = CyberCyanBrush,
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
            Grid brandStack = new Grid();
            brandStack.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            brandStack.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            brandStack.Children.Add(iconContainer);

            StackPanel titleStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            titleStack.Children.Add(new TextBlock
            {
                Text = GetStr(a, "category") + (isInstalled ? "  •  ✓ INSTALADO (" + ver + ")" : ""),
                Foreground = isInstalled ? RiskLowText : CyberCyanBrush,
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
                TextWrapping = TextWrapping.Wrap
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
            Grid.SetColumn(titleStack, 1);
            header.Children.Add(brandStack);
            sp.Children.Add(header);

            sp.Children.Add(new TextBlock
            {
                Text = GetStr(a, "description"),
                Foreground = TextSecondary,
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 10),
                TextTrimming = TextTrimming.None
            });

            DockPanel footer = new DockPanel();
            Button singleBtn = CreateStyledButton(isInstalled ? "Actualizar" : "Instalar", false);
            singleBtn.Padding = new Thickness(9, 4, 9, 4);
            singleBtn.Click += async (s, e) =>
            {
                if (!singleBtn.IsEnabled) return;
                singleBtn.IsEnabled = false;
                try { await InstallSingleAppAsync(a); }
                finally { singleBtn.IsEnabled = true; }
            };
            DockPanel.SetDock(singleBtn, Dock.Right);
            footer.Children.Add(singleBtn);

            footer.Children.Add(new TextBlock
            {
                Text = GetStr(a, "wingetId"),
                Foreground = TextSecondary,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 10.5,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = GetStr(a, "wingetId"),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            var cardLayout = new Grid();
            cardLayout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            cardLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            cardLayout.Children.Add(sp);
            Grid.SetRow(footer, 1);
            cardLayout.Children.Add(footer);
            card.Child = cardLayout;
            return card;
        }

        // --------------------------------------------------------------------
        // MÓDULO D: MONITOR DE SCRIPTS EN VIVO
        // --------------------------------------------------------------------
        private DockPanel CardTitle(string glyph, string text, UIElement right = null)
        {
            var line = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
            if (right != null) { DockPanel.SetDock(right, Dock.Right); line.Children.Add(right); }
            var title = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            title.Children.Add(new TextBlock { Text = glyph, FontFamily = NavIconFont, FontSize = 16, Foreground = CyberCyanBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
            title.Children.Add(new TextBlock { Text = text, Foreground = TextPrimary, FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            line.Children.Add(title);
            return line;
        }

        private UIElement BuildMonitorView()
        {
            _monitorProcessMemoryLabels.Clear();
            StackPanel root = new StackPanel();

            // 1. CABECERA COMPACTA
            Border head = CreateCardContainer();
            head.Margin = new Thickness(0, 0, 0, 14);
            head.Padding = new Thickness(20, 16, 20, 16);
            var headStack = new StackPanel();
            headStack.Children.Add(new TextBlock { Text = "Actividad", Foreground = TextPrimary, FontSize = 22, FontWeight = FontWeights.SemiBold });
            _activitySummary = new TextBlock { Foreground = TextSecondary, FontSize = 12.5, Margin = new Thickness(0, 6, 0, 0) };
            headStack.Children.Add(_activitySummary);
            head.Child = headStack;
            root.Children.Add(head);

            // 2. OPERACIONES
            Border opsCard = CreateCardContainer();
            opsCard.Margin = new Thickness(0, 0, 0, 14);
            var opsStack = new StackPanel();
            opsStack.Children.Add(CardTitle("", "Operaciones"));
            _activityOpsPanel = new StackPanel();
            opsStack.Children.Add(_activityOpsPanel);
            opsCard.Child = opsStack;
            root.Children.Add(opsCard);
            RefreshActivityUi();

            // 3. REGISTRO
            Border logCard = CreateCardContainer();
            logCard.Margin = new Thickness(0, 0, 0, 14);
            var logStack = new StackPanel();
            var logActions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            Button copyBtn = CreateLinkButton("Copiar");
            copyBtn.Click += (s, e) => { try { Clipboard.SetText(ActivityLogText()); SetStatus("Registro copiado al portapapeles", "Listo"); } catch (Exception ex) { AppendLog("No se pudo copiar el registro: " + ex.Message, "warn", "Sistema"); } };
            Button exportBtn = CreateLinkButton("Exportar .log");
            exportBtn.Click += (s, e) =>
            {
                var dialog = new SaveFileDialog { FileName = "Blackwall-actividad-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".log", Filter = "Registro (*.log)|*.log|Texto (*.txt)|*.txt" };
                if (dialog.ShowDialog(this) != true) return;
                try { File.WriteAllText(dialog.FileName, ActivityLogText(), new UTF8Encoding(false)); AppendLog("Registro exportado en " + dialog.FileName, "info", "Sistema"); }
                catch (Exception ex) { AppendLog("No se pudo exportar el registro: " + ex.Message, "error", "Sistema"); }
            };
            Button clearBtn = CreateLinkButton("Limpiar");
            clearBtn.Click += (s, e) => { _activityLog.Clear(); RefreshLogRows(); };
            foreach (var b in new[] { copyBtn, exportBtn, clearBtn }) { b.Margin = new Thickness(8, 0, 0, 0); logActions.Children.Add(b); }
            logStack.Children.Add(CardTitle("", "Registro", logActions));

            var filters = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
            foreach (var option in new[] { new[] { "all", "Todo" }, new[] { "problems", "Errores y avisos" }, new[] { "Optimización", "Optimizaciones" }, new[] { "Instalación", "Instalaciones" }, new[] { "Sistema", "Sistema" }, new[] { "Seguridad", "Seguridad" } })
            {
                string key = option[0];
                AddFilterButton(filters, option[1], _activityFilter == key, () => { _activityFilter = key; RenderCurrentTab(); });
            }
            logStack.Children.Add(filters);

            var logSurface = new Border { Background = BgApp, BorderBrush = BorderSubtle, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 12, 6, 12) };
            var logGrid = new Grid();
            // Virtualized: only the ~15 visible lines are laid out and drawn, however long the log is.
            _activityLogRows = (ItemsControl)System.Windows.Markup.XamlReader.Parse(@"
                <ItemsControl xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
                    Height='320' VirtualizingStackPanel.IsVirtualizing='True' VirtualizingStackPanel.VirtualizationMode='Recycling' ScrollViewer.CanContentScroll='True'>
                    <ItemsControl.ItemsPanel><ItemsPanelTemplate><VirtualizingStackPanel Margin='0,0,8,0'/></ItemsPanelTemplate></ItemsControl.ItemsPanel>
                    <ItemsControl.Template><ControlTemplate TargetType='ItemsControl'>
                        <ScrollViewer x:Name='Scroll' Background='Transparent' PanningMode='VerticalOnly' CanContentScroll='True' VerticalScrollBarVisibility='Auto' HorizontalScrollBarVisibility='Disabled' Focusable='False'><ItemsPresenter/></ScrollViewer>
                    </ControlTemplate></ItemsControl.Template>
                    <ItemsControl.ItemTemplate><DataTemplate>
                        <TextBlock TextWrapping='Wrap' FontSize='12.5' Margin='0,0,0,5' Foreground='{Binding Text}'><Run Text='{Binding Time, Mode=OneWay}' FontFamily='Consolas' FontSize='11.5' Foreground='{Binding Muted}'/><Run Text='   •  ' FontWeight='Bold' FontSize='14' Foreground='{Binding Dot}'/><Run Text='{Binding Source, Mode=OneWay}' FontWeight='SemiBold' FontSize='11.5' Foreground='{Binding Muted}'/><Run Text='   '/><Run Text='{Binding Message, Mode=OneWay}'/></TextBlock>
                    </DataTemplate></ItemsControl.ItemTemplate>
                </ItemsControl>");
            _activityLogRows.ItemsSource = _activityRows;
            _activityLogRows.ApplyTemplate();
            _activityLogScroll = (ScrollViewer)_activityLogRows.Template.FindName("Scroll", _activityLogRows);
            _activityLogScroll.PreviewMouseWheel += ChainWheelToPage;
            _activityLogEmpty = new TextBlock { Text = "Sin entradas para este filtro.", Foreground = TextSecondary, FontSize = 12.5, Margin = new Thickness(0, 4, 0, 0), IsHitTestVisible = false };
            logGrid.Children.Add(_activityLogRows);
            logGrid.Children.Add(_activityLogEmpty);
            logSurface.Child = logGrid;
            logStack.Children.Add(logSurface);
            logCard.Child = logStack;
            root.Children.Add(logCard);
            RefreshLogRows();

            // 4. SCRIPTS EN SEGUNDO PLANO
            Border scriptsCard = CreateCardContainer();
            var scriptsStack = new StackPanel();
            Button refreshBtn = CreateLinkButton("↻ Actualizar");
            refreshBtn.Click += async (s, e) => await RefreshLiveScriptsNativeAsync();
            var titleLine = CardTitle("", "Scripts en segundo plano", refreshBtn);
            _monitorProcessTitle = (TextBlock)((StackPanel)titleLine.Children[titleLine.Children.Count - 1]).Children[1];
            scriptsStack.Children.Add(titleLine);
            scriptsStack.Children.Add(new TextBlock { Text = "PowerShell, cmd y Windows Script Host abiertos por cualquier programa. Se actualiza cada 3 s mientras esta pantalla está abierta.", Foreground = TextSecondary, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, -6, 0, 12) });
            _monitorProcessPanel = new StackPanel();
            scriptsStack.Children.Add(_monitorProcessPanel);
            scriptsCard.Child = scriptsStack;
            root.Children.Add(scriptsCard);
            RebuildProcessList();
            return root;
        }

        // Scroll chaining: at its top or bottom the log hands the wheel/touchpad to the page instead of swallowing it.
        private void ChainWheelToPage(object sender, MouseWheelEventArgs e)
        {
            var inner = (ScrollViewer)sender;
            bool atLimit = e.Delta < 0 ? inner.VerticalOffset >= inner.ScrollableHeight - 0.5 : inner.VerticalOffset <= 0.5;
            if (!atLimit) return;
            e.Handled = true;
            // Re-raise above the log list so it bubbles straight to the page's smooth scroller.
            var outside = _activityLogRows == null ? null : VisualTreeHelper.GetParent(_activityLogRows) as UIElement;
            if (outside != null) outside.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = UIElement.MouseWheelEvent, Source = inner });
        }

        private void RebuildProcessList()
        {
            if (_monitorProcessPanel == null) return;
            _monitorProcessPanel.Children.Clear();
            _monitorProcessMemoryLabels.Clear();
            if (_monitorProcessTitle != null) _monitorProcessTitle.Text = "Scripts en segundo plano · " + _liveScripts.Count;
            if (_liveScripts.Count == 0)
            {
                _monitorProcessPanel.Children.Add(new TextBlock { Text = "No hay scripts ejecutándose en segundo plano.", Foreground = TextSecondary, FontSize = 12.5 });
                return;
            }
            foreach (var p in _liveScripts) _monitorProcessPanel.Children.Add(BuildProcessRow(p));
        }

        private UIElement BuildProcessRow(Dictionary<string, object> p)
        {
            int pid = GetInt(p, "pid");
            string name = GetStr(p, "name");
            string lower = name.ToLowerInvariant();
            string glyph = lower.Contains("powershell") || lower.Contains("pwsh") ? "" : lower == "cmd.exe" ? "" : "";
            var row = new Border { Background = BgApp, BorderBrush = BorderSubtle, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 10, 12, 10), Margin = new Thickness(0, 0, 0, 8) };
            var stack = new StackPanel();
            var line = new DockPanel();

            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            Button endBtn = CreateStyledButton("Terminar", false);
            endBtn.Padding = new Thickness(14, 4, 14, 4);
            System.Windows.Automation.AutomationProperties.SetName(endBtn, "Terminar " + name + " PID " + pid);
            actions.Children.Add(endBtn);
            DockPanel.SetDock(actions, Dock.Right);
            line.Children.Add(actions);

            var identity = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            identity.Children.Add(new TextBlock { Text = glyph, FontFamily = NavIconFont, FontSize = 15, Foreground = TextSecondary, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
            identity.Children.Add(new TextBlock { Text = name, Foreground = TextPrimary, FontSize = 13.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
            var meta = new TextBlock { Text = ProcessMetaText(p), Foreground = TextSecondary, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            _monitorProcessMemoryLabels[pid] = meta;
            identity.Children.Add(meta);
            line.Children.Add(identity);
            stack.Children.Add(line);

            string command = GetStr(p, "commandLine");
            var commandText = new TextBlock { Text = command, Foreground = TextSecondary, FontFamily = new FontFamily("Consolas"), FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(25, 6, 0, 0), ToolTip = command };
            stack.Children.Add(commandText);
            Button expand = CreateLinkButton("Ver comando");
            expand.HorizontalAlignment = HorizontalAlignment.Left;
            expand.Margin = new Thickness(21, 2, 0, 0);
            expand.FontSize = 11.5;
            expand.Click += (s, e) =>
            {
                bool open = commandText.TextWrapping == TextWrapping.NoWrap;
                commandText.TextWrapping = open ? TextWrapping.Wrap : TextWrapping.NoWrap;
                commandText.TextTrimming = open ? TextTrimming.None : TextTrimming.CharacterEllipsis;
                expand.Content = open ? "Ocultar comando" : "Ver comando";
            };
            stack.Children.Add(expand);

            // Killing a process can close the app that owns it: confirm inline before doing it.
            var confirm = new Border { Background = RiskHighBg, CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
            var confirmLine = new DockPanel();
            var confirmButtons = new StackPanel { Orientation = Orientation.Horizontal };
            Button yes = CreateStyledButton("Terminar", true);
            yes.Padding = new Thickness(14, 4, 14, 4);
            yes.Margin = new Thickness(8, 0, 0, 0);
            Button no = CreateLinkButton("Cancelar");
            no.Margin = new Thickness(8, 0, 0, 0);
            confirmButtons.Children.Add(no);
            confirmButtons.Children.Add(yes);
            DockPanel.SetDock(confirmButtons, Dock.Right);
            confirmLine.Children.Add(confirmButtons);
            confirmLine.Children.Add(new TextBlock { Text = "¿Terminar " + name + " (PID " + pid + ")? Puede cerrar el programa que lo abrió.", Foreground = RiskHighText, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
            confirm.Child = confirmLine;
            stack.Children.Add(confirm);
            endBtn.Click += (s, e) => { confirm.Visibility = Visibility.Visible; endBtn.IsEnabled = false; no.Focus(); };
            no.Click += (s, e) => { confirm.Visibility = Visibility.Collapsed; endBtn.IsEnabled = true; endBtn.Focus(); };
            yes.Click += async (s, e) =>
            {
                yes.IsEnabled = false;
                try { Process.GetProcessById(pid).Kill(); AppendLog(name + " (PID " + pid + ") terminado.", "info", "Procesos"); }
                catch (Exception ex) { AppendLog("No se pudo terminar " + name + " (PID " + pid + "): " + ex.Message, "warn", "Procesos"); }
                await RefreshLiveScriptsNativeAsync();
            };

            row.Child = stack;
            return row;
        }

        // --------------------------------------------------------------------
        // MÓDULO C: SEGURIDAD Y RESTAURACIÓN
        // --------------------------------------------------------------------
        private UIElement BuildSafetyView()
        {
            StackPanel root = new StackPanel();
            root.Children.Add(CreateBanner(
                "Seguridad y recuperación",
                "Blackwall utiliza la API WMI nativa root\\default:SystemRestore sin scripts externos."
            ));

            AdaptiveCardGrid grid = new AdaptiveCardGrid { Columns = 2, Margin = new Thickness(0, 0, 0, 14) };

            Border rpCard = CreateCardContainer();
            rpCard.Margin = new Thickness(0, 0, 12, 0);
            StackPanel rpStack = new StackPanel();
            rpStack.Children.Add(new TextBlock { Text = "🛡️ Protección Automática y Puntos de Restauración", Foreground = TextPrimary, FontWeight = FontWeights.Bold, FontSize = 14, TextWrapping = TextWrapping.Wrap });
            rpStack.Children.Add(new TextBlock { Text = "Blackwall no cambia el sistema sin un punto de restauración que Windows siga teniendo; si se borra, pide uno nuevo.", Foreground = TextSecondary, FontSize = 12.3, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 10) });
            bool pointGone = _restorePointRecord != null && _restorePointStatus == "deleted";
            rpStack.Children.Add(new TextBlock
            {
                Text = _restorePointRecord == null ? "Punto verificado: ninguno todavía (se pedirá antes del primer cambio)" : pointGone ? "⚠ El punto " + RestorePointSummary() + " ya no está en Windows: crea uno nuevo" : "Punto verificado: " + RestorePointSummary(),
                Foreground = pointGone ? RiskHighText : _restorePointRecord == null ? TextSecondary : RiskLowText,
                FontSize = 12.3, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12)
            });

            WrapPanel rpBtns = new WrapPanel();
            Button createRpBtn = CreateStyledButton("➕ Crear Punto de Restauración Ahora", true);
            createRpBtn.Margin = new Thickness(0, 0, 8, 0);
            createRpBtn.Click += async (s, e) => await RunUiActionAsync("restore", createRpBtn, () => CreateRestorePointNowAsync());
            rpBtns.Children.Add(createRpBtn);

            Button rstruiBtn = CreateStyledButton("🕰️ Abrir rstrui.exe", false);
            rstruiBtn.Click += (s, e) => { try { Process.Start(SystemToolPath("rstrui.exe")); } catch (System.ComponentModel.Win32Exception ex) { AppendLog("No se pudo abrir Restaurar sistema: " + ex.Message, "WARN"); } };
            rpBtns.Children.Add(rstruiBtn);
            rpStack.Children.Add(rpBtns);
            rpCard.Child = rpStack;
            grid.Children.Add(rpCard);

            Border repairCard = CreateCardContainer();
            StackPanel repStack = new StackPanel();
            repStack.Children.Add(new TextBlock { Text = "🔧 Reparación de Imagen de Windows (SFC, DISM & Red)", Foreground = TextPrimary, FontWeight = FontWeights.Bold, FontSize = 14, TextWrapping = TextWrapping.Wrap });
            repStack.Children.Add(new TextBlock { Text = "Invoca directamente las herramientas nativas de mantenimiento de Microsoft.", Foreground = TextSecondary, FontSize = 12.3, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 12) });

            WrapPanel repBtns = new WrapPanel();
            Button sfcBtn = CreateStyledButton("🔍 Ejecutar SFC /verifyonly", false);
            sfcBtn.Margin = new Thickness(0, 0, 8, 8);
            sfcBtn.Click += async (s, e) =>
            {
                SetStatus("🔍 Ejecutando SFC...", "sfc.exe /verifyonly");
                if (await RunOperationAsync("Verificar archivos de Windows (SFC)", "Sistema", op => Task.Run(() => { StepOperation(op, "sfc.exe /verifyonly · puede tardar varios minutos"); return "SFC: " + LastMeaningfulLines(RunSystemToolDirect("sfc.exe", "/verifyonly"), 2); })))
                    SetStatus("🟢 Verificación SFC finalizada", "Listo");
            };
            repBtns.Children.Add(sfcBtn);

            Button dismBtn = CreateStyledButton("🩺 Ejecutar DISM /CheckHealth", false);
            dismBtn.Margin = new Thickness(0, 0, 8, 8);
            dismBtn.Click += async (s, e) =>
            {
                SetStatus("🩺 Ejecutando DISM...", "DISM.exe /CheckHealth");
                if (await RunOperationAsync("Comprobar la imagen de Windows (DISM)", "Sistema", op => Task.Run(() => { StepOperation(op, "DISM /Online /Cleanup-Image /CheckHealth"); return "DISM: " + LastMeaningfulLines(RunSystemToolDirect("DISM.exe", "/Online /Cleanup-Image /CheckHealth"), 2); })))
                    SetStatus("🟢 Diagnóstico DISM finalizado", "Listo");
            };
            repBtns.Children.Add(dismBtn);

            Button dnsBtn = CreateStyledButton("🌐 Limpiar caché DNS", false);
            dnsBtn.Margin = new Thickness(0, 0, 8, 8);
            dnsBtn.Click += async (s, e) =>
            {
                if (await RunOperationAsync("Limpiar la caché DNS", "Sistema", op => Task.Run(() => { StepOperation(op, "ipconfig /flushdns"); return "DNS: " + LastMeaningfulLines(RunSystemToolDirect("ipconfig.exe", "/flushdns"), 1); })))
                    SetStatus("🟢 Caché DNS limpiada", "Listo");
            };
            repBtns.Children.Add(dnsBtn);

            repStack.Children.Add(repBtns);
            repairCard.Child = repStack;
            grid.Children.Add(repairCard);
            root.Children.Add(grid);
            root.Children.Add(BuildChangesCard());

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

        // What Blackwall changed and still can undo exactly, newest first.
        private Border BuildChangesCard()
        {
            Border card = CreateCardContainer();
            card.Margin = new Thickness(0, 0, 0, 14);
            StackPanel stack = new StackPanel();
            DockPanel head = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            Button revertAll = CreateStyledButton(_changes.Count == 0 ? "Revertir todo" : "Revertir todo (" + _changes.Count + ")", false);
            revertAll.IsEnabled = _changes.Count > 0;
            revertAll.Click += async (s, e) => await RunUiActionAsync("tweaks-batch", revertAll, RevertAllChangesAsync);
            DockPanel.SetDock(revertAll, Dock.Right);
            head.Children.Add(revertAll);
            // Covers what older versions applied without a journal too: the problem fixer's main button.
            Button resetAll = CreateStyledButton("Restablecer todo lo de Blackwall", true);
            resetAll.Margin = new Thickness(0, 0, 8, 0);
            resetAll.ToolTip = "Deshace todos los ajustes de Blackwall de este equipo, de cualquier versión, y vuelve a los valores de Windows";
            resetAll.Click += async (s, e) => await RunUiActionAsync("tweaks-batch", resetAll, ResetEverythingAsync);
            DockPanel.SetDock(resetAll, Dock.Right);
            head.Children.Add(resetAll);
            Button repairPowerButton = CreateStyledButton("Reparar plan de energía", false);
            repairPowerButton.Margin = new Thickness(0, 0, 8, 0);
            repairPowerButton.ToolTip = "Crea una copia del plan Equilibrado con el procesador al 100 % y la activa; antes guarda copia de todos tus planes";
            repairPowerButton.Click += async (s, e) => await RunUiActionAsync("tweaks-batch", repairPowerButton, RepairPowerPlanAsync);
            DockPanel.SetDock(repairPowerButton, Dock.Right);
            head.Children.Add(repairPowerButton);
            Button exportState = CreateStyledButton("Exportar estado de los ajustes", false);
            exportState.Margin = new Thickness(0, 0, 8, 0);
            exportState.ToolTip = "Guarda en el Escritorio un informe con cada ajuste y cada valor: ahora, de Blackwall y de Windows";
            exportState.Click += async (s, e) => await RunUiActionAsync("tweaks-report", exportState, ExportTweakStateAsync);
            DockPanel.SetDock(exportState, Dock.Right);
            head.Children.Add(exportState);
            head.Children.Add(new TextBlock { Text = "Cambios hechos por Blackwall (" + _changes.Count + ")", Foreground = TextPrimary, FontWeight = FontWeights.Bold, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
            stack.Children.Add(head);
            stack.Children.Add(new TextBlock { Text = "Antes de cada cambio se guarda el valor que tenía Windows. «Revertir todo» los restaura sin depender del punto de restauración. Cada operación y su comprobación quedan en " + ChangeLogPath + ".", Foreground = TextSecondary, FontSize = 12.3, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
            if (_changes.Count == 0) stack.Children.Add(new TextBlock { Text = "No hay cambios pendientes de revertir.", Foreground = TextSecondary, FontSize = 12.3 });
            foreach (var change in _changes.AsEnumerable().Reverse())
                stack.Children.Add(new TextBlock { Text = GetStr(change, "at") + "  ·  " + GetStr(change, "title") + "  ·  " + ConvertToDictList(change["restoreOps"]).Count + " valores guardados", Foreground = TextPrimary, FontSize = 12.2, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 3) });
            card.Child = stack;
            return card;
        }

        // ====================================================================
        // MOTOR REAL DE AUTO-ACTUALIZACIÓN REMOTA (OTA) EN BINARIO PORTABLE
        // ====================================================================
        private string GetConfiguredOtaManifestUrl()
        {
            return GetStr(_versionInfo, "updateManifestUrl");
        }

        internal void ReportOtaRollback(string stage)
        {
            SetStatus("Actualización no completada; versión anterior conservada.", "Consulta la consola");
            AppendLog("Se inició la versión anterior. Registro de actualización: " + Path.Combine(stage, "result.txt"), "OTA");
        }

        private bool IsNewerVersion(string remoteVersion, string localVersion)
        {
            return OtaProtocol.ParseVersion(remoteVersion) > OtaProtocol.ParseVersion(localVersion);
        }

        private async Task<OtaRelease> FetchSignedReleaseAsync(string url, string stage, System.Threading.CancellationToken cancel, bool retain = false)
        {
            string manifest = Path.Combine(stage, "update.json");
            try
            {
                await OtaDownload.Fetch(url, manifest, 262144, 0, null, cancel);
                return OtaProtocol.Verify(File.ReadAllBytes(manifest), OtaProtocol.PublicKey());
            }
            finally { if (!retain) { try { if (File.Exists(manifest)) File.Delete(manifest); Directory.Delete(stage); } catch { } } }
        }

        private async Task CheckRemoteOtaAsync(bool silentOnUpToDate)
        {
            try
            {
                var release = await FetchSignedReleaseAsync(GetConfiguredOtaManifestUrl(), OtaInstaller.NewStage(), System.Threading.CancellationToken.None);
                if (IsNewerVersion(release.Version, Assembly.GetExecutingAssembly().GetName().Version.ToString(3)))
                {
                    _availableRelease = release;
                    _otaBtn.Content = "Actualizar a v" + release.Version;
                    _otaBtn.ToolTip = "Paquete firmado disponible. Abre Actualizaciones para revisarlo.";
                    AppendLog("Versión firmada disponible: " + release.Version, "OTA");
                    AnnounceAvailableUpdate();
                }
            }
            catch (Exception ex) { if (!silentOnUpToDate) AppendLog(ex.Message, "OTA"); }
        }

        private void AnnounceAvailableUpdate()
        {
            if (UpdateOnly || _availableRelease == null || _announcedRelease == _availableRelease.Version || _otaDialog != null || !IsLoaded || !IsVisible || WindowState == WindowState.Minimized) return;
            _announcedRelease = _availableRelease.Version;
            if (_otaNotice != null) _rootGrid.Children.Remove(_otaNotice);
            var content = new StackPanel();
            content.Children.Add(new TextBlock { Text = "Actualización disponible · " + _availableRelease.Version, FontWeight = FontWeights.SemiBold, Foreground = TextPrimary, TextWrapping = TextWrapping.Wrap });
            content.Children.Add(new TextBlock { Text = "Revisa las novedades cuando quieras.", Foreground = TextSecondary, Margin = new Thickness(0, 4, 0, 8), TextWrapping = TextWrapping.Wrap });
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            var open = CreateStyledButton("Ver novedades", true);
            var later = CreateStyledButton("Más tarde", false); later.Margin = new Thickness(8, 0, 0, 0);
            Action dismiss = () => { if (_otaNotice != null) _rootGrid.Children.Remove(_otaNotice); _otaNotice = null; };
            open.Click += (s, e) => { dismiss(); ShowOtaUpdateDialog(); };
            later.Click += (s, e) => dismiss();
            actions.Children.Add(open); actions.Children.Add(later); content.Children.Add(actions);
            _otaNotice = CreateCardContainer(); _otaNotice.Child = content; _otaNotice.Width = 350; _otaNotice.Margin = new Thickness(16); _otaNotice.HorizontalAlignment = HorizontalAlignment.Right; _otaNotice.VerticalAlignment = VerticalAlignment.Bottom;
            Grid.SetRowSpan(_otaNotice, Math.Max(1, _rootGrid.RowDefinitions.Count)); Grid.SetColumnSpan(_otaNotice, Math.Max(1, _rootGrid.ColumnDefinitions.Count)); Panel.SetZIndex(_otaNotice, int.MaxValue - 1);
            _rootGrid.Children.Add(_otaNotice);
        }

        private void ShowOtaUpdateDialog()
        {
            if (_otaInProgress) { SetStatus("La actualización está en curso", "Consulta el progreso"); return; }
            if (_otaDialog != null) { _otaDialog.Activate(); return; }
            if (_otaNotice != null) { _rootGrid.Children.Remove(_otaNotice); _otaNotice = null; }
            IInputElement previousFocus = Keyboard.FocusedElement;
            Window dialog = CreateOtaUpdateDialog();
            _otaDialog = dialog;
            SetModalBackdrop(true);
            try { dialog.ShowDialog(); }
            finally { _otaDialog = null; SetModalBackdrop(false); if (previousFocus != null) Keyboard.Focus(previousFocus); }
        }

        private Border _modalScrim;

        private Border CreateOtaProgress(Action cancel, out TextBlock message, out ProgressBar bar, out Button action)
        {
            var content = new StackPanel();
            content.Children.Add(new TextBlock { Text = "Actualizando Blackwall", Foreground = TextPrimary, FontWeight = FontWeights.SemiBold, FontSize = 15 });
            message = new TextBlock { Text = "Preparando descarga…", Foreground = TextSecondary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 8) };
            System.Windows.Automation.AutomationProperties.SetLiveSetting(message, System.Windows.Automation.AutomationLiveSetting.Polite);
            content.Children.Add(message);
            bar = new ProgressBar { Height = 5, Maximum = 100, Foreground = CyberCyanBrush, Background = BgElevated, IsIndeterminate = MotionAllowed };
            System.Windows.Automation.AutomationProperties.SetName(bar, "Progreso de descarga de la actualización");
            content.Children.Add(bar);
            action = CreateStyledButton("Cancelar", false); action.HorizontalAlignment = HorizontalAlignment.Right; action.Margin = new Thickness(0, 8, 0, 0);
            action.Click += (s, e) => cancel(); content.Children.Add(action);
            var panel = CreateCardContainer(); panel.Child = content; panel.Width = 350; panel.Margin = new Thickness(16); panel.HorizontalAlignment = HorizontalAlignment.Right; panel.VerticalAlignment = VerticalAlignment.Bottom;
            Grid.SetRowSpan(panel, Math.Max(1, _rootGrid.RowDefinitions.Count)); Grid.SetColumnSpan(panel, Math.Max(1, _rootGrid.ColumnDefinitions.Count)); Panel.SetZIndex(panel, int.MaxValue - 1);
            _rootGrid.Children.Add(panel);
            AnimateEntrance(panel, 4, 180);
            return panel;
        }

        // Same dimming as the former 55 % opacity (content blended toward the app background) without a
        // full-window blur, which re-rendered the whole window every time anything underneath changed.
        private void SetModalBackdrop(bool visible)
        {
            if (visible && _modalScrim == null)
            {
                _modalScrim = new Border { Background = BgApp, Opacity = 0.45, IsHitTestVisible = false };
                Grid.SetRowSpan(_modalScrim, Math.Max(1, _rootGrid.RowDefinitions.Count));
                Grid.SetColumnSpan(_modalScrim, Math.Max(1, _rootGrid.ColumnDefinitions.Count));
                Panel.SetZIndex(_modalScrim, int.MaxValue);
                _rootGrid.Children.Add(_modalScrim);
            }
            else if (!visible && _modalScrim != null)
            {
                _rootGrid.Children.Remove(_modalScrim);
                _modalScrim = null;
            }
        }

        // Topics come from a short "Tema: texto" prefix: the signed manifest is a flat list and old clients reject new fields.
        internal static List<KeyValuePair<string, List<string>>> GroupReleaseNotes(IEnumerable<string> notes)
        {
            var groups = new List<KeyValuePair<string, List<string>>>();
            foreach (string raw in notes)
            {
                string note = (raw ?? "").Trim();
                if (note.Length == 0) continue;
                var match = Regex.Match(note, @"\A(\p{Lu}\p{L}*(?: \p{L}+){0,2}): (.+)\z", RegexOptions.Singleline);
                bool topical = match.Success && match.Groups[1].Length <= 24;
                string topic = topical ? match.Groups[1].Value : "Novedades";
                var group = groups.FirstOrDefault(g => g.Key == topic);
                if (group.Value == null) { group = new KeyValuePair<string, List<string>>(topic, new List<string>()); groups.Add(group); }
                group.Value.Add(topical ? match.Groups[2].Value : note);
            }
            return groups;
        }

        private void FillReleaseSheet(Grid sheet, IEnumerable<string> notes)
        {
            var groups = GroupReleaseNotes(notes);
            if (groups.Count == 1 && groups[0].Value.Count > 3)
            {
                var all = groups[0].Value; int half = (all.Count + 1) / 2;
                groups = new List<KeyValuePair<string, List<string>>> { new KeyValuePair<string, List<string>>(groups[0].Key, all.Take(half).ToList()), new KeyValuePair<string, List<string>>("", all.Skip(half).ToList()) };
            }
            // Keep the topics in order and break where the two columns come out closest in length.
            Func<KeyValuePair<string, List<string>>, int> weight = g => 60 + g.Value.Sum(n => n.Length + 30);
            int total = groups.Sum(weight), left = 0, split = 0, gap = total;
            for (int i = 0; i < groups.Count; i++)
            {
                left += weight(groups[i]);
                if (Math.Abs(total - 2 * left) < gap) { gap = Math.Abs(total - 2 * left); split = i + 1; }
            }
            bool twoColumns = groups.Count > 1 && groups.Sum(g => g.Value.Count) > 3;
            if (!twoColumns) split = groups.Count;
            var columns = new[] { new StackPanel(), new StackPanel() };
            for (int i = 0; i < groups.Count; i++)
            {
                var column = columns[i < split ? 0 : 1];
                if (groups[i].Key.Length > 0)
                    column.Children.Add(new TextBlock { Text = groups[i].Key.ToUpperInvariant(), Foreground = CyberCyanBrush, FontSize = 11, FontWeight = FontWeights.Bold, Margin = new Thickness(0, column.Children.Count == 0 ? 0 : 12, 0, 4) });
                foreach (string note in groups[i].Value)
                {
                    var row = new DockPanel { Margin = new Thickness(0, 2, 0, 3) };
                    var bullet = new TextBlock { Text = "•", Foreground = TextSecondary, FontSize = 12, Width = 12 };
                    DockPanel.SetDock(bullet, Dock.Left); row.Children.Add(bullet);
                    row.Children.Add(new TextBlock { Text = note, Foreground = TextPrimary, FontSize = 12.5, LineHeight = 17, TextWrapping = TextWrapping.Wrap });
                    column.Children.Add(row);
                }
            }
            sheet.Children.Clear(); sheet.ColumnDefinitions.Clear();
            if (!twoColumns) { sheet.Children.Add(columns[0]); return; }
            sheet.ColumnDefinitions.Add(new ColumnDefinition());
            sheet.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            sheet.ColumnDefinitions.Add(new ColumnDefinition());
            columns[0].Margin = new Thickness(0, 0, 14, 0); columns[1].Margin = new Thickness(14, 0, 0, 0);
            var rule = new Border { Width = 1, Background = BorderSubtle };
            Grid.SetColumn(rule, 1); Grid.SetColumn(columns[1], 2);
            sheet.Children.Add(columns[0]); sheet.Children.Add(rule); sheet.Children.Add(columns[1]);
        }

        private Window CreateOtaUpdateDialog()
        {
            string installed = Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
            var dialog = new Window { Title = "Actualizaciones de Blackwall", Width = Math.Min(680, SystemParameters.WorkArea.Width - 32), Height = Math.Min(620, SystemParameters.WorkArea.Height - 40), WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = BgApp, Foreground = TextPrimary, FontFamily = FontFamily, Icon = Icon };
            WindowChrome.SetWindowChrome(dialog, new WindowChrome { CaptionHeight = 0, CornerRadius = new CornerRadius(18), GlassFrameThickness = new Thickness(0), ResizeBorderThickness = new Thickness(0) });
            dialog.Resources = Resources;
            KeyboardNavigation.SetTabNavigation(dialog, KeyboardNavigationMode.Cycle);
            var cancel = new System.Threading.CancellationTokenSource();
            bool busy = false;
            bool handedOff = false;
            string activeStage = null;
            dialog.PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape) { dialog.Close(); e.Handled = true; } };
            dialog.Closed += (s, e) => { cancel.Cancel(); if (!handedOff && activeStage != null) { try { File.WriteAllText(Path.Combine(activeStage, "cancelled"), "1"); } catch { } } };
            var hero = new StackPanel();
            var brand = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
            brand.MouseLeftButtonDown += (s, e) => { if (e.LeftButton == MouseButtonState.Pressed) dialog.DragMove(); };
            var dismiss = CreateWindowControlButton(GlyphClose, "Posponer actualización", () => dialog.Close(), true);
            DockPanel.SetDock(dismiss, Dock.Right); brand.Children.Add(dismiss);
            brand.Children.Add(new Image { Source = Icon, Width = 30, Height = 30, Margin = new Thickness(0, 0, 10, 0) });
            brand.Children.Add(new TextBlock { Text = "BLACKWALL", Foreground = TextPrimary, FontSize = 11, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center });
            brand.Children.Add(new TextBlock { Text = "|", Foreground = BorderSubtle, FontSize = 13, Margin = new Thickness(10, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
            brand.Children.Add(new TextBlock { Text = "Notas de versión", Foreground = TextSecondary, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            hero.Children.Add(brand);
            OtaRelease available = _availableRelease != null && IsNewerVersion(_availableRelease.Version, installed) ? _availableRelease : null;
            var titleRow = new DockPanel();
            var version = new TextBlock { Text = "Instalada: " + installed, Foreground = TextSecondary, FontSize = 12, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(12, 0, 0, 4) };
            DockPanel.SetDock(version, Dock.Right); titleRow.Children.Add(version);
            var heading = new TextBlock { Foreground = TextPrimary, FontSize = 24, FontWeight = FontWeights.SemiBold };
            titleRow.Children.Add(heading);
            hero.Children.Add(titleRow);
            var subtitleRow = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
            var notesToggle = CreateLinkButton(""); notesToggle.FontSize = 12; notesToggle.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(notesToggle, Dock.Right); subtitleRow.Children.Add(notesToggle);
            var subtitle = new TextBlock { Foreground = TextSecondary, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
            subtitleRow.Children.Add(subtitle);
            hero.Children.Add(subtitleRow);
            var heroFrame = new Border { Child = hero, Padding = new Thickness(28, 20, 28, 14), BorderBrush = BorderSubtle, BorderThickness = new Thickness(0, 0, 0, 1) };
            var body = new Grid { Margin = new Thickness(28, 16, 28, 16) };
            string manifestUrl = GetConfiguredOtaManifestUrl();
            var status = new TextBlock { Text = available != null ? "✓  Firma verificada" : "Comprueba si hay una versión nueva.", Foreground = TextSecondary, TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Margin = new Thickness(0, 0, 0, 10) };
            System.Windows.Automation.AutomationProperties.SetLiveSetting(status, System.Windows.Automation.AutomationLiveSetting.Polite);
            var progress = new ProgressBar { Height = 5, Maximum = 100, Foreground = CyberCyanBrush, Background = BgElevated, Margin = new Thickness(0, 0, 0, 10), Visibility = Visibility.Collapsed };
            var installedNotes = _versionInfo != null && _versionInfo.ContainsKey("changelog") ? _versionInfo["changelog"] as System.Collections.IEnumerable : null;
            var installedLines = installedNotes == null ? new List<string>() : installedNotes.Cast<object>().Select(x => Convert.ToString(x)).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            bool showingInstalled = false;
            Action showNotes = () =>
            {
                bool hasNew = available != null && IsNewerVersion(available.Version, installed);
                bool showNew = hasNew && !showingInstalled;
                var lines = showNew ? available.Notes.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).Where(x => !string.IsNullOrWhiteSpace(x)).ToList() : installedLines;
                FillReleaseSheet(body, lines);
                heading.Inlines.Clear();
                heading.Inlines.Add(new System.Windows.Documents.Run("Qué cambia en "));
                heading.Inlines.Add(new System.Windows.Documents.Run(showNew ? available.Version : installed) { Foreground = CyberCyanBrush });
                string count = lines.Count == 1 ? "La nota" : "Las " + lines.Count + " notas";
                subtitle.Text = lines.Count == 0 ? "No hay notas disponibles para esta versión." : showNew ? count + ", en una sola vista." : count + " de la versión instalada.";
                notesToggle.Visibility = hasNew ? Visibility.Visible : Visibility.Collapsed;
                if (hasNew) notesToggle.Content = showingInstalled ? "Ver novedades de la " + available.Version : "Ver notas de la " + installed + " instalada";
            };
            notesToggle.Click += (s, e) => { showingInstalled = !showingInstalled; showNotes(); };
            showNotes();
            var actions = new Grid { Width = 340 }; actions.ColumnDefinitions.Add(new ColumnDefinition()); actions.ColumnDefinitions.Add(new ColumnDefinition());
            var check = CreateStyledButton("Buscar actualizaciones", false); check.Visibility = available == null ? Visibility.Visible : Visibility.Collapsed;
            var install = CreateStyledButton("Actualizar ahora", true); install.IsEnabled = available != null; install.Margin = new Thickness(4, 0, 0, 0); install.HorizontalAlignment = HorizontalAlignment.Stretch;
            var close = CreateStyledButton("Más tarde", false); close.Margin = new Thickness(0, 0, 4, 0); close.HorizontalAlignment = HorizontalAlignment.Stretch;
            close.Click += (s, e) => dialog.Close();
            actions.Children.Add(close); Grid.SetColumn(install, 1); actions.Children.Add(install);
            Func<Task> checkForUpdates = async () =>
            {
                if (busy) return;
                busy = true; check.IsEnabled = false; install.IsEnabled = false; available = null;
                progress.Visibility = Visibility.Visible; progress.IsIndeterminate = MotionAllowed;
                status.Text = "Comprobando firma y versión…";
                try
                {
                    OtaProtocol.Https(manifestUrl);
                    available = await FetchSignedReleaseAsync(manifestUrl, OtaInstaller.NewStage(), cancel.Token);
                    if (UpdateOnly && (available.Version != RequestedUpdateVersion || available.Hash != RequestedUpdateHash)) throw new InvalidDataException("La publicación cambió desde tu confirmación. Cierra este aviso y comprueba de nuevo.");
                    bool newer = IsNewerVersion(available.Version, installed);
                    showingInstalled = false; showNotes();
                    status.Text = newer ? "✓  Firma verificada · versión " + available.Version + " disponible" : "Ya estás al día: no hay una versión posterior a la instalada.";
                    install.IsEnabled = newer;
                }
                catch (Exception ex) { status.Text = cancel.IsCancellationRequested ? "Comprobación cancelada." : "No se pudo comprobar: " + ex.Message; }
                finally { busy = false; check.IsEnabled = true; progress.Visibility = Visibility.Collapsed; }
            };
            check.Click += async (s, e) => await checkForUpdates();
            Func<Task> installUpdate = async () =>
            {
                if (busy || available == null) return;
                busy = true; install.IsEnabled = false; check.IsEnabled = false;
                _otaInProgress = true;
                TextBlock toastStatus; ProgressBar toastProgress; Button toastAction;
                var toast = CreateOtaProgress(() => cancel.Cancel(), out toastStatus, out toastProgress, out toastAction);
                dialog.Hide();
                var enabledStates = _rootGrid.Children.OfType<UIElement>().Where(element => element != toast).ToDictionary(element => element, element => element.IsEnabled);
                foreach (var element in enabledStates.Keys) element.IsEnabled = false;
                var focusToast = Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => toastAction.Focus()));
                bool helperLaunched = false;
                try
                {
                    string stage = OtaInstaller.NewStage();
                    activeStage = stage;
                    toastStatus.Text = "Verificando la actualización…";
                    var release = await FetchSignedReleaseAsync(manifestUrl, stage, cancel.Token, true);
                    if (release.Version != available.Version || release.Hash != available.Hash || release.Size != available.Size || !IsNewerVersion(release.Version, installed)) throw new InvalidDataException("La versión publicada cambió; vuelve a comprobarla.");
                    toastProgress.IsIndeterminate = false;
                    await OtaDownload.Fetch(release.Url, Path.Combine(stage, "Blackwall.exe"), 104857600, release.Size, (bytes, total) => Dispatcher.BeginInvoke(new Action(() => { toastProgress.Value = bytes * 100.0 / total; toastStatus.Text = "Descargando · " + (int)(bytes * 100.0 / total) + " %"; })), cancel.Token);
                    toastStatus.Text = "Verificando el archivo descargado…";
                    OtaProtocol.VerifyBinary(Path.Combine(stage, "Blackwall.exe"), release);
                    while (_pendingTweaks.Count > 0 || _uiActions.Count > 0 || _operations.Any(operation => !operation.Finished))
                    {
                        toastStatus.Text = "Esperando a que terminen las tareas en curso…";
                        cancel.Token.ThrowIfCancellationRequested();
                        await Task.Delay(200);
                    }
                    toastStatus.Text = "Paquete verificado. Preparando reinicio…";
                    cancel.Token.ThrowIfCancellationRequested();
                    if (OtaProtocol.Elevated())
                    {
                        string token = Guid.NewGuid().ToString("N");
                        string arguments = "--ota-prepared " + OtaInstaller.Quote(stage) + " " + OtaInstaller.Quote(release.Version) + " " + OtaInstaller.Quote(release.Hash) + " " + Process.GetCurrentProcess().Id + " " + token;
                        Program.LaunchUnelevated(Process.GetCurrentProcess().MainModule.FileName, arguments);
                        helperLaunched = true;
                        string ready = Path.Combine(stage, "prepared-ready"), error = Path.Combine(stage, "prepared-error");
                        for (int attempt = 0; attempt < 150; attempt++)
                        {
                            cancel.Token.ThrowIfCancellationRequested();
                            if (File.Exists(error)) throw new IOException("El instalador no pudo prepararse: " + File.ReadAllText(error));
                            if (File.Exists(ready) && File.ReadAllText(ready) == token) break;
                            if (attempt == 149) throw new TimeoutException("El instalador no respondió; Blackwall permanece abierto.");
                            await Task.Delay(100);
                        }
                    }
                    else await OtaInstaller.Start(stage);
                    cancel.Token.ThrowIfCancellationRequested();
                    toastAction.IsEnabled = false;
                    handedOff = true;
                    Application.Current.Shutdown();
                }
                catch (Exception ex)
                {
                    bool wasCancelled = cancel.IsCancellationRequested;
                    if (activeStage != null)
                    {
                        if (helperLaunched) { try { File.WriteAllText(Path.Combine(activeStage, "cancelled"), "1"); } catch { } }
                        else { try { OtaInstaller.CleanStage(activeStage); activeStage = null; } catch { } }
                    }
                    toastProgress.Visibility = Visibility.Collapsed;
                    toastStatus.Text = wasCancelled ? "Descarga cancelada. La aplicación no se modificó." : "No se pudo actualizar: " + ex.Message;
                    toastAction.Content = wasCancelled ? "Cerrar" : "Reintentar";
                    toastAction.Click += (s, e) => { _rootGrid.Children.Remove(toast); if (!wasCancelled) ShowOtaUpdateDialog(); };
                    dialog.Close();
                }
                finally
                {
                    if (!handedOff) foreach (var entry in enabledStates) entry.Key.IsEnabled = entry.Value;
                    busy = false; _otaInProgress = false; check.IsEnabled = true; install.IsEnabled = available != null && IsNewerVersion(available.Version, installed);
                }
            };
            install.Click += async (s, e) => await installUpdate();
            dialog.Loaded += async (s, e) =>
            {
                AnimateEntrance(body, 4, 180);
                if (UpdateOnly)
                {
                    await checkForUpdates();
                    if (dialog.IsVisible && !cancel.IsCancellationRequested && install.IsEnabled) await installUpdate();
                }
                else if (install.IsEnabled) install.Focus(); else check.Focus();
            };
            var layout = new Grid(); layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); layout.RowDefinitions.Add(new RowDefinition()); layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.Children.Add(heroFrame);
            var notesScroll = new SmoothScrollViewer { ReduceMotion = !MotionAllowed, Background = BgApp, Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            Grid.SetRow(notesScroll, 1); layout.Children.Add(notesScroll);
            var buttonRow = new DockPanel { LastChildFill = false };
            DockPanel.SetDock(actions, Dock.Right); buttonRow.Children.Add(actions);
            DockPanel.SetDock(check, Dock.Left); buttonRow.Children.Add(check);
            var footerStack = new StackPanel();
            footerStack.Children.Add(status); footerStack.Children.Add(progress); footerStack.Children.Add(buttonRow);
            var footer = new Border { Background = BgSidebar, BorderBrush = BorderSubtle, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(28, 12, 28, 14), Child = footerStack };
            Grid.SetRow(footer, 2); layout.Children.Add(footer);
            var frame = new Border { Background = BgApp, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(18), Child = layout };
            frame.SetResourceReference(Border.BorderBrushProperty, "MaterialEdge"); dialog.Content = frame;
            return dialog;
        }


        // ====================================================================
        // INFORME DEL EQUIPO: un modelo común (secciones, campos, tablas) y un renderizador por formato
        // ====================================================================
        private class ReportTable
        {
            public string Title;
            public string[] Columns;
            public List<string[]> Rows = new List<string[]>();
        }

        private class ReportSection
        {
            public string Id, Title, Summary;
            public List<KeyValuePair<string, string>> Fields = new List<KeyValuePair<string, string>>();
            public List<ReportTable> Tables = new List<ReportTable>();
            public double? Meter; public string MeterLabel; // optional usage/health bar for the HTML report
            public void Add(string label, string value) { if (!string.IsNullOrWhiteSpace(value)) Fields.Add(new KeyValuePair<string, string>(label, value)); }
        }

        private class HardwareReport
        {
            public string Title, Device, GeneratedAt, ScannedAt, AppVersion;
            public bool Anonymized;
            public List<ReportSection> Sections = new List<ReportSection>();
        }

        private static readonly string[][] ReportFormats =
        {
            new[] { "html", "Informe visual (HTML)", "Índice, tarjetas, tablas y barras. Se abre en el navegador y se imprime o guarda como PDF.", "\uE774" },
            new[] { "md", "Markdown", "Títulos y tablas para pegar en Discord, GitHub o foros.", "\uE8A5" },
            new[] { "txt", "Texto plano", "Columnas alineadas para soporte técnico o el bloc de notas.", "\uE8C4" },
            new[] { "xlsx", "Excel (.xlsx)", "Una hoja por sección, cabeceras con formato y columnas ajustadas.", "\uE80A" },
            new[] { "csv", "CSV para otras herramientas", "Texto separado por punto y coma para importar en bases de datos o scripts.", "\uE9F9" },
            new[] { "json", "Datos estructurados (JSON)", "Secciones con campos y tablas, indentado, para herramientas.", "\uE943" }
        };

        private string MaskSerial(string value, bool anonymize)
        {
            if (!anonymize || string.IsNullOrWhiteSpace(value)) return value;
            string clean = value.Trim();
            return clean.Length <= 4 ? "••••" : "••••" + clean.Substring(clean.Length - 4);
        }

        private string Hidden(string value, bool anonymize) { return anonymize && !string.IsNullOrWhiteSpace(value) ? "Oculto" : value; }

        private static string Unit(string value, string unit) { return string.IsNullOrWhiteSpace(value) ? "" : value + " " + unit; }

        internal static string FormatCpuCacheMb(string kilobytes)
        {
            long value;
            return long.TryParse(kilobytes, out value) && value > 0
                ? (value / 1024m).ToString("0.##", System.Globalization.CultureInfo.GetCultureInfo("es-ES")) + " MB" : "";
        }

        private HardwareReport BuildHardwareReport(bool anonymize)
        {
            var chassis = GetDict(_hardware, "chassis"); var mb = GetDict(_hardware, "motherboard"); var cpu = GetDict(_hardware, "cpu");
            var mem = GetDict(_hardware, "memory"); var bat = GetDict(_hardware, "battery"); var os = GetDict(_hardware, "osNetwork");
            var storage = ConvertToDictList(_hardware.ContainsKey("storage") ? _hardware["storage"] : null);
            var gpus = ConvertToDictList(_hardware.ContainsKey("gpus") ? _hardware["gpus"] : null);
            string device = (GetStr(chassis, "manufacturer") + " " + GetStr(chassis, "model")).Trim();
            var report = new HardwareReport
            {
                Title = "Informe del equipo", Device = device, Anonymized = anonymize,
                GeneratedAt = DateTime.Now.ToString("dd/MM/yyyy HH:mm"), ScannedAt = GetStr(_hardware, "scannedAt"),
                AppVersion = GetStr(_versionInfo, "appVersion")
            };

            double totalDisk = storage.Sum(d => GetDouble(d, "sizeGb")), freeDisk = storage.Sum(d => GetDouble(d, "freeGb"));
            var summary = new ReportSection { Id = "resumen", Title = "Resumen", Summary = "Lo esencial del equipo en una vista." };
            summary.Add("Equipo", device + " · " + GetStr(chassis, "deviceType"));
            summary.Add("Procesador", GetStr(cpu, "name") + " · " + GetStr(cpu, "physicalCores") + " núcleos, " + GetStr(cpu, "logicalThreads") + " hilos");
            summary.Add("Memoria", Unit(GetStr(mem, "totalGb"), "GB") + " " + GetStr(mem, "memoryType") + " a " + Unit(GetStr(mem, "speedMhz"), "MHz"));
            if (storage.Count > 0) summary.Add("Almacenamiento", storage.Count + (storage.Count == 1 ? " unidad · " : " unidades · ") + totalDisk.ToString("0.#") + " GB, " + freeDisk.ToString("0.#") + " GB libres");
            if (gpus.Count > 0) summary.Add("Gráficos", string.Join(" · ", gpus.Select(g => GetStr(g, "name"))));
            if (GetBool(bat, "present")) summary.Add("Batería", "Salud " + GetStr(bat, "healthPercent") + " % · desgaste " + GetStr(bat, "wearLevelPercent") + " %");
            summary.Add("Windows", GetStr(os, "caption") + " " + GetStr(os, "displayVersion") + " (compilación " + GetStr(os, "buildNumber") + ")");
            report.Sections.Add(summary);

            var system = new ReportSection { Id = "equipo", Title = "Equipo y firmware", Summary = "Identidad del equipo, placa base y arranque." };
            system.Add("Fabricante", GetStr(chassis, "manufacturer")); system.Add("Modelo", GetStr(chassis, "model"));
            system.Add("Tipo de chasis", GetStr(chassis, "deviceType")); system.Add("SKU", GetStr(chassis, "sku"));
            system.Add("Número de serie", MaskSerial(GetStr(chassis, "serialNumber"), anonymize)); system.Add("UUID SMBIOS", Hidden(GetStr(chassis, "uuid"), anonymize));
            system.Add("Placa base", (GetStr(mb, "manufacturer") + " " + GetStr(mb, "product")).Trim()); system.Add("Serie de la placa", MaskSerial(GetStr(mb, "serialNumber"), anonymize));
            system.Add("BIOS", (GetStr(mb, "biosVendor") + " " + GetStr(mb, "biosVersion")).Trim() + (GetStr(mb, "biosDate").Length > 0 ? " · " + GetStr(mb, "biosDate") : ""));
            system.Add("Modo de arranque", GetStr(mb, "bootMode")); system.Add("Secure Boot", GetStr(mb, "secureBoot")); system.Add("Certificados Secure Boot 2023", GetStr(mb, "secureBootCerts")); system.Add("TPM", GetStr(mb, "tpmVersion"));
            report.Sections.Add(system);

            var processor = new ReportSection { Id = "procesador", Title = "Procesador", Summary = GetStr(cpu, "name") };
            processor.Add("Modelo", GetStr(cpu, "name")); processor.Add("Arquitectura", GetStr(cpu, "architecture")); processor.Add("Socket", GetStr(cpu, "socket"));
            processor.Add("Núcleos e hilos", GetStr(cpu, "physicalCores") + " núcleos, " + GetStr(cpu, "logicalThreads") + " hilos"); // "6 / 12" becomes a date in Excel
            processor.Add("Frecuencia base / actual", Unit(GetStr(cpu, "baseClockMhz"), "MHz") + " / " + Unit(GetStr(cpu, "currentClockMhz"), "MHz"));
            processor.Add("Caché L2 / L3", FormatCpuCacheMb(GetStr(cpu, "l2CacheKb")) + " / " + FormatCpuCacheMb(GetStr(cpu, "l3CacheKb")));
            processor.Add("Virtualización", GetBool(cpu, "virtualizationEnabled") ? "Activada" : "Desactivada");
            processor.Add("Carga al escanear", Unit(GetStr(cpu, "loadPercent"), "%"));
            report.Sections.Add(processor);

            var memory = new ReportSection { Id = "memoria", Title = "Memoria", Summary = Unit(GetStr(mem, "totalGb"), "GB") + " " + GetStr(mem, "memoryType"), Meter = GetDouble(mem, "usagePercent"), MeterLabel = "En uso" };
            memory.Add("Total", Unit(GetStr(mem, "totalGb"), "GB")); memory.Add("En uso / disponible", Unit(GetStr(mem, "usedGb"), "GB") + " / " + Unit(GetStr(mem, "availableGb"), "GB"));
            memory.Add("Uso", Unit(GetStr(mem, "usagePercent"), "%")); memory.Add("Tipo y velocidad", GetStr(mem, "memoryType") + " · " + Unit(GetStr(mem, "speedMhz"), "MHz"));
            memory.Add("Canales", GetStr(mem, "channelMode")); memory.Add("Ranuras ocupadas", GetStr(mem, "occupiedSlots") + " de " + GetStr(mem, "totalSlots"));
            var slots = new ReportTable { Title = "Ranuras de memoria", Columns = new[] { "Ranura", "Banco", "Estado", "Capacidad", "Velocidad", "Fabricante", "Nº de parte", "Serie", "Voltaje" } };
            foreach (var s in ConvertToDictList(mem.ContainsKey("slots") ? mem["slots"] : null))
            {
                bool used = GetBool(s, "occupied");
                slots.Rows.Add(new[] { GetStr(s, "slotName"), GetStr(s, "bankLabel"), used ? "Ocupada" : "Libre", used ? Unit(GetStr(s, "capacityGb"), "GB") : "—", used ? Unit(GetStr(s, "speedMhz"), "MHz") : "—",
                    used ? GetStr(s, "manufacturer") : "—", used ? GetStr(s, "partNumber").Trim() : "—", used ? MaskSerial(GetStr(s, "serialNumber"), anonymize) : "—", used ? Unit(GetStr(s, "voltageMv"), "mV") : "—" });
            }
            if (slots.Rows.Count > 0) memory.Tables.Add(slots);
            report.Sections.Add(memory);

            var disks = new ReportSection { Id = "almacenamiento", Title = "Almacenamiento", Summary = totalDisk.ToString("0.#") + " GB en total", Meter = totalDisk > 0 ? (totalDisk - freeDisk) * 100 / totalDisk : (double?)null, MeterLabel = "Ocupado" };
            var diskTable = new ReportTable { Title = "Unidades", Columns = new[] { "Unidad", "Tipo", "Bus", "Partición", "Capacidad", "Libre", "Uso", "Salud", "Serie" } };
            foreach (var d in storage)
            {
                double size = GetDouble(d, "sizeGb"), free = GetDouble(d, "freeGb");
                diskTable.Rows.Add(new[] { GetStr(d, "friendlyName"), GetStr(d, "mediaType"), GetStr(d, "busType"), GetStr(d, "partitionStyle"), Unit(GetStr(d, "sizeGb"), "GB"), Unit(GetStr(d, "freeGb"), "GB"),
                    size > 0 ? ((size - free) * 100 / size).ToString("0") + " %" : "—", GetStr(d, "healthStatus"), MaskSerial(GetStr(d, "serialNumber"), anonymize) });
            }
            if (diskTable.Rows.Count > 0) disks.Tables.Add(diskTable);
            report.Sections.Add(disks);

            var graphics = new ReportSection { Id = "graficos", Title = "Gráficos", Summary = gpus.Count == 1 ? GetStr(gpus[0], "name") : gpus.Count + " adaptadores" };
            var gpuTable = new ReportTable { Title = "Adaptadores gráficos", Columns = new[] { "GPU", "Fabricante", "Tipo", "VRAM", "Driver", "Proveedor del driver", "Fecha del driver", "Resolución", "Frecuencia" } };
            Func<object, string, string> measured = (value, unit) => value is double || value is int ? Convert.ToString(value, System.Globalization.CultureInfo.CurrentCulture) + " " + unit : Convert.ToString(value);
            foreach (var g in gpus) gpuTable.Rows.Add(new[] { GetStr(g, "name"), GetStr(g, "vendor"), GetStr(g, "kindLabel"), measured(g.ContainsKey("vramGb") ? g["vramGb"] : "", "GB"), GetStr(g, "driverVersion") + (GetStr(g, "nvidiaDriver").Length > 0 ? " (NVIDIA " + GetStr(g, "nvidiaDriver") + ")" : ""), GetStr(g, "driverProvider"), GetStr(g, "driverDate"), GetStr(g, "resolution"), measured(g.ContainsKey("refreshRateHz") ? g["refreshRateHz"] : "", "Hz") });
            if (gpuTable.Rows.Count > 0) graphics.Tables.Add(gpuTable);
            report.Sections.Add(graphics);

            if (GetBool(bat, "present"))
            {
                var battery = new ReportSection { Id = "bateria", Title = "Batería", Summary = "Salud " + GetStr(bat, "healthPercent") + " %", Meter = GetDouble(bat, "healthPercent"), MeterLabel = "Salud" };
                battery.Add("Estado", GetStr(bat, "status")); battery.Add("Carga", Unit(GetStr(bat, "chargePercent"), "%"));
                battery.Add("Capacidad de diseño", Unit(GetStr(bat, "designedCapacityMwh"), "mWh")); battery.Add("Capacidad actual", Unit(GetStr(bat, "fullChargedCapacityMwh"), "mWh"));
                battery.Add("Salud / desgaste", Unit(GetStr(bat, "healthPercent"), "%") + " / " + Unit(GetStr(bat, "wearLevelPercent"), "%"));
                battery.Add("Química", GetStr(bat, "chemistry")); battery.Add("Consumo al escanear", Unit(GetStr(bat, "dischargeRateMw"), "mW"));
                report.Sections.Add(battery);
            }

            var windows = new ReportSection { Id = "windows", Title = "Windows y red", Summary = GetStr(os, "caption") };
            windows.Add("Edición", GetStr(os, "caption")); windows.Add("Versión / compilación", GetStr(os, "displayVersion") + " · " + GetStr(os, "buildNumber"));
            windows.Add("Instalado", GetStr(os, "installDate")); windows.Add("Tiempo encendido", GetStr(os, "uptime"));
            windows.Add("Clave parcial", Hidden(GetStr(os, "partialProductKey"), anonymize));
            var adapters = new ReportTable { Title = "Adaptadores de red activos", Columns = new[] { "Nombre", "Descripción", "MAC", "IPv4", "Velocidad", "Estado" } };
            foreach (var n in ConvertToDictList(os.ContainsKey("adapters") ? os["adapters"] : null))
                adapters.Rows.Add(new[] { GetStr(n, "name"), GetStr(n, "description"), Hidden(GetStr(n, "macAddress"), anonymize), Hidden(GetStr(n, "ipv4"), anonymize), GetStr(n, "linkSpeed"), GetStr(n, "status") });
            if (adapters.Rows.Count > 0) windows.Tables.Add(adapters);
            report.Sections.Add(windows);

            var blackwall = new ReportSection { Id = "blackwall", Title = "Estado de Blackwall", Summary = "Optimizaciones detectadas y recuperación." };
            blackwall.Add("Versión de Blackwall", report.AppVersion);
            if (_loadedModules.Contains("tweaks"))
            {
                var active = _tweaks.Where(t => _tweakStates.ContainsKey(GetStr(t, "id")) && _tweakStates[GetStr(t, "id")]).ToList();
                blackwall.Add("Optimizaciones activas", active.Count + " de " + _tweaks.Count);
                var table = new ReportTable { Title = "Optimizaciones detectadas como activas", Columns = new[] { "Optimización", "Categoría", "Riesgo" } };
                foreach (var t in active) table.Rows.Add(new[] { GetStr(t, "shortTitle"), GetStr(t, "subcategory"), GetStr(t, "riskLevel") == "high" ? "Alto" : GetStr(t, "riskLevel") == "medium" ? "Precaución" : "Bajo" });
                if (table.Rows.Count > 0) blackwall.Tables.Add(table);
            }
            else blackwall.Add("Optimizaciones activas", "Sin consultar (abre WinUtil o Ceito para revisarlas)");
            blackwall.Add("Puntos de restauración", _loadedModules.Contains("safety") ? _restorePoints.Count.ToString() : "Sin consultar");
            blackwall.Add("Punto de restauración verificado", RestorePointSummary());
            blackwall.Add("Cambios que se pueden revertir", _changes.Count.ToString());
            report.Sections.Add(blackwall);
            return report;
        }

        private double GetDouble(Dictionary<string, object> d, string key)
        {
            double value;
            return d != null && d.ContainsKey(key) && d[key] != null && double.TryParse(Convert.ToString(d[key]), out value) ? value : 0;
        }

        // Escapes only markup characters; accents stay readable in the UTF-8 file (WebUtility turns them into &#225;).
        private static string Html(string value)
        {
            return (value ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&#39;");
        }

        private string RenderReportHtml(HardwareReport r)
        {
            var b = new StringBuilder();
            b.Append("<!doctype html><html lang=\"es\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
            b.Append("<title>").Append(Html(r.Title + " · " + r.Device)).Append("</title><style>");
            b.Append(":root{--bg:#0d1016;--surface:#161b23;--raised:#1d232d;--line:#2a313c;--text:#e8ebf0;--muted:#9aa3b1;--accent:#ff6b67;--ok:#5fd3a3;--warn:#f0b35a;}");
            b.Append("@media (prefers-color-scheme:light){:root{--bg:#d1d6de;--surface:#e3e6eb;--raised:#dadfe6;--line:#bcc6d3;--text:#24303e;--muted:#465464;--accent:#873a36;--ok:#1f7a50;--warn:#8a5a10;}}");
            b.Append("*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:14px/1.55 'Segoe UI Variable','Segoe UI',system-ui,sans-serif}");
            b.Append(".wrap{max-width:1080px;margin:0 auto;padding:40px 24px 64px}header{border-bottom:1px solid var(--line);padding-bottom:24px;margin-bottom:28px}");
            b.Append(".brand{color:var(--accent);font-weight:700;letter-spacing:.12em;font-size:12px;text-transform:uppercase}h1{font-size:30px;margin:6px 0 4px;font-weight:600}");
            b.Append(".meta{color:var(--muted);font-size:13px}.privacy{display:inline-block;margin-top:12px;padding:4px 10px;border-radius:999px;background:var(--raised);border:1px solid var(--line);font-size:12px;color:var(--muted)}");
            b.Append("nav{display:flex;flex-wrap:wrap;gap:8px;margin:0 0 28px}nav a{color:var(--text);text-decoration:none;font-size:13px;padding:6px 12px;border:1px solid var(--line);border-radius:999px;background:var(--surface)}nav a:hover{border-color:var(--accent)}");
            b.Append(".tiles{display:grid;grid-template-columns:repeat(auto-fit,minmax(220px,1fr));gap:12px;margin-bottom:28px}.tile{background:var(--surface);border:1px solid var(--line);border-radius:14px;padding:14px 16px}");
            b.Append(".tile .k{color:var(--muted);font-size:12px}.tile .v{font-size:14.5px;font-weight:600;margin-top:4px}");
            b.Append("section{background:var(--surface);border:1px solid var(--line);border-radius:16px;padding:22px 24px;margin-bottom:18px;break-inside:avoid}");
            b.Append("section h2{font-size:18px;margin:0;font-weight:600}section .sub{color:var(--muted);font-size:13px;margin:2px 0 16px}");
            b.Append("dl{display:grid;grid-template-columns:minmax(160px,240px) 1fr;gap:8px 18px;margin:0}dt{color:var(--muted)}dd{margin:0;font-weight:500;word-break:break-word}");
            b.Append(".meter{margin:4px 0 18px}.meter .bar{height:8px;border-radius:999px;background:var(--raised);overflow:hidden;border:1px solid var(--line)}.meter .fill{height:100%;background:var(--accent)}.meter .lbl{font-size:12px;color:var(--muted);margin-bottom:6px}");
            b.Append("h3{font-size:14px;margin:22px 0 10px;font-weight:600}.table{overflow-x:auto;border:1px solid var(--line);border-radius:12px}table{border-collapse:collapse;width:100%;font-size:13px}");
            b.Append("th{text-align:left;font-weight:600;color:var(--muted);background:var(--raised);padding:9px 12px;white-space:nowrap}td{padding:9px 12px;border-top:1px solid var(--line);white-space:nowrap}");
            b.Append(".kicker{font-size:13px;color:var(--muted);font-weight:600;text-transform:uppercase;letter-spacing:.08em;margin:0 0 10px}");
            b.Append("footer{color:var(--muted);font-size:12px;margin-top:28px;text-align:center}");
            b.Append("@media print{:root{--bg:#fff;--surface:#fff;--raised:#f2f3f5;--line:#cfd4da;--text:#111;--muted:#555;--accent:#a0302c}nav{display:none}.wrap{padding:0}section{border-radius:0}}");
            b.Append("</style></head><body><div class=\"wrap\"><header><div class=\"brand\">Blackwall · Informe del equipo</div>");
            b.Append("<h1>").Append(Html(r.Device)).Append("</h1><div class=\"meta\">Generado el ").Append(Html(r.GeneratedAt)).Append(" · escaneo ").Append(Html(r.ScannedAt)).Append(" · Blackwall ").Append(Html(r.AppVersion)).Append("</div>");
            b.Append("<div class=\"privacy\">").Append(r.Anonymized ? "Datos identificativos ocultos (series, UUID, MAC, IP y clave)" : "Incluye números de serie, MAC e IP: compártelo solo con quien confíes").Append("</div></header><nav>");
            foreach (var s in r.Sections.Skip(1)) b.Append("<a href=\"#").Append(s.Id).Append("\">").Append(Html(s.Title)).Append("</a>");
            b.Append("</nav><h2 class=\"kicker\">").Append(Html(r.Sections[0].Title)).Append("</h2><div class=\"tiles\">");
            foreach (var f in r.Sections[0].Fields) b.Append("<div class=\"tile\"><div class=\"k\">").Append(Html(f.Key)).Append("</div><div class=\"v\">").Append(Html(f.Value)).Append("</div></div>");
            b.Append("</div>");
            foreach (var s in r.Sections.Skip(1))
            {
                b.Append("<section id=\"").Append(s.Id).Append("\"><h2>").Append(Html(s.Title)).Append("</h2><div class=\"sub\">").Append(Html(s.Summary)).Append("</div>");
                if (s.Meter.HasValue)
                {
                    double pct = Math.Max(0, Math.Min(100, s.Meter.Value));
                    b.Append("<div class=\"meter\"><div class=\"lbl\">").Append(Html(s.MeterLabel)).Append(": ").Append(pct.ToString("0")).Append(" %</div><div class=\"bar\"><div class=\"fill\" style=\"width:")
                        .Append(pct.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)).Append("%\"></div></div></div>");
                }
                if (s.Fields.Count > 0) { b.Append("<dl>"); foreach (var f in s.Fields) b.Append("<dt>").Append(Html(f.Key)).Append("</dt><dd>").Append(Html(f.Value)).Append("</dd>"); b.Append("</dl>"); }
                foreach (var t in s.Tables)
                {
                    b.Append("<h3>").Append(Html(t.Title)).Append("</h3><div class=\"table\"><table><thead><tr>");
                    foreach (var c in t.Columns) b.Append("<th>").Append(Html(c)).Append("</th>");
                    b.Append("</tr></thead><tbody>");
                    foreach (var row in t.Rows) { b.Append("<tr>"); foreach (var cell in row) b.Append("<td>").Append(Html(cell)).Append("</td>"); b.Append("</tr>"); }
                    b.Append("</tbody></table></div>");
                }
                b.Append("</section>");
            }
            b.Append("<footer>Generado con Blackwall · Captura puntual del equipo, sin telemetría.</footer></div></body></html>");
            return b.ToString();
        }

        private static string MdCell(string value) { return (value ?? "").Replace("|", "\\|").Replace("\r", " ").Replace("\n", " "); }

        private string RenderReportMarkdown(HardwareReport r)
        {
            var b = new StringBuilder();
            b.AppendLine("# " + r.Title + ": " + r.Device).AppendLine();
            b.AppendLine("> Generado el " + r.GeneratedAt + " · escaneo " + r.ScannedAt + " · Blackwall " + r.AppVersion + "  ");
            b.AppendLine("> " + (r.Anonymized ? "Datos identificativos ocultos." : "Incluye números de serie, MAC e IP.")).AppendLine();
            b.AppendLine("**Contenido:** " + string.Join(" · ", r.Sections.Select(s => s.Title))).AppendLine();
            foreach (var s in r.Sections)
            {
                b.AppendLine("## " + s.Title).AppendLine();
                if (!string.IsNullOrEmpty(s.Summary)) b.AppendLine("_" + s.Summary + "_").AppendLine();
                if (s.Fields.Count > 0)
                {
                    b.AppendLine("| Propiedad | Valor |").AppendLine("| --- | --- |");
                    foreach (var f in s.Fields) b.AppendLine("| " + MdCell(f.Key) + " | " + MdCell(f.Value) + " |");
                    b.AppendLine();
                }
                foreach (var t in s.Tables)
                {
                    b.AppendLine("### " + t.Title).AppendLine();
                    b.AppendLine("| " + string.Join(" | ", t.Columns.Select(MdCell)) + " |");
                    b.AppendLine("|" + string.Concat(t.Columns.Select(c => " --- |")));
                    foreach (var row in t.Rows) b.AppendLine("| " + string.Join(" | ", row.Select(MdCell)) + " |");
                    b.AppendLine();
                }
            }
            return b.ToString();
        }

        private string RenderReportText(HardwareReport r)
        {
            var b = new StringBuilder();
            string rule = new string('=', 78);
            b.AppendLine(rule).AppendLine("  BLACKWALL · " + r.Title.ToUpperInvariant()).AppendLine("  " + r.Device).AppendLine(rule);
            b.AppendLine("  Generado: " + r.GeneratedAt + "   Escaneo: " + r.ScannedAt + "   Versión: " + r.AppVersion);
            b.AppendLine("  " + (r.Anonymized ? "Datos identificativos ocultos." : "Incluye números de serie, MAC e IP.")).AppendLine();
            int n = 1;
            foreach (var s in r.Sections)
            {
                string head = n++ + ". " + s.Title.ToUpperInvariant();
                b.AppendLine(head).AppendLine(new string('-', Math.Max(head.Length, 30)));
                int width = s.Fields.Count == 0 ? 0 : s.Fields.Max(f => f.Key.Length) + 2;
                foreach (var f in s.Fields) b.AppendLine("  " + (f.Key + ":").PadRight(width + 1) + f.Value);
                foreach (var t in s.Tables)
                {
                    b.AppendLine().AppendLine("  " + t.Title);
                    var widths = t.Columns.Select((c, i) => Math.Min(34, Math.Max(c.Length, t.Rows.Count == 0 ? 0 : t.Rows.Max(row => (row[i] ?? "").Length)))).ToArray();
                    Func<string[], string> line = cells => "  " + string.Join("  ", cells.Select((c, i) => { c = c ?? ""; if (c.Length > widths[i]) c = c.Substring(0, widths[i] - 1) + "…"; return c.PadRight(widths[i]); })).TrimEnd();
                    b.AppendLine(line(t.Columns)).AppendLine("  " + string.Join("  ", widths.Select(w => new string('-', w))));
                    foreach (var row in t.Rows) b.AppendLine(line(row));
                }
                b.AppendLine();
            }
            b.AppendLine(rule).AppendLine("  Generado con Blackwall · captura puntual, sin telemetría.");
            return b.ToString();
        }

        private static string CsvCell(string value)
        {
            value = value ?? "";
            return value.IndexOfAny(new[] { ';', '"', '\n', '\r' }) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
        }

        // Semicolon-separated, UTF-8 with BOM: opens with accents and columns intact in Spanish-locale Excel.
        private string RenderReportCsv(HardwareReport r)
        {
            var b = new StringBuilder();
            b.AppendLine("Sección;Elemento;Propiedad;Valor");
            foreach (var s in r.Sections)
            {
                foreach (var f in s.Fields) b.AppendLine(string.Join(";", new[] { s.Title, "", f.Key, f.Value }.Select(CsvCell)));
                foreach (var t in s.Tables)
                    foreach (var row in t.Rows)
                        for (int i = 1; i < t.Columns.Length; i++) b.AppendLine(string.Join(";", new[] { s.Title, t.Title + ": " + row[0], t.Columns[i], row[i] }.Select(CsvCell)));
            }
            return b.ToString();
        }

        private string RenderReportJson(HardwareReport r)
        {
            var root = new Dictionary<string, object>
            {
                { "informe", new Dictionary<string, object> { { "titulo", r.Title }, { "equipo", r.Device }, { "generado", r.GeneratedAt }, { "escaneo", r.ScannedAt }, { "versionBlackwall", r.AppVersion }, { "datosOcultos", r.Anonymized } } },
                { "secciones", r.Sections.Select(s => new Dictionary<string, object>
                    {
                        { "id", s.Id }, { "titulo", s.Title }, { "resumen", s.Summary },
                        { "campos", s.Fields.ToDictionary(f => f.Key, f => (object)f.Value) },
                        { "tablas", s.Tables.Select(t => new Dictionary<string, object> { { "titulo", t.Title }, { "filas", t.Rows.Select(row => t.Columns.Select((c, i) => new KeyValuePair<string, string>(c, row[i])).ToDictionary(p => p.Key, p => (object)p.Value)).ToList() } }).ToList() }
                    }).ToList() }
            };
            if (!r.Anonymized) root["datosOriginales"] = _hardware;
            return PrettyJson(_json.Serialize(root));
        }

        // JavaScriptSerializer writes one line; indent it (string-aware) so the file is readable.
        private static string PrettyJson(string json)
        {
            var b = new StringBuilder(); int indent = 0; bool quoted = false;
            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '"' && (i == 0 || json[i - 1] != '\\' || (i > 1 && json[i - 2] == '\\'))) quoted = !quoted;
                if (quoted) { b.Append(c); continue; }
                if (c == '{' || c == '[') { b.Append(c); if (i + 1 < json.Length && (json[i + 1] == '}' || json[i + 1] == ']')) continue; b.AppendLine().Append(' ', ++indent * 2); }
                else if (c == '}' || c == ']') { if (json[i - 1] != '{' && json[i - 1] != '[') b.AppendLine().Append(' ', --indent * 2); b.Append(c); }
                else if (c == ',') b.Append(c).AppendLine().Append(' ', indent * 2);
                else if (c == ':') b.Append(": ");
                else b.Append(c);
            }
            return b.ToString();
        }

        // Real Excel workbook built with System.IO.Packaging (WindowsBase, already referenced): one sheet per section,
        // styled headers, fitted column widths and every value stored as text so Excel never turns "6 / 12" into a date.
        private const string SheetNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private const string RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

        private static string Xml(string value)
        {
            var b = new StringBuilder();
            foreach (char c in value ?? "")
            {
                if (c == '&') b.Append("&amp;"); else if (c == '<') b.Append("&lt;"); else if (c == '>') b.Append("&gt;"); else if (c == '"') b.Append("&quot;");
                else if (c < 0x20 && c != '\t' && c != '\n' && c != '\r') continue; // invalid in XML
                else b.Append(c);
            }
            return b.ToString();
        }

        private static string ColumnName(int index) { string name = ""; for (index++; index > 0; index = (index - 1) / 26) name = (char)('A' + (index - 1) % 26) + name; return name; }

        private static string SheetName(string title, HashSet<string> used)
        {
            string name = Regex.Replace(title ?? "Hoja", @"[\[\]\*\?/\\:]", " ").Trim();
            if (name.Length > 31) name = name.Substring(0, 31);
            string unique = name; for (int i = 2; !used.Add(unique); i++) unique = name.Substring(0, Math.Min(name.Length, 28)) + " " + i;
            return unique;
        }

        // Styles: 0 normal · 1 title · 2 header · 3 muted note · 4 bold label · 5 wrapped value
        private const string XlsxStyles = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><styleSheet xmlns=\"" + SheetNs + "\">"
            + "<fonts count=\"5\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"16\"/><color rgb=\"FF8A2B2B\"/><name val=\"Calibri\"/></font>"
            + "<font><b/><sz val=\"11\"/><color rgb=\"FFFFFFFF\"/><name val=\"Calibri\"/></font><font><i/><sz val=\"10\"/><color rgb=\"FF5A6472\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"12\"/><name val=\"Calibri\"/></font></fonts>"
            + "<fills count=\"4\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill>"
            + "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF2B313C\"/><bgColor indexed=\"64\"/></patternFill></fill>"
            + "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFF2F3F5\"/><bgColor indexed=\"64\"/></patternFill></fill></fills>"
            + "<borders count=\"2\"><border><left/><right/><top/><bottom/><diagonal/></border><border><left/><right/><top/><bottom style=\"thin\"><color rgb=\"FFD0D5DC\"/></bottom><diagonal/></border></borders>"
            + "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>"
            + "<cellXfs count=\"6\"><xf numFmtId=\"49\" fontId=\"0\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyNumberFormat=\"1\" applyBorder=\"1\"><alignment vertical=\"top\"/></xf>"
            + "<xf numFmtId=\"49\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/>"
            + "<xf numFmtId=\"49\" fontId=\"2\" fillId=\"2\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\"><alignment vertical=\"center\"/></xf>"
            + "<xf numFmtId=\"49\" fontId=\"3\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/>"
            + "<xf numFmtId=\"49\" fontId=\"4\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/>"
            + "<xf numFmtId=\"49\" fontId=\"0\" fillId=\"3\" borderId=\"1\" xfId=\"0\" applyFill=\"1\" applyBorder=\"1\"><alignment vertical=\"top\" wrapText=\"1\"/></xf></cellXfs>"
            + "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles></styleSheet>";

        private static string WorksheetXml(List<KeyValuePair<int, string[]>> rows)
        {
            int columns = rows.Count == 0 ? 1 : rows.Max(r => r.Value.Length);
            var widths = new double[columns];
            foreach (var row in rows)
                if (row.Key != 1 && row.Key != 3) // titles and notes may overflow; they do not size the columns
                    for (int c = 0; c < row.Value.Length; c++) widths[c] = Math.Max(widths[c], Math.Min(70, (row.Value[c] ?? "").Length * 1.1 + 3));
            var b = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"" + SheetNs + "\">");
            b.Append("<sheetViews><sheetView workbookViewId=\"0\" showGridLines=\"0\"/></sheetViews><sheetFormatPr defaultRowHeight=\"16\"/><cols>");
            for (int c = 0; c < columns; c++) b.Append("<col min=\"" + (c + 1) + "\" max=\"" + (c + 1) + "\" width=\"" + Math.Max(12, widths[c]).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "\" customWidth=\"1\"/>");
            b.Append("</cols><sheetData>");
            for (int r = 0; r < rows.Count; r++)
            {
                var row = rows[r];
                b.Append("<row r=\"" + (r + 1) + "\"" + (row.Key == 1 ? " ht=\"24\" customHeight=\"1\"" : row.Key == 2 ? " ht=\"20\" customHeight=\"1\"" : "") + ">");
                for (int c = 0; c < row.Value.Length; c++)
                {
                    if (row.Value[c] == null) continue;
                    b.Append("<c r=\"" + ColumnName(c) + (r + 1) + "\" t=\"inlineStr\" s=\"" + row.Key + "\"><is><t xml:space=\"preserve\">" + Xml(row.Value[c]) + "</t></is></c>");
                }
                b.Append("</row>");
            }
            b.Append("</sheetData><pageMargins left=\"0.5\" right=\"0.5\" top=\"0.6\" bottom=\"0.6\" header=\"0.3\" footer=\"0.3\"/></worksheet>");
            return b.ToString();
        }

        private static List<KeyValuePair<int, string[]>> SheetRows(ReportSection section, HardwareReport report, bool first)
        {
            var rows = new List<KeyValuePair<int, string[]>>();
            Action<int, string[]> add = (style, cells) => rows.Add(new KeyValuePair<int, string[]>(style, cells));
            add(1, new[] { first ? "Blackwall · " + report.Title + ": " + report.Device : section.Title });
            if (first)
            {
                add(3, new[] { "Generado el " + report.GeneratedAt + " · escaneo " + report.ScannedAt + " · Blackwall " + report.AppVersion });
                add(3, new[] { report.Anonymized ? "Datos identificativos ocultos (series, UUID, MAC, IP y clave)." : "Incluye números de serie, MAC e IP: compártelo solo con quien confíes." });
            }
            else if (!string.IsNullOrEmpty(section.Summary)) add(3, new[] { section.Summary });
            if (section.Fields.Count > 0)
            {
                add(0, new string[0]);
                add(2, new[] { "Propiedad", "Valor" });
                foreach (var f in section.Fields) add(5, new[] { f.Key, f.Value });
            }
            foreach (var t in section.Tables)
            {
                add(0, new string[0]);
                add(4, new[] { t.Title });
                add(2, t.Columns);
                foreach (var row in t.Rows) add(0, row);
            }
            return rows;
        }

        internal byte[] RenderHardwareReportXlsx(bool anonymize)
        {
            var report = BuildHardwareReport(anonymize);
            using (var memory = new MemoryStream())
            {
                using (var package = System.IO.Packaging.Package.Open(memory, FileMode.Create))
                {
                    Func<string, string, string, System.IO.Packaging.PackagePart> part = (uri, type, content) =>
                    {
                        var created = package.CreatePart(new Uri(uri, UriKind.Relative), type, System.IO.Packaging.CompressionOption.Normal);
                        using (var writer = new StreamWriter(created.GetStream(), new UTF8Encoding(false))) writer.Write(content);
                        return created;
                    };
                    var used = new HashSet<string>();
                    var sheets = new StringBuilder();
                    var workbookRels = new List<KeyValuePair<string, string>>();
                    for (int i = 0; i < report.Sections.Count; i++)
                    {
                        var section = report.Sections[i];
                        part("/xl/worksheets/sheet" + (i + 1) + ".xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml", WorksheetXml(SheetRows(section, report, i == 0)));
                        sheets.Append("<sheet name=\"" + Xml(SheetName(section.Title, used)) + "\" sheetId=\"" + (i + 1) + "\" r:id=\"rId" + (i + 1) + "\"/>");
                        workbookRels.Add(new KeyValuePair<string, string>("rId" + (i + 1), "worksheets/sheet" + (i + 1) + ".xml"));
                    }
                    var workbook = part("/xl/workbook.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml",
                        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"" + SheetNs + "\" xmlns:r=\"" + RelNs + "\"><sheets>" + sheets + "</sheets></workbook>");
                    part("/xl/styles.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml", XlsxStyles);
                    foreach (var rel in workbookRels) workbook.CreateRelationship(new Uri(rel.Value, UriKind.Relative), System.IO.Packaging.TargetMode.Internal, RelNs + "/worksheet", rel.Key);
                    workbook.CreateRelationship(new Uri("styles.xml", UriKind.Relative), System.IO.Packaging.TargetMode.Internal, RelNs + "/styles", "rIdStyles");
                    package.CreateRelationship(new Uri("/xl/workbook.xml", UriKind.Relative), System.IO.Packaging.TargetMode.Internal, RelNs + "/officeDocument", "rId1");
                }
                return memory.ToArray();
            }
        }

        internal string RenderHardwareReport(string format, bool anonymize)
        {
            var report = BuildHardwareReport(anonymize);
            switch (format)
            {
                case "html": return RenderReportHtml(report);
                case "md": return RenderReportMarkdown(report);
                case "txt": return RenderReportText(report);
                case "csv": return RenderReportCsv(report);
                default: return RenderReportJson(report);
            }
        }

        private void ExportReport(string format, bool anonymize)
        {
            if (_hardware == null) return;
            var chassis = GetDict(_hardware, "chassis");
            string device = Regex.Replace((GetStr(chassis, "manufacturer") + "-" + GetStr(chassis, "model")).Trim('-'), @"[^\w\-]+", "-");
            var info = ReportFormats.First(f => f[0] == format);
            var sfd = new SaveFileDialog
            {
                FileName = "Blackwall-Informe-" + device + "-" + DateTime.Now.ToString("yyyy-MM-dd") + "." + format,
                Filter = info[1] + " (*." + format + ")|*." + format
            };
            if (sfd.ShowDialog(this) != true) return;
            var activity = BeginOperation("Exportar informe · " + info[1], "Sistema", 0);
            try
            {
                // CSV carries a BOM so Excel detects UTF-8; the rest are plain UTF-8.
                if (format == "xlsx") File.WriteAllBytes(sfd.FileName, RenderHardwareReportXlsx(anonymize));
                else File.WriteAllText(sfd.FileName, RenderHardwareReport(format, anonymize), new UTF8Encoding(format == "csv"));
                CompleteOperation(activity, true, "Informe guardado en " + sfd.FileName + (anonymize ? " (datos identificativos ocultos)" : ""));
                SetStatus("🟢 Informe exportado: " + Path.GetFileName(sfd.FileName), "Exportado");
            }
            catch (Exception ex) { CompleteOperation(activity, false, "No se pudo guardar el informe: " + ex.Message); }
        }

        // Format picker: one flyout with the five formats and the privacy switch, anchored to the export button.
        private Button CreateExportReportButton()
        {
            Button trigger = CreateStyledButton("Exportar informe ▾", true);
            var popup = new Popup { PlacementTarget = trigger, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true, VerticalOffset = 8 };
            var panel = new StackPanel { Width = 380 };
            panel.Children.Add(new TextBlock { Text = "Exportar informe del equipo", Foreground = TextPrimary, FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(6, 2, 6, 10) });
            var anonymize = new CheckBox { Content = "Ocultar datos identificativos (serie, UUID, MAC, IP y clave)", IsChecked = true, Foreground = TextSecondary, FontSize = 12, Margin = new Thickness(6, 8, 6, 2) };
            foreach (var format in ReportFormats)
            {
                string key = format[0];
                var option = new Button { HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 0, 0, 2), Cursor = Cursors.Hand, Foreground = TextPrimary };
                option.SetResourceReference(FrameworkElement.StyleProperty, "NavButton");
                var row = new DockPanel();
                var icon = new TextBlock { Text = format[3], FontFamily = NavIconFont, FontSize = 18, Foreground = CyberCyanBrush, Width = 30, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
                DockPanel.SetDock(icon, Dock.Left); row.Children.Add(icon);
                var text = new StackPanel();
                text.Children.Add(new TextBlock { Text = format[1], FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = TextPrimary });
                text.Children.Add(new TextBlock { Text = format[2], FontSize = 11.5, Foreground = TextSecondary, TextWrapping = TextWrapping.Wrap });
                row.Children.Add(text);
                option.Content = row;
                System.Windows.Automation.AutomationProperties.SetName(option, "Exportar " + format[1]);
                option.Click += (s, e) => { popup.IsOpen = false; ExportReport(key, anonymize.IsChecked == true); };
                panel.Children.Add(option);
            }
            panel.Children.Add(anonymize);
            var frame = new Border { Background = BgSurface, BorderBrush = BorderSubtle, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Padding = new Thickness(10), Child = panel };
            frame.SetResourceReference(Border.BorderBrushProperty, "MaterialEdge");
            popup.Child = frame;
            trigger.Click += (s, e) => popup.IsOpen = !popup.IsOpen;
            return trigger;
        }

        // ====================================================================
        // HELPERS VISUALES Y DE DATOS
        // ====================================================================
        private Border CreateBanner(string title, string subtitle, UIElement rightAction = null)
        {
            Border banner = CreateCardContainer();
            banner.Padding = new Thickness(22);
            banner.Margin = new Thickness(0, 0, 0, 20);
            var body = new StackPanel();
            body.Children.Add(new TextBlock { Text = title, Foreground = TextPrimary, FontWeight = FontWeights.SemiBold, FontSize = 24, LineHeight = 30, TextWrapping = TextWrapping.Wrap });
            body.Children.Add(new TextBlock { Text = subtitle, Foreground = TextSecondary, FontSize = 13, LineHeight = 20, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap });
            if (rightAction != null)
            {
                var actions = rightAction as FrameworkElement;
                if (actions != null) { actions.Margin = new Thickness(0, 16, 0, 0); actions.HorizontalAlignment = HorizontalAlignment.Left; }
                body.Children.Add(rightAction);
            }
            banner.Child = body;
            return banner;
        }

        private Border CreateCardContainer()
        {
            Border card = new MaterialCard
            {
                Animate = () => MotionAllowed,
                Uid = "motion-card",
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(16),
                Padding = new Thickness(18)
            };
            card.SetResourceReference(Border.BackgroundProperty, "MaterialSurface");
            card.SetResourceReference(Border.BorderBrushProperty, "MaterialEdge");
            return card;
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
                    Padding = new Thickness(0, 8, 0, 8)
                };
                Grid rdp = new Grid();
                rdp.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42, GridUnitType.Star) });
                rdp.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58, GridUnitType.Star) });
                TextBox valBox = new TextBox
                {
                    Text = rows[i, 1],
                    Foreground = TextPrimary,
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    IsReadOnly = true,
                    FontWeight = FontWeights.SemiBold,
                    FontFamily = FontFamily,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Right,
                    Padding = new Thickness(8, 0, 0, 0)
                };
                Grid.SetColumn(valBox, 1);
                rdp.Children.Add(valBox);
                rdp.Children.Add(new TextBlock { Text = rows[i, 0], Foreground = TextSecondary, FontSize = 11.5, TextWrapping = TextWrapping.Wrap });
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
                CornerRadius = new CornerRadius(6),
                BorderBrush = fg,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(8, 4, 8, 4),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = text,
                MaxWidth = 160,
                Child = new TextBlock { Text = text, Foreground = fg, FontFamily = FontFamily, FontSize = 10, FontWeight = FontWeights.Bold, TextTrimming = TextTrimming.CharacterEllipsis }
            };
        }

        private Button CreateStyledButton(string text, bool primary)
        {
            Button btn = new Button
            {
                Content = text,
                Foreground = primary ? BrushHex("#182232") : TextPrimary,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(14, 8, 14, 8),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Cursor = Cursors.Hand
            };
            btn.SetResourceReference(Control.BackgroundProperty, primary ? "MaterialSilver" : "MaterialButtonSurface");
            btn.SetResourceReference(Control.BorderBrushProperty, "MaterialEdge");

            var pressScale = new ScaleTransform(1, 1);
            btn.RenderTransformOrigin = new Point(0.5, 0.5);
            btn.RenderTransform = pressScale;
            btn.PreviewMouseLeftButtonDown += (s, e) =>
            {
                if (!MotionAllowed) return;
                PlayButtonSheen(btn);
                pressScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 0.97, TimeSpan.FromMilliseconds(90)) { AutoReverse = true, FillBehavior = FillBehavior.Stop });
                pressScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 0.97, TimeSpan.FromMilliseconds(90)) { AutoReverse = true, FillBehavior = FillBehavior.Stop });
            };
            return btn;
        }

        private void StyleDashboardButtonEdge(Button button)
        {
            button.BorderThickness = new Thickness(2.5);
            button.SetResourceReference(Control.BorderBrushProperty, "DashboardButtonEdge");
            button.SetResourceReference(Control.BackgroundProperty, "DashboardButtonSurface");
            button.Foreground = TextPrimary;
        }

        private async Task RunUiActionAsync(string key, Button button, Func<Task> action)
        {
            if (!_uiActions.Add(key)) return;
            button.IsEnabled = false;
            object label = button.Content;
            button.Content = "En curso…";
            try { await action(); }
            catch (Exception ex) { SetStatus("No se completó: " + ex.Message, "Consulta la consola"); AppendLog(ex.Message, "ERROR"); }
            finally { _uiActions.Remove(key); button.IsEnabled = true; button.Content = label; }
        }

        private void AddFilterButton(Panel parent, string text, bool active, Action onClick)
        {
            Button b = CreateStyledButton(text, active);
            b.Margin = new Thickness(0, 0, 8, 6);
            b.Click += (s, e) => onClick();
            parent.Children.Add(b);
        }

        // Legacy tags → level and source, so existing AppendLog(msg, TAG) calls keep working unchanged.
        private static readonly Dictionary<string, string[]> LogTags = new Dictionary<string, string[]>
        {
            { "ERROR", new[] { "error", "Sistema" } }, { "WARN", new[] { "warn", "Sistema" } },
            { "TWEAK", new[] { "info", "Optimización" } }, { "PACK", new[] { "info", "Optimización" } },
            { "INSTALL", new[] { "info", "Instalación" } }, { "WINGET", new[] { "info", "Instalación" } }, { "DOWNLOAD", new[] { "info", "Instalación" } },
            { "FALLBACK", new[] { "warn", "Instalación" } }, { "SUCCESS", new[] { "success", "Instalación" } },
            { "SAFETY", new[] { "info", "Seguridad" } }, { "BACKUP", new[] { "info", "Seguridad" } },
            { "OTA", new[] { "info", "Actualizaciones" } }, { "KILL", new[] { "info", "Procesos" } }
        };

        private void AppendLog(string msg, string tag)
        {
            string[] map;
            if (!LogTags.TryGetValue(tag ?? "", out map)) map = new[] { "info", "Sistema" };
            AppendLog(msg, map[0], map[1]);
        }

        private void AppendLog(string msg, string level, string source)
        {
            var entry = new ActivityLogEntry { Time = DateTime.Now, Level = level, Source = source, Message = (msg ?? "").Replace("\0", "").Trim() };
            if (entry.Message.Length == 0) return;
            RunOnUi(() =>
            {
                _activityLog.Add(entry);
                if (_activityLog.Count > ActivityLogLimit) _activityLog.RemoveRange(0, _activityLog.Count - ActivityLogLimit);
                AppendLogRow(entry);
            });
        }

        // Background work never waits for the UI: updates are queued in order on the dispatcher.
        private void RunOnUi(Action action)
        {
            if (Dispatcher.CheckAccess()) action(); else Dispatcher.BeginInvoke(action);
        }

        private bool LogMatches(ActivityLogEntry entry)
        {
            if (_activityFilter == "all") return true;
            if (_activityFilter == "problems") return entry.Level == "error" || entry.Level == "warn";
            return entry.Source == _activityFilter;
        }

        private SolidColorBrush LevelBrush(string level)
        {
            return level == "error" ? RiskHighText : level == "warn" ? RiskMedText : level == "success" ? RiskLowText : TextSecondary;
        }

        private readonly System.Collections.ObjectModel.ObservableCollection<ActivityLogRow> _activityRows = new System.Collections.ObjectModel.ObservableCollection<ActivityLogRow>();
        private readonly List<ActivityLogEntry> _pendingLogRows = new List<ActivityLogEntry>();
        private bool _logFlushQueued;

        private ActivityLogRow BuildLogRow(ActivityLogEntry entry)
        {
            return new ActivityLogRow { Time = entry.Time.ToString("HH:mm:ss"), Source = entry.Source, Message = entry.Message, Dot = LevelBrush(entry.Level), Text = entry.Level == "error" ? RiskHighText : TextPrimary, Muted = TextSecondary };
        }

        // Lines arriving in a burst are added in one pass per frame, with a single scroll to the end.
        private void AppendLogRow(ActivityLogEntry entry)
        {
            if (_activityLogRows == null || !LogMatches(entry)) return;
            _pendingLogRows.Add(entry);
            if (_logFlushQueued) return;
            _logFlushQueued = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(FlushLogRows));
        }

        private void FlushLogRows()
        {
            _logFlushQueued = false;
            var batch = _pendingLogRows.Skip(Math.Max(0, _pendingLogRows.Count - ActivityRowLimit)).ToList();
            _pendingLogRows.Clear();
            if (_activityLogRows == null || batch.Count == 0) return;
            bool following = _activityLogScroll == null || _activityLogScroll.VerticalOffset >= _activityLogScroll.ScrollableHeight - 1;
            int overflow = _activityRows.Count + batch.Count - ActivityRowLimit;
            for (int i = 0; i < overflow && _activityRows.Count > 0; i++) _activityRows.RemoveAt(0);
            foreach (var entry in batch) _activityRows.Add(BuildLogRow(entry));
            if (_activityLogEmpty != null) _activityLogEmpty.Visibility = Visibility.Collapsed;
            if (following && _activityLogScroll != null) _activityLogScroll.ScrollToEnd();
        }

        private void RefreshLogRows()
        {
            if (_activityLogRows == null) return;
            _pendingLogRows.Clear();
            _activityRows.Clear();
            var visible = _activityLog.Where(LogMatches).ToList();
            foreach (var entry in visible.Skip(Math.Max(0, visible.Count - ActivityRowLimit))) _activityRows.Add(BuildLogRow(entry));
            if (_activityLogEmpty != null) _activityLogEmpty.Visibility = _activityRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_activityLogScroll != null) _activityLogScroll.ScrollToEnd();
        }

        private string ActivityLogText()
        {
            return string.Join("\r\n", _activityLog.Where(LogMatches).Select(e => e.Time.ToString("yyyy-MM-dd HH:mm:ss") + "  " + e.Level.ToUpperInvariant().PadRight(7) + "  " + e.Source.PadRight(15) + "  " + e.Message));
        }

        internal ActivityOperation BeginOperation(string title, string source, int steps)
        {
            var op = new ActivityOperation { Title = title, Source = source, Steps = Math.Max(0, steps) };
            RunOnUi(() => { _operations.Add(op); TrimOperations(); QueueActivityRefresh(); });
            AppendLog(title, "info", source);
            return op;
        }

        internal void StepOperation(ActivityOperation op, string text, bool advance = true, bool log = true)
        {
            RunOnUi(() => { op.Step = text; if (advance && op.Steps > 0) op.Done = Math.Min(op.Steps, op.Done + 1); QueueActivityRefresh(); });
            if (log) AppendLog(text, "info", op.Source);
        }

        internal void CompleteOperation(ActivityOperation op, bool ok, string summary)
        {
            RunOnUi(() =>
            {
                if (op.Finished) return;
                op.Finished = true; op.Ok = ok; op.Summary = summary; op.Ended = DateTime.Now;
                if (ok && op.Steps > 0) op.Done = op.Steps;
                if (ok) _operationsCompleted++; else _operationsFailed++;
                TrimOperations();
                QueueActivityRefresh();
            });
            AppendLog(summary, ok ? "success" : "error", op.Source);
        }

        // Wraps a one-shot system task (SFC, DISM, upgrades...) so it always ends as completed or failed; never throws to a click handler.
        private async Task<bool> RunOperationAsync(string title, string source, Func<ActivityOperation, Task<string>> work)
        {
            var op = BeginOperation(title, source, 0);
            try { CompleteOperation(op, true, await work(op) ?? title + ": finalizado"); return true; }
            catch (Exception ex) { CompleteOperation(op, false, title + ": " + ex.Message); SetStatus("No se completó: " + title, "Consulta el registro de actividad"); return false; }
        }

        // Console tools print spinners and progress bars; the log keeps only their last meaningful lines.
        private static string LastMeaningfulLines(string output, int count)
        {
            var lines = (output ?? "").Replace("\0", "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim()).Where(l => l.Length > 2 && l.Any(char.IsLetterOrDigit) && !l.All(c => "-\\|/█▒░ %0123456789.".IndexOf(c) >= 0)).ToList();
            return string.Join(" · ", lines.Skip(Math.Max(0, lines.Count - count)));
        }

        private void TrimOperations()
        {
            var finished = _operations.Where(o => o.Finished).ToList();
            foreach (var old in finished.Take(Math.Max(0, finished.Count - 5))) _operations.Remove(old);
        }

        private void QueueActivityRefresh()
        {
            if (_activityRefreshQueued) return;
            _activityRefreshQueued = true; // many steps in one frame collapse into a single UI update
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(RefreshActivityUi));
        }

        private void RefreshActivityUi()
        {
            _activityRefreshQueued = false;
            var active = _operations.LastOrDefault(o => !o.Finished);
            if (_footerProgress != null)
            {
                _footerProgress.Visibility = active == null ? Visibility.Collapsed : Visibility.Visible;
                if (active != null)
                {
                    _footerProgress.IsIndeterminate = active.Steps == 0;
                    _footerProgress.Value = active.Steps == 0 ? 0 : active.Done * 100.0 / active.Steps;
                    if (_taskText != null) _taskText.Text = active.Title + (active.Steps > 0 ? " · " + active.Done + "/" + active.Steps : "");
                }
                else if (_taskText != null && _operations.Count > 0) _taskText.Text = _operations[_operations.Count - 1].Ok ? "Listo" : "Terminado con errores";
            }
            if (_activitySummary != null)
                _activitySummary.Text = "En curso " + _operations.Count(o => !o.Finished) + "  ·  Completadas " + _operationsCompleted + "  ·  Con errores " + _operationsFailed;
            if (_activityOpsPanel == null) return;
            _activityOpsPanel.Children.Clear();
            if (_operations.Count == 0)
            {
                _activityOpsPanel.Children.Add(new TextBlock { Text = "No hay operaciones en curso. Lo que apliques o instales aparecerá aquí paso a paso.", Foreground = TextSecondary, FontSize = 12.5, TextWrapping = TextWrapping.Wrap });
                return;
            }
            foreach (var op in Enumerable.Reverse(_operations).Take(6)) _activityOpsPanel.Children.Add(BuildOperationRow(op));
        }

        private UIElement BuildOperationRow(ActivityOperation op)
        {
            var row = new Border { BorderBrush = BorderSubtle, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 8, 0, 10) };
            var stack = new StackPanel();
            var head = new DockPanel();
            string state = !op.Finished ? (op.Steps > 0 ? op.Done + " de " + op.Steps : "En curso")
                : (op.Ok ? "✓ Completada" : "⚠ Con errores") + " · " + Math.Max(0.1, (op.Ended - op.Started).TotalSeconds).ToString("0.#") + " s";
            var stateText = new TextBlock { Text = state, Foreground = !op.Finished ? CyberCyanBrush : op.Ok ? RiskLowText : RiskHighText, FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(stateText, Dock.Right);
            head.Children.Add(stateText);
            head.Children.Add(new TextBlock { Text = op.Title, Foreground = TextPrimary, FontSize = 13.5, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 12, 0) });
            stack.Children.Add(head);
            string detail = op.Finished ? op.Summary : op.Step;
            if (!string.IsNullOrEmpty(detail))
                stack.Children.Add(new TextBlock { Text = op.Source + " · " + detail, Foreground = op.Finished && !op.Ok ? RiskHighText : TextSecondary, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });
            if (!op.Finished)
                stack.Children.Add(new ProgressBar { Height = 4, Maximum = 100, Value = op.Steps == 0 ? 0 : op.Done * 100.0 / op.Steps, IsIndeterminate = op.Steps == 0, Foreground = CyberCyanBrush, Background = BgElevated, BorderThickness = new Thickness(0), Margin = new Thickness(0, 8, 0, 0) });
            row.Child = stack;
            return row;
        }

        // Never blocks the caller (installers, WMI reads) and skips layout when nothing changed.
        private void SetStatus(string status, string task)
        {
            RunOnUi(() =>
            {
                if (_statusText != null && _statusText.Text != status) _statusText.Text = status;
                if (_taskText != null && _taskText.Text != task) _taskText.Text = task;
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
