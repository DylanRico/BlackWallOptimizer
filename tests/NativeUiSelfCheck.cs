using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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

// Runs without showing a window or invoking Program.Main; registry writes use a unique disposable HKCU fixture.
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
            CheckStartupRouting();
            CheckSmoothScrolling();
            CheckCatalogSelection();
            CheckAdaptiveCards();

            MarkLoaded("hardware");
            Call("RenderCurrentTab");
            Snapshot(args[1], "native-initial.png");
            Button navTipButton = ((Dictionary<string, Button>)Field("_navButtons"))["monitor"];
            navTipButton.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, null, navTipButton) { RoutedEvent = Keyboard.GotKeyboardFocusEvent });
            Check(!((ToolTip)navTipButton.ToolTip).IsOpen, "keyboard focus does not pin a category tooltip open");
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

            string quietStatus = ((TextBlock)Field("_statusText")).Text;
            var quietSource = new TaskCompletionSource<int>();
            Task quiet = (Task)Call("RunReadAsync", "monitor", "Sondeo simulado", new Func<Task>(() => quietSource.Task), true);
            PumpUntil(() => Reads.Contains("monitor"));
            Check(Activity.Visibility == Visibility.Collapsed, "background polling never flashes the activity overlay");
            quietSource.SetResult(0); PumpUntil(() => quiet.IsCompleted);
            Check(((TextBlock)Field("_statusText")).Text == quietStatus, "background success does not overwrite the user's status");
            SetField("_activeTab", "monitor");
            Task quietFailure = (Task)Call("RunReadAsync", "monitor", "Sondeo simulado", new Func<Task>(() => { throw new InvalidOperationException("E_TEST_BACKGROUND"); }), true);
            PumpUntil(() => quietFailure.IsCompleted);
            Check(Activity.Visibility == Visibility.Visible && Errors.Contains("monitor"), "background failures remain visible and recoverable");
            ((Button)Field("_activityDismiss")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            SetField("_activeTab", "test");

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
            if ((bool)Field("_sidebarCollapsed")) Call("ToggleSidebar");
            CheckVisualPreferences(args[1], preferences);
            CheckTweakPresentation(args[1]);
            CheckTweakEngine();
            CheckGraphiteInteractions(args[1]);
            CheckStableSectionFilters(args[1]);
            CheckContrast();
            ((Button)Field("_themeBtn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ((Button)Field("_themeBtn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ((Button)Field("_themeBtn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Snapshot(args[1], "native-light-small.png", 1080, 700);
            Check(!(bool)Field("_isDark"), "theme control switches to light mode");
            CheckContrast();
            foreach (double scale in new[] { 1.25, 1.5, 2.0 }) Snapshot(args[1], "native-light-scale-" + (int)(scale * 100) + ".png", 1080, 700, null, scale);
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
            // Disposing a presentation source invalidates template visuals in offscreen renders.
            CheckExoticMotion(args[1]);
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
        SetField("_reduceMotion", true);
        Call("UpdateMotionPreference");
        if ((bool)Field("_sidebarCollapsed")) Call("ToggleSidebar");
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
            Check(_window.HasAnimatedProperties, "overlay uses a native animation clock");
            double intermediate = ((Border)Field("_sidebarBorder")).Width;
            Check(((ColumnDefinition)Field("_sidebarColumn")).Width.Value == 72, "animated overlay leaves the content rail stable");
            Call("ToggleSidebar");
            PumpUntil(() => Math.Abs(((Border)Field("_sidebarBorder")).Width - 248) < 0.01);
            Check(true, "rapid reversal settles at the most recent sidebar target");
            var scroll = (ScrollViewer)Field("_mainScroll");
            var page = (FrameworkElement)scroll.Content;
            Call("AnimatePage", page);
            var animated = (IList)Field("_motionEntrances");
            Check(animated.Count > 1, "page entrance animates multiple shared cards");
            var first = (FrameworkElement)animated[0];
            var transform = (TransformGroup)first.RenderTransform;
            var translate = (TranslateTransform)transform.Children[1];
            Check(translate.HasAnimatedProperties && first.HasAnimatedProperties, "card entrance has native opacity and translation clocks");
            Console.WriteLine("Motion sample: sidebar=" + intermediate.ToString("F1") + " DIP; cardY=" + translate.Y.ToString("F1") + "; opacity=" + first.Opacity.ToString("F2"));
            Call("PlayButtonSheen", (Button)Field("_themeBtn"));
            Button button = (Button)Field("_themeBtn");
            var sheen = (Border)button.Template.FindName("Sheen", button);
            Check(sheen.BorderBrush is LinearGradientBrush, "button reflection uses an animated gradient");
            var focusRing = button.Template.FindName("FocusRing", button) as Border;
            Check(button.FocusVisualStyle == null && focusRing != null && focusRing.BorderThickness.Left == 2 && focusRing.Visibility == Visibility.Hidden, "buttons use a rounded 2px keyboard focus ring instead of the rectangular system dots");
            object energy = Field("_headerEnergy");
            energy.GetType().GetMethod("SetRunning").Invoke(energy, new object[] { true });
            Check((bool)energy.GetType().GetProperty("Running").GetValue(energy, null), "electric trace runs on demand");
            SetField("_reduceMotion", true);
            Call("UpdateMotionPreference");
            Check(animated.Count == 0 && !first.HasAnimatedProperties && first.RenderTransform == Transform.Identity, "reduced motion removes in-flight card animations");
            Check(!(bool)energy.GetType().GetProperty("Running").GetValue(energy, null), "reduced motion stops the electric trace timer");
            Check(sheen.BorderBrush == null, "reduced motion clears active button reflection");
            Snapshot(directory, "native-motion-rest.png", 1080, 700, content);
            host.RootVisual = null;
        }
        _window.Content = content;
        SetField("_reduceMotion", false);
        Call("UpdateMotionPreference");
    }

    private static void CheckGraphiteInteractions(string directory)
    {
        SetField("_reduceMotion", true);
        Call("UpdateMotionPreference");
        if (!(bool)Field("_sidebarCollapsed")) Call("ToggleSidebar");
        var input = (TextBox)Field("_omniboxInput");
        input.Text = "zzzz-no-match-987";
        Check(((StackPanel)Field("_omniboxResultsPanel")).Children[0] is TextBlock, "global search has a no-results state");
        input.Text = "Windows";
        IList results = (IList)Field("_omniboxItems");
        Check(results.Count > 0 && results[0] is Button, "search results expose keyboard-compatible controls");
        using (var source = new HwndSource(new HwndSourceParameters("Blackwall keyboard check") { Width = 1, Height = 1, WindowStyle = unchecked((int)0x80000000) }))
        {
            int selectedIndex = (int)Field("_omniboxSelected");
            var down = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Down) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            Call("OnOmniboxKeyDown", input, down);
            Check(down.Handled && (int)Field("_omniboxSelected") == (selectedIndex + 1) % results.Count, "Down key advances the visible search selection");
            var up = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Up) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            Call("OnOmniboxKeyDown", input, up);
            Check(up.Handled && (int)Field("_omniboxSelected") == selectedIndex, "Up key restores the previous search selection");
        }
        Snapshot(directory, "native-search.png", 1366, 768);
        Snapshot(directory, "native-search-results.png", 520, 390, (FrameworkElement)((System.Windows.Controls.Primitives.Popup)Field("_omniboxPopup")).Child);
        input.Text = "Brave";
        var selected = (HashSet<string>)Field("_selectedApps");
        int previousSelection = selected.Count;
        using (var source = new HwndSource(new HwndSourceParameters("Blackwall Enter check") { Width = 1, Height = 1, WindowStyle = unchecked((int)0x80000000) }))
            Call("OnOmniboxKeyDown", input, new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        Check((string)Field("_activeTab") == "apps" && selected.Count == previousSelection, "opening a software search result never selects or installs it");
        Check(!((System.Windows.Controls.Primitives.Popup)Field("_omniboxPopup")).IsOpen, "opening a search result closes the popup");
        SetField("_activeTab", "optimization");
        Call("UpdateNavSelection");
        MarkLoaded("tweaks");
        SetField("_hardware", new Dictionary<string, object>());
        var fixture = new Dictionary<string, object> { { "id", "ui-action" }, { "title", "Prueba de contrato" }, { "summary", "Un resumen con texto largo que debe permanecer legible en tamaños reducidos sin esconder el riesgo ni la acción." }, { "riskLevel", "high" }, { "riskWarning", "Riesgo visible" }, { "applyOps", new object[] { "apply-payload" } }, { "undoOps", new object[] { "undo-payload" } } };
        var states = (Dictionary<string, bool>)Field("_tweakStates");
        states["ui-action"] = false;
        TaskCompletionSource<int> pending = new TaskCompletionSource<int>();
        int calls = 0;
        bool requestedApply = false;
        object payload = null;
        object originalHandler = Field("_executeTweakAction");
        SetField("_executeTweakAction", new Func<Dictionary<string, object>, bool, Task>((t, apply) => { calls++; payload = t; requestedApply = apply; return pending.Task; }));
        Border card = (Border)Call("BuildTweakCard", fixture);
        Grid layout = (Grid)card.Child;
        Button action = (Button)((DockPanel)layout.Children[1]).Children[0];
        action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(calls == 1 && Object.ReferenceEquals(payload, fixture) && requestedApply, "apply sends the unchanged catalog object once while pending");
        Check(!action.IsEnabled && !states["ui-action"], "pending action cannot announce success before the backend responds");
        pending.SetException(new InvalidOperationException("E_TEST_ACTION"));
        PumpUntil(() => action.IsEnabled);
        Check(!states["ui-action"] && ((DockPanel)layout.Children[1]).Children.OfType<TextBlock>().Single().Text.Contains("E_TEST_ACTION"), "failed action preserves system state and displays the backend error");
        states["ui-action"] = true;
        pending = new TaskCompletionSource<int>();
        action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(calls == 2 && !requestedApply && Object.ReferenceEquals(payload, fixture), "revert sends the same catalog object with apply=false");
        pending.SetResult(0);
        PumpUntil(() => action.IsEnabled);
        SetField("_executeTweakAction", originalHandler);
        var batchButton = new Button { Content = "Prueba de tarea" };
        var batch = new TaskCompletionSource<int>();
        int batchCalls = 0;
        Func<Task> operation = () => { batchCalls++; return batch.Task; };
        Task batchRun = (Task)Call("RunUiActionAsync", "test-batch", batchButton, operation);
        Task duplicateBatch = (Task)Call("RunUiActionAsync", "test-batch", batchButton, operation);
        Check(batchCalls == 1 && !batchButton.IsEnabled && duplicateBatch.IsCompleted, "repeated batch clicks share the existing execution guard");
        batch.SetException(new InvalidOperationException("E_TEST_BATCH"));
        PumpUntil(() => batchRun.IsCompleted);
        Check(!batchRun.IsFaulted && batchButton.IsEnabled && Convert.ToString(batchButton.Content) == "Prueba de tarea", "failed batch restores its button without announcing success");
        states.Remove("ui-action");
        SetField("_hardware", null);
        Window modalOwner = (Window)Activator.CreateInstance(_type, new object[] { Path.Combine(directory, "modal-preferences.json") });
        ((DispatcherTimer)_type.GetField("_scriptMonitorTimer", Private).GetValue(modalOwner)).Stop();
        Type releaseType = _type.Assembly.GetType("Blackwall.OtaRelease", true);
        object release = Activator.CreateInstance(releaseType);
        releaseType.GetField("Version").SetValue(release, "99.0.0");
        releaseType.GetField("Hash").SetValue(release, new string('a', 64));
        releaseType.GetField("Notes").SetValue(release, "Fixture de prueba: desplazamiento continuo.\n\nNotas largas para verificar que la tarjeta conserva sus acciones y permite leer todas las mejoras sin ejecutar una actualización.");
        _type.GetField("_availableRelease", Private).SetValue(modalOwner, release);
        new WindowInteropHelper(modalOwner).EnsureHandle();
        Window modal = (Window)_type.GetMethod("CreateOtaUpdateDialog", Private).Invoke(modalOwner, null);
        Check(modal.Owner == modalOwner && Descendants((DependencyObject)modal.Content).OfType<ScrollViewer>().Any(), "OTA dialog retains an owner and scrollable content without making requests");
        Check(modal.Width < modal.Height, "verified update invitation uses a portrait layout");
        var updateButtons = Descendants((DependencyObject)modal.Content).OfType<Button>().Select(b => Convert.ToString(b.Content)).ToList();
        Check(updateButtons.Contains("Actualizar ahora") && updateButtons.Contains("Más tarde"), "update invitation retains an explicit install choice and postponement");
        Check(Descendants((DependencyObject)modal.Content).OfType<TextBox>().All(t => t.IsReadOnly), "update dialog exposes no editable update source");
        Check(!Descendants((DependencyObject)modal.Content).OfType<Expander>().Any(), "update origin is absent from the production dialog");
        var ownerRoot = (Border)_type.GetField("_rootBorder", Private).GetValue(modalOwner);
        var ownerGrid = (Grid)_type.GetField("_rootGrid", Private).GetValue(modalOwner);
        int ownerChildren = ownerGrid.Children.Count;
        _type.GetMethod("SetModalBackdrop", Private).Invoke(modalOwner, new object[] { true });
        Check(ownerRoot.Effect == null && ownerRoot.Opacity == 1 && ownerGrid.Children.Count == ownerChildren + 1, "update backdrop dims with a scrim instead of a full-window blur");
        _type.GetMethod("SetModalBackdrop", Private).Invoke(modalOwner, new object[] { false });
        Check(ownerGrid.Children.Count == ownerChildren, "closing the update dialog removes its backdrop");
        Snapshot(directory, "native-update-dialog.png", 492, 700, (FrameworkElement)modal.Content);
        modal.Close();
        _type.GetField("_isDark", Private).SetValue(modalOwner, false);
        _type.GetMethod("UpdateThemeBrushes", Private).Invoke(modalOwner, null);
        modal = (Window)_type.GetMethod("CreateOtaUpdateDialog", Private).Invoke(modalOwner, null);
        Snapshot(directory, "native-update-dialog-light.png", 492, 700, (FrameworkElement)modal.Content);
        modal.Close();
        modalOwner.Close();
        object originalProcesses = Field("_liveScripts");
        var processFixture = new Dictionary<string, object> { { "pid", 2147483000 }, { "parentPid", 0 }, { "name", "fixture.exe" }, { "commandLine", "UI fixture only" }, { "memoryMb", 12.5 } };
        SetField("_liveScripts", new List<Dictionary<string, object>> { processFixture });
        Call("BuildMonitorView");
        var labels = (Dictionary<int, TextBlock>)Field("_monitorProcessMemoryLabels");
        TextBlock memoryLabel = labels[2147483000];
        processFixture["memoryMb"] = 24.5;
        Call("UpdateMonitorProcessMemoryLabels");
        Check(Object.ReferenceEquals(memoryLabel, labels[2147483000]) && memoryLabel.Text.Contains("24"), "live RAM updates reuse the existing process row");
        SetField("_liveScripts", originalProcesses);
        Call("RenderCurrentTab");
        // The native popup has its own presentation source; capture its actual child separately.
        Check(((ColumnDefinition)Field("_sidebarColumn")).Width.Value == 72, "overlay menu reserves a stable 72 DIP rail");
        object page = ((ScrollViewer)Field("_mainScroll")).Content;
        Call("ToggleSidebar");
        Check(((ColumnDefinition)Field("_sidebarColumn")).Width.Value == 72 && Object.ReferenceEquals(page, ((ScrollViewer)Field("_mainScroll")).Content), "opening the overlay neither reflows nor rebuilds the page");
        Snapshot(directory, "native-overlay.png", 1366, 768);
        Call("ToggleSidebar");
        Snapshot(directory, "native-graphite.png", 1366, 768);
        var scroll = (ScrollViewer)Field("_mainScroll");
        scroll.ScrollToVerticalOffset(540); scroll.UpdateLayout();
        Snapshot(directory, "native-cards.png", 1366, 768);
        foreach (double scale in new[] { 1.25, 1.5, 2.0 }) Snapshot(directory, "native-dark-scale-" + (int)(scale * 100) + ".png", 1080, 700, null, scale);
        var search = (TextBox)Field("_sectionSearch");
        SetField("_optRiskScope", "high");
        search.Text = "zzzz-empty-filter";
        Check((string)Field("_tweakSearchText") == "zzzz-empty-filter" && (string)Field("_optRiskScope") == "high", "section search preserves the risk filter across renders");
        search = (TextBox)Field("_sectionSearch");
        search.Text = "";
        SetField("_optRiskScope", "all");
        Call("RenderCurrentTab");
        SetField("_reduceMotion", false);
        Call("UpdateMotionPreference");
    }

    private static double Luminance(Color color)
    {
        Func<byte, double> channel = b => { double v = b / 255.0; return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); };
        return 0.2126 * channel(color.R) + 0.7152 * channel(color.G) + 0.0722 * channel(color.B);
    }

    private static IEnumerable<FrameworkElement> Descendants(DependencyObject node)
    {
        var element = node as FrameworkElement;
        if (element != null) yield return element;
        foreach (object child in LogicalTreeHelper.GetChildren(node))
        {
            var dependency = child as DependencyObject;
            if (dependency != null) foreach (var descendant in Descendants(dependency)) yield return descendant;
        }
    }

    private static void CheckStableSectionFilters(string directory)
    {
        SetField("_activeTab", "optimization");
        SetField("_optDeviceScope", "all");
        SetField("_optRiskScope", "all");
        SetField("_tweakSearchText", "");
        Call("RenderCurrentTab");
        var viewer = (ScrollViewer)Field("_mainScroll");
        var page = (DependencyObject)viewer.Content;
        var field = (TextBox)Field("_sectionSearch");
        var cards = Descendants(page).Where(c => c.Uid == "motion-card" && c.Tag != null).ToArray();
        Check(cards.Length > 0, "filter regression uses real tweak catalog cards");
        var updaters = (IDictionary)Field("_tweakCardUpdaters");
        object updater = updaters[cards[0].Tag];
        field.Text = "zzzz-no-filter-results";
        Check(Object.ReferenceEquals(page, viewer.Content) && Object.ReferenceEquals(field, Field("_sectionSearch")), "typing retains the search field and complete page");
        Check(cards.All(c => c.Visibility == Visibility.Collapsed), "no-results filter collapses existing cards");
        Check(Descendants(page).OfType<TextBlock>().Any(t => t.Text == "Sin resultados" && t.Parent != null), "filter retains an accessible empty-results message");
        Snapshot(directory, "native-filter-empty.png", 1366, 768);
        field.Text = "";
        Check(cards.All(c => c.Visibility == Visibility.Visible) && Object.ReferenceEquals(updater, updaters[cards[0].Tag]), "clearing a filter reuses the cards and action bindings");
        var item = ((List<Dictionary<string, object>>)Field("_tweaks")).First(t => Convert.ToString(t["id"]) == Convert.ToString(cards[0].Tag));
        string title = Convert.ToString(item["title"]);
        field.Text = title;
        Check(cards[0].Visibility == Visibility.Visible, "section filter retains case-insensitive catalog matching");
        field.Select(0, Math.Min(3, field.Text.Length));
        Check(Object.ReferenceEquals(field, Field("_sectionSearch")) && field.SelectionLength == Math.Min(3, title.Length), "the stable field retains text selection");
        field.Text = "";
        SetField("_activeTab", "apps");
        SetField("_appCategoryScope", "Todas");
        SetField("_appSearchText", "");
        Call("RenderCurrentTab");
        page = (DependencyObject)viewer.Content;
        field = (TextBox)Field("_sectionSearch");
        var appCard = Descendants(page).First(c => c.Uid == "motion-card" && c.Tag != null);
        var checkbox = Descendants(appCard).OfType<CheckBox>().First();
        var selected = (HashSet<string>)Field("_selectedApps");
        bool previous = selected.Contains(Convert.ToString(appCard.Tag));
        checkbox.IsChecked = !previous;
        checkbox.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        Check(Object.ReferenceEquals(page, viewer.Content) && selected.Contains(Convert.ToString(appCard.Tag)) == !previous, "app selection updates in place without rebuilding or executing an install");
        Check(Convert.ToString(((Button)Field("_installSelectedButton")).Content).Contains("(" + selected.Count + ")"), "selection count follows the real selected set");
        field.Text = "zzzz-no-app-results";
        Check(appCard.Visibility == Visibility.Collapsed && Object.ReferenceEquals(field, Field("_sectionSearch")), "app filter preserves its input and selected card");
        Snapshot(directory, "native-app-filter-empty.png", 1366, 768);
        field.Text = "";
        Check(checkbox.IsChecked == !previous && appCard.Visibility == Visibility.Visible, "selected app survives hiding and restoring results");
        checkbox.IsChecked = previous;
        checkbox.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        SetField("_activeTab", "monitor");
        Call("RenderCurrentTab");
        var rows = (ItemsControl)Field("_activityLogRows");
        int before = rows.Items.Count;
        _type.GetMethod("AppendLog", Private, null, new[] { typeof(string), typeof(string) }, null).Invoke(_window, new object[] { "PERF_TEST_FIRST", "TEST" });
        _type.GetMethod("AppendLog", Private, null, new[] { typeof(string), typeof(string) }, null).Invoke(_window, new object[] { "PERF_TEST_SECOND", "ERROR" });
        PumpUntil(() => !(bool)Field("_logFlushQueued"));
        var entries = ((IList)Field("_activityLog")).Cast<object>().ToList();
        Func<object, string, string> member = (o, n) => Convert.ToString(o.GetType().GetField(n).GetValue(o));
        Check(rows.Items.Count == before + 2 && member(entries[entries.Count - 1], "Message") == "PERF_TEST_SECOND" && member(entries[entries.Count - 1], "Level") == "error", "log rows append in place with level from the legacy tag");
        for (int i = 0; i < 700; i++) _type.GetMethod("AppendLog", Private, null, new[] { typeof(string), typeof(string), typeof(string) }, null).Invoke(_window, new object[] { "bulk " + i, "info", "Sistema" });
        PumpUntil(() => !(bool)Field("_logFlushQueued"));
        Check(((IList)Field("_activityLog")).Count == 500 && rows.Items.Count == 200, "activity log is bounded (500 kept, 200 rows shown)");
        // Wheel and touchpad over any part of the log (text or the empty space beside it) must scroll the log itself.
        var logScroll = (ScrollViewer)Field("_activityLogScroll");
        rows.Measure(new Size(900, 320)); rows.Arrange(new Rect(0, 0, 900, 320)); rows.UpdateLayout();
        logScroll.ScrollToEnd(); rows.UpdateLayout();
        var chain = _type.GetMethod("ChainWheelToPage", Private);
        var downAtEnd = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = UIElement.PreviewMouseWheelEvent };
        chain.Invoke(_window, new object[] { logScroll, downAtEnd });
        var upAtEnd = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120) { RoutedEvent = UIElement.PreviewMouseWheelEvent };
        chain.Invoke(_window, new object[] { logScroll, upAtEnd });
        Check(logScroll.ScrollableHeight > 0 && downAtEnd.Handled && !upAtEnd.Handled, "at the end of the log the wheel goes on to the page; scrolling back up stays in the log");
        SetField("_activityFilter", "problems");
        Call("RenderCurrentTab");
        rows = (ItemsControl)Field("_activityLogRows");
        Check(rows.Items.Count == ((IList)Field("_activityLog")).Cast<object>().Count(o => member(o, "Level") == "error" || member(o, "Level") == "warn"), "errors filter shows only problems");
        SetField("_activityFilter", "all");
        Call("RenderCurrentTab");

        object op = Call("BeginOperation", "Prueba de operación", "Optimización", 2);
        _type.GetMethod("StepOperation", Private).Invoke(_window, new object[] { op, "Paso uno", true, true });
        _type.GetMethod("StepOperation", Private).Invoke(_window, new object[] { op, "Paso dos", true, true });
        PumpUntil(() => !(bool)Field("_activityRefreshQueued"));
        var footer = (ProgressBar)Field("_footerProgress");
        Check(footer.Visibility == Visibility.Visible && Math.Abs(footer.Value - 100) < 0.1 && ((StackPanel)Field("_activityOpsPanel")).Children.Count >= 1, "a running operation shows its steps in the card and the footer");
        Call("CompleteOperation", op, false, "Prueba: falló a propósito");
        PumpUntil(() => !(bool)Field("_activityRefreshQueued"));
        Check(footer.Visibility == Visibility.Collapsed && (bool)op.GetType().GetField("Finished").GetValue(op) && member(((IList)Field("_activityLog")).Cast<object>().Last(), "Level") == "error", "a failed operation ends with an error entry and clears the footer");

        var processes = (List<Dictionary<string, object>>)Field("_liveScripts");
        var original = processes.ToList();
        processes.Clear();
        processes.Add(new Dictionary<string, object> { { "pid", 2147483001 }, { "parentPid", 0 }, { "name", "cmd.exe" }, { "commandLine", "fixture" }, { "memoryMb", 1.0 } });
        Call("RebuildProcessList");
        var killButton = Descendants((DependencyObject)Field("_monitorProcessPanel")).OfType<Button>().First(b => Convert.ToString(b.Content) == "Terminar");
        killButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var confirmBox = Descendants((DependencyObject)Field("_monitorProcessPanel")).OfType<Border>().First(b => b.Background == Field("RiskHighBg"));
        Check(confirmBox.Visibility == Visibility.Visible && !killButton.IsEnabled && processes.Count == 1, "Terminar asks for confirmation before killing anything");
        processes.Clear(); processes.AddRange(original);
        Snapshot(directory, "native-activity.png", 1366, 900);

        SetField("_activeTab", "optimization");
        Call("RenderCurrentTab");
        Check(Field("_activityLogRows") == null && Field("_monitorProcessPanel") == null, "leaving Actividad releases its live references");
    }

    private static void CheckContrast()
    {
        foreach (var pair in new[] { new[] { "TextPrimary", "BgElevated" }, new[] { "TextSecondary", "BgElevated" }, new[] { "RiskLowText", "RiskLowBg" }, new[] { "RiskMedText", "RiskMedBg" }, new[] { "RiskHighText", "RiskHighBg" }, new[] { "CyberCyanBrush", "BgSurface" } })
        {
            double foreground = Luminance(((SolidColorBrush)Field(pair[0])).Color), background = Luminance(((SolidColorBrush)Field(pair[1])).Color);
            double ratio = (Math.Max(foreground, background) + 0.05) / (Math.Min(foreground, background) + 0.05);
            Check(ratio >= 4.5, "text contrast >= 4.5:1 for " + pair[0] + " / " + pair[1]);
        }
    }

    private static void CheckAdaptiveCards()
    {
        Panel grid = (Panel)Activator.CreateInstance(_type.Assembly.GetType("Blackwall.AdaptiveCardGrid", true));
        grid.Children.Add(new Border { Height = 40 }); grid.Children.Add(new Border { Height = 80 }); grid.Children.Add(new Border { Height = 30 });
        grid.Measure(new Size(620, double.PositiveInfinity)); grid.Arrange(new Rect(0, 0, 620, grid.DesiredSize.Height));
        Check(grid.DesiredSize.Height == 110, "card rows use their own height instead of stretching the entire catalog");
        grid.Measure(new Size(310, double.PositiveInfinity)); grid.Arrange(new Rect(0, 0, 310, grid.DesiredSize.Height));
        Check(grid.DesiredSize.Height == 150 && grid.Children[0].RenderSize.Width == 310, "narrow layout keeps full-width cards without horizontal overflow");
        grid.Measure(new Size(930, double.PositiveInfinity)); grid.Arrange(new Rect(0, 0, 930, grid.DesiredSize.Height));
        Check(grid.DesiredSize.Height == 80, "wider layout restores columns without replacing the controls");
        grid.Children[1].Visibility = Visibility.Collapsed;
        grid.Measure(new Size(620, double.PositiveInfinity)); grid.Arrange(new Rect(0, 0, 620, grid.DesiredSize.Height));
        Check(grid.DesiredSize.Height == 40 && grid.Children[2].RenderSize.Width == 310, "filtered cards reflow without empty cells or oversized rows");
        grid.Children[1].Visibility = Visibility.Visible;
        grid.Measure(new Size(620, double.PositiveInfinity)); grid.Arrange(new Rect(0, 0, 620, grid.DesiredSize.Height));
        Check(grid.DesiredSize.Height == 110, "clearing filters restores original row geometry");
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
        Check(((Border)Field("_sidebarBorder")).Width == 72, "collapsed menu is 72 DIP");
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
        Check(((Border)Field("_sidebarBorder")).Width == 248, "expanded menu restores 248 DIP");
        SetField("_optRiskScope", "all");
        ((HashSet<string>)Field("_selectedApps")).Remove("fixture-selection");
        ((Button)Field("_motionToggle")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        File.WriteAllText(preferences, "not json");
        Window corrupt = (Window)Activator.CreateInstance(_type, new object[] { preferences });
        try { Check((bool)_type.GetField("_sidebarCollapsed", Private).GetValue(corrupt), "corrupt preferences use safe defaults"); }
        finally { corrupt.Close(); }
    }

    private static void CheckTweakPresentation(string directory)
    {
        var fixture = new Dictionary<string, object> { { "id", "ui-only" }, { "title", "Título técnico largo" }, { "shortTitle", "Ajuste de prueba" }, { "summary", "Resumen breve" }, { "description", "Explicación completa" }, { "riskLevel", "high" }, { "riskWarning", "Consecuencia visible antes de actuar" } };
        Border card = (Border)Call("BuildTweakCard", fixture);
        Grid layout = (Grid)card.Child;
        StackPanel body = (StackPanel)layout.Children[0];
        DockPanel footer = (DockPanel)layout.Children[1];
        Check(!((Button)footer.Children[0]).IsEnabled, "unknown tweak state disables the action");
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
        foreach (UIElement child in footer.Children)
            if (child is Button && Convert.ToString(((Button)child).Content).StartsWith("Detalles")) disclosure = (Button)child;
        Check(disclosure != null && body.Children.OfType<TextBlock>().All(text => text.Text != "Explicación completa"), "card shows one benefit line and keeps the technical text behind Detalles");
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
        var pair = (Panel)Activator.CreateInstance(_type.Assembly.GetType("Blackwall.AdaptiveCardGrid", true));
        pair.GetType().GetProperty("Columns").SetValue(pair, 2, null);
        pair.Children.Add((Border)Call("BuildTweakCard", fixture));
        Border neighbour = (Border)Call("BuildTweakCard", fixture);
        pair.Children.Add(neighbour);
        Action layoutPair = () => { pair.Measure(new Size(900, double.PositiveInfinity)); pair.Arrange(new Rect(0, 0, 900, pair.DesiredSize.Height)); pair.UpdateLayout(); };
        layoutPair();
        double neighbourHeight = neighbour.ActualHeight;
        Button opener = ((DockPanel)((Grid)((Border)pair.Children[0]).Child).Children[1]).Children.OfType<Button>().First(b => Convert.ToString(b.Content).StartsWith("Detalles"));
        opener.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        layoutPair();
        Check(((Border)pair.Children[0]).ActualHeight > neighbourHeight + 40 && Math.Abs(neighbour.ActualHeight - neighbourHeight) < 0.5, "opening details grows only that card, not its row neighbour");
        opener.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        layoutPair();
        Check(Math.Abs(((Border)pair.Children[0]).ActualHeight - neighbourHeight) < 0.5, "closing details returns the card to the row height");
        SetField("_tweakAdvancedOpen", false); SetField("_tweakSearchText", ""); SetField("_optDeviceScope", "all");
        var view = (FrameworkElement)Call("BuildTweaksView", "optimization");
        Button advancedToggle = Descendants(view).OfType<Button>().FirstOrDefault(b => Convert.ToString(System.Windows.Automation.AutomationProperties.GetName(b)).StartsWith("Mostrar u ocultar"));
        var advancedHost = advancedToggle == null ? null : (Panel)advancedToggle.Parent;
        var advancedBody = advancedHost == null ? null : advancedHost.Children[advancedHost.Children.IndexOf(advancedToggle) + 1];
        Check(advancedBody != null && advancedBody.Visibility == Visibility.Collapsed, "risky optimizations start in a closed, labelled Avanzadas group");
        advancedToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(advancedBody.Visibility == Visibility.Visible, "the Avanzadas group opens on demand");
        SetField("_tweakAdvancedOpen", false); SetField("_optDeviceScope", "auto");

        // Section switch paints the first cards at once and adds the rest on idle frames: none may be lost.
        object loadedModules = Field("_loadedModules");
        loadedModules.GetType().GetMethod("Add").Invoke(loadedModules, new object[] { "tweaks" });
        SetField("_optDeviceScope", "all"); SetField("_tweakSearchText", ""); SetField("_pendingSearchId", null);
        string previousTab = (string)Field("_activeTab");
        Call("SwitchTab", "safety");
        Call("SwitchTab", "debloat");
        var catalog = (List<Dictionary<string, object>>)Field("_tweaks");
        var debloatIds = new HashSet<string>(catalog.Where(t => Convert.ToString(t["category"]) == "debloat").Select(t => Convert.ToString(t["id"])));
        Func<int> shownCards = () => Descendants((DependencyObject)((ScrollViewer)Field("_mainScroll")).Content).Count(e => e.Tag is string && debloatIds.Contains((string)e.Tag));
        int firstFrameCards = shownCards();
        PumpUntil(() => shownCards() == debloatIds.Count);
        Check(firstFrameCards < debloatIds.Count && shownCards() == debloatIds.Count, "section switch defers cards but every card arrives (" + firstFrameCards + " then " + debloatIds.Count + ")");
        SetField("_optDeviceScope", "auto");
        Call("SwitchTab", previousTab);
        scroll.Content = previous;
        SetField("_reduceMotion", false);
        Call("UpdateMotionPreference");
    }

    // Real registry round-trip in a throwaway HKCU key; no system setting is touched.
    private static void CheckTweakEngine()
    {
        string testKey = @"Software\BlackwallSelfCheck\" + Guid.NewGuid().ToString("N");
        Func<Dictionary<string, object>, bool, Task> run = (t, apply) => (Task)Call("ExecuteTweakAsync", t, apply);
        Func<object, int> toDword = v => (int)_type.GetMethod("ToRegistryDword", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new[] { v });
        Check(toDword(2147483649L) == unchecked((int)0x80000001) && toDword(4294967295L) == -1 && toDword(-1) == -1 && toDword(38) == 38, "REG_DWORD values above int.MaxValue wrap instead of becoming 0");
        bool rejected = false;
        try { toDword(5000000000L); } catch (TargetInvocationException) { rejected = true; }
        Check(rejected, "values outside REG_DWORD are rejected");

        MethodInfo toolResult = _type.GetMethod("RequireSuccessfulToolResult", BindingFlags.Static | BindingFlags.NonPublic);
        Check((string)toolResult.Invoke(null, new object[] { "fixture.exe", 0, "Completed" }) == "Completed", "successful system tool output is preserved");
        foreach (int code in new[] { 5, -1 })
        {
            bool toolFailed = false;
            try { toolResult.Invoke(null, new object[] { "fixture.exe", code, "Denied" }); }
            catch (TargetInvocationException ex) { toolFailed = ex.InnerException is InvalidOperationException && ex.InnerException.Message.Contains(code.ToString()); }
            Check(toolFailed, "failed or timed-out system tool cannot become a success (" + code + ")");
        }

        object restore = Field("_autoRestorePoint");
        SetField("_autoRestorePoint", false);
        var states = (Dictionary<string, bool>)Field("_tweakStates");
        Func<string, object, Dictionary<string, object>> dword = (name, value) => new Dictionary<string, object> { { "type", "reg_dword" }, { "hive", "HKCU" }, { "key", testKey }, { "name", name }, { "value", value } };
        try
        {
            var good = new Dictionary<string, object> { { "id", "engine-good" }, { "title", "Prueba" },
                { "applyOps", new object[] { dword("Big", 2147483649L), dword("Small", 1) } },
                { "undoOps", new object[] { new Dictionary<string, object> { { "type", "reg_delete" }, { "hive", "HKCU" }, { "key", testKey }, { "name", "Big" } }, dword("Small", 0) } } };
            Task applied = run(good, true);
            PumpUntil(() => applied.IsCompleted);
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(testKey))
                Check(applied.Status == TaskStatus.RanToCompletion && unchecked((uint)(int)k.GetValue("Big")) == 0x80000001 && states["engine-good"] && (bool)Call("IsTweakActive", good, null), "apply writes 0x80000001 correctly and detection confirms every value");
            Task undone = run(good, false);
            PumpUntil(() => undone.IsCompleted);
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(testKey))
                Check(undone.Status == TaskStatus.RanToCompletion && k.GetValue("Big") == null && !(bool)Call("IsTweakActive", good, null), "undo restores and detection reports inactive");

            var partial = new Dictionary<string, object> { { "id", "engine-partial" }, { "title", "Prueba" },
                { "applyOps", new object[] { dword("Partial", 7), dword("Broken", 5000000000L) } } };
            Task failed = run(partial, true);
            PumpUntil(() => failed.IsCompleted);
            Check(failed.IsFaulted && failed.Exception.InnerException.Message.Contains("1 de 2") && !states["engine-partial"], "a rejected change is reported as an error, never as applied");
        }
        finally
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(testKey, false);
            SetField("_autoRestorePoint", restore);
        }

        // Static catalog contract: every value fits, every tweak can be reverted, no write to TrustedInstaller keys.
        var tweaks = (List<Dictionary<string, object>>)Field("_tweaks");
        var known = new HashSet<string> { "reg_dword", "reg_sz", "reg_delete", "reg_delete_tree", "powercfg", "service_stop" };
        var problems = new List<string>();
        foreach (var t in tweaks)
        {
            string id = Convert.ToString(t["id"]);
            var apply = ((IEnumerable)t["applyOps"]).Cast<Dictionary<string, object>>().ToList();
            var undo = t.ContainsKey("undoOps") ? ((IEnumerable)t["undoOps"]).Cast<Dictionary<string, object>>().ToList() : new List<Dictionary<string, object>>();
            if (undo.Count == 0) problems.Add(id + ": sin reversión");
            foreach (var op in apply.Concat(undo))
            {
                string type = Convert.ToString(op["type"]);
                if (!known.Contains(type)) problems.Add(id + ": tipo " + type);
                if (type == "reg_dword") { try { toDword(op["value"]); } catch (TargetInvocationException) { problems.Add(id + ": valor fuera de rango"); } }
                if (op.ContainsKey("key") && Convert.ToString(op["key"]).Contains("WindowsRuntime\\ActivatableClassId")) problems.Add(id + ": clave de TrustedInstaller");
            }
            foreach (var op in apply.Where(o => Convert.ToString(o["type"]) == "reg_dword" && Convert.ToString(o["name"]) == "Start"))
                if (!undo.Any(u => Convert.ToString(u["type"]) == "reg_dword" && Convert.ToString(u["key"]) == Convert.ToString(op["key"])) && Convert.ToInt64(op["value"]) == 4) problems.Add(id + ": servicio desactivado sin reversión");
        }
        Check(problems.Count == 0, "catalog contract holds for " + tweaks.Count + " tweaks" + (problems.Count > 0 ? ": " + string.Join("; ", problems) : ""));
        Check(!tweaks.Any(t => Convert.ToString(t["id"]) == "debloat_wifi_sense" || Convert.ToString(t["id"]) == "debloat_cortana_websearch"), "features removed from Windows 11 are no longer offered");
    }

    private static void CheckCatalogSelection()
    {
        string embedded = "{\"lastUpdated\":\"2026-10-24T00:00:00Z\",\"tweaks\":[]}";
        string old = "{\"lastUpdated\":\"2026-10-23T00:00:00Z\",\"tweaks\":[]}";
        string newer = "{\"lastUpdated\":\"2026-10-25T00:00:00Z\",\"tweaks\":[]}";
        Check((string)Call("SelectCatalog", embedded, old) == embedded, "stale OTA cache cannot override shipped short titles and catalog");
        Check((string)Call("SelectCatalog", embedded, newer) == embedded, "unsigned newer catalogs cannot override the packaged catalog");
        Check((string)Call("SelectCatalog", embedded, "invalid") == embedded, "invalid cache falls back to embedded catalog");
        Check((string)Call("SelectCatalog", embedded, "{\"lastUpdated\":\"2026-10-25\"}") == embedded, "cache without catalog entries is rejected");
        Check(((ICollection)Field("_tweaks")).Count >= 43, "current catalog includes concise presentation fields");
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

    private static void CheckStartupRouting()
    {
        Type program = _type.Assembly.GetType("Blackwall.Program", true);
        MethodInfo routing = program.GetMethod("RequiresElevation", BindingFlags.Static | BindingFlags.NonPublic);
        Func<string[], bool> requires = args => (bool)routing.Invoke(null, new object[] { args });
        Check(requires(new string[0]), "normal opening requests UAC before building the UI");
        Check(!requires(new[] { "--ota-update", "1.4.2", new string('a', 64) }), "verified update handoff has an unelevated startup route");
        Check(requires(new[] { "--ota-update", "invalid", new string('a', 64) }), "malformed update arguments do not enter the helper route");
        Check(!requires(new[] { "--ota-health", "fixture", new string('a', 32) }), "update health confirmation runs before normal elevation");
        Check(requires(new[] { "--ota-rollback", "fixture" }), "rollback reopening follows the normal permission flow");
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
        int wheelTimestamp = 1000;
        Func<int, MouseWheelEventArgs> input = delta => new MouseWheelEventArgs(Mouse.PrimaryDevice, wheelTimestamp += 200, delta) { RoutedEvent = Mouse.MouseWheelEvent };
        MouseWheelEventArgs down = input(-120);
        wheel.Invoke(scroll, new object[] { down });
        scroll.UpdateLayout();
        Check(down.Handled, "wheel input is handled by the scroll viewer");
        if (SystemParameters.ClientAreaAnimation && SystemParameters.WheelScrollLines != 0)
        {
            double firstTarget = (double)smoothType.GetField("_targetOffset", Private).GetValue(scroll);
            Check((bool)smoothType.GetField("_scrolling", Private).GetValue(scroll) && firstTarget > 0, "wheel input starts a single frame progression without an instant jump");
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
            Check(!(bool)smoothType.GetField("_scrolling", Private).GetValue(scroll), "direct scrollbar or keyboard input stops the frame progression");
            scroll.Content = new Border { Height = 100 };
            Check(!(bool)smoothType.GetField("_scrolling", Private).GetValue(scroll), "switching section content cancels old scroll motion");
        }
        scroll.Content = new Border { Height = 4000, Width = 400 };
        scroll.UpdateLayout(); scroll.ScrollToVerticalOffset(0); scroll.UpdateLayout();
        double step = SystemParameters.WheelScrollLines < 0 ? scroll.ViewportHeight : SystemParameters.WheelScrollLines * 16.0;
        for (int i = 0; i < 60; i++)
            wheel.Invoke(scroll, new object[] { new MouseWheelEventArgs(Mouse.PrimaryDevice, 10000 + i * 8, -6) { RoutedEvent = Mouse.MouseWheelEvent } });
        Check(!scroll.HasAnimatedProperties, "precision touchpad burst allocates no repeated animation clocks");
        PumpUntil(() => !(bool)smoothType.GetField("_scrolling", Private).GetValue(scroll));
        scroll.UpdateLayout();
        Check(Math.Abs(scroll.VerticalOffset - Math.Min(scroll.ScrollableHeight, step * 3)) < 0.6, "touchpad deltas accumulate even before a layout frame");
        wheel.Invoke(scroll, new object[] { new MouseWheelEventArgs(Mouse.PrimaryDevice, 10480, 6) { RoutedEvent = Mouse.MouseWheelEvent } });
        PumpUntil(() => !(bool)smoothType.GetField("_scrolling", Private).GetValue(scroll));
        scroll.UpdateLayout();
        Check(Math.Abs(scroll.VerticalOffset - step * 2.95) < 0.6, "touchpad reversal responds without waiting for a tween");
        for (int i = 0; i < 30; i++)
            wheel.Invoke(scroll, new object[] { new MouseWheelEventArgs(Mouse.PrimaryDevice, 11000 + i * 8, -120) { RoutedEvent = Mouse.MouseWheelEvent } });
        Check(!scroll.HasAnimatedProperties, "quantized bursts use the same frame progression without repeated clocks");
        PumpUntil(() => !(bool)smoothType.GetField("_scrolling", Private).GetValue(scroll));
        scroll.UpdateLayout();
        Check(Math.Abs(scroll.VerticalOffset - Math.Min(scroll.ScrollableHeight, step * 32.95)) < 0.6, "switching from notches to a continuous gesture preserves the pending distance");
        wheel.Invoke(scroll, new object[] { input(-18) });
        scroll.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        Check(!(bool)smoothType.GetField("_scrolling", Private).GetValue(scroll), "unloading a section detaches its rendering progression");
        smoothType.GetProperty("ReduceMotion").SetValue(scroll, true, null);
        scroll.ScrollToVerticalOffset(0); scroll.UpdateLayout();
        wheel.Invoke(scroll, new object[] { new MouseWheelEventArgs(Mouse.PrimaryDevice, 12000, -6) { RoutedEvent = Mouse.MouseWheelEvent } });
        scroll.UpdateLayout();
        Check(!scroll.HasAnimatedProperties && Math.Abs(scroll.VerticalOffset - step * 0.05) < 0.6, "reduced motion preserves fractional touchpad movement");
        }
    }

    private static IDictionary Reads { get { return (IDictionary)Field("_readTasks"); } }
    private static IDictionary Errors { get { return (IDictionary)Field("_readErrors"); } }
    private static Border Activity { get { return (Border)Field("_activityPanel"); } }
    private static object Field(string name) { return _type.GetField(name, Private).GetValue(_window); }
    private static void SetField(string name, object value) { _type.GetField(name, Private).SetValue(_window, value); }
    private static object Call(string name, params object[] args) { return _type.GetMethod(name, Private).Invoke(_window, args); }
    private static Task Read(string key, string label, Func<Task> read) { return (Task)Call("RunReadAsync", key, label, read, false); }
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

    private static void Snapshot(string directory, string name, int width = 1400, int height = 880, FrameworkElement visual = null, double scale = 1)
    {
        Directory.CreateDirectory(directory);
        FrameworkElement content = visual ?? (FrameworkElement)_window.Content;
        content.Measure(new Size(width, height));
        content.Arrange(new Rect(0, 0, width, height));
        content.UpdateLayout();
        RenderTargetBitmap image = new RenderTargetBitmap((int)(width * scale), (int)(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        image.Render(content);
        PngBitmapEncoder encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using (FileStream output = File.Create(Path.Combine(directory, name))) encoder.Save(output);
        _screenshots++;
    }
}
