using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

// Runs without showing a window, invoking Program.Main, or reading/modifying system settings.
internal static class NativeUiSelfCheck
{
    private static Type _type;
    private static Window _window;
    private static int _checks;
    private static int _screenshots;
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length < 2 || args.Length > 3 || (args.Length == 3 && args[2] != "--scan"))
        {
            Console.Error.WriteLine("Usage: NativeUiSelfCheck.exe <Blackwall.preview.exe> <screenshot-directory> [--scan]");
            return 2;
        }

        Application app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try
        {
            Stopwatch shellWatch = Stopwatch.StartNew();
            _type = Assembly.LoadFrom(Path.GetFullPath(args[0])).GetType("Blackwall.MainWindow", true);
            string preferences = Path.Combine(Path.GetFullPath(args[1]), "test-ui-preferences.json");
            Directory.CreateDirectory(Path.GetDirectoryName(preferences));
            File.WriteAllText(preferences, "{}");
            _window = (Window)Activator.CreateInstance(_type, new object[] { preferences });
            ((DispatcherTimer)Field("_scriptMonitorTimer")).Stop();
            Check(Reads.Count == 0, "construction does not start module reads");
            Check(Field("_hardware") == null, "construction does not audit hardware");
            Check(Activity.Visibility == Visibility.Collapsed, "no persistent startup overlay");
            Check(_window.WindowState == WindowState.Maximized, "application starts maximized");
            CheckWindowBounds();
            CheckSmoothScrolling();
            CheckCatalogSelection();

            MarkLoaded("hardware");
            Call("RenderCurrentTab");
            Snapshot(args[1], "native-initial.png");
            Console.WriteLine("Shell construction + offscreen render: " + shellWatch.ElapsedMilliseconds + " ms (not a full launch benchmark).");
            SetField("_activeTab", "test");

            int executions = 0;
            TaskCompletionSource<int> source = new TaskCompletionSource<int>();
            Func<Task> read = () => { executions++; return source.Task; };
            Task first = Read("test", "Prueba de carga", read);
            Task second = Read("test", "Prueba de carga", read);
            Check(Object.ReferenceEquals(first, second), "in-flight reads share one task");
            PumpUntil(() => executions == 1);
            Check(executions == 1 && Reads.Count == 1, "duplicate requests execute once");
            Check(Activity.Visibility == Visibility.Visible, "pending read shows activity");
            Check(((Button)Field("_activityRetry")).Visibility == Visibility.Collapsed, "retry is hidden while loading");

            Call("ReportReadProgress", "test", 3, 8, "Prueba de etapas completadas");
            ProgressBar progress = (ProgressBar)Field("_activityProgress");
            Check(progress.Value == 37 && !progress.IsIndeterminate, "progress reflects completed stages");
            Snapshot(args[1], "native-progress.png");
            source.SetResult(0);
            PumpUntil(() => first.IsCompleted);
            Check(Reads.Count == 0, "completed read releases in-flight guard");
            Check((bool)Field("_loadedModules").GetType().GetMethod("Contains").Invoke(Field("_loadedModules"), new object[] { "test" }), "successful module is marked loaded");
            Check(Activity.Visibility == Visibility.Collapsed, "successful read hides activity");

            Task failed = Read("test", "Prueba de error", () => { throw new InvalidOperationException("E_TEST_READ: error simulado para verificar la recuperación"); });
            PumpUntil(() => failed.IsCompleted);
            Check(Errors.Contains("test"), "failed read retains a recoverable error");
            Check(Activity.Visibility == Visibility.Visible, "failed read shows compact error panel");
            Check(((TextBlock)Field("_activityDetail")).Text.Contains("E_TEST_READ"), "error panel identifies the failure");
            Check(((Button)Field("_activityRetry")).Visibility == Visibility.Visible, "retry appears only after failure");
            Check(progress.Visibility == Visibility.Collapsed, "failed read does not show a running progress bar");
            Snapshot(args[1], "native-error.png");

            ((Button)Field("_activityDismiss")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(!Errors.Contains("test") && Activity.Visibility == Visibility.Collapsed, "dismiss clears the visible error");

            failed = Read("test", "Prueba de reintento", () => { throw new InvalidOperationException("E_TEST_RETRY"); });
            PumpUntil(() => failed.IsCompleted);
            Button retry = (Button)Field("_activityRetry");
            Check(Convert.ToString(retry.Tag) == "test", "retry targets the failed module");
            retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(!Errors.Contains("test"), "retry clears the previous failure");
            // Unknown test module is a no-op, so this never invokes a native system read.
            Call("UpdateActivityPanel");
            Check(Activity.Visibility == Visibility.Collapsed, "cleared error leaves no persistent panel");

            // Remove remote icon fallbacks from fixtures so rendering cannot download/cache files.
            List<Dictionary<string, object>> apps = new List<Dictionary<string, object>>();
            foreach (Dictionary<string, object> entry in (IEnumerable)Field("_apps"))
            {
                Dictionary<string, object> fixture = new Dictionary<string, object>(entry);
                fixture["iconUrl"] = "";
                apps.Add(fixture);
            }
            SetField("_apps", apps);
            foreach (string tab in new[] { "hardware", "optimization", "debloat", "apps", "monitor", "safety" })
            {
                SetField("_activeTab", tab);
                MarkLoaded(tab == "optimization" || tab == "debloat" ? "tweaks" : tab);
                Call("UpdateNavSelection");
                Call("RenderCurrentTab");
                Snapshot(args[1], "native-" + tab + ".png");
            }
            SetField("_activeTab", "optimization");
            Call("UpdateNavSelection");
            Call("RenderCurrentTab");
            Snapshot(args[1], "native-optimization-small.png", 1080, 700);
            CheckVisualPreferences(args[1], preferences);
            CheckTweakPresentation(args[1]);
            CheckExoticMotion(args[1]);
            ((Button)Field("_themeBtn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ((Button)Field("_themeBtn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ((Button)Field("_themeBtn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Snapshot(args[1], "native-light-small.png", 1080, 700);
            Check(!(bool)Field("_isDark"), "theme control switches to light mode");
            Check(Reads.Count == 0 && Field("_hardware") == null, "offscreen rendering never starts system reads");
            if (args.Length == 3)
            {
                SetField("_activeTab", "hardware");
                Call("UpdateNavSelection");
                Stopwatch scanWatch = Stopwatch.StartNew();
                Task scan = (Task)Call("RefreshHardwareNativeAsync");
                PumpUntil(() => scan.IsCompleted, 60);
                Check(!Reads.Contains("hardware"), "real read releases its in-flight guard");
                Check(Field("_hardware") != null || Errors.Contains("hardware"), "real read produces data or a recoverable error");
                Console.WriteLine("Read-only hardware scan: " + scanWatch.ElapsedMilliseconds + " ms; " + (Errors.Contains("hardware") ? "recoverable failure: " + Errors["hardware"] : "report available"));
                if (Field("_hardware") != null)
                {
                    ((Button)Field("_themeBtn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Call("RenderCurrentTab");
                    Snapshot(args[1], "native-hardware.png");
                    Snapshot(args[1], "native-hardware-live.png");
                    ScrollViewer scroll = (ScrollViewer)Field("_mainScroll");
                    int page = 2;
                    while (scroll.VerticalOffset + scroll.ViewportHeight < scroll.ExtentHeight - 1)
                    {
                        double before = scroll.VerticalOffset;
                        scroll.ScrollToVerticalOffset(before + scroll.ViewportHeight - 60);
                        scroll.UpdateLayout();
                        Snapshot(args[1], "native-hardware-live-" + page++ + ".png");
                        if (scroll.VerticalOffset <= before) break;
                    }
                }
            }
            Console.WriteLine("PASS: " + _checks + " native UI checks; " + _screenshots + " offscreen screenshots.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL: " + ex);
            return 1;
        }
        finally
        {
            if (_window != null) _window.Close();
            app.Shutdown();
        }
    }

    private static void CheckExoticMotion(string directory)
    {
        if (!SystemParameters.ClientAreaAnimation) return;
        SetField("_reduceMotion", false);
        Call("UpdateMotionPreference");
        FrameworkElement content = (FrameworkElement)_window.Content;
        _window.Content = null;
        using (var host = new HwndSource(new HwndSourceParameters("Blackwall animation self-check") { Width = 1080, Height = 700, WindowStyle = unchecked((int)0x80000000) }))
        {
            host.RootVisual = content;
            content.Measure(new Size(1080, 700));
            content.Arrange(new Rect(0, 0, 1080, 700));
            content.UpdateLayout();
            Button testButton = (Button)Field("_themeBtn");
            testButton.Style = (Style)_window.Resources[typeof(Button)];
            testButton.ApplyTemplate();
            Call("OnMotionButtonLoaded", testButton, new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Call("ToggleSidebar");
            PumpUntil(() => { double width = ((ColumnDefinition)Field("_sidebarColumn")).Width.Value; return width > 73 && width < 247; });
            double intermediate = ((ColumnDefinition)Field("_sidebarColumn")).Width.Value;
            Check(intermediate > 72 && intermediate < 248, "sidebar width traverses intermediate values");
            Call("ToggleSidebar");
            PumpUntil(() => Math.Abs(((ColumnDefinition)Field("_sidebarColumn")).Width.Value - 248) < 0.01);
            Check(true, "rapid reversal settles at the most recent sidebar target");
            var scroll = (ScrollViewer)Field("_mainScroll");
            var page = (FrameworkElement)scroll.Content;
            Call("AnimatePage", page);
            var animated = (IList)Field("_motionEntrances");
            Check(animated.Count > 1, "page entrance animates multiple shared cards");
            var first = (FrameworkElement)animated[0];
            var transform = (TransformGroup)first.RenderTransform;
            var translate = (TranslateTransform)transform.Children[1];
            PumpUntil(() => translate.Y > 0.1 && translate.Y < 29);
            Check(first.Opacity > 0 && first.Opacity < 1, "card entrance passes through intermediate opacity and translation");
            Console.WriteLine("Motion sample: sidebar=" + intermediate.ToString("F1") + " DIP; cardY=" + translate.Y.ToString("F1") + "; opacity=" + first.Opacity.ToString("F2"));
            Call("PlayButtonSheen", (Button)Field("_themeBtn"));
            Button button = (Button)Field("_themeBtn");
            var sheen = (Border)button.Template.FindName("Sheen", button);
            Check(sheen.Background is LinearGradientBrush, "button reflection uses an animated gradient");
            object energy = Field("_headerEnergy");
            energy.GetType().GetMethod("SetRunning").Invoke(energy, new object[] { true });
            Check((bool)energy.GetType().GetProperty("Running").GetValue(energy, null), "electric trace runs on demand");
            SetField("_reduceMotion", true);
            Call("UpdateMotionPreference");
            Check(animated.Count == 0 && !first.HasAnimatedProperties && first.RenderTransform == Transform.Identity, "reduced motion removes in-flight card animations");
            Check(!(bool)energy.GetType().GetProperty("Running").GetValue(energy, null), "reduced motion stops the electric trace timer");
            Check(sheen.Background == null, "reduced motion clears active button reflection");
            host.RootVisual = null;
        }
        _window.Content = content;
        Snapshot(directory, "native-motion-rest.png", 1080, 700);
        SetField("_reduceMotion", false);
        Call("UpdateMotionPreference");
    }

    private static void CheckVisualPreferences(string directory, string preferences)
    {
        ScrollViewer scroll = (ScrollViewer)Field("_mainScroll");
        scroll.ScrollToVerticalOffset(100);
        scroll.UpdateLayout();
        double offset = scroll.VerticalOffset;
        object page = scroll.Content;
        SetField("_optRiskScope", "medium");
        ((HashSet<string>)Field("_selectedApps")).Add("fixture-selection");
        Call("ToggleSidebar");
        scroll.UpdateLayout();
        Check(((ColumnDefinition)Field("_sidebarColumn")).Width.Value == 72, "collapsed menu is 72 DIP");
        Check(Object.ReferenceEquals(page, scroll.Content) && scroll.VerticalOffset == offset, "menu toggle preserves page and scroll");
        Check((string)Field("_activeTab") == "optimization" && (string)Field("_optRiskScope") == "medium" && ((HashSet<string>)Field("_selectedApps")).Contains("fixture-selection"), "menu toggle preserves navigation, filters and app selection");
        Snapshot(directory, "native-compact-small.png", 1080, 700);
        ((Button)Field("_motionToggle")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check((bool)Field("_reduceMotion"), "motion preference toggles");
        Check((bool)scroll.GetType().GetProperty("ReduceMotion").GetValue(scroll, null), "reduced motion reaches smooth scroll");
        Window restored = (Window)Activator.CreateInstance(_type, new object[] { preferences });
        try
        {
            ((DispatcherTimer)_type.GetField("_scriptMonitorTimer", Private).GetValue(restored)).Stop();
            Check((bool)_type.GetField("_sidebarCollapsed", Private).GetValue(restored) && (bool)_type.GetField("_reduceMotion", Private).GetValue(restored), "menu and motion preferences survive restart");
            Check(restored.Icon != null, "generated icon is embedded in the window");
        }
        finally { restored.Close(); }
        Call("ToggleSidebar");
        Check(((ColumnDefinition)Field("_sidebarColumn")).Width.Value == 248, "expanded menu restores 248 DIP");
        SetField("_optRiskScope", "all");
        ((HashSet<string>)Field("_selectedApps")).Remove("fixture-selection");
        ((Button)Field("_motionToggle")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        File.WriteAllText(preferences, "not json");
        Window corrupt = (Window)Activator.CreateInstance(_type, new object[] { preferences });
        try { Check(!(bool)_type.GetField("_sidebarCollapsed", Private).GetValue(corrupt), "corrupt preferences use safe defaults"); }
        finally { corrupt.Close(); }
    }

    private static void CheckTweakPresentation(string directory)
    {
        var fixture = new Dictionary<string, object> { { "id", "ui-only" }, { "title", "Título técnico largo" }, { "shortTitle", "Ajuste de prueba" }, { "summary", "Resumen breve" }, { "description", "Explicación completa" }, { "riskLevel", "high" }, { "riskWarning", "Consecuencia visible antes de actuar" } };
        Border card = (Border)Call("BuildTweakCard", fixture);
        StackPanel body = (StackPanel)card.Child;
        DockPanel header = (DockPanel)body.Children[0];
        Check(!((Button)header.Children[0]).IsEnabled, "unknown tweak state disables the action");
        Button disclosure = null;
        StackPanel details = null;
        bool warningVisible = false;
        foreach (UIElement child in body.Children)
        {
            if (child is Button) disclosure = (Button)child;
            if (child is StackPanel) details = (StackPanel)child;
            Border border = child as Border;
            if (border != null && border.Child is TextBlock && ((TextBlock)border.Child).Text.Contains("Consecuencia visible")) warningVisible = border.Visibility == Visibility.Visible;
        }
        Check(warningVisible && details.Visibility == Visibility.Collapsed, "risk warning is outside the closed disclosure");
        SetField("_reduceMotion", true);
        disclosure.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(details.Visibility == Visibility.Visible && !details.HasAnimatedProperties, "keyboard-compatible disclosure respects reduced motion");
        ScrollViewer scroll = (ScrollViewer)Field("_mainScroll");
        object previous = scroll.Content;
        scroll.Content = card;
        Snapshot(directory, "native-risk-details.png", 1080, 700);
        disclosure.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(details.Visibility == Visibility.Collapsed, "disclosure closes without leaving hidden space");
        scroll.Content = previous;
        SetField("_reduceMotion", false);
        Call("UpdateMotionPreference");
    }

    private static void CheckCatalogSelection()
    {
        string embedded = "{\"lastUpdated\":\"2026-10-24T00:00:00Z\",\"tweaks\":[]}";
        string old = "{\"lastUpdated\":\"2026-10-23T00:00:00Z\",\"tweaks\":[]}";
        string newer = "{\"lastUpdated\":\"2026-10-25T00:00:00Z\",\"tweaks\":[]}";
        Check((string)Call("SelectCatalog", embedded, old) == embedded, "stale OTA cache cannot override shipped short titles and catalog");
        Check((string)Call("SelectCatalog", embedded, newer) == newer, "newer OTA catalog remains usable");
        Check((string)Call("SelectCatalog", embedded, "invalid") == embedded, "invalid cache falls back to embedded catalog");
        Check((string)Call("SelectCatalog", embedded, "{\"lastUpdated\":\"2026-10-25\"}") == embedded, "cache without catalog entries is rejected");
        Check(((ICollection)Field("_tweaks")).Count >= 45, "current catalog includes concise presentation fields");
    }

    private static void CheckWindowBounds()
    {
        MethodInfo calculate = _type.GetMethod("CalculateMaximizedBounds", BindingFlags.Static | BindingFlags.NonPublic);
        Rect monitor = new Rect(0, 0, 1920, 1080);
        Check((Rect)calculate.Invoke(null, new object[] { monitor, new Rect(0, 0, 1920, 1040) }) == new Rect(0, 0, 1920, 1040), "bottom taskbar is outside maximized bounds");
        Check((Rect)calculate.Invoke(null, new object[] { monitor, new Rect(0, 40, 1920, 1040) }) == new Rect(0, 40, 1920, 1040), "top taskbar is outside maximized bounds");
        Check((Rect)calculate.Invoke(null, new object[] { new Rect(-1920, 0, 1920, 1080), new Rect(-1880, 0, 1880, 1080) }) == new Rect(40, 0, 1880, 1080), "left taskbar works on negative-coordinate monitor");
        Check((Rect)calculate.Invoke(null, new object[] { monitor, new Rect(0, 0, 1880, 1080) }) == new Rect(0, 0, 1880, 1080), "right taskbar is outside maximized bounds");
        Check((Rect)calculate.Invoke(null, new object[] { new Rect(0, -2160, 3840, 2160), new Rect(0, -2160, 3840, 2100) }) == new Rect(0, 0, 3840, 2100), "high-DPI bounds retain native pixel dimensions");
        IntPtr pointer = Marshal.AllocHGlobal(40);
        try
        {
            int[] native = new[] { 0, 0, 0, 0, 0, 0, 777, 555, 5000, 4000 };
            Marshal.Copy(native, 0, pointer, native.Length);
            object[] message = { IntPtr.Zero, 0x0024, IntPtr.Zero, pointer, false };
            _type.GetMethod("WindowBoundsHook", Private).Invoke(_window, message);
            Marshal.Copy(pointer, native, 0, native.Length);
            Check((bool)message[4] && native[2] > 0 && native[3] > 0, "native maximize message reads actual monitor work area");
            Check(native[6] == 777 && native[7] == 555 && native[8] == 5000 && native[9] == 4000, "maximize hook preserves resize tracking limits");
        }
        finally { Marshal.FreeHGlobal(pointer); }
    }

    private static void CheckSmoothScrolling()
    {
        Type smoothType = _type.Assembly.GetType("Blackwall.SmoothScrollViewer", true);
        Check(Field("_mainScroll").GetType() == smoothType, "all section content uses the smooth scroll viewer");
        ScrollViewer scroll = (ScrollViewer)Activator.CreateInstance(smoothType);
        using (HwndSource host = new HwndSource(new HwndSourceParameters("Blackwall scroll self-check") { Width = 600, Height = 300, WindowStyle = unchecked((int)0x80000000) }))
        {
        host.RootVisual = scroll;
        scroll.Content = new Border { Height = 4000, Width = 400 };
        scroll.Measure(new Size(600, 300));
        scroll.Arrange(new Rect(0, 0, 600, 300));
        scroll.UpdateLayout();
        Check(scroll.ScrollableHeight > 0, "scroll fixture has content beyond the viewport");
        MethodInfo wheel = smoothType.GetMethod("OnMouseWheel", Private);
        Func<int, MouseWheelEventArgs> input = delta => new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = Mouse.MouseWheelEvent };
        MouseWheelEventArgs down = input(-120);
        wheel.Invoke(scroll, new object[] { down });
        scroll.UpdateLayout();
        Check(down.Handled, "wheel input is handled by the scroll viewer");
        if (SystemParameters.ClientAreaAnimation && SystemParameters.WheelScrollLines != 0)
        {
            double firstTarget = (double)smoothType.GetField("_targetOffset", Private).GetValue(scroll);
            Check(scroll.HasAnimatedProperties && firstTarget > 0, "wheel scroll uses an animation instead of an instant jump");
            PumpUntil(() => { scroll.UpdateLayout(); return scroll.VerticalOffset > 0; });
            Check(scroll.VerticalOffset < firstTarget, "scroll passes through intermediate offsets before reaching its target");
            PumpUntil(() => !(bool)smoothType.GetField("_scrolling", Private).GetValue(scroll));
            scroll.UpdateLayout();
            Check(Math.Abs(scroll.VerticalOffset - firstTarget) < 0.5, "scroll settles at the requested offset without jumping back");
            wheel.Invoke(scroll, new object[] { input(-120) });
            double nextTarget = (double)smoothType.GetField("_targetOffset", Private).GetValue(scroll);
            Check(nextTarget >= firstTarget, "successive wheel inputs accumulate smoothly");
            wheel.Invoke(scroll, new object[] { input(120) });
            Check((double)smoothType.GetField("_targetOffset", Private).GetValue(scroll) <= scroll.VerticalOffset, "reversing direction responds from the current offset");
            wheel.Invoke(scroll, new object[] { input(-120000) });
            Check((double)smoothType.GetField("_targetOffset", Private).GetValue(scroll) == scroll.ScrollableHeight, "scroll target cannot exceed content bounds");
            smoothType.GetMethod("StopSmoothScroll", Private).Invoke(scroll, null);
            Check(!scroll.HasAnimatedProperties, "direct scrollbar or keyboard input can stop animation");
            scroll.Content = new Border { Height = 100 };
            Check(!scroll.HasAnimatedProperties, "switching section content cancels old scroll motion");
        }
        }
    }

    private static IDictionary Reads { get { return (IDictionary)Field("_readTasks"); } }
    private static IDictionary Errors { get { return (IDictionary)Field("_readErrors"); } }
    private static Border Activity { get { return (Border)Field("_activityPanel"); } }
    private static object Field(string name) { return _type.GetField(name, Private).GetValue(_window); }
    private static void SetField(string name, object value) { _type.GetField(name, Private).SetValue(_window, value); }
    private static object Call(string name, params object[] args) { return _type.GetMethod(name, Private).Invoke(_window, args); }
    private static Task Read(string key, string label, Func<Task> read) { return (Task)Call("RunReadAsync", key, label, read); }
    private static void MarkLoaded(string key)
    {
        object loaded = Field("_loadedModules");
        loaded.GetType().GetMethod("Add").Invoke(loaded, new object[] { key });
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _checks++;
    }

    private static void PumpUntil(Func<bool> complete, int timeoutSeconds = 5)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (!complete())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(timeoutSeconds)) throw new TimeoutException("UI continuation did not complete.");
            DispatcherFrame frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(1);
        }
    }

    private static void Snapshot(string directory, string name, int width = 1400, int height = 880)
    {
        Directory.CreateDirectory(directory);
        FrameworkElement content = (FrameworkElement)_window.Content;
        content.Measure(new Size(width, height));
        content.Arrange(new Rect(0, 0, width, height));
        content.UpdateLayout();
        RenderTargetBitmap image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        image.Render(content);
        PngBitmapEncoder encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using (FileStream output = File.Create(Path.Combine(directory, name))) encoder.Save(output);
        _screenshots++;
    }
}
