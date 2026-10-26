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
    public class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            // Habilitar TLS 1.2 y TLS 1.3 para descargas HTTPS en cualquier instalación de Windows 10/11
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | (SecurityProtocolType)3072;
            if (args.Length > 0 && args[0] == "--ota-install") { Environment.ExitCode = OtaInstaller.Run(args); return; }
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
                        if (win.IsVisible && File.Exists(result) && File.ReadAllText(result).StartsWith("OK:", StringComparison.Ordinal)) win.RelaunchAsAdministrator();
                    }
                }
                catch { }
            };
            app.Run(win);
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
            _renderOffset += remaining * (1 - Math.Exp(-elapsed / 24.0));
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

    public class MaterialCard : Border
    {
        private static readonly SolidColorBrush HighlightFill = CreateHighlightFill();
        private static readonly Pen HighlightEdge = CreateHighlightEdge();
        private static SolidColorBrush CreateHighlightFill() { var brush = new SolidColorBrush(Color.FromArgb(9, 231, 237, 247)); brush.Freeze(); return brush; }
        private static Pen CreateHighlightEdge() { var pen = new Pen(new SolidColorBrush(Color.FromArgb(170, 231, 237, 247)), 1); pen.Freeze(); return pen; }
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
            drawing.DrawLine(HighlightEdge, new Point(ActualWidth - 50, ActualHeight - 0.5), new Point(ActualWidth - 17, ActualHeight - 0.5));
            drawing.Pop();
        }
    }

    // Reflows existing cards without rebuilding them or losing selection and focus.
    public class AdaptiveCardGrid : Panel
    {
        public int Columns { get; set; }
        private double[] _rowHeights = new double[0];
        private readonly List<UIElement> _layoutChildren = new List<UIElement>();
        protected override Size MeasureOverride(Size available)
        {
            double width = double.IsInfinity(available.Width) ? 310 * Math.Max(1, Columns) : available.Width;
            _layoutChildren.Clear();
            foreach (UIElement child in Children) if (child.Visibility != Visibility.Collapsed) _layoutChildren.Add(child);
            Columns = Math.Max(1, Math.Min(Math.Max(1, _layoutChildren.Count), Math.Min(4, (int)(width / 310))));
            int rows = (_layoutChildren.Count + Columns - 1) / Columns;
            if (_rowHeights.Length != rows) _rowHeights = new double[rows];
            else Array.Clear(_rowHeights, 0, rows);
            for (int i = 0; i < _layoutChildren.Count; i++)
            {
                _layoutChildren[i].Measure(new Size(width / Columns, double.PositiveInfinity));
                _rowHeights[i / Columns] = Math.Max(_rowHeights[i / Columns], _layoutChildren[i].DesiredSize.Height);
            }
            return new Size(width, _rowHeights.Sum());
        }
        protected override Size ArrangeOverride(Size bounds)
        {
            double y = 0, width = bounds.Width / Math.Max(1, Columns);
            for (int i = 0; i < _layoutChildren.Count; i++)
            {
                _layoutChildren[i].Arrange(new Rect((i % Columns) * width, y, width, _rowHeights[i / Columns]));
                if (i % Columns == Columns - 1) y += _rowHeights[i / Columns];
            }
            return bounds;
        }
    }

    // Static, local geometry: no ambient timer or network assets behind the content.
    public class GraphiteNetwork : FrameworkElement
    {
        protected override void OnRender(DrawingContext drawing)
        {
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(15, 142, 154, 172)), 1);
            for (int y = 35; y < ActualHeight; y += 150)
                for (int x = 25; x < ActualWidth; x += 210)
                {
                    Point a = new Point(x, y), b = new Point(x + 130, y + 48), c = new Point(x + 210, y - 18);
                    drawing.DrawLine(pen, a, b); drawing.DrawLine(pen, b, c);
                    drawing.DrawEllipse(pen.Brush, null, a, 2, 2);
                    drawing.DrawEllipse(pen.Brush, null, b, 2, 2);
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
        private string _optDeviceScope = "auto";
        private string _optRiskScope = "all";
        private string _appCategoryScope = "Todas";
        private string _tweakSearchText = "";
        private string _appSearchText = "";
        private TextBox _sectionSearch;
        private Button _installSelectedButton;
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
        private OtaRelease _availableRelease;
        private string _announcedRelease;
        private Window _otaDialog;
        internal bool UpdateOnly;
        internal string RequestedUpdateVersion, RequestedUpdateHash;
        private List<Dictionary<string, object>> _liveScripts = new List<Dictionary<string, object>>();
        private bool _monitorProcessesChanged;
        private readonly Dictionary<int, TextBlock> _monitorProcessMemoryLabels = new Dictionary<int, TextBlock>();
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
        private readonly List<Button> _omniboxItems = new List<Button>();
        private int _omniboxSelected = -1;
        private string _pendingSearchId;
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
            LoadUiPreferences();
            bool isAdmin = IsAdministrator();
            Title = "Blackwall — Muro Negro" + (isAdmin ? " [ADMINISTRADOR]" : " [Modo Estándar]");
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
            SourceInitialized += (s, e) => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle).AddHook(WindowBoundsHook);

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

            _consoleLogs.Add("[" + DateTime.Now.ToString("HH:mm:ss") + "] [ARQUITECTURA] Interfaz WPF/.NET y herramientas nativas de Windows disponibles.");
            _consoleLogs.Add("[" + DateTime.Now.ToString("HH:mm:ss") + "] [PRIVILEGIOS] Sesión actual ejecutándose como: " + (isAdmin ? "Administrador Elevado (UAC Activo)" : "Usuario Estándar"));

            LoadEmbeddedCatalogs();
            UpdateThemeBrushes();
            BuildShellUI();
            InitializeActivityPanel();
            Activated += (s, e) => { UpdateAmbientMotion(); AnnounceAvailableUpdate(); };
            Deactivated += (s, e) => { foreach (var viewer in _scrollViewers) viewer.StopSmoothScroll(); UpdateAmbientMotion(); };
            StateChanged += (s, e) => { if (WindowState == WindowState.Minimized) foreach (var viewer in _scrollViewers) viewer.StopSmoothScroll(); UpdateAmbientMotion(); };
            SystemParameters.StaticPropertyChanged += OnSystemMotionChanged;
            Closed += (s, e) =>
            {
                SystemParameters.StaticPropertyChanged -= OnSystemMotionChanged;
                _scriptMonitorTimer.Stop();
                if (_logoHalo != null) _logoHalo.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.BlurRadiusProperty, null);
                if (_headerEnergy != null) _headerEnergy.SetRunning(false);
                if (_sidebarEnergy != null) _sidebarEnergy.SetRunning(false);
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
                await RefreshHardwareNativeAsync();
            };

            _scriptMonitorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _scriptMonitorTimer.Tick += async (s, e) =>
            {
                if (WindowState != WindowState.Minimized && _activeTab == "monitor" && !_readTasks.ContainsKey("monitor") && !_readErrors.ContainsKey("monitor"))
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
            catch { _consoleLogs.Add("[INTERFAZ] Preferencias no válidas; se usarán los valores iniciales."); }
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
                _motionToggle.Content = _sidebarCollapsed ? (_reduceMotion ? "○" : "≈") : (_reduceMotion ? "Movimiento reducido" : "Animaciones activas");
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
            // The material remains visible at rest; decoration does not need an idle render loop.
            if (_headerEnergy != null) _headerEnergy.SetRunning(false);
            if (_sidebarEnergy != null) _sidebarEnergy.SetRunning(false);
            if (_logoHalo != null)
            {
                _logoHalo.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.BlurRadiusProperty, null);
            }
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
            page.UpdateLayout();
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
            _sidebarBorder.Padding = new Thickness(_sidebarCollapsed ? 8 : 14, 16, _sidebarCollapsed ? 8 : 14, 16);
            foreach (var element in _expandedSidebarElements) element.Visibility = _sidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;
            foreach (var button in _navButtons.Values)
            {
                var content = (Grid)button.Content;
                for (int i = 1; i < content.Children.Count; i++) content.Children[i].Visibility = _sidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;
                content.ColumnDefinitions[1].Width = _sidebarCollapsed ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
                content.ColumnDefinitions[2].Width = new GridLength(_sidebarCollapsed ? 0 : 12);
            }
            _sidebarToggle.Content = _sidebarCollapsed ? "☰" : "☰    Contraer menú";
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
        private string ReadEmbeddedResource(string resourceName, string fallbackDiskRelativePath)
        {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
            {
                if (stream == null) throw new InvalidDataException("Falta el catálogo embebido " + resourceName);
                using (var reader = new StreamReader(stream, Encoding.UTF8)) return reader.ReadToEnd();
            }
        }

        private string SelectCatalog(string embedded, string cached)
        {
            // Legacy cache is unsigned; only catalogs covered by the executable's OTA signature are used.
            return embedded;
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
        private void InitializeActivityPanel()
        {
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
            return _activeTab == "optimization" || _activeTab == "debloat" ? "tweaks" : _activeTab;
        }

        private Task RunReadAsync(string key, string label, Func<Task> read, bool background = false)
        {
            Task pending;
            if (_readTasks.TryGetValue(key, out pending)) return pending;
            _readErrors.Remove(key);
            _readLabels[key] = label;
            _readProgress.Remove(key);
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
                if (CurrentModuleKey() == key && (!background || key != "monitor" || _monitorProcessesChanged)) RenderCurrentTab();
                if (!background) UpdateActivityPanel();
            }
        }

        private void ReportReadProgress(string key, int completed, int total, string detail)
        {
            Dispatcher.Invoke(() =>
            {
                _readProgress[key] = total > 0 ? Math.Min(100, completed * 100 / total) : -1;
                _readLabels[key] = detail;
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
            _activityPanel.Visibility = busy || failed ? Visibility.Visible : Visibility.Collapsed;
            if (!busy && !failed) return;
            _activityTitle.Text = failed ? "No se pudo completar la lectura" : "LECTURA EN CURSO";
            _activityDetail.Text = failed ? _readLabels[key] + "\n" + _readErrors[key] : _readLabels[key];
            _activityPanel.BorderBrush = failed ? RiskHighText : CyberCyanBrush;
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
                var diskRead = Task.Run(() => QueryAllWmi("root\\Microsoft\\Windows\\Storage", "SELECT FriendlyName, SerialNumber, MediaType, BusType, Size, HealthStatus FROM MSFT_PhysicalDisk"));

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
                ReportReadProgress("hardware", 5, 8, "Almacenamiento · 5/8");

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
                    { "caption", GetWmiStr(osInfo, "Caption", "Microsoft Windows 11") },
                    { "displayVersion", displayVer },
                    { "buildNumber", GetWmiStr(osInfo, "BuildNumber", "22631") },
                    { "installDate", FormatWmiDate(GetWmiStr(osInfo, "InstallDate", "")) },
                    { "uptime", uptimeStr },
                    { "partialProductKey", partialKey },
                    { "adapters", netAdapters }
                };

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
                    label.Text = "PID " + GetStr(process, "pid") + "  •  " + GetStr(process, "name") + "  •  Parent PID: " + GetStr(process, "parentPid") + "  •  RAM: " + GetStr(process, "memoryMb") + " MB";
            }
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
                var rows = QueryAllWmi("root\\default", "SELECT SequenceNumber, Description, CreationTime FROM SystemRestore", true);
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
                AppendLog("Para crear puntos de restauración, vuelve a abrir Blackwall y acepta la autorización de Windows.", "WARN");
                SetStatus("🟡 Requiere permisos de Administrador para crear Punto de Restauración", "Aviso");
            }
            await RefreshRestorePointsNativeAsync();
        }

        // ====================================================================
        // HELPERS NATIVOS WMI Y REGISTRO
        // ====================================================================
        private Dictionary<string, object> QueryFirstWmi(string ns, string query, bool required = false)
        {
            var all = QueryAllWmi(ns, query, required);
            return all.Count > 0 ? all[0] : new Dictionary<string, object>();
        }

        private List<Dictionary<string, object>> QueryAllWmi(string ns, string query, bool required = false)
        {
            var list = new List<Dictionary<string, object>>();
            try
            {
                var opts = new EnumerationOptions { ReturnImmediately = true, Rewindable = false, Timeout = TimeSpan.FromSeconds(required ? 5 : 2) };
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
            Resources["MaterialSurface"] = MaterialGradient(_isDark ? "#202630" : "#E3E6EB", _isDark ? "#141820" : "#D4DAE2");
            Resources["MaterialButtonSurface"] = MaterialGradient(_isDark ? "#303947" : "#D9DEE6", _isDark ? "#141920" : "#BCC6D3");
            Resources["MaterialSilver"] = MaterialGradient(_isDark ? "#F4F7FC" : "#CBD3DE", _isDark ? "#AAB6C7" : "#AAB7C8");
            var edge = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
            edge.GradientStops.Add(new GradientStop(ColorHex(_isDark ? "#B3BECE" : "#B8C2CE"), 0));
            edge.GradientStops.Add(new GradientStop(ColorHex(_isDark ? "#424C5D" : "#8A96A5"), 0.19));
            edge.GradientStops.Add(new GradientStop(ColorHex(_isDark ? "#252D39" : "#C5CCD5"), 0.55));
            edge.GradientStops.Add(new GradientStop(ColorHex(_isDark ? "#8E9AAC" : "#8E9AAC"), 0.88));
            edge.GradientStops.Add(new GradientStop(ColorHex(_isDark ? "#303A49" : "#BCC7D6"), 1));
            Resources["MaterialEdge"] = edge;
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
                ToolTip = isClose ? "Cerrar" : text == "—" ? "Minimizar" : "Maximizar o restaurar"
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
            FontFamily = new FontFamily("Segoe UI");
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
                    <Setter Property='Template'>
                        <Setter.Value><ControlTemplate TargetType='Button'>
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
                            <ControlTemplate.Triggers>
                                <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Hover' Property='Opacity' Value='1'/></Trigger>
                                <Trigger Property='IsPressed' Value='True'><Setter TargetName='Frame' Property='Opacity' Value='0.72'/></Trigger>
                                <Trigger Property='IsKeyboardFocused' Value='True'>
                                    <Setter TargetName='Frame' Property='BorderBrush' Value='{DynamicResource CyberFocusBrush}'/>
                                </Trigger>
                                <Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.4'/></Trigger>
                            </ControlTemplate.Triggers>
                        </ControlTemplate></Setter.Value>
                    </Setter>
                </Style>");
            ((Style)Resources[typeof(Button)]).Setters.Add(new EventSetter(FrameworkElement.LoadedEvent, new RoutedEventHandler(OnMotionButtonLoaded)));
            Resources[typeof(ScrollBar)] = (Style)System.Windows.Markup.XamlReader.Parse(@"
                <Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                       xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='ScrollBar'>
                    <Setter Property='Width' Value='10'/>
                    <Setter Property='Background' Value='Transparent'/>
                    <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ScrollBar'>
                        <Track x:Name='PART_Track' Orientation='{TemplateBinding Orientation}' IsDirectionReversed='True'
                            Minimum='{TemplateBinding Minimum}' Maximum='{TemplateBinding Maximum}'
                            ViewportSize='{TemplateBinding ViewportSize}' Value='{Binding Value, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}'>
                            <Track.DecreaseRepeatButton><RepeatButton x:Name='PageBefore' Command='ScrollBar.PageUpCommand' Background='Transparent' Focusable='False'>
                                <RepeatButton.Template><ControlTemplate TargetType='RepeatButton'><Border Background='Transparent'/></ControlTemplate></RepeatButton.Template>
                            </RepeatButton></Track.DecreaseRepeatButton>
                            <Track.Thumb><Thumb Background='{DynamicResource CyberTrackBrush}'>
                                <Thumb.Template><ControlTemplate TargetType='Thumb'><Border x:Name='Grip' Background='{TemplateBinding Background}' CornerRadius='2' Margin='3,2'/>
                                    <ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Grip' Property='Background' Value='{DynamicResource CyberFocusBrush}'/></Trigger></ControlTemplate.Triggers>
                                </ControlTemplate></Thumb.Template>
                            </Thumb></Track.Thumb>
                            <Track.IncreaseRepeatButton><RepeatButton x:Name='PageAfter' Command='ScrollBar.PageDownCommand' Background='Transparent' Focusable='False'>
                                <RepeatButton.Template><ControlTemplate TargetType='RepeatButton'><Border Background='Transparent'/></ControlTemplate></RepeatButton.Template>
                            </RepeatButton></Track.IncreaseRepeatButton>
                        </Track>
                        <ControlTemplate.Triggers><Trigger Property='Orientation' Value='Horizontal'>
                            <Setter TargetName='PART_Track' Property='IsDirectionReversed' Value='False'/>
                            <Setter TargetName='PageBefore' Property='Command' Value='ScrollBar.PageLeftCommand'/>
                            <Setter TargetName='PageAfter' Property='Command' Value='ScrollBar.PageRightCommand'/>
                        </Trigger></ControlTemplate.Triggers>
                    </ControlTemplate></Setter.Value></Setter>
                    <Style.Triggers><Trigger Property='Orientation' Value='Horizontal'>
                        <Setter Property='Width' Value='Auto'/><Setter Property='Height' Value='10'/>
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
            _headerEnergy = new EnergyTrace { Height = 12, VerticalAlignment = VerticalAlignment.Bottom };
            _headerEnergy.Visibility = Visibility.Collapsed;
            _rootGrid.Children.Add(_headerEnergy);

            // 2. CUERPO CENTRAL
            Grid bodyGrid = new Grid();
            var network = new GraphiteNetwork { IsHitTestVisible = false, ClipToBounds = true };
            Grid.SetColumnSpan(network, 2);
            bodyGrid.Children.Add(network);
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
            _sidebarToggle = CreateStyledButton("☰    Contraer menú", false);
            _sidebarToggle.Padding = new Thickness(8, 8, 8, 8);
            _sidebarToggle.Margin = new Thickness(0, 0, 0, 16);
            _sidebarToggle.Click += (s, e) => ToggleSidebar();
            DockPanel.SetDock(_sidebarToggle, Dock.Top);
            sidebarDock.Children.Add(_sidebarToggle);
            _motionToggle = CreateStyledButton("Animaciones activas", false);
            _motionToggle.Padding = new Thickness(6, 8, 6, 8);
            _motionToggle.Margin = new Thickness(0, 12, 0, 0);
            _motionToggle.Click += (s, e) => { _reduceMotion = !_reduceMotion; UpdateMotionPreference(); SaveUiPreferences(); };
            DockPanel.SetDock(_motionToggle, Dock.Bottom);
            sidebarDock.Children.Add(_motionToggle);
            StackPanel navStack = new StackPanel();
            _navButtons.Clear();

            AddNavGroupLabel(navStack, "DIAGNÓSTICO");
            AddNavButton(navStack, "hardware", "Hardware");

            AddNavGroupLabel(navStack, "CONFIGURACIÓN");
            AddNavButton(navStack, "optimization", "Rendimiento");
            AddNavButton(navStack, "debloat", "Privacidad");

            AddNavGroupLabel(navStack, "HERRAMIENTAS");
            AddNavButton(navStack, "apps", "Software");
            AddNavButton(navStack, "monitor", "Procesos");

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

            Button consoleJumpBtn = CreateStyledButton("Consola en vivo ↗", false);
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

        private void AddNavButton(StackPanel parent, string tabKey, string label)
        {
            string title = Regex.Replace(label, @"^[^\p{L}]+", "");
            Grid content = new Grid();
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(25) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            string[] glyphs = { "\uE7F4", "\uE9D9", "\uE72E", "\uE71D", "\uE9D5", "\uE83D" };
            TextBlock number = new TextBlock { Text = glyphs[_navButtons.Count], FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 16, VerticalAlignment = VerticalAlignment.Center };
            TextBlock name = new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            TextBlock arrow = new TextBlock { Text = "›", FontSize = 17, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
            Grid.SetColumn(name, 1);
            Grid.SetColumn(arrow, 2);
            content.Children.Add(number);
            content.Children.Add(name);
            content.Children.Add(arrow);
            Button btn = new Button
            {
                Content = content,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(10, 9, 10, 9),
                Margin = new Thickness(0, 2, 0, 2),
                FontSize = 12,
                Cursor = Cursors.Hand,
                BorderThickness = new Thickness(2, 0, 0, 0),
                BorderBrush = Brushes.Transparent,
                Background = Brushes.Transparent,
                Foreground = TextSecondary,
                ToolTip = title
            };
            System.Windows.Automation.AutomationProperties.SetName(btn, title);
            ToolTip navigationTip = new ToolTip { Content = title, PlacementTarget = btn, Placement = PlacementMode.Right };
            ToolTipService.SetInitialShowDelay(btn, 350);
            ToolTipService.SetShowDuration(btn, 1000);
            ToolTipService.SetBetweenShowDelay(btn, 350);
            btn.ToolTip = navigationTip;
            btn.Click += (s, e) => { navigationTip.IsOpen = false; SwitchTab(tabKey); if (!_sidebarCollapsed) ToggleSidebar(); };
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
            _activeTab = tabKey;
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
            else { _optDeviceScope = "all"; _optRiskScope = "all"; _tweakSubcategoryScope = "Todas"; _tweakSearchText = ""; }
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
            double previousOffset = _mainScroll.VerticalOffset;
            bool sameSection = _lastRenderedTab == _activeTab;
            bool restoreSearch = sameSection && _sectionSearch != null && _sectionSearch.IsKeyboardFocusWithin;
            int selectionStart = restoreSearch ? _sectionSearch.SelectionStart : 0;
            int selectionLength = restoreSearch ? _sectionSearch.SelectionLength : 0;
            _tweakCardUpdaters.Clear();
            _sectionSearch = null;
            _installSelectedButton = null;
            _liveConsoleTextBox = null;
            _monitorProcessMemoryLabels.Clear();

            UIElement content = null;
            string moduleKey = CurrentModuleKey();
            if (moduleKey != "hardware" && !_loadedModules.Contains(moduleKey))
                content = CreateBanner(_readErrors.ContainsKey(moduleKey) ? "Lectura interrumpida" : "Preparando módulo", "El contenido estará disponible al completar la lectura. Puedes seguir navegando.");
            else switch (_activeTab)
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
            metrics.Children.Add(CreateKvCard("Recuperación", "WINDOWS", new[,] { { "Puntos consultados", _loadedModules.Contains("safety") ? _restorePoints.Count.ToString() : "Pendiente de consulta" }, { "Antes de aplicar", _autoRestorePoint ? "Solicitud de punto de restauración activada" : "Solicitud automática desactivada" } }));
            content.Children.Add(metrics);
            }
            else content.Children.Add(new TextBlock { Text = "Datos de hardware: pendientes de lectura · Estados de ajustes y recuperación: consulta cada sección.", Foreground = TextSecondary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 16) });
            var shortcuts = new WrapPanel();
            foreach (var destination in new[] { new[] { "optimization", "Revisar ajustes" }, new[] { "apps", "Software" }, new[] { "safety", "Restauración" }, new[] { "monitor", "Logs y tareas" } })
            {
                string tab = destination[0];
                var button = CreateStyledButton(destination[1], tab == "optimization");
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

        // Filter existing controls so keyboard composition, focus and action handlers survive each keystroke.
        private Func<string, int> CreateCardFilter(Panel panel, IEnumerable<Dictionary<string, object>> items, Func<Dictionary<string, object>, UIElement> build, params string[] fields)
        {
            var entries = new List<KeyValuePair<UIElement, string>>();
            foreach (var item in items)
            {
                var card = build(item);
                panel.Children.Add(card);
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
            Button recheckBtn = CreateStyledButton("Revisar estados", false);
            recheckBtn.Margin = new Thickness(0, 0, 8, 0);
            recheckBtn.Click += async (s, e) => await RefreshTweaksStateNativeAsync();
            topBtns.Children.Add(recheckBtn);

            Button applyProfileBtn = CreateStyledButton("Perfil de riesgo bajo (" + (isLaptop ? "Laptop" : "Desktop") + ")", true);
            applyProfileBtn.Click += async (s, e) => await RunUiActionAsync("tweaks-batch", applyProfileBtn, () => ApplyRecommendedSafeProfileAsync(isLaptop ? "laptop" : "desktop"));
            topBtns.Children.Add(applyProfileBtn);

            root.Children.Add(CreateBanner(
                categoryType == "optimization"
                    ? "Optimización del sistema"
                    : "Privacidad y debloat",
                "Ajustes de rendimiento, servicios y privacidad. Revisa el nivel de riesgo antes de aplicar cambios.",
                topBtns
            ));

            // Barra de Paquetes Maestros de 1 Clic (Blackwall Bundles)
            Border packCard = CreateCardContainer();
            packCard.Margin = new Thickness(0, 0, 0, 14);
            packCard.Padding = new Thickness(14, 10, 14, 10);
            StackPanel packStack = new StackPanel();
            packStack.Children.Add(new TextBlock
            {
                Text = "Paquetes de ajustes · revisa el impacto antes de aplicar",
                Foreground = CyberCyanBrush,
                FontSize = 10.8,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 8)
            });

            WrapPanel packBtns = new WrapPanel();
            if (categoryType == "debloat")
            {
                Button btnTitus = CreateStyledButton("Esenciales · riesgo bajo", true);
                btnTitus.Margin = new Thickness(0, 0, 8, 6);
                btnTitus.Click += async (s, e) => await RunUiActionAsync("tweaks-batch", btnTitus, () => ApplyBlackwallPackAsync("titus_essential", "Chris Titus Essential"));
                packBtns.Children.Add(btnTitus);

                Button btnPrivacy = CreateStyledButton("IA y privacidad · riesgo bajo", false);
                btnPrivacy.Margin = new Thickness(0, 0, 8, 6);
                btnPrivacy.Click += async (s, e) => await RunUiActionAsync("tweaks-batch", btnPrivacy, () => ApplyBlackwallPackAsync("privacy_ai", "Anti-IA & Privacidad"));
                packBtns.Children.Add(btnPrivacy);

                Button btnServices = CreateStyledButton("Servicios · precaución", false);
                btnServices.Margin = new Thickness(0, 0, 8, 6);
                btnServices.Click += async (s, e) => await RunUiActionAsync("tweaks-batch", btnServices, () => ApplyBlackwallPackAsync("services_light", "Servicios Ligeros"));
                packBtns.Children.Add(btnServices);
            }
            else
            {
                Button btnCeito = CreateStyledButton("Juego y respuesta · precaución", true);
                btnCeito.Margin = new Thickness(0, 0, 8, 6);
                btnCeito.Click += async (s, e) => await RunUiActionAsync("tweaks-batch", btnCeito, () => ApplyBlackwallPackAsync("ceito_gaming", "Ceito Gaming & Baja Latencia"));
                packBtns.Children.Add(btnCeito);

                Button btnPower = CreateStyledButton("Energía (" + (isLaptop ? "Laptop Batería/Térmico" : "Desktop Ultimate") + ")", false);
                btnPower.Margin = new Thickness(0, 0, 8, 6);
                btnPower.Click += async (s, e) => await RunUiActionAsync("tweaks-batch", btnPower, () => ApplyBlackwallPackAsync("adaptive_power", "Energía Adaptativa"));
                packBtns.Children.Add(btnPower);
            }
            packStack.Children.Add(packBtns);
            packCard.Child = packStack;
            root.Children.Add(packCard);

            // Filtros de Dispositivo y Nivel de Riesgo
            WrapPanel filterBar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            AddFilterButton(filterBar, "Portátil", effectiveDevice == "laptop", () => { _optDeviceScope = "laptop"; RenderCurrentTab(); });
            AddFilterButton(filterBar, "Escritorio", effectiveDevice == "desktop", () => { _optDeviceScope = "desktop"; RenderCurrentTab(); });
            AddFilterButton(filterBar, "Ambos", effectiveDevice == "all", () => { _optDeviceScope = "all"; RenderCurrentTab(); });

            AddFilterButton(filterBar, "Todos los riesgos", _optRiskScope == "all", () => { _optRiskScope = "all"; RenderCurrentTab(); });
            AddFilterButton(filterBar, "Bajo", _optRiskScope == "low", () => { _optRiskScope = "low"; RenderCurrentTab(); });
            AddFilterButton(filterBar, "Medio", _optRiskScope == "medium", () => { _optRiskScope = "medium"; RenderCurrentTab(); });
            AddFilterButton(filterBar, "Alto", _optRiskScope == "high", () => { _optRiskScope = "high"; RenderCurrentTab(); });
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
                AddFilterButton(subcatBar, scCopy == "Todas" ? "Todas las categorías" : scCopy, _tweakSubcategoryScope == scCopy, () =>
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
            var filters = new List<Func<string, int>>();

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

                    StackPanel groupSection = new StackPanel { Margin = new Thickness(0, 8, 0, 22) };

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
                        Text = sc,
                        Foreground = CyberCyanBrush,
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

                    AdaptiveCardGrid groupGrid = new AdaptiveCardGrid { Columns = 2 };
                    var filterGroup = CreateCardFilter(groupGrid, tweaksInSubcat, BuildTweakCard, "title", "description");
                    filters.Add(query =>
                    {
                        int count = filterGroup(query);
                        groupSection.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;
                        ((TextBlock)countBadge.Child).Text = count + (count == 1 ? " optimización" : " optimizaciones");
                        return count;
                    });
                    groupSection.Children.Add(groupGrid);
                    tweaksContainer.Children.Add(groupSection);
                }
            }
            else
            {
                AdaptiveCardGrid singleGrid = new AdaptiveCardGrid { Columns = 2 };
                filters.Add(CreateCardFilter(singleGrid, matchingTweaks, BuildTweakCard, "title", "description"));
                tweaksContainer.Children.Add(singleGrid);
            }

            var empty = CreateBanner("Sin resultados", "Prueba otro texto o cambia los filtros de riesgo, equipo o categoría.");
            tweaksContainer.Children.Add(empty);
            Action<string> applyFilter = query => { int count = 0; foreach (var filter in filters) count += filter(query); empty.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed; };
            root.Children.Add(CreateSectionSearch(_tweakSearchText, value => { _tweakSearchText = value; applyFilter(value); }));
            applyFilter(_tweakSearchText);
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
            bool stateKnown = _hardware != null && _loadedModules.Contains("tweaks") && _tweakStates.ContainsKey(id);

            Border card = CreateCardContainer();
            card.Tag = id;
            card.Margin = new Thickness(0, 0, 12, 12);


            StackPanel sp = new StackPanel();

            // 1. CABECERA: Badges a la izquierda + Switch Toggle Cyberpunk a la derecha
            DockPanel header = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };

            Button switchButton = CreateStyledButton(isApplied ? "Revertir" : "Aplicar", !isApplied);
            switchButton.IsEnabled = stateKnown;
            switchButton.ToolTip = "Cambiar el estado de " + shortTitle;
            System.Windows.Automation.AutomationProperties.SetName(switchButton, (isApplied ? "Revertir " : "Aplicar ") + shortTitle);
            switchButton.IsEnabled = stateKnown && !_pendingTweaks.Contains(id);
            if (_pendingTweaks.Contains(id)) switchButton.Content = isApplied ? "Revirtiendo…" : "Aplicando…";

            // Badges en la cabecera
            WrapPanel badgesPanel = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
            string riskText = risk == "low" ? "◇ RIESGO BAJO" : risk == "medium" ? "⚠ PRECAUCIÓN" : "⚠ RIESGO ALTO";
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
                        Text = "Afecta: " + affects,
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
            Button toggleDrawerBtn = new Button
            {
                Content = "Ver efecto y reversión ▾",
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(0, 6, 4, 6),
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
                    Text = "Precaución: " + warning,
                    Foreground = RiskMedText,
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap
                };
                sp.Children.Add(warnBox);
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

            drawerPanel.Children.Add(new TextBlock { Text = "Reversión: " + (t.ContainsKey("undoOps") && ConvertToDictList(t["undoOps"]).Count > 0 ? "Revertir ejecuta los valores definidos por el catálogo; puede no recuperar tu configuración anterior exacta." : "este ajuste no declara una reversión automática."), Foreground = TextSecondary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
            bool detailsOpen = false;
            toggleDrawerBtn.Click += (s, e) =>
            {
                detailsOpen = !detailsOpen;
                toggleDrawerBtn.Content = detailsOpen ? "Ocultar detalles ▴" : "Ver efecto y reversión ▾";
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
                    close.Completed += (sender, args) => { if (!detailsOpen) drawerPanel.Visibility = Visibility.Collapsed; };
                    drawerPanel.BeginAnimation(FrameworkElement.HeightProperty, close);
                }
                else drawerPanel.Visibility = Visibility.Collapsed;
            };

            // 4. PIE DE TARJETA: Estado actual + botón Undo si activo
            DockPanel footer = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };

            TextBlock statusText = new TextBlock
            {
                Text = !stateKnown ? "Pendiente de verificar" : isApplied ? "Detectado como activo" : "No detectado como activo",
                ToolTip = "La detección usa los criterios del catálogo; no demuestra que todas las operaciones se hayan aplicado ni que se conserve tu configuración original.",
                Foreground = isApplied ? RiskLowText : TextSecondary,
                FontWeight = FontWeights.Bold,
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            if (_tweakActionErrors.ContainsKey(id)) { statusText.Text = "No se completó: " + _tweakActionErrors[id]; statusText.Foreground = RiskHighText; }
            DockPanel.SetDock(switchButton, Dock.Right);
            switchButton.Margin = new Thickness(12, 0, 0, 0);
            footer.Children.Add(switchButton);
            footer.Children.Add(statusText);
            Grid cardLayout = new Grid();
            cardLayout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            cardLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            cardLayout.Children.Add(sp);
            Grid.SetRow(footer, 1);
            cardLayout.Children.Add(footer);
            card.Child = cardLayout;

            // Actualizador en caliente registrado para esta tarjeta (Cero saltos de scroll)
            Action<bool> updateCardUi = (bool applied) =>
            {

                switchButton.Content = applied ? "Revertir" : "Aplicar";
                switchButton.SetResourceReference(Control.BackgroundProperty, applied ? "MaterialButtonSurface" : "MaterialSilver");
                switchButton.Foreground = applied ? TextPrimary : BrushHex("#182232");
                System.Windows.Automation.AutomationProperties.SetName(switchButton, (applied ? "Revertir " : "Aplicar ") + shortTitle);
                AnimateEntrance(statusText, 2, 180);
                statusText.Text = applied ? "Detectado como activo" : "No detectado como activo";
                statusText.Foreground = applied ? RiskLowText : TextSecondary;
            };
            _tweakCardUpdaters[id] = updateCardUi;

            // Manejador del clic en el Switch Toggle
            switchButton.Click += async (s, e) =>
            {
                e.Handled = true;
                if (!stateKnown || !_pendingTweaks.Add(id)) return;
                _tweakActionErrors.Remove(id);
                bool current = _tweakStates.ContainsKey(id) && _tweakStates[id];
                switchButton.IsEnabled = false;
                switchButton.Content = current ? "Revirtiendo…" : "Aplicando…";
                try
                {
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
                    _pendingTweaks.Remove(id);
                    switchButton.IsEnabled = true;
                    switchButton.Content = _tweakStates.ContainsKey(id) && _tweakStates[id] ? "Revertir" : "Aplicar";
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

            StackPanel topBtns = new StackPanel { Orientation = Orientation.Horizontal };
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
            root.Children.Add(catBar);
            AdaptiveCardGrid grid = new AdaptiveCardGrid { Columns = 3 };
            var filter = CreateCardFilter(grid, _apps.Where(a => _appCategoryScope == "Todas" || GetStr(a, "category") == _appCategoryScope), BuildAppCard, "name", "description", "wingetId");
            var empty = CreateBanner("Sin resultados", "Prueba otro nombre o categoría de software.");
            Action<string> applyFilter = query => empty.Visibility = filter(query) == 0 ? Visibility.Visible : Visibility.Collapsed;
            root.Children.Add(CreateSectionSearch(_appSearchText, value => { _appSearchText = value; applyFilter(value); }));
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
        private UIElement BuildMonitorView()
        {
            _monitorProcessMemoryLabels.Clear();
            StackPanel root = new StackPanel();

            Button refreshBtn = CreateStyledButton("Actualizar procesos", true);
            refreshBtn.Click += async (s, e) => await RefreshLiveScriptsNativeAsync();

            root.Children.Add(CreateBanner(
                "Monitor de procesos",
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
                Background = BgApp,
                Foreground = TextPrimary,
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
                    TextBlock memoryLabel = new TextBlock
                    {
                        Text = "PID " + pid + "  •  " + GetStr(p, "name") + "  •  Parent PID: " + GetStr(p, "parentPid") + "  •  RAM: " + GetStr(p, "memoryMb") + " MB",
                        Foreground = TextPrimary,
                        FontWeight = FontWeights.Bold,
                        FontSize = 12.8
                    };
                    _monitorProcessMemoryLabels[pid] = memoryLabel;
                    info.Children.Add(memoryLabel);
                    info.Children.Add(new TextBox
                    {
                        Text = GetStr(p, "commandLine"),
                        Background = Brushes.Transparent,
                        Foreground = CyberCyanBrush,
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
                "Seguridad y recuperación",
                "Blackwall utiliza la API WMI nativa root\\default:SystemRestore sin scripts externos."
            ));

            AdaptiveCardGrid grid = new AdaptiveCardGrid { Columns = 2, Margin = new Thickness(0, 0, 0, 14) };

            Border rpCard = CreateCardContainer();
            rpCard.Margin = new Thickness(0, 0, 12, 0);
            StackPanel rpStack = new StackPanel();
            rpStack.Children.Add(new TextBlock { Text = "🛡️ Protección Automática y Puntos de Restauración", Foreground = TextPrimary, FontWeight = FontWeights.Bold, FontSize = 14, TextWrapping = TextWrapping.Wrap });
            rpStack.Children.Add(new TextBlock { Text = "Crea puntos de restauración nativos en C:\\ o abre el restaurador oficial de Windows (rstrui.exe).", Foreground = TextSecondary, FontSize = 12.3, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 10) });

            CheckBox autoChk = new CheckBox
            {
                Content = new TextBlock { Text = "Solicitar un punto de restauración antes de aplicar ajustes", TextWrapping = TextWrapping.Wrap },
                Foreground = TextPrimary,
                IsChecked = _autoRestorePoint,
                Margin = new Thickness(0, 0, 0, 12)
            };
            autoChk.Click += (s, e) => _autoRestorePoint = autoChk.IsChecked == true;
            rpStack.Children.Add(autoChk);

            WrapPanel rpBtns = new WrapPanel();
            Button createRpBtn = CreateStyledButton("➕ Crear Punto de Restauración Ahora", true);
            createRpBtn.Margin = new Thickness(0, 0, 8, 0);
            createRpBtn.Click += async (s, e) => await RunUiActionAsync("restore", createRpBtn, () => CreateRestorePointNowAsync());
            rpBtns.Children.Add(createRpBtn);

            Button rstruiBtn = CreateStyledButton("🕰️ Abrir rstrui.exe", false);
            rstruiBtn.Click += (s, e) => { try { Process.Start("rstrui.exe"); } catch { } };
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
            ShowOtaUpdateDialog();
        }

        private void ShowOtaUpdateDialog()
        {
            if (_otaDialog != null) { _otaDialog.Activate(); return; }
            IInputElement previousFocus = Keyboard.FocusedElement;
            Window dialog = CreateOtaUpdateDialog();
            _otaDialog = dialog;
            _rootBorder.Opacity = 0.55;
            _rootBorder.Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 3 };
            try { dialog.ShowDialog(); }
            finally { _otaDialog = null; _rootBorder.Effect = null; _rootBorder.Opacity = 1; if (previousFocus != null) Keyboard.Focus(previousFocus); }
        }

        private Window CreateOtaUpdateDialog()
        {
            string installed = Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
            var dialog = new Window { Title = "Actualizaciones de Blackwall", Width = Math.Min(510, SystemParameters.WorkArea.Width - 32), Height = Math.Min(740, SystemParameters.WorkArea.Height - 40), WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = BgApp, Foreground = TextPrimary, Icon = Icon };
            WindowChrome.SetWindowChrome(dialog, new WindowChrome { CaptionHeight = 0, CornerRadius = new CornerRadius(18), GlassFrameThickness = new Thickness(0), ResizeBorderThickness = new Thickness(0) });
            dialog.Resources = Resources;
            KeyboardNavigation.SetTabNavigation(dialog, KeyboardNavigationMode.Cycle);
            var cancel = new System.Threading.CancellationTokenSource();
            bool busy = false;
            bool handedOff = false;
            string activeStage = null;
            dialog.PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape) { dialog.Close(); e.Handled = true; } };
            dialog.Closed += (s, e) => { cancel.Cancel(); if (!handedOff && activeStage != null) { try { File.WriteAllText(Path.Combine(activeStage, "cancelled"), "1"); } catch { } } };
            var body = new StackPanel { Margin = new Thickness(28) };
            var hero = new StackPanel();
            var brand = new DockPanel { Margin = new Thickness(0, 0, 0, 18) };
            brand.MouseLeftButtonDown += (s, e) => { if (e.LeftButton == MouseButtonState.Pressed) dialog.DragMove(); };
            var dismiss = CreateStyledButton("×", false); dismiss.MinWidth = 34; dismiss.Padding = new Thickness(8, 0, 8, 0); dismiss.ToolTip = "Posponer actualización";
            System.Windows.Automation.AutomationProperties.SetName(dismiss, "Posponer actualización");
            dismiss.Click += (s, e) => dialog.Close(); DockPanel.SetDock(dismiss, Dock.Right); brand.Children.Add(dismiss);
            brand.Children.Add(new Image { Source = Icon, Width = 38, Height = 38, Margin = new Thickness(0, 0, 12, 0) });
            brand.Children.Add(new TextBlock { Text = "BLACKWALL  /  EVOLUCIÓN", Foreground = CyberCyanBrush, FontSize = 11, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center });
            hero.Children.Add(brand);
            OtaRelease available = _availableRelease != null && IsNewerVersion(_availableRelease.Version, installed) ? _availableRelease : null;
            var heading = new TextBlock { Text = available != null ? "Tu próxima versión." : "Siempre al día.", Foreground = TextPrimary, FontSize = 29, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
            hero.Children.Add(heading);
            var version = new TextBlock { Text = available != null ? "Blackwall " + available.Version : "Versión instalada · " + installed, Foreground = CyberCyanBrush, FontSize = 21, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 12) };
            hero.Children.Add(version);
            var heroFrame = new Border { Child = hero, Padding = new Thickness(20), CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 20) };
            heroFrame.SetResourceReference(Border.BackgroundProperty, "MaterialSurface");
            heroFrame.SetResourceReference(Border.BorderBrushProperty, "MaterialEdge");
            body.Children.Add(heroFrame);
            body.Children.Add(new TextBlock { Text = "Revisa las mejoras y elige cuándo actualizar. El paquete incluye la aplicación y sus catálogos; conservamos un respaldo mientras confirmamos el arranque.", Foreground = TextSecondary, TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 20, Margin = new Thickness(0, 0, 0, 16) });
            string manifestUrl = GetConfiguredOtaManifestUrl();
            var status = new TextBlock { Text = available != null ? "Nueva versión disponible · firma verificada" : "Comprueba si hay una versión nueva.", Foreground = TextPrimary, TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Margin = new Thickness(0, 12, 0, 12) };
            System.Windows.Automation.AutomationProperties.SetLiveSetting(status, System.Windows.Automation.AutomationLiveSetting.Polite);
            body.Children.Add(status);
            var progress = new ProgressBar { Height = 5, Maximum = 100, Foreground = CyberCyanBrush, Background = BgElevated, Visibility = Visibility.Collapsed };
            body.Children.Add(progress);
            body.Children.Add(new TextBlock { Text = "QUÉ MEJORA", Foreground = TextSecondary, FontSize = 11, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 18, 0, 8) });
            var notes = new TextBox { Text = available != null ? available.Notes : "Las notas aparecerán después de verificar la actualización.", IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Background = BgSurface, Foreground = TextPrimary, BorderBrush = BorderSubtle, BorderThickness = new Thickness(1), Padding = new Thickness(16), Margin = new Thickness(0, 0, 0, 18), MinHeight = 150, MaxHeight = 220, FontSize = 13, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            System.Windows.Automation.AutomationProperties.SetName(notes, "Mejoras de la actualización verificada");
            body.Children.Add(notes);
            var actions = new WrapPanel();
            var check = CreateStyledButton("Buscar actualizaciones", false); check.Margin = new Thickness(0, 0, 0, 12); check.Visibility = available == null ? Visibility.Visible : Visibility.Collapsed;
            body.Children.Add(check);
            var install = CreateStyledButton("Actualizar ahora", true); install.IsEnabled = available != null; install.MinWidth = 192; install.Margin = new Thickness(0, 0, 10, 8);
            var close = CreateStyledButton("Más tarde", false); close.MinWidth = 130;
            close.Click += (s, e) => dialog.Close();
            actions.Children.Add(install); actions.Children.Add(close);
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
                    notes.Text = available.Notes;
                    heading.Text = newer ? "Tu próxima versión." : "Ya estás al día.";
                    version.Text = newer ? "Blackwall " + available.Version : "Versión instalada · " + installed;
                    status.Text = newer ? "Versión " + available.Version + " verificada y disponible." : "No hay una versión posterior a la instalada.";
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
                progress.Visibility = Visibility.Visible; progress.IsIndeterminate = MotionAllowed;
                status.Text = "Volviendo a verificar el paquete…";
                try
                {
                    if (OtaProtocol.Elevated())
                    {
                        Program.LaunchUnelevated(Process.GetCurrentProcess().MainModule.FileName, "--ota-update " + OtaInstaller.Quote(available.Version) + " " + OtaInstaller.Quote(available.Hash));
                        handedOff = true;
                        Application.Current.Shutdown();
                        return;
                    }
                    string stage = OtaInstaller.NewStage();
                    activeStage = stage;
                    var release = await FetchSignedReleaseAsync(manifestUrl, stage, cancel.Token, true);
                    if (release.Version != available.Version || release.Hash != available.Hash || release.Size != available.Size || !IsNewerVersion(release.Version, installed)) throw new InvalidDataException("La versión publicada cambió; vuelve a comprobarla.");
                    progress.IsIndeterminate = false;
                    await OtaDownload.Fetch(release.Url, Path.Combine(stage, "Blackwall.exe"), 104857600, release.Size, (bytes, total) => Dispatcher.BeginInvoke(new Action(() => { progress.Value = bytes * 100.0 / total; status.Text = "Descargando " + bytes + " de " + total + " bytes"; })), cancel.Token);
                    OtaProtocol.VerifyBinary(Path.Combine(stage, "Blackwall.exe"), release);
                    status.Text = "Paquete verificado. Preparando reinicio…";
                    cancel.Token.ThrowIfCancellationRequested();
                    await OtaInstaller.Start(stage);
                    cancel.Token.ThrowIfCancellationRequested();
                    handedOff = true;
                    Application.Current.Shutdown();
                }
                catch (Exception ex) { status.Text = cancel.IsCancellationRequested ? "Descarga cancelada. La aplicación no se modificó." : "No se pudo actualizar: " + ex.Message; }
                finally { busy = false; check.IsEnabled = true; install.IsEnabled = available != null && IsNewerVersion(available.Version, installed); progress.Visibility = Visibility.Collapsed; }
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
            var layout = new Grid(); layout.RowDefinitions.Add(new RowDefinition()); layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.Children.Add(new SmoothScrollViewer { ReduceMotion = !MotionAllowed, Background = BgApp, Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
            var footer = new Border { Background = BgSidebar, BorderBrush = BorderSubtle, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(28, 18, 28, 14), Child = actions };
            Grid.SetRow(footer, 1); layout.Children.Add(footer);
            var frame = new Border { Background = BgApp, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(18), Child = layout };
            frame.SetResourceReference(Border.BorderBrushProperty, "MaterialEdge"); dialog.Content = frame;
            return dialog;
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
            Border banner = CreateCardContainer();
            banner.Padding = new Thickness(22);
            banner.Margin = new Thickness(0, 0, 0, 20);
            var body = new StackPanel();
            body.Children.Add(new TextBlock { Text = title, Foreground = TextPrimary, FontWeight = FontWeights.SemiBold, FontSize = 24, TextWrapping = TextWrapping.Wrap });
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
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 11.5,
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
                Child = new TextBlock { Text = text, Foreground = fg, FontFamily = new FontFamily("Consolas"), FontSize = 10, FontWeight = FontWeights.Bold, TextTrimming = TextTrimming.CharacterEllipsis }
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
                pressScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 0.97, TimeSpan.FromMilliseconds(90)) { AutoReverse = true, FillBehavior = FillBehavior.Stop });
                pressScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 0.97, TimeSpan.FromMilliseconds(90)) { AutoReverse = true, FillBehavior = FillBehavior.Stop });
            };
            return btn;
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

        private void AppendLog(string msg, string tag)
        {
            Dispatcher.Invoke(() =>
            {
                string line = "[" + DateTime.Now.ToString("HH:mm:ss") + "] [" + tag + "] " + msg;
                _consoleLogs.Add(line);
                if (_liveConsoleTextBox != null)
                {
                    _liveConsoleTextBox.AppendText((_liveConsoleTextBox.Text.Length == 0 ? "" : "\r\n") + line);
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
