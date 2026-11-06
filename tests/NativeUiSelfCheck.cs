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
            var appxRunner = _type.GetField("_appxTool", Private);
            if (appxRunner != null) appxRunner.SetValue(_window, new Func<string, string, string>((tool, command) =>
            {
                if (tool == "powershell" && !command.Contains("Remove-Appx")) return "{\"installed\":false,\"provisioned\":false,\"inventory\":\"\"}";
                throw new InvalidOperationException("APPX/Store write outside its fake fixture: " + tool);
            }));
            ((DispatcherTimer)Field("_scriptMonitorTimer")).Stop();
            Check(Reads.Count == 0, "construction does not start module reads");
            Check(Field("_hardware") == null, "construction does not audit hardware");
            Check(Activity.Visibility == Visibility.Collapsed, "no persistent startup overlay");
            Check(_window.WindowState == WindowState.Maximized, "application starts maximized");
            CheckWindowBounds();
            CheckStartupRouting();
            CheckSmoothScrolling();
            CheckParticles();
            CheckEmbeddedCatalog();
            CheckCpuCacheMb();
            CheckAdaptiveCards();
            CheckCeitoMotionLifecycle();
            CheckHiddenMonitorPolling();
            CheckActiveNavDoesNotRebuild();

            MarkLoaded("hardware");
            Call("RenderCurrentTab");
            Snapshot(args[1], "native-initial.png");
            Button navTipButton = ((Dictionary<string, Button>)Field("_navButtons"))["monitor"];
            navTipButton.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, null, navTipButton) { RoutedEvent = Keyboard.GotKeyboardFocusEvent });
            Check(!((ToolTip)navTipButton.ToolTip).IsOpen, "keyboard focus does not pin a category tooltip open");
            var scanSource = new TaskCompletionSource<int>();
            Task simulatedScan = Read("hardware", "Auditoría de hardware", () => scanSource.Task);
            PumpUntil(() => Reads.Contains("hardware"));
            Check(Activity.HorizontalAlignment == HorizontalAlignment.Center && Activity.VerticalAlignment == VerticalAlignment.Center, "hardware scan is centered");
            Check(((ProgressBar)Field("_activityProgress")).Value == 0, "hardware scan begins at zero completed stages");
            Call("ReportReadProgress", "hardware", 4, 8, "Memoria y sistema · 4/8");
            Check(((ProgressBar)Field("_activityProgress")).Value == 50 && ((TextBlock)Field("_activityDetail")).Text.Contains("Memoria"), "hardware scan shows real completed stage progress");
            Snapshot(args[1], "native-hardware-scan.png");
            scanSource.SetResult(0); PumpUntil(() => simulatedScan.IsCompleted);
            Check(Activity.Visibility == Visibility.Collapsed, "completed hardware scan hides its panel");
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
            foreach (string tab in new[] { "hardware", "winutil", "ceito", "apps", "monitor", "safety" })
            {
                SetField("_activeTab", tab);
                MarkLoaded(tab == "winutil" || tab == "ceito" ? "tweaks" : tab);
                Call("UpdateNavSelection");
                Call("RenderCurrentTab");
                Snapshot(args[1], "native-" + tab + ".png");
            }
            SetField("_activeTab", "winutil");
            Call("UpdateNavSelection");
            Call("RenderCurrentTab");
            Snapshot(args[1], "native-winutil-small.png", 1080, 700);
            if ((bool)Field("_sidebarCollapsed")) Call("ToggleSidebar");
            CheckVisualPreferences(args[1], preferences);
            CheckTweakPresentation(args[1]);
            CheckTweakEngine();
            CheckApplyPreview(args[1]);
            CheckFullReset();
            CheckOfficialSources();
            CheckEveryValueIsUndoable();
            CheckRevertContinuesPastRefusal();
            CheckResetLinkEndToEnd();
            CheckPowerPlanRepair();
            CheckStartupAndCleanup();
            CheckWinUtilSection(args[1]);
            CheckRevertsEverything147(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[0])), "..", "tests", "fixtures", "tweaks-v1.4.7.json"));
            CheckHardwareReports();
            CheckGraphiteInteractions(args[1]);
            CheckStableSectionFilters(args[1]);
            SetField("_hardware", null); // the section filter checks ran on a hardware fixture
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
            CheckAppxRemoval();
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
            var liquidEdge = (RotateTransform)Field("_dashboardEdgeRotation");
            Call("SetDashboardEdgeMotion", true);
            Check(liquidEdge.HasAnimatedProperties, "dashboard silver edge starts a shared animation");
            PumpUntil(() => liquidEdge.Angle > 1);
            Check(liquidEdge.Angle > 1, "dashboard silver edge moves without pointer interaction");
            object energy = Field("_headerEnergy");
            energy.GetType().GetMethod("SetRunning").Invoke(energy, new object[] { true });
            Check((bool)energy.GetType().GetProperty("Running").GetValue(energy, null), "electric trace runs on demand");
            SetField("_reduceMotion", true);
            Call("UpdateMotionPreference");
            Check(!liquidEdge.HasAnimatedProperties, "reduced motion stops the dashboard silver edge");
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
        input.Text = "VLC";
        var selected = (HashSet<string>)Field("_selectedApps");
        int previousSelection = selected.Count;
        using (var source = new HwndSource(new HwndSourceParameters("Blackwall Enter check") { Width = 1, Height = 1, WindowStyle = unchecked((int)0x80000000) }))
            Call("OnOmniboxKeyDown", input, new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        Check((string)Field("_activeTab") == "apps" && selected.Count == previousSelection, "opening a software search result never selects or installs it");
        Check(!((System.Windows.Controls.Primitives.Popup)Field("_omniboxPopup")).IsOpen, "opening a search result closes the popup");
        SetField("_activeTab", "winutil");
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
        object originalConfirm = Field("_confirmApply");
        int confirmations = 0;
        SetField("_confirmApply", new Func<string, List<Dictionary<string, object>>, Task<List<Dictionary<string, object>>>>((label, list) => { confirmations++; return Task.FromResult(list); }));
        SetField("_executeTweakAction", new Func<Dictionary<string, object>, bool, Task>((t, apply) => { calls++; payload = t; requestedApply = apply; return pending.Task; }));
        object originalCatalogRevert = Field("_confirmCatalogRevert");
        int catalogConfirmations = 0;
        SetField("_confirmCatalogRevert", new Func<Dictionary<string, object>, bool>(t => { catalogConfirmations++; return true; }));
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
        Check(confirmations == 1, "only applying asks for the preview and restore point; reverting does not");
        Check(catalogConfirmations == 1, "reverting without saved values asks before writing the catalog's values");
        pending.SetResult(0);
        PumpUntil(() => action.IsEnabled);
        states["ui-action"] = false;
        SetField("_confirmApply", new Func<string, List<Dictionary<string, object>>, Task<List<Dictionary<string, object>>>>((label, list) => Task.FromResult<List<Dictionary<string, object>>>(null)));
        action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => action.IsEnabled);
        Check(calls == 2 && !states["ui-action"], "a cancelled preview or missing restore point applies nothing");
        SetField("_confirmApply", originalConfirm);
        SetField("_confirmCatalogRevert", originalCatalogRevert);
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
        Check(!Descendants((DependencyObject)modal.Content).OfType<TextBox>().Any(), "update notes use readable content rather than an editable field");
        var updateText = Descendants((DependencyObject)modal.Content).OfType<TextBlock>().Select(t => t.Text).ToList();
        Check(updateText.Any(t => t.Contains("EN ESTA VERSIÓN")) && updateText.Any(t => t.Contains("NOVEDADES DE 99.0.0")), "installed notes and verified new version have separate headings");
        var updateActions = Descendants((DependencyObject)modal.Content).OfType<Button>().Where(b => Convert.ToString(b.Content) == "Actualizar ahora" || Convert.ToString(b.Content) == "Más tarde").ToList();
        Check(updateActions.Count == 2 && Object.ReferenceEquals(updateActions[0].Parent, updateActions[1].Parent) && updateActions[0].Parent is Grid && ((Grid)updateActions[0].Parent).ColumnDefinitions.Count == 2, "update actions share two aligned columns");
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
        _type.GetField("_availableRelease", Private).SetValue(modalOwner, null);
        modal = (Window)_type.GetMethod("CreateOtaUpdateDialog", Private).Invoke(modalOwner, null);
        Check(Descendants((DependencyObject)modal.Content).OfType<TextBlock>().Any(t => t.Text.Contains("EN ESTA VERSIÓN")), "installed notes remain visible without a remote update");
        modal.Close();
        _type.GetField("_reduceMotion", Private).SetValue(modalOwner, true);
        object[] toastArgs = { new Action(() => { }), null, null, null };
        var toast = (Border)_type.GetMethod("CreateOtaProgress", Private).Invoke(modalOwner, toastArgs);
        Check(ownerGrid.Children.Contains(toast) && toastArgs[1] is TextBlock && toastArgs[2] is ProgressBar, "update progress is an in-window panel with real status and progress controls");
        ((TextBlock)toastArgs[1]).Text = "Descargando · 64 % (fixture)";
        ((ProgressBar)toastArgs[2]).IsIndeterminate = false; ((ProgressBar)toastArgs[2]).Value = 64;
        Snapshot(directory, "native-update-progress.png", 1400, 880, (FrameworkElement)modalOwner.Content);
        ownerGrid.Children.Remove(toast);
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
        search.Text = "zzzz-empty-filter";
        Check((string)Field("_tweakSearchText") == "zzzz-empty-filter", "section search keeps its text across renders");
        search = (TextBox)Field("_sectionSearch");
        search.Text = "";
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

    // Every capability present unless listed as missing, so a gating check decides exactly what is absent.
    private static Dictionary<string, object> HardwareFixture(bool laptop, params string[] missing)
    {
        var caps = new Dictionary<string, object>();
        foreach (string k in new[] { "battery", "dedicatedGpu", "nvidia", "amdGpu", "intelGpu", "hags", "modernStandby", "s3", "hibernation", "systemSsd", "win11", "recallPolicy", "edge", "brave" }) caps[k] = !missing.Contains(k);
        caps["laptop"] = laptop; caps["desktop"] = !laptop; caps["integratedOnly"] = false; caps["systemHdd"] = false; caps["win10"] = false;
        return new Dictionary<string, object> { { "chassis", new Dictionary<string, object> { { "isLaptop", laptop } } }, { "capabilities", caps }, { "gpus", new List<object>() } };
    }

    private static void CheckCeitoMotionLifecycle()
    {
        Window saved = _window;
        _window = (Window)Activator.CreateInstance(_type, new object[] { Path.Combine(Path.GetTempPath(), "blackwall-motion-test.json") });
        try
        {
            PrepareOffscreenWindow();
            SetField("_reduceMotion", false);
            MarkLoaded("tweaks");
            Call("SwitchTab", "ceito");
            Func<DependencyObject> page = () => (DependencyObject)((ScrollViewer)Field("_mainScroll")).Content;
            var edge = (RotateTransform)Field("_ceitoEdgeRotation");
            var pulse = (ScaleTransform)Descendants(page()).OfType<Border>().First(b => b.Width == 84 && b.RenderTransform is ScaleTransform).RenderTransform;
            Check(!edge.HasAnimatedProperties && !pulse.HasAnimatedProperties, "hidden Ceito starts without emblem motion");

            var activeKey = (DependencyPropertyKey)typeof(Window).GetField("IsActivePropertyKey", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            _window.Show(); _window.SetValue(activeKey, true); Call("UpdateAmbientMotion");
            Check(_window.IsVisible && _window.IsActive && edge.HasAnimatedProperties && pulse.HasAnimatedProperties, "visible active Ceito starts emblem motion");
            _window.SetValue(activeKey, false); Call("UpdateAmbientMotion");
            Check(!edge.HasAnimatedProperties && !pulse.HasAnimatedProperties, "deactivation stops Ceito motion");
            _window.SetValue(activeKey, true); Call("UpdateAmbientMotion");
            _window.Hide();
            var delay = Stopwatch.StartNew(); PumpUntil(() => delay.ElapsedMilliseconds >= 650);
            Check(!edge.HasAnimatedProperties && !pulse.HasAnimatedProperties, "hiding during the entrance cannot restart the pulse");
            _window.Show(); _window.SetValue(activeKey, true); Call("UpdateAmbientMotion");
            Check(edge.HasAnimatedProperties && pulse.HasAnimatedProperties, "showing and activating Ceito resumes motion");
            _window.WindowState = WindowState.Minimized;
            Check(!edge.HasAnimatedProperties && !pulse.HasAnimatedProperties, "minimizing stops Ceito motion");
            _window.WindowState = WindowState.Normal; _window.SetValue(activeKey, true); Call("UpdateAmbientMotion");
            SetField("_reduceMotion", true); Call("UpdateMotionPreference");
            Check(!edge.HasAnimatedProperties && !pulse.HasAnimatedProperties, "reduced motion stops Ceito motion");
            SetField("_reduceMotion", false); Call("UpdateMotionPreference");
            Check(edge.HasAnimatedProperties && pulse.HasAnimatedProperties, "restoring motion preference resumes Ceito motion");

            SetField("_ceitoChecking", true);
            Descendants(page()).OfType<Button>().First(b => Convert.ToString(b.Content) == "Siguiente").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var arc = (RotateTransform)Descendants(page()).OfType<System.Windows.Shapes.Path>().First(p => p.RenderTransform is RotateTransform).RenderTransform;
            Check(!pulse.HasAnimatedProperties && arc.HasAnimatedProperties, "changing wizard step releases the old pulse and starts the waiting arc");
            _window.Hide();
            Check(!edge.HasAnimatedProperties && !arc.HasAnimatedProperties, "hiding stops the waiting arc");
            _window.Show(); _window.SetValue(activeKey, true); Call("UpdateAmbientMotion");
            Check(edge.HasAnimatedProperties && arc.HasAnimatedProperties, "the waiting arc resumes when allowed");
            Call("SwitchTab", "winutil");
            Check(!edge.HasAnimatedProperties && !arc.HasAnimatedProperties, "leaving Ceito releases its clocks");
        }
        finally { _window.Close(); _window = saved; }
    }

    private static void PrepareOffscreenWindow()
    {
        ((DispatcherTimer)Field("_scriptMonitorTimer")).Stop();
        // Do not run the app's startup WMI/update handlers when showing this offscreen window.
        object store = typeof(UIElement).GetProperty("EventHandlersStore", Private).GetValue(_window, null);
        var handlers = (RoutedEventHandlerInfo[])store.GetType().GetMethod("GetRoutedEventHandlers", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Invoke(store, new object[] { FrameworkElement.LoadedEvent });
        foreach (var handler in handlers) _window.RemoveHandler(FrameworkElement.LoadedEvent, handler.Handler);
        _window.WindowState = WindowState.Normal;
        _window.ShowInTaskbar = false; _window.ShowActivated = false;
        _window.Left = -12000; _window.Top = -12000;
    }

    private static void CheckHiddenMonitorPolling()
    {
        Window saved = _window;
        _window = (Window)Activator.CreateInstance(_type, new object[] { Path.Combine(Path.GetTempPath(), "blackwall-monitor-test.json") });
        try
        {
            PrepareOffscreenWindow();
            var timer = (DispatcherTimer)Field("_scriptMonitorTimer");
            var onTick = (EventHandler)typeof(DispatcherTimer).GetField("Tick", Private).GetValue(timer);
            Action tick = () => onTick(timer, EventArgs.Empty);
            SetField("_activeTab", "monitor");
            tick();
            Check(!Reads.Contains("monitor") && !Errors.Contains("monitor"), "hidden monitor does not start polling");
            _window.Show();
            SetField("_activeTab", "winutil"); tick();
            Check(!Reads.Contains("monitor"), "another tab does not start monitor polling");
            SetField("_activeTab", "monitor"); _window.WindowState = WindowState.Minimized; tick();
            Check(!Reads.Contains("monitor"), "minimized monitor does not start polling");
            _window.WindowState = WindowState.Normal;
            var pending = new TaskCompletionSource<int>();
            Reads["monitor"] = pending.Task; tick();
            Check(Object.ReferenceEquals(Reads["monitor"], pending.Task), "an in-flight monitor read is not replaced");
            Reads.Remove("monitor");
            Errors["monitor"] = "fixture"; tick();
            Check(!Reads.Contains("monitor"), "a monitor error prevents automatic retry");
            Errors.Remove("monitor");
            tick();
            Check(Reads.Contains("monitor"), "visible monitor starts polling");
            Task read = (Task)Reads["monitor"];
            _window.Hide();
            PumpUntil(() => read.IsCompleted, 30);
        }
        finally { _window.Close(); _window = saved; }
    }

    private static void CheckActiveNavDoesNotRebuild()
    {
        Window saved = _window;
        _window = (Window)Activator.CreateInstance(_type, new object[] { Path.Combine(Path.GetTempPath(), "blackwall-nav-test.json") });
        try
        {
            ((DispatcherTimer)Field("_scriptMonitorTimer")).Stop();
            SetField("_reduceMotion", true);
            SetField("_hardware", HardwareFixture(false));
            MarkLoaded("tweaks");
            Call("SwitchTab", "winutil");
            var scroll = (ScrollViewer)Field("_mainScroll");
            var root = (FrameworkElement)_window.Content;
            _window.Content = null;
            using (var host = new HwndSource(new HwndSourceParameters("Blackwall navigation check") { Width = 1100, Height = 650, PositionX = -12000, PositionY = -12000, WindowStyle = unchecked((int)0x90000000) }))
            {
                host.RootVisual = root;
                PumpUntil(() => { root.UpdateLayout(); return scroll.ScrollableHeight > 300; }, 10);
                scroll.ScrollToVerticalOffset(200); scroll.UpdateLayout();
                double offset = scroll.VerticalOffset;
                Check(offset > 0, "active navigation fixture is scrolled");
                if ((bool)Field("_sidebarCollapsed")) Call("ToggleSidebar");
                object page = scroll.Content;
                ((Dictionary<string, Button>)Field("_navButtons"))["winutil"].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                scroll.UpdateLayout();
                Check(Object.ReferenceEquals(page, scroll.Content) && scroll.VerticalOffset == offset, "clicking the active tab preserves the page and scroll");
                Check((bool)Field("_sidebarCollapsed"), "clicking the active tab still closes the sidebar");

                string target = Convert.ToString(Descendants((DependencyObject)page).First(e => e.Uid == "motion-card" && e.Tag != null).Tag);
                var item = ((List<Dictionary<string, object>>)Field("_tweaks")).First(t => Convert.ToString(t["id"]) == target);
                string title = Convert.ToString(item["title"]);
                ((TextBox)Field("_sectionSearch")).Text = "zzzz-hide-target";
                ((TextBox)Field("_omniboxInput")).Text = title;
                var result = ((IList)Field("_omniboxItems")).Cast<Button>().First(b => System.Windows.Automation.AutomationProperties.GetName(b).StartsWith(title + "."));
                result.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => Field("_pendingSearchId") == null, 10);
                Check(!Object.ReferenceEquals(page, scroll.Content) && (string)Field("_tweakSearchText") == "" && Descendants((DependencyObject)scroll.Content).Any(e => e.Uid == "motion-card" && Convert.ToString(e.Tag) == target && e.Visibility == Visibility.Visible), "global search opens its target in the current tab after clearing the filter");
                host.RootVisual = null;
            }
            _window.Content = root;
        }
        finally { _window.Close(); _window = saved; }
    }

    private static void CheckStableSectionFilters(string directory)
    {
        SetField("_activeTab", "winutil");
        SetField("_hardware", HardwareFixture(false));
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

        SetField("_activeTab", "winutil");
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
        ((HashSet<string>)Field("_selectedApps")).Add("fixture-selection");
        Call("ToggleSidebar");
        scroll.UpdateLayout();
        Check(((Border)Field("_sidebarBorder")).Width == 72, "collapsed menu is 72 DIP");
        Check(Object.ReferenceEquals(page, scroll.Content) && scroll.VerticalOffset == offset, "menu toggle preserves page and scroll");
        Check((string)Field("_activeTab") == "winutil" && ((HashSet<string>)Field("_selectedApps")).Contains("fixture-selection"), "menu toggle preserves navigation, filters and app selection");
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
        object presentationHardware = Field("_hardware");
        SetField("_tweakSearchText", ""); SetField("_hardware", HardwareFixture(false));
        var view = (FrameworkElement)Call("BuildWinUtilView");
        string pageText = string.Join(" | ", Descendants(view).OfType<TextBlock>().Select(t => t.Text));
        var buttons = Descendants(view).OfType<Button>().Select(b => Convert.ToString(b.Content)).ToList();
        Check(new[] { "WinUtil", "Esenciales", "Avanzados", "Planes de energía", "Preferencias", "Liberar espacio" }.All(pageText.Contains) && new[] { "Mínimo", "Recomendado", "Completo" }.All(buttons.Contains)
            && !pageText.Contains("Recomendadas") && ((Dictionary<string, Button>)Field("_navButtons")).Keys.Contains("winutil") && ((Dictionary<string, Button>)Field("_navButtons")).Keys.Contains("ceito") && !((Dictionary<string, Button>)Field("_navButtons")).Keys.Any(k => k == "optimization" || k == "debloat"),
            "WinUtil shows Esenciales, Avanzados, energía, Preferencias and cleanup, with its three profiles and nothing from the old sections (falta: " + string.Join(",", new[] { "WinUtil", "Esenciales", "Avanzados", "Planes de energía", "Preferencias", "Liberar espacio" }.Where(x => !pageText.Contains(x))) + "; botones: " + string.Join(",", buttons.Take(12)) + "; viejos: " + pageText.Contains("Rendimiento") + pageText.Contains("Recomendadas") + ")");

        // Section switch paints the first cards at once and adds the rest on idle frames: none may be lost.
        object loadedModules = Field("_loadedModules");
        loadedModules.GetType().GetMethod("Add").Invoke(loadedModules, new object[] { "tweaks" });
        SetField("_tweakSearchText", ""); SetField("_pendingSearchId", null);
        string previousTab = (string)Field("_activeTab");
        Call("SwitchTab", "safety");
        Call("SwitchTab", "winutil");
        var catalog = (List<Dictionary<string, object>>)Field("_tweaks");
        var debloatIds = new HashSet<string>(catalog.Where(t => Convert.ToString(t["section"]) == "winutil" && !(t.ContainsKey("preference") && (bool)t["preference"]) && (bool)Call("TweakAvailable", t)).Select(t => Convert.ToString(t["id"])));
        Func<int> shownCards = () => Descendants((DependencyObject)((ScrollViewer)Field("_mainScroll")).Content).Count(e => e.Tag is string && debloatIds.Contains((string)e.Tag));
        int firstFrameCards = shownCards();
        PumpUntil(() => shownCards() == debloatIds.Count);
        Check(firstFrameCards < debloatIds.Count && shownCards() == debloatIds.Count, "section switch defers cards but every card arrives (" + firstFrameCards + " then " + debloatIds.Count + ")");
        SetField("_hardware", presentationHardware);
        Call("SwitchTab", previousTab);
        scroll.Content = previous;
        SetField("_reduceMotion", false);
        Call("UpdateMotionPreference");
    }

    private static void CheckCpuCacheMb()
    {
        MethodInfo format = _type.GetMethod("FormatCpuCacheMb", BindingFlags.Static | BindingFlags.NonPublic);
        Check(format != null, "CPU cache has a shared MB formatter");
        Func<string, string> mb = value => (string)format.Invoke(null, new object[] { value });
        var culture = Thread.CurrentThread.CurrentCulture;
        object saved = Field("_hardware");
        try
        {
            Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
            Check(mb("3072") == "3 MB" && mb("8192") == "8 MB", "CPU cache uses whole MB without decimals");
            Check(mb("1280") == "1,25 MB" && mb("1536") == "1,5 MB" && mb("1365") == "1,33 MB", "CPU cache uses at most two decimals and a Spanish comma on any locale");
            Check(mb("0") == "" && mb("") == "" && mb(null) == "" && mb("N/D") == "", "missing or zero CPU cache stays unavailable");
            var hardware = HardwareFixture(false);
            hardware["cpu"] = new Dictionary<string, object> { { "l2CacheKb", 3072 }, { "l3CacheKb", 1280 } };
            SetField("_hardware", hardware);
            var view = (DependencyObject)Call("BuildHardwareView");
            Check(Descendants(view).OfType<TextBox>().Any(t => t.Text == "3 MB / 1,25 MB"), "CPU card displays cache in MB");
            Check(((string)Call("RenderHardwareReport", "txt", false)).Contains("3 MB / 1,25 MB"), "hardware report displays cache in MB");
        }
        finally { Thread.CurrentThread.CurrentCulture = culture; SetField("_hardware", saved); }
    }

    private static void CheckAppxRemoval()
    {
        MethodInfo guard = _type.GetMethod("IsAllowedAppxPackage", BindingFlags.Static | BindingFlags.NonPublic);
        Check(guard != null, "APPX removal has a catalog-independent package guard");
        Func<string, bool> allowed = name => (bool)guard.Invoke(null, new object[] { name });
        Func<string, string, Dictionary<string, object>> op = (type, name) => new Dictionary<string, object> { { "type", type }, { "name", name } };
        var names = new[] { "Microsoft.WindowsFeedbackHub", "Microsoft.GetHelp", "Microsoft.MicrosoftOfficeHub", "Clipchamp.Clipchamp", "Microsoft.WindowsAlarms", "MicrosoftCorporationII.QuickAssist", "Microsoft.WindowsSoundRecorder", "Microsoft.MicrosoftStickyNotes", "Microsoft.Todos", "Microsoft.MicrosoftSolitaireCollection", "Microsoft.PowerAutomateDesktop", "Microsoft.Windows.DevHome", "Microsoft.BingWeather", "Microsoft.StartExperiencesApp", "Microsoft.BingNews", "Microsoft.Copilot", "Microsoft.BingSearch" };
        Check(names.All(allowed), "all 17 approved APPX packages are accepted");
        var protectedNames = new[] { "Microsoft.WindowsStore", "Microsoft.DesktopAppInstaller", "Microsoft.StorePurchaseApp", "Microsoft.WindowsCalculator", "Microsoft.XboxApp", "Microsoft.XboxGamingOverlay", "Microsoft.GamingApp", "Microsoft.SecHealthUI", "Microsoft.Windows.ShellExperienceHost", "Microsoft.Windows.StartMenuExperienceHost", "MicrosoftWindows.Client.CBS", "Microsoft.UI.Xaml.2.8", "Microsoft.VCLibs.140", "Microsoft.NET.Native.Runtime.2.2", "Microsoft.WindowsAppRuntime.1.6", "Microsoft.Windows.Photos", "Microsoft.WindowsNotepad", "Microsoft.Paint", "Microsoft.ScreenSketch", "Microsoft.WindowsTerminal" };
        Check(protectedNames.All(n => !allowed(n) && !allowed(n.ToLowerInvariant())), "APPX deny list protects system, Store, Calculator, Xbox and frameworks regardless of case");
        Check(new[] { "Microsoft.OtherApp", "Microsoft.YourPhone", "Microsoft.*", "Microsoft.GetHelp'", "Microsoft.GetHelp;calc", "Microsoft.GetHelp\n", "GetHelp", "", null }.All(n => !allowed(n)), "APPX guard rejects unknown names, wildcards, quotes and malformed names");

        object savedRunner = Field("_appxTool"), savedHardware = Field("_hardware");
        var tweaks = (List<Dictionary<string, object>>)Field("_tweaks");
        var states = (Dictionary<string, bool>)Field("_tweakStates");
        Func<List<Dictionary<string, object>>> changes = () => (List<Dictionary<string, object>>)Field("_changes");
        Func<List<Dictionary<string, object>>> restores = () => changes().SelectMany(c => ((IEnumerable)c["restoreOps"]).Cast<Dictionary<string, object>>()).ToList();
        var installed = new HashSet<string>(); var provisioned = new HashSet<string>();
        var calls = new List<string>(); var installs = new List<string>();
        string fail = null, inventoryOverride = null; bool partial = false, unreportedRemoval = false, wrongStoreId = false, appearAfterCapture = false; int queries = 0;
        var json = new System.Web.Script.Serialization.JavaScriptSerializer();
        Func<string, string, string> fake = (tool, command) =>
        {
            calls.Add(tool + " " + command);
            if (tool == "winget")
            {
                var ids = new Dictionary<string, string> { { "9NBLGGH4R32N", names[0] }, { "9PKDZBMV1H3T", names[1] }, { "9NBLGGH5R558", names[8] } };
                string id = command.Split('"')[1];
                if (!command.StartsWith("install --id \"") || !command.Contains("--source msstore") || !command.Contains("--exact") || !command.Contains("--disable-interactivity") || !ids.ContainsKey(id))
                    throw new InvalidOperationException("unexpected Store install: " + command);
                if (wrongStoreId) throw new InvalidOperationException("Store no disponible");
                installs.Add(ids[id]); installed.Add(ids[id]); return "Installed";
            }
            if (tool != "powershell") throw new InvalidOperationException("unexpected tool: " + tool);
            var match = System.Text.RegularExpressions.Regex.Match(command, @"\$name = '([^']+)'");
            if (!match.Success || !names.Contains(match.Groups[1].Value) || !command.Contains("-AllUsers") || !command.Contains("Name -eq $name") || !command.Contains("DisplayName -eq $name"))
                throw new InvalidOperationException("unsafe APPX query: " + command);
            string name = match.Groups[1].Value;
            if (!command.Contains("Remove-Appx") && appearAfterCapture && name == names[0] && ++queries == 2) installed.Add(name);
            if (!command.Contains("Remove-Appx")) return json.Serialize(new { installed = installed.Contains(name), provisioned = provisioned.Contains(name), inventory = inventoryOverride ?? (installed.Contains(name) ? "package:" + name + ";registered:" + name + ":user" : "") + (provisioned.Contains(name) ? ";provisioned:" + name : "") });
            if (!command.Contains("Remove-AppxPackage -Package $pkg.PackageFullName -AllUsers -ErrorAction Stop") || !command.Contains("Remove-AppxProvisionedPackage -Online -PackageName $prov.PackageName -ErrorAction Stop") || !command.Contains("-PackageTypeFilter Bundle,Main"))
                throw new InvalidOperationException("incomplete APPX removal");
            string journal = File.ReadAllText((string)Field("_changesPath"));
            if (!journal.Contains(name) || !journal.Contains("appx_install")) throw new InvalidOperationException("APPX removal was not journaled first");
            bool removed = false;
            if (fail != name || partial) removed = installed.Remove(name);
            if (fail != name) removed = provisioned.Remove(name) || removed;
            return json.Serialize(new { removed = unreportedRemoval ? false : removed, error = fail == name ? "acceso denegado" : "" });
        };
        Func<string[], Dictionary<string, object>> fixture = packages => new Dictionary<string, object> { { "id", "test-appx" }, { "title", "APPX de prueba" }, { "shortTitle", "APPX de prueba" }, { "section", "winutil" }, { "riskLevel", "medium" }, { "deviceScope", new[] { "laptop", "desktop" } },
            { "applyOps", packages.Select(n => op("appx_remove", n)).ToList() }, { "undoOps", new[] { op("appx_restore", "") } } };
        Func<Dictionary<string, object>, bool, Task> run = (t, apply) => { Task task = (Task)Call("ExecuteTweakAsync", t, apply); PumpUntil(() => task.IsCompleted, 10); return task; };
        Dictionary<string, string> storeIds = null; string savedId = null;
        try
        {
            SetField("_appxTool", fake); SetField("_hardware", HardwareFixture(false));
            changes().Clear(); Call("SaveJournal");
            var injected = fixture(protectedNames.Concat(new[] { "Microsoft.OtherApp", "Microsoft.GetHelp';calc" }).ToArray());
            tweaks.Add(injected);
            try
            {
                bool rejected = true;
                foreach (var candidate in ((IEnumerable)injected["applyOps"]).Cast<Dictionary<string, object>>())
                {
                    foreach (string method in new[] { "CaptureRestoreOps", "RunTweakOperation" })
                    {
                        try { Call(method, candidate); rejected = false; }
                        catch (TargetInvocationException ex) { rejected &= ex.InnerException is InvalidOperationException; }
                    }
                    rejected &= !(bool)Call("IsAllowedRestoreOp", op("appx_install", Convert.ToString(candidate["name"])));
                }
                Check(rejected && calls.Count == 0, "even injected catalog operations cannot remove or restore denied or unapproved APPX packages");
            }
            finally { tweaks.Remove(injected); }

            var absent = fixture(new[] { names[4] });
            Check(run(absent, true).Status == TaskStatus.RanToCompletion && restores().Count == 0 && !calls.Any(c => c.Contains("Remove-Appx")), "absent APPX package is a no-op success without a reinstall record");
            Check(run(absent, false).Status == TaskStatus.RanToCompletion && installs.Count == 0, "reverting an absent APPX package installs nothing");
            Check(run(absent, false).Status == TaskStatus.RanToCompletion && installs.Count == 0, "APPX catalog undo without a journal never installs all packages");
            changes().Clear(); Call("SaveJournal"); calls.Clear();
            installed.Add(names[0]); provisioned.Add(names[0]); provisioned.Add(names[8]);
            var selected = fixture(new[] { names[0], names[4], names[8] });
            Check(run(selected, true).Status == TaskStatus.RanToCompletion && installed.Count == 0 && provisioned.Count == 0, "APPX apply removes all-user registrations and provisioning, including provisioned-only apps");
            Check(restores().Select(o => Convert.ToString(o["name"])).OrderBy(n => n).SequenceEqual(new[] { names[0], names[8] }.OrderBy(n => n)) && restores().All(o => !(o.ContainsKey("pending") && (bool)o["pending"])), "APPX journal records only packages actually present and removed");
            Check(calls.Count(c => c.Contains("Remove-AppxProvisionedPackage -Online")) == 2 && ((string)Call("VerifyOperation", op("appx_remove", names[0]))).Contains("verificado"), "APPX deprovisioning is called and absence is verified afterwards");
            Call("LoadJournal");
            Check(restores().Count == 2 && restores().All(o => (bool)Call("IsAllowedRestoreOp", o)), "APPX reinstall records survive journal validation and reload");
            Check((bool)Call("IsTweakTouched", selected, null) && (bool)Call("IsTweakActive", selected, null), "removed APPX packages stay revertible after a state refresh");
            Check(run(selected, false).Status == TaskStatus.RanToCompletion && installs.OrderBy(n => n).SequenceEqual(new[] { names[0], names[8] }.OrderBy(n => n)) && changes().Count == 0, "APPX revert reinstalls only the removed packages from msstore");

            installed.Add(names[1]); provisioned.Add(names[1]); fail = names[1];
            var broken = fixture(new[] { names[1] });
            Check(run(broken, true).IsFaulted && restores().Count == 0, "a failed APPX removal that changed nothing leaves no reinstall record");
            changes().Clear(); Call("SaveJournal"); partial = true;
            Check(run(broken, true).IsFaulted && !installed.Contains(names[1]) && provisioned.Contains(names[1]) && restores().Count == 1, "a partial APPX removal keeps its recovery record and reports failure");
            Check(states["test-appx"] && ((HashSet<string>)Field("_tweakPartial")).Contains("test-appx"), "a partial APPX failure immediately exposes the revert action");
            fail = null; partial = false; wrongStoreId = true;
            Check(run(broken, false).IsFaulted && restores().Count == 1, "a failed Store reinstall keeps the APPX journal for retry");
            Check(states["test-appx"] && ((HashSet<string>)Field("_tweakPartial")).Contains("test-appx"), "a failed Store reinstall keeps the immediate revert action available");
            wrongStoreId = false;
            Task all = (Task)Call("RevertAllRecordedChangesAsync"); PumpUntil(() => all.IsCompleted, 10);
            Check(all.Status == TaskStatus.RanToCompletion && installed.Contains(names[1]) && changes().Count == 0, "Revertir todo also restores APPX through the fake Store runner");

            installs.Clear(); installed.Clear(); provisioned.Clear(); installed.Add(names[0]);
            Check(run(fixture(new[] { names[0] }), true).Status == TaskStatus.RanToCompletion, "APPX fixture is removed before testing missing Store metadata");
            storeIds = (Dictionary<string, string>)_type.GetField("AppxStoreIds", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            savedId = storeIds[names[0]]; storeIds[names[0]] = "";
            Check(run(fixture(new[] { names[0] }), false).Status == TaskStatus.RanToCompletion && installs.Count == 0
                && File.ReadAllText((string)Call("get_ChangeLogPath")).Contains("reinstálala desde Microsoft Store"), "an APPX package without a confirmed Store ID gives manual instructions without failing revert");
            storeIds[names[0]] = savedId;

            installed.Clear(); provisioned.Clear(); installs.Clear(); changes().Clear();
            var interrupted = op("appx_install", names[0]); interrupted["pending"] = true; interrupted["installed"] = true; interrupted["provisioned"] = false;
            changes().Add(new Dictionary<string, object> { { "id", "test-appx" }, { "title", "Aplicación interrumpida" }, { "restoreOps", new[] { interrupted } } }); Call("SaveJournal");
            Check(run(fixture(new[] { names[0] }), true).Status == TaskStatus.RanToCompletion && restores().Count == 1 && (bool)restores()[0]["pending"], "an absent retry preserves an older interrupted APPX recovery record");
            Check(run(fixture(new[] { names[0] }), false).Status == TaskStatus.RanToCompletion && installs.SequenceEqual(new[] { names[0] }), "APPX undo recovers a removal interrupted before its journal confirmation");
            installed.Clear(); provisioned.Clear(); installs.Clear(); calls.Clear(); appearAfterCapture = true; queries = 0;
            Check(run(fixture(new[] { names[0] }), true).IsFaulted && installed.Contains(names[0]) && !calls.Any(c => c.Contains("Remove-Appx")), "an APPX package appearing after capture cannot be removed without a saved recovery record");
            appearAfterCapture = false; changes().Clear(); Call("SaveJournal");

            installed.Add(names[0]); provisioned.Add(names[0]); installs.Clear(); inventoryOverride = "version-2|user-B;provisioned";
            interrupted["pending"] = true; interrupted["installed"] = true; interrupted["provisioned"] = true; interrupted["inventory"] = "version-1|user-A;version-2|user-B;provisioned";
            changes().Add(new Dictionary<string, object> { { "id", "test-appx" }, { "title", "Versión quitada antes de interrumpirse" }, { "restoreOps", new[] { interrupted } } }); Call("SaveJournal");
            Check(run(fixture(new[] { names[0] }), false).Status == TaskStatus.RanToCompletion && installs.SequenceEqual(new[] { names[0] }), "pending APPX undo detects a removed version or user registration even when other versions and provisioning remain");
            installs.Clear(); interrupted["inventory"] = inventoryOverride;
            changes().Add(new Dictionary<string, object> { { "id", "test-appx" }, { "title", "Captura sin cambios" }, { "restoreOps", new[] { interrupted } } }); Call("SaveJournal");
            Check(run(fixture(new[] { names[0] }), false).Status == TaskStatus.RanToCompletion && installs.Count == 0, "an interrupted APPX capture with an unchanged inventory installs nothing");
            inventoryOverride = "version-2|user-B;version-2|user-C;provisioned";
            changes().Add(new Dictionary<string, object> { { "id", "test-appx" }, { "title", "Otro usuario agregó la app" }, { "restoreOps", new[] { interrupted } } }); Call("SaveJournal");
            Check(run(fixture(new[] { names[0] }), false).Status == TaskStatus.RanToCompletion && installs.Count == 0, "additional APPX registrations after an interrupted capture are not treated as removals");
            inventoryOverride = null;

            installed.Clear(); provisioned.Clear(); installs.Clear(); installed.Add(names[1]); provisioned.Add(names[1]); fail = names[1]; partial = true; unreportedRemoval = true;
            Check(run(broken, true).IsFaulted && !installed.Contains(names[1]) && restores().Count == 1, "an APPX cmdlet that removes a registration before throwing keeps recovery even without a success marker");
            fail = null; partial = false; unreportedRemoval = false;
            Check(run(broken, false).Status == TaskStatus.RanToCompletion && installs.SequenceEqual(new[] { names[1] }), "APPX recovery survives a cmdlet failure after partial progress");

            var catalog = tweaks.FirstOrDefault(t => Convert.ToString(t["id"]) == "winutil_remove_microsoft_apps");
            Check(catalog != null && ((IEnumerable)catalog["applyOps"]).Cast<Dictionary<string, object>>().Select(o => Convert.ToString(o["name"])).SequenceEqual(names), "WinUtil catalog removes exactly the 17 approved apps, without Calculator or Xbox");
            var profiles = (Dictionary<string, string[]>)_type.GetField("WinUtilProfiles", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            Check(Convert.ToString(catalog["riskLevel"]) == "medium" && Convert.ToString(catalog["section"]) == "winutil" && !profiles.Values.Any(p => p.Contains("winutil_remove_microsoft_apps")) && !catalog.ContainsKey("defaultOn"), "APPX removal is medium risk and outside all default profiles");
        }
        finally
        {
            if (storeIds != null && savedId != null) storeIds[names[0]] = savedId;
            changes().Clear(); Call("SaveJournal"); states.Remove("test-appx"); ((HashSet<string>)Field("_tweakPartial")).Remove("test-appx");
            SetField("_appxTool", savedRunner); SetField("_hardware", savedHardware);
        }
    }

    private static void CheckHardwareReports()
    {
        object saved = Field("_hardware");
        Func<object[], List<object>> list = items => items.ToList();
        var fixture = new Dictionary<string, object>
        {
            { "chassis", new Dictionary<string, object> { { "manufacturer", "ACME" }, { "model", "Modelo <script>alert(1)</script>" }, { "deviceType", "Laptop" }, { "isLaptop", true }, { "serialNumber", "SERIAL-ABC-12345" }, { "uuid", "UUID-SECRET-0001" } } },
            { "motherboard", new Dictionary<string, object> { { "manufacturer", "ACME" }, { "product", "Board" }, { "serialNumber", "BOARD-SERIAL-9876" }, { "bootMode", "UEFI" } } },
            { "cpu", new Dictionary<string, object> { { "name", "CPU de prueba" }, { "physicalCores", 6 }, { "logicalThreads", 12 } } },
            { "memory", new Dictionary<string, object> { { "totalGb", 16 }, { "usagePercent", 40 }, { "memoryType", "DDR4" }, { "slots", list(new object[] { new Dictionary<string, object> { { "slotName", "DIMM 0" }, { "occupied", true }, { "capacityGb", 8 }, { "serialNumber", "STICK-SERIAL-5555" } } }) } } },
            { "storage", list(new object[] { new Dictionary<string, object> { { "friendlyName", "Disco; \"rápido\"" }, { "sizeGb", 500 }, { "freeGb", 200 }, { "serialNumber", "DISK-SERIAL-7777" } } }) },
            { "gpus", list(new object[] { new Dictionary<string, object> { { "name", "GPU de prueba" } } }) },
            { "battery", new Dictionary<string, object> { { "present", true }, { "healthPercent", 90 } } },
            { "osNetwork", new Dictionary<string, object> { { "caption", "Windows 11" }, { "partialProductKey", "KEY99" }, { "adapters", list(new object[] { new Dictionary<string, object> { { "name", "Wi-Fi" }, { "macAddress", "AA-BB-CC-DD-EE-FF" }, { "ipv4", "192.168.1.50" } } }) } } },
            { "scannedAt", "12:00:00" }
        };
        SetField("_hardware", fixture);
        try
        {
            string[] secrets = { "SERIAL-ABC-12345", "UUID-SECRET-0001", "BOARD-SERIAL-9876", "STICK-SERIAL-5555", "DISK-SERIAL-7777", "AA-BB-CC-DD-EE-FF", "192.168.1.50", "KEY99" };
            foreach (string format in new[] { "html", "md", "txt", "csv", "json" })
            {
                string hidden = (string)Call("RenderHardwareReport", format, true);
                string shown = (string)Call("RenderHardwareReport", format, false);
                Check(secrets.All(secret => !hidden.Contains(secret)) && hidden.Contains("2345"), format + " report hides identifiers but keeps the last 4 serial characters");
                Check(shown.Contains("SERIAL-ABC-12345") && shown.Contains("192.168.1.50"), format + " report includes identifiers when the user chooses to");
                Check(new[] { "Resumen", "Equipo y firmware", "Procesador", "Memoria", "Almacenamiento", "Gráficos", "Batería", "Windows y red", "Estado de Blackwall" }.All(title => hidden.IndexOf(title, StringComparison.OrdinalIgnoreCase) >= 0), format + " report has every section");
            }
            string html = (string)Call("RenderHardwareReport", "html", true);
            Check(!html.Contains("<script>alert(1)</script>") && html.Contains("&lt;script&gt;"), "HTML report escapes text taken from the hardware");
            string csv = (string)Call("RenderHardwareReport", "csv", true);
            Check(csv.StartsWith("Sección;Elemento;Propiedad;Valor") && csv.Contains("\"Unidades: Disco; \"\"rápido\"\"\""), "CSV report quotes cells with separators and quotes");
            string json = (string)Call("RenderHardwareReport", "json", true);
            var parsed = (Dictionary<string, object>)new System.Web.Script.Serialization.JavaScriptSerializer().DeserializeObject(json);
            Check(parsed.ContainsKey("secciones") && !parsed.ContainsKey("datosOriginales") && json.Contains("\n  "), "JSON report is valid, indented and drops raw data when anonymized");
            byte[] xlsx = (byte[])Call("RenderHardwareReportXlsx", true);
            using (var stream = new MemoryStream(xlsx))
            using (var package = System.IO.Packaging.Package.Open(stream, FileMode.Open, FileAccess.Read))
            {
                var sheets = package.GetParts().Where(p => p.Uri.ToString().StartsWith("/xl/worksheets/")).ToList();
                string all = string.Concat(package.GetParts().Select(p => new StreamReader(p.GetStream()).ReadToEnd()));
                Check(sheets.Count == 9 && package.GetRelationships().Any() && all.Contains("<sheet name=\"Equipo y firmware\""), "Excel report has one named sheet per section");
                Check(secrets.All(secret => !all.Contains(secret)) && all.Contains("6 núcleos, 12 hilos") && !all.Contains("<v>"), "Excel report hides identifiers and stores every value as text (no auto dates)");
            }
        }
        finally { SetField("_hardware", saved); }
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

        var states = (Dictionary<string, bool>)Field("_tweakStates");
        var changes = (List<Dictionary<string, object>>)Field("_changes");
        object savedHardware = Field("_hardware");
        SetField("_hardware", HardwareFixture(false));
        Func<string, object, Dictionary<string, object>> dword = (name, value) => new Dictionary<string, object> { { "type", "reg_dword" }, { "hive", "HKCU" }, { "key", testKey }, { "name", name }, { "value", value } };
        try
        {
            var good = new Dictionary<string, object> { { "id", "engine-good" }, { "title", "Prueba" },
                { "applyOps", new object[] { dword("Big", 2147483649L), dword("Small", 1) } },
                { "undoOps", new object[] { new Dictionary<string, object> { { "type", "reg_delete" }, { "hive", "HKCU" }, { "key", testKey }, { "name", "Big" } }, dword("Small", 0) } } };
            Task applied = run(good, true);
            PumpUntil(() => applied.IsCompleted);
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(testKey))
                Check(applied.Status == TaskStatus.RanToCompletion && unchecked((uint)(int)k.GetValue("Big")) == 0x80000001 && states["engine-good"] && (bool)Call("IsTweakActive", good, null), "apply writes 0x80000001 correctly and detection confirms every value" + (applied.Exception == null ? "" : ": " + applied.Exception.GetBaseException().Message));
            Task undone = run(good, false);
            PumpUntil(() => undone.IsCompleted);
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(testKey))
                Check(undone.Status == TaskStatus.RanToCompletion && k.GetValue("Big") == null && !(bool)Call("IsTweakActive", good, null), "undo restores and detection reports inactive");

            var partial = new Dictionary<string, object> { { "id", "engine-partial" }, { "title", "Prueba" },
                { "applyOps", new object[] { dword("Partial", 7), dword("Broken", 5000000000L) } } };
            Task failed = run(partial, true);
            PumpUntil(() => failed.IsCompleted);
            Check(failed.IsFaulted && failed.Exception.InnerException.Message.Contains("1 de 2") && !states["engine-partial"], "a rejected change is reported as an error, never as applied");
            Check(changes.Any(c => Convert.ToString(c["id"]) == "engine-partial"), "a half-applied tweak keeps its saved values so it can still be reverted");
            changes.Clear();

            // The user's own values (not the catalog defaults) come back, per tweak and with Revertir todo.
            using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(testKey))
            {
                k.SetValue("Mine", 41, Microsoft.Win32.RegistryValueKind.DWord);
                k.SetValue("Text", "original", Microsoft.Win32.RegistryValueKind.ExpandString);
                k.SetValue("List", new[] { "a", "b" }, Microsoft.Win32.RegistryValueKind.MultiString);
            }
            Func<string, object[], Dictionary<string, object>> tweak = (id, ops) => new Dictionary<string, object> { { "id", id }, { "title", id }, { "shortTitle", id }, { "applyOps", ops },
                { "undoOps", new object[] { dword("Mine", 0) } } };
            var first = tweak("engine-first", new object[] { dword("Mine", 7), new Dictionary<string, object> { { "type", "reg_sz" }, { "hive", "HKCU" }, { "key", testKey }, { "name", "Text" }, { "value", "nuevo" } } });
            var second = tweak("engine-second", new object[] { dword("Mine", 9), dword("Fresh", 1), new Dictionary<string, object> { { "type", "reg_delete" }, { "hive", "HKCU" }, { "key", testKey }, { "name", "List" } } });
            foreach (var t in new[] { first, second }) { Task step = run(t, true); PumpUntil(() => step.IsCompleted); Check(step.Status == TaskStatus.RanToCompletion, "journaled apply succeeds: " + t["id"]); }
            Check(changes.Count == 2 && File.Exists(Path.Combine(Path.GetDirectoryName((string)Field("_preferencesPath")), "cambios.json")), "each apply is journaled to disk before it runs");
            Task single = run(first, false);
            PumpUntil(() => single.IsCompleted);
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(testKey))
                Check(single.Status == TaskStatus.RanToCompletion && (string)k.GetValue("Text", null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames) == "original" && k.GetValueKind("Text") == Microsoft.Win32.RegistryValueKind.ExpandString && changes.Count == 1,
                    "reverting one tweak restores the exact previous value and type, not the catalog default");
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(testKey))
                Check((int)k.GetValue("Mine") == 9, "reverting an older tweak keeps a newer tweak's change to the same value");
            Task again = run(first, true);
            PumpUntil(() => again.IsCompleted);
            Task all = (Task)Call("RevertAllRecordedChangesAsync");
            PumpUntil(() => all.IsCompleted);
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(testKey))
                Check(all.Status == TaskStatus.RanToCompletion && changes.Count == 0 && (int)k.GetValue("Mine") == 41 && k.GetValue("Fresh") == null && ((string[])k.GetValue("List")).SequenceEqual(new[] { "a", "b" }) && (string)k.GetValue("Text", null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames) == "original",
                    "Revertir todo undoes every change newest first back to the original values");
            string log = File.Exists((string)Call("get_ChangeLogPath")) ? File.ReadAllText((string)Call("get_ChangeLogPath")) : "";
            Check(log.Contains("| APLICAR | engine-first |") && log.Contains("Mine = 7 | OK · verificado") && log.Contains("| REVERTIR TODO |") && log.Contains("FALLO:"),
                "every write, revert and failure is logged with a re-read of the value");
            var report = (string)Call("BuildTweakStateReport");
            int rows = report.Split('\n').Count(l => l.StartsWith("[APLICADO") || l.StartsWith("[no aplicado") || l.StartsWith("[preferencia: "));
            Check(rows == ((List<Dictionary<string, object>>)Field("_tweaks")).Count && report.Contains("Cambios guardados por Blackwall"), "the state report lists every tweak and the journal");

            // A revert without saved values writes the catalog's values, and is itself journaled so it can be undone.
            using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(testKey)) k.SetValue("Mine", 41, Microsoft.Win32.RegistryValueKind.DWord);
            var catalogOnly = new Dictionary<string, object> { { "id", "engine-catalog" }, { "title", "Catálogo" }, { "shortTitle", "Catálogo" }, { "applyOps", new object[] { dword("Mine", 7) } }, { "undoOps", new object[] { dword("Mine", 0) } } };
            Task reset = run(catalogOnly, false);
            PumpUntil(() => reset.IsCompleted);
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(testKey))
                Check(reset.Status == TaskStatus.RanToCompletion && (int)k.GetValue("Mine") == 0 && changes.Count == 1 && Convert.ToString(changes[0]["id"]) == "engine-catalog#restablecer", "a revert without saved values is journaled before writing the catalog's values");
            Task undoReset = (Task)Call("RevertAllRecordedChangesAsync");
            PumpUntil(() => undoReset.IsCompleted);
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(testKey))
                Check((int)k.GetValue("Mine") == 41 && changes.Count == 0, "Revertir todo also undoes a catalog reset");

            // Revertir todo stops at the first failure so older entries never overwrite an unrestored change.
            var bad = new Dictionary<string, object> { { "type", "reg_value" }, { "hive", "HKCU" }, { "key", testKey }, { "name", "Mine" }, { "kind", "NoEsUnTipo" }, { "value", 1 } };
            var older = new Dictionary<string, object> { { "type", "reg_value" }, { "hive", "HKCU" }, { "key", testKey }, { "name", "Mine" }, { "kind", "DWord" }, { "value", 5 } };
            changes.Add(new Dictionary<string, object> { { "id", "older" }, { "title", "Antiguo" }, { "restoreOps", new List<Dictionary<string, object>> { older } } });
            changes.Add(new Dictionary<string, object> { { "id", "newer" }, { "title", "Reciente" }, { "restoreOps", new List<Dictionary<string, object>> { bad } } });
            Task stopping = (Task)Call("RevertAllRecordedChangesAsync");
            PumpUntil(() => stopping.IsCompleted);
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(testKey))
                Check(changes.Count == 2 && (int)k.GetValue("Mine") == 41, "Revertir todo stops at a failed entry and leaves the older ones untouched");
            changes.Clear();

            // Restarting a service only after its startup type is restored.
            var order = (List<Dictionary<string, object>>)Call("StartServicesLast", new List<Dictionary<string, object>> {
                new Dictionary<string, object> { { "type", "service_start" }, { "name", "A" } }, dword("Start", 2) });
            Check(Convert.ToString(order[1]["type"]) == "service_start", "service restarts run after the startup type is restored");

            // A deleted branch (classic context menu style) is captured value by value, empty keys included.
            string tree = testKey + @"\Arbol";
            using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(tree + @"\Vacia")) { }
            using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(tree)) k.SetValue("Valor", "x");
            var treeOps = (List<Dictionary<string, object>>)Call("CaptureRestoreOps", new Dictionary<string, object> { { "type", "reg_delete_tree" }, { "hive", "HKCU" }, { "key", tree } });
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(tree);
            foreach (var op in treeOps) Call("RunTweakOperation", op);
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(tree))
            using (var empty = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(tree + @"\Vacia"))
                Check(k != null && (string)k.GetValue("Valor") == "x" && empty != null, "a deleted registry branch can be written back, empty keys included");

            // Every catalog change must be undoable exactly; capture only reads registry, services and powercfg.
            var uncapturable = new List<string>();
            foreach (var t in (List<Dictionary<string, object>>)Field("_tweaks"))
                foreach (var op in ((IEnumerable)t["applyOps"]).Cast<Dictionary<string, object>>())
                    try { Call("CaptureRestoreOps", op); }
                    catch (TargetInvocationException ex) { uncapturable.Add(t["id"] + ": " + ex.InnerException.Message); }
            Check(uncapturable.Count == 0, "every catalog change records its previous value before running" + (uncapturable.Count > 0 ? ": " + string.Join("; ", uncapturable) : ""));
        }
        finally
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(testKey, false);
            changes.Clear();
            Call("SaveJournal");
            SetField("_hardware", savedHardware);
        }

        // Static catalog contract: every value fits, every tweak can be reverted, no write to TrustedInstaller keys.
        var tweaks = (List<Dictionary<string, object>>)Field("_tweaks");
        var known = new HashSet<string> { "reg_dword", "reg_sz", "reg_value", "reg_delete", "reg_delete_tree", "powercfg", "service_stop", "appx_remove", "appx_restore" };
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
        object packHardware = Field("_hardware");
        var profiles = (Dictionary<string, string[]>)_type.GetField("WinUtilProfiles", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        Func<string, bool, List<Dictionary<string, object>>> profile = (name, laptop) => { SetField("_hardware", HardwareFixture(laptop)); return (List<Dictionary<string, object>>)Call("ProfileTweaks", name); };
        var everyProfileId = profiles.Values.SelectMany(ids => ids).Distinct().ToList();
        Check(profiles.Keys.SequenceEqual(new[] { "Mínimo", "Recomendado", "Completo" }) && everyProfileId.All(id => tweaks.Any(t => Convert.ToString(t["id"]) == id && Convert.ToString(t["section"]) == "winutil" && !(t.ContainsKey("preference") && (bool)t["preference"]) && !(t.ContainsKey("retired") && (bool)t["retired"])))
            && profiles["Mínimo"].All(profiles["Recomendado"].Contains) && profiles["Recomendado"].All(profiles["Completo"].Contains),
            "the three profiles only hold existing WinUtil tweaks, never Ceito, retired or preferences, and each builds on the previous one");
        Check(profile("Completo", true).All(t => (bool)Call("TweakAvailable", t)) && profile("Completo", true).Count > 0 && !profile("Completo", true).Any(t => Convert.ToString(t["id"]).StartsWith("desktop_")),
            "a profile on a laptop carries only what this laptop supports");
        SetField("_hardware", packHardware);
        var winutilIds = tweaks.Where(t => Convert.ToString(t["section"]) == "winutil").Select(t => Convert.ToString(t["id"])).ToList();
        Check(!winutilIds.Any(id => new[] { "bitlocker", "wpbt", "utc", "ipv6", "teredo", "remove_edge", "remove_onedrive", "storage_sense", "reserved_storage", "adobe", "store_search", "start_menu_previous" }.Any(id.Contains))
            && tweaks.All(t => !t.ContainsKey("pack") && (Convert.ToString(t["section"]) == "winutil" || Convert.ToString(t["section"]) == "ceito")),
            "nothing unsafe from WinUtil was added and every tweak belongs to WinUtil or Ceito");
        // Every tweak 1.4.7 could apply must stay known, or the preview could neither see nor revert it.
        var published147 = new[] { "laptop_modern_standby_network", "laptop_cpu_boost_efficiency", "laptop_hibernation_fast_boot", "desktop_ultimate_performance_plan", "desktop_disable_pcie_usb_suspend", "desktop_disable_hibernation_full", "ceito_mouse_raw_no_accel", "ceito_mmcss_games_priority", "ceito_hags_game_mode", "ceito_fso_presence_writer", "ceito_gpu_tdr_telemetry", "ceito_power_throttling_off", "ceito_keyboard_mouse_queues", "common_ui_responsiveness_menushowdelay", "ceito_svchost_ram_split", "ceito_ntfs_ssd_gaming", "ceito_visual_effects_fast", "common_network_throttling_nagle", "debloat_telemetry_diagtrack", "debloat_ceip_compat_telemetry", "debloat_activity_history", "debloat_location_tracking", "debloat_advertising_id", "debloat_wer_error_reporting", "debloat_services_manual_pack", "debloat_sysmain_superfetch", "debloat_remote_registry_diag", "debloat_windows_search_indexing", "debloat_print_spooler_manual", "debloat_copilot_recall_ai", "debloat_bing_start_widgets", "debloat_consumer_features", "debloat_edge_background", "debloat_onedrive_autostart", "debloat_win11_classic_context_menu", "debloat_show_file_extensions", "debloat_explorer_this_pc", "debloat_sticky_keys_off", "debloat_lockscreen_ads_off", "debloat_wupdate_no_drivers", "debloat_delivery_opt_p2p", "debloat_xbox_gamedvr_capture", "advanced_disable_vbs_hvci" };
        Check(published147.Count(id => tweaks.Any(t => Convert.ToString(t["id"]) == id)) >= published147.Length - 5, "1.4.7 tweaks stay in the catalog, except the duplicates now merged (their values are checked one by one below)");
        CheckHardwareGating(tweaks);
        Func<string, string> risk = id => Convert.ToString(tweaks.First(t => Convert.ToString(t["id"]) == id)["riskLevel"]);
        Check(new[] { "desktop_ultimate_performance_plan", "ceito_power_throttling_off", "desktop_disable_pcie_usb_suspend" }.All(id => risk(id) == "medium") && tweaks.Where(t => Convert.ToString(t["id"]) == "advanced_disable_vbs_hvci").All(t => t.ContainsKey("retired") && (bool)t["retired"]),
            "heat-raising power tweaks are no longer low risk (out of Recomendado) and disabling memory integrity is gone");
        MethodInfo toolPath = _type.GetMethod("SystemToolPath", BindingFlags.Static | BindingFlags.NonPublic);
        Check((string)toolPath.Invoke(null, new object[] { "powercfg.exe" }) == Path.Combine(Environment.SystemDirectory, "powercfg.exe") && (string)toolPath.Invoke(null, new object[] { @"C:\x\winget.exe" }) == @"C:\x\winget.exe", "system tools never resolve next to Blackwall.exe");
        MethodInfo trusted = _type.Assembly.GetType("Blackwall.Authenticode", true).GetMethod("IsTrusted");
        string signed = Path.Combine(Environment.GetEnvironmentVariable("WINDIR"), @"Microsoft.NET\Framework64\v4.0.30319\csc.exe");
        Check((bool)trusted.Invoke(null, new object[] { signed }) && !(bool)trusted.Invoke(null, new object[] { _type.Assembly.Location }), "downloaded installers run only with a valid publisher signature");
        Check(!tweaks.Any(t => Convert.ToString(t["id"]) == "debloat_wifi_sense" || Convert.ToString(t["id"]) == "debloat_cortana_websearch"), "features removed from Windows 11 are no longer offered");
    }

    // The profile preview is built without showing it: nothing is applied here.
    private static void CheckApplyPreview(string directory)
    {
        // srrestoreptapi.h packs to 1 byte: a naturally packed STATEMGRSTATUS read the sequence from the wrong offset.
        Type info = _type.Assembly.GetType("Blackwall.RestorePointApi+RestorePointInfo", true), status = _type.Assembly.GetType("Blackwall.RestorePointApi+StateManagerStatus", true);
        Check(System.Runtime.InteropServices.Marshal.SizeOf(info) == 528 && (int)System.Runtime.InteropServices.Marshal.OffsetOf(info, "SequenceNumber") == 8 && (int)System.Runtime.InteropServices.Marshal.OffsetOf(info, "Description") == 16
            && System.Runtime.InteropServices.Marshal.SizeOf(status) == 12 && (int)System.Runtime.InteropServices.Marshal.OffsetOf(status, "SequenceNumber") == 4, "restore point structures match the native 1-byte packed layout");

        // A tampered cambios.json cannot make Blackwall run changes the catalog never makes.
        Func<Dictionary<string, object>, bool> allowed = op => (bool)Call("IsAllowedRestoreOp", op);
        Func<string, string, string, Dictionary<string, object>> reg = (hive, key, name) => new Dictionary<string, object> { { "type", "reg_value" }, { "hive", hive }, { "key", key }, { "name", name }, { "kind", "DWord" }, { "value", 0 } };
        Func<string, Dictionary<string, object>> power = args => new Dictionary<string, object> { { "type", "powercfg" }, { "args", args } };
        Check(allowed(reg("HKLM", @"SYSTEM\CurrentControlSet\Services\DiagTrack", "Start")) && allowed(power("/setactive 381b4222-f694-41f0-9685-ff5bb260df2e"))
            && allowed(new Dictionary<string, object> { { "type", "service_start" }, { "name", "DiagTrack" } }) && allowed(power("/delete b1ac0a11-5e7f-4d2a-9c3e-0000000001a5")),
            "journal accepts restoring what the catalog changes");
        Check(!allowed(reg("HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "Malware")) && !allowed(new Dictionary<string, object> { { "type", "reg_delete_tree" }, { "hive", "HKLM" }, { "key", "SOFTWARE" } })
            && !allowed(power("/delete 11111111-2222-3333-4444-555555555555")) && !allowed(power("/setactive x & calc")) && !allowed(new Dictionary<string, object> { { "type", "service_start" }, { "name", "OtroServicio" } }),
            "journal rejects anything the catalog never touches");

        // One system change at a time, across windows too.
        Check((bool)Call("TryBeginSystemChange", "Prueba") && !(bool)Call("TryBeginSystemChange", "Prueba"), "a second system change waits for the one in progress");
        Call("EndSystemChange");
        Check((bool)Call("TryBeginSystemChange", "Prueba"), "the change lock is released afterwards");
        Call("EndSystemChange");

        var states = (Dictionary<string, bool>)Field("_tweakStates");
        Func<string, string, Dictionary<string, object>> item = (id, risk) => new Dictionary<string, object> { { "id", id }, { "shortTitle", "Ajuste " + id }, { "summary", "Resumen " + id }, { "riskLevel", risk }, { "riskWarning", risk == "high" ? "Puede romper algo" : "" } };
        var safe = item("preview-safe", "low"); var risky = item("preview-risky", "high"); var done = item("preview-done", "low");
        states["preview-done"] = true;
        List<Dictionary<string, object>> chosen = null;
        try
        {
            Window dialog = (Window)Call("CreateApplyPreviewDialog", "Perfil de prueba", new List<Dictionary<string, object>> { safe, risky, done }, new Action<List<Dictionary<string, object>>>(list => chosen = list));
            var boxes = Descendants((DependencyObject)dialog.Content).OfType<CheckBox>().ToList();
            Func<string, CheckBox> box = name => boxes.First(b => System.Windows.Automation.AutomationProperties.GetName(b) == name);
            Button apply = Descendants((DependencyObject)dialog.Content).OfType<Button>().First(b => Convert.ToString(b.Content).StartsWith("Aplicar"));
            CheckBox acknowledge = boxes.First(b => b.Content is TextBlock && ((TextBlock)b.Content).Text.StartsWith("Entiendo"));
            string text = string.Join(" ", Descendants((DependencyObject)dialog.Content).OfType<TextBlock>().Select(t => t.Text));
            Check(boxes.Count == 3 && box("Ajuste preview-safe").IsChecked == true && box("Ajuste preview-risky").IsChecked == false && !boxes.Any(b => System.Windows.Automation.AutomationProperties.GetName(b) == "Ajuste preview-done"), "preview lists pending changes, skips applied ones and leaves risky ones unticked");
            Check(text.Contains("Puede romper algo") && text.Contains("1 ajuste ya estaba aplicado"), "preview shows the risk warning and what it skips");
            Check(apply.IsEnabled && Convert.ToString(apply.Content) == "Aplicar 1 cambio", "safe selection can be applied directly");
            box("Ajuste preview-risky").IsChecked = true;
            Check(!apply.IsEnabled, "a risky change needs the explicit acknowledgement");
            acknowledge.IsChecked = true;
            Check(apply.IsEnabled && Convert.ToString(apply.Content) == "Aplicar 2 cambios", "acknowledged risks can be applied");
            apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(chosen != null && chosen.Count == 2 && chosen.Contains(safe) && chosen.Contains(risky), "apply returns exactly the ticked changes");
            object previewHardware = Field("_hardware");
            SetField("_hardware", HardwareFixture(true, "hags"));
            Window real = (Window)Call("CreateApplyPreviewDialog", "Perfil Completo", Call("ProfileTweaks", "Completo"), new Action<List<Dictionary<string, object>>>(list => { }));
            SetField("_hardware", previewHardware);
            var riskyBox = Descendants((DependencyObject)real.Content).OfType<CheckBox>().FirstOrDefault(b => !(b.Content is TextBlock && ((TextBlock)b.Content).Text.StartsWith("Entiendo")) && b.IsChecked == false);
            if (riskyBox != null) riskyBox.IsChecked = true;
            Snapshot(directory, "native-profile-preview.png", 580, 820, (FrameworkElement)real.Content);
        }
        finally { states.Remove("preview-done"); }
    }

    // Options exist only for hardware that supports them; retired placebo tweaks are never offered again.
    private static void CheckHardwareGating(List<Dictionary<string, object>> tweaks)
    {
        object saved = Field("_hardware");
        MethodInfo classify = _type.GetMethod("ClassifyGpu", BindingFlags.Static | BindingFlags.NonPublic);
        Func<string, string, string> kind = (vendor, name) => (string)classify.Invoke(null, new object[] { vendor, name });
        Check(kind("10DE", "NVIDIA GeForce RTX 4070") == "dedicated" && kind("1002", "AMD Radeon RX 7600") == "dedicated" && kind("1002", "AMD Radeon RX 7600M XT") == "dedicated" && kind("8086", "Intel(R) Arc(TM) A770 Graphics") == "dedicated",
            "dedicated GPUs are recognised by PCI vendor and model");
        Check(kind("1002", "AMD Radeon(TM) Graphics") == "integrated" && kind("1002", "AMD Radeon 780M Graphics") == "integrated" && kind("1002", "AMD Radeon(TM) RX Vega 11 Graphics") == "integrated"
            && kind("8086", "Intel(R) UHD Graphics 770") == "integrated" && kind("8086", "Intel(R) Arc(TM) Graphics") == "integrated" && kind("1414", "Microsoft Basic Render Driver") == "virtual",
            "integrated and virtual adapters are never taken for a dedicated card");
        var gpus = (List<Dictionary<string, object>>)Call("ReadGpus");
        Check(gpus.Count > 0 && gpus.All(g => Convert.ToString(g["name"]).Length > 0 && new[] { "dedicated", "integrated", "virtual", "unknown" }.Contains(Convert.ToString(g["kind"])))
            && gpus.Select(g => Convert.ToString(g["kind"]) == "dedicated" ? 0 : Convert.ToString(g["kind"]) == "integrated" ? 1 : 2).SequenceEqual(gpus.Select(g => Convert.ToString(g["kind"]) == "dedicated" ? 0 : Convert.ToString(g["kind"]) == "integrated" ? 1 : 2).OrderBy(x => x)),
            "this PC's GPUs are read from their drivers, classified, dedicated first (" + string.Join(", ", gpus.Select(g => g["name"] + "=" + g["kind"])) + ")");

        Func<string, Dictionary<string, object>> tweak = id => tweaks.First(t => Convert.ToString(t["id"]) == id);
        Func<string, string> reason = id => (string)Call("UnmetRequirement", tweak(id));
        SetField("_hardware", HardwareFixture(true, "hags", "modernStandby", "dedicatedGpu", "nvidia"));
        Check(reason("gpu_hags") != null && reason("gpu_hags").Contains("HAGS") && reason("laptop_modern_standby_network") != null && reason("desktop_ultimate_performance_plan") != null && reason("debloat_activity_history") == null,
            "an integrated-GPU laptop without HAGS or Modern Standby is not offered those options, nor desktop-only ones");
        Check(reason("pref_battery_percent") == null && reason("winutil_brave") == null, "a laptop gets the battery preference");
        SetField("_hardware", HardwareFixture(false, "battery", "brave"));
        Check(reason("pref_battery_percent") != null && reason("winutil_brave") == "necesita el navegador Brave instalado" && reason("laptop_cpu_boost_efficiency") != null && reason("desktop_ultimate_performance_plan") == null,
            "a desktop without a battery or Brave is not offered those; laptop battery tweaks stay hidden");
        Check(tweaks.Where(t => t.ContainsKey("retired") && (bool)t["retired"]).All(t => ((string)Call("UnmetRequirement", t)).StartsWith("se retiró")), "retired placebo tweaks can never be applied again");

        // A laptop that still carries desktop-only tweaks from an older version (the overheating case): they are
        // shown with their reason and reverted in one go; nothing applicable is touched.
        var states = (Dictionary<string, bool>)Field("_tweakStates");
        var leftoverIds = new[] { "desktop_ultimate_performance_plan", "desktop_disable_hibernation_full" };
        var savedStates = leftoverIds.Concat(new[] { "debloat_activity_history" }).ToDictionary(id => id, id => states.ContainsKey(id) ? (bool?)states[id] : null);
        foreach (string id in leftoverIds) states[id] = true;
        states["debloat_activity_history"] = true;
        SetField("_hardware", HardwareFixture(true));
        var view = (FrameworkElement)Call("BuildWinUtilView");
        string viewText = string.Join(" ", Descendants(view).OfType<TextBlock>().Select(t => t.Text));
        Button revertLeftovers = Descendants(view).OfType<Button>().FirstOrDefault(b => System.Text.RegularExpressions.Regex.IsMatch(Convert.ToString(b.Content), "^Revertir [0-9]+ ajustes?$"));
        // Cards past the first screenful arrive on idle frames, so the reason is checked on the card itself.
        var leftoverCard = (FrameworkElement)Call("BuildTweakCard", tweaks.First(t => Convert.ToString(t["id"]) == "desktop_ultimate_performance_plan"));
        string cardText = string.Join(" ", Descendants(leftoverCard).OfType<TextBlock>().Select(t => t.Text));
        Check(revertLeftovers != null && viewText.Contains("no corresponden a este equipo") && leftoverIds.All(id => viewText.Contains(Convert.ToString(tweaks.First(t => Convert.ToString(t["id"]) == id)["shortTitle"]))) && cardText.Contains("es solo para PC de escritorio"), "desktop-only tweaks left active on a laptop are flagged with their reason and one revert button");
        object originalExecute = Field("_executeTweakAction"), originalConfirm = Field("_confirmLeftoverRevert");
        var reverted = new List<string>();
        SetField("_executeTweakAction", new Func<Dictionary<string, object>, bool, Task>((t, apply) => { if (!apply) reverted.Add(Convert.ToString(t["id"])); return Task.FromResult(0); }));
        SetField("_confirmLeftoverRevert", new Func<List<Dictionary<string, object>>, bool>(list => true));
        var leftovers = tweaks.Where(t => leftoverIds.Contains(Convert.ToString(t["id"]))).ToList();
        Task revert = (Task)Call("RevertLeftoversAsync", leftovers);
        PumpUntil(() => revert.IsCompleted);
        SetField("_executeTweakAction", originalExecute);
        SetField("_confirmLeftoverRevert", originalConfirm);
        foreach (var pair in savedStates) { if (pair.Value.HasValue) states[pair.Key] = pair.Value.Value; else states.Remove(pair.Key); }
        Check(revert.Status == TaskStatus.RanToCompletion && reverted.OrderBy(x => x).SequenceEqual(leftoverIds.OrderBy(x => x)), "Revertir ajustes incompatibles undoes exactly those, never an applicable tweak");
        // Reverting the Ultimate plan deletes Blackwall's own copy: that step needs nothing saved, any other deletion still refuses.
        Check(((List<Dictionary<string, object>>)Call("CaptureRestoreOps", new Dictionary<string, object> { { "type", "powercfg" }, { "args", "/delete b1ac0a11-5e7f-4d2a-9c3e-0000000001a5" } })).Count == 0, "reverting the Ultimate plan can journal the deletion of its own copy");
        bool foreignRefused = false;
        try { Call("CaptureRestoreOps", new Dictionary<string, object> { { "type", "powercfg" }, { "args", "/delete 381b4222-f694-41f0-9685-ff5bb260df2e" } }); }
        catch (TargetInvocationException) { foreignRefused = true; }
        Check(foreignRefused, "deleting any other power plan is never journaled as harmless");

        // The last barrier: even if a stale view offered it, the engine refuses unsupported hardware.
        SetField("_hardware", HardwareFixture(true, "hags"));
        Task refused = (Task)Call("ExecuteTweakAsync", tweak("gpu_hags"), true);
        PumpUntil(() => refused.IsCompleted);
        Check(refused.IsFaulted && refused.Exception.InnerException.Message.Contains("no es compatible"), "applying a tweak this PC does not support fails loudly and changes nothing");
        SetField("_hardware", saved);
    }

    // The published 1.4.7 catalog (tests/fixtures) against today's: every value 1.4.7 could write must be detected
    // and have a revert, so nothing it applied can be left stuck on a PC.
    private static void CheckRevertsEverything147(string fixture)
    {
        var old = (Dictionary<string, object>)new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(File.ReadAllText(fixture));
        var current = (List<Dictionary<string, object>>)Field("_tweaks");
        Func<object, List<Dictionary<string, object>>> ops = o => ((IEnumerable)o).Cast<Dictionary<string, object>>().ToList();
        Func<Dictionary<string, object>, string, string> get = (d, k) => d.ContainsKey(k) && d[k] != null ? Convert.ToString(d[k]) : "";
        Func<Dictionary<string, object>, string> regTarget = o => (get(o, "hive") + "|" + get(o, "key") + "|" + get(o, "name")).ToLowerInvariant();
        Func<string, string> powerTarget = a =>
        {
            string[] p = a.ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (p[0].StartsWith("/setacvalueindex") || p[0].StartsWith("/setdcvalueindex")) return p[0] + "|" + p[3];
            if (p[0] == "/setactive") return p[1] == "scheme_current" ? null : "plan";
            if (p[0] == "/duplicatescheme") return "copy:" + p[2];
            if (p[0] == "/hibernate") return p[1] == "/size" ? "size" : "hibernate";
            return a;
        };
        var gaps = new List<string>();
        foreach (Dictionary<string, object> t in (IEnumerable)old["tweaks"])
        {
            string id = get(t, "id");
            var now = current.FirstOrDefault(c => Convert.ToString(c["id"]) == id);
            // A value may now live in another tweak (a policy moved to a revert-only one): that tweak detects and undoes it.
            var undo = current.Where(c => Convert.ToString(c["id"]) == id || ops(c["applyOps"]).Any(x => get(x, "type").StartsWith("reg_") && ops(t["applyOps"]).Any(o => get(o, "type").StartsWith("reg_") && regTarget(o) == regTarget(x))))
                .SelectMany(c => ops(c["undoOps"])).ToList();
            foreach (var op in ops(t["applyOps"]))
            {
                string type = get(op, "type");
                bool covered;
                if (type.StartsWith("reg_"))
                    covered = undo.Any(u => (get(u, "type").StartsWith("reg_") && get(u, "type") != "reg_delete_tree" && regTarget(u) == regTarget(op))
                        || (get(u, "type") == "reg_delete_tree" && (get(op, "key") + "\\").StartsWith(get(u, "key") + "\\", StringComparison.OrdinalIgnoreCase)))
                        // 1.4.7 deleted this sync policy: absent is Windows' default and its earlier value is unknowable.
                        || (type == "reg_delete" && get(op, "name") == "DisableFileSyncNGSC");
                else if (type == "powercfg")
                {
                    string target = powerTarget(get(op, "args"));
                    covered = target == null || (target.StartsWith("copy:") ? undo.Any(u => get(u, "args").ToLowerInvariant().Contains("/delete " + target.Substring(5)))
                        : undo.Any(u => get(u, "type") == "powercfg" && powerTarget(get(u, "args")) == target));
                }
                else covered = type == "service_stop"; // its startup type is restored by the registry op; it runs again after a restart
                if (!covered) gaps.Add(id + " → " + (type == "powercfg" ? get(op, "args") : get(op, "key") + " › " + get(op, "name")));
            }
            // Still detected: a tweak that grew new values (Telemetría, Ubicación) reads as «En parte» from any value 1.4.7
            // wrote, which is enough to offer and run the revert. Merged tweaks are checked value by value above.
            var written = new HashSet<string>(ops(t["applyOps"]).Select(o => OpType(o) + regTarget(o) + get(o, "args") + get(o, "value")));
            if (now != null && !ops(now["applyOps"]).Any(op => written.Contains(OpType(op) + regTarget(op) + get(op, "args") + get(op, "value")))) gaps.Add(id + ": ningún valor de 1.4.7 sirve para detectarlo");
        }
        Check(gaps.Count == 0, "every value 1.4.7 could write is detected and revertible in this version" + (gaps.Count > 0 ? ": " + string.Join("; ", gaps) : ""));
    }

    // «Restablecer todo»: the journal is replayed first (an entry the catalog allows and that changes nothing:
    // re-applying the current power plan), then every tweak still detected is reverted. Catalog reverts are
    // recorded, not run, so no system setting is touched. A foreign journal entry is never replayed.
    // Every cited Microsoft page backs a real operation of its tweak (the live pages are checked by
    // scripts/check-official-sources.mjs); the log names where each session came from and the source of each value.
    private static void CheckOfficialSources()
    {
        var tweaks = (List<Dictionary<string, object>>)Field("_tweaks");
        Func<Dictionary<string, object>, string, List<Dictionary<string, object>>> list = (t, key) => t.ContainsKey(key) ? ((System.Collections.IEnumerable)t[key]).Cast<Dictionary<string, object>>().ToList() : new List<Dictionary<string, object>>();
        var bad = new List<string>();
        foreach (var t in tweaks)
        {
            var ops = list(t, "applyOps").Select(op => (string)Call("FullOperation", op)).ToList();
            foreach (var src in list(t, "sources"))
            {
                if (!Convert.ToString(src["url"]).StartsWith("https://") || Convert.ToString(src["title"]).Length == 0) bad.Add(t["id"] + ": fuente sin url o título");
                foreach (object cover in (System.Collections.IEnumerable)src["covers"])
                    if (!ops.Any(o => o.IndexOf(Convert.ToString(cover).Split('|')[0], StringComparison.OrdinalIgnoreCase) >= 0)) bad.Add(t["id"] + ": " + cover);
            }
        }
        Check(bad.Count == 0 && tweaks.All(t => t.ContainsKey("sources")), "every tweak lists its official sources and each cover matches one of its operations (" + string.Join(", ", bad) + ")");
        Func<string, Dictionary<string, object>> byId = id => tweaks.First(t => Convert.ToString(t["id"]) == id);
        Check((string)Call("DocumentationStatus", byId("advanced_disable_vbs_hvci")) == "oficial" && (string)Call("DocumentationStatus", byId("gpu_hags")) == "sin documentación oficial"
            && (string)Call("DocumentationStatus", byId("debloat_copilot_recall_ai")) == "oficial" && (string)Call("DocumentationStatus", byId("retired_turn_off_copilot")) == "sin documentación oficial", "documentation status counts only current, documented operations");
        var copilot = list(byId("retired_turn_off_copilot"), "applyOps").First(op => Convert.ToString(op["name"]) == "TurnOffWindowsCopilot");
        Check(((string)Call("DescribeSource", byId("retired_turn_off_copilot"), copilot)).StartsWith("OBSOLETO según Microsoft"), "a policy Microsoft deprecated is flagged as obsolete");
        Check((bool)Call("TryBeginSystemChange", "Origen de prueba"), "a change session starts");
        Call("EndSystemChange");
        string log = File.ReadAllText((string)Call("get_ChangeLogPath"));
        Check(System.Text.RegularExpressions.Regex.IsMatch(log, @"==== INICIO #\d{6} · origen: Origen de prueba · Blackwall [\d.]+ · Windows \d+") && log.Contains("· Origen de prueba ·") && log.Contains("==== FIN #"),
            "each change session is logged with its origin, app version and Windows build");
        Check(((string)Call("BuildTweakStateReport")).Contains("· documentación: "), "the state report shows how much of each tweak Microsoft documents");
    }

    // A value Windows refuses no longer stops «Revertir todo»: unrelated changes still revert, an older change to
    // the refused value waits in the journal, and a value that already holds its target is never rewritten.
    // Every registry value any tweak writes has a catalog undo for the same value, and the journal accepts both ways
    // of putting it back (the exact earlier value, or deleting it): a new tweak can never ship without a way back.
    private static void CheckEveryValueIsUndoable()
    {
        var missing = new List<string>();
        Func<Dictionary<string, object>, string, object> get = (d, k) => d.ContainsKey(k) ? d[k] : null;
        foreach (var t in (List<Dictionary<string, object>>)Field("_tweaks"))
        {
            var apply = ((System.Collections.IEnumerable)t["applyOps"]).Cast<Dictionary<string, object>>().Where(o => Convert.ToString(o["type"]).StartsWith("reg_") && Convert.ToString(o["type"]) != "reg_delete_tree");
            var undo = t.ContainsKey("undoOps") ? ((System.Collections.IEnumerable)t["undoOps"]).Cast<Dictionary<string, object>>().ToList() : new List<Dictionary<string, object>>();
            foreach (var op in apply)
            {
                string hive = Convert.ToString(op["hive"]), key = Convert.ToString(op["key"]), name = Convert.ToString(get(op, "name") ?? "");
                bool undone = undo.Any(u => Convert.ToString(get(u, "hive")) == hive && (Convert.ToString(u["type"]) == "reg_delete_tree"
                    ? (key + "\\").StartsWith(Convert.ToString(get(u, "key")) + "\\", StringComparison.OrdinalIgnoreCase)
                    : string.Equals(Convert.ToString(get(u, "key")), key, StringComparison.OrdinalIgnoreCase) && string.Equals(Convert.ToString(get(u, "name") ?? ""), name, StringComparison.OrdinalIgnoreCase)));
                var restore = new Dictionary<string, object> { { "type", "reg_value" }, { "hive", hive }, { "key", key }, { "name", name }, { "kind", "DWord" }, { "value", 0 } };
                var remove = new Dictionary<string, object> { { "type", "reg_delete" }, { "hive", hive }, { "key", key }, { "name", name } };
                if (!undone || !(bool)Call("IsAllowedRestoreOp", restore) || !(bool)Call("IsAllowedRestoreOp", remove)) missing.Add(t["id"] + ": " + hive + "\\" + key + " › " + name);
            }
        }
        Check(missing.Count == 0, "every value a tweak writes can be undone and journaled (" + string.Join("; ", missing) + ")");
        var ids = new[] { "privacy_device_metadata", "privacy_clipboard_local", "winutil_widgets", "winutil_background_apps" };
        var tweaks = (List<Dictionary<string, object>>)Field("_tweaks");
        object savedHardware = Field("_hardware");
        SetField("_hardware", HardwareFixture(true));
        bool offered = ids.All(id => tweaks.Any(t => Convert.ToString(t["id"]) == id && (bool)Call("TweakAvailable", t)));
        // Recall policies exist only from 26100.3915; the deprecated Copilot policy is never offered, only reverted.
        Func<string, Dictionary<string, object>> byId = id => tweaks.First(t => Convert.ToString(t["id"]) == id);
        bool recallOnNewBuild = (bool)Call("TweakAvailable", byId("privacy_remove_recall")) && (bool)Call("TweakAvailable", byId("debloat_copilot_recall_ai"));
        SetField("_hardware", HardwareFixture(true, "recallPolicy"));
        string recallOnOldBuild = (string)Call("UnmetRequirement", byId("privacy_remove_recall"));
        SetField("_hardware", HardwareFixture(true, "edge"));
        string edgeRemoved = (string)Call("UnmetRequirement", byId("privacy_edge_quiet"));
        bool copilotRetired = !(bool)Call("TweakAvailable", byId("retired_turn_off_copilot")) && ((string)Call("UnmetRequirement", byId("retired_turn_off_copilot"))).StartsWith("se retiró");
        SetField("_hardware", savedHardware);
        Check(recallOnNewBuild && recallOnOldBuild == "necesita Windows 11 24H2 con la actualización KB5055627 (26100.3915) o posterior" && copilotRetired && edgeRemoved == "necesita Microsoft Edge instalado" && (string)Call("DocumentationStatus", byId("privacy_edge_quiet")) == "oficial",
            "Recall options need the build Microsoft names, and the deprecated Copilot policy is revert-only (" + recallOnOldBuild + ")");
        Check(byId("privacy_remove_recall")["riskLevel"].Equals("medium") && Convert.ToString(byId("privacy_remove_recall")["riskWarning"]).Contains("no se recuperan"),
            "removing Recall is flagged as not exactly reversible");
        Check(offered && ids.All(id => tweaks.Any(t => Convert.ToString(t["id"]) == id && (string)Call("DocumentationStatus", t) == "oficial")),
            "policy-based tweaks are listed, fully documented by Microsoft and offered on any PC");
    }

    private static void CheckRevertContinuesPastRefusal()
    {
        string testKey = @"Software\BlackwallSelfCheck\" + Guid.NewGuid().ToString("N");
        Func<string, Dictionary<string, object>, Dictionary<string, object>> entry = (id, op) => new Dictionary<string, object> { { "id", id }, { "title", id }, { "at", "hoy" }, { "restoreOps", new List<Dictionary<string, object>> { op } } };
        Func<string, string, object, Dictionary<string, object>> value = (name, kind, v) => new Dictionary<string, object> { { "type", "reg_value" }, { "hive", "HKCU" }, { "key", testKey }, { "name", name }, { "kind", kind }, { "value", v } };
        var changes = (List<Dictionary<string, object>>)Field("_changes");
        object savedHardware = Field("_hardware");
        try
        {
            SetField("_hardware", HardwareFixture(true));
            using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(testKey)) { k.SetValue("Other", 9, Microsoft.Win32.RegistryValueKind.DWord); k.SetValue("Same", 3, Microsoft.Win32.RegistryValueKind.DWord); }
            changes.Clear();
            changes.Add(entry("oldest-same-value", value("Broken", "DWord", 1)));
            changes.Add(entry("unrelated", value("Other", "DWord", 4)));
            changes.Add(entry("already-there", value("Same", "DWord", 3)));
            changes.Add(entry("refused", value("Broken", "Binary", "no es base64")));
            Task all = (Task)Call("RevertAllRecordedChangesAsync");
            PumpUntil(() => all.IsCompleted);
            var left = changes.Select(c => Convert.ToString(c["id"])).OrderBy(x => x).ToList();
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(testKey))
                Check(all.Status == TaskStatus.RanToCompletion && (int)k.GetValue("Other") == 4 && k.GetValue("Broken") == null && left.SequenceEqual(new[] { "oldest-same-value", "refused" }),
                    "a refused value keeps only its own changes journaled; the rest still revert (" + string.Join(", ", left) + ")");
            var protectedOp = new Dictionary<string, object> { { "type", "reg_dword" }, { "hive", "HKLM" }, { "key", @"SAM\SAM" }, { "name", "BlackwallSelfCheck" }, { "value", 1 } };
            Exception refused = null;
            try { Call("RunTweakOperation", protectedOp); } catch (System.Reflection.TargetInvocationException ex) { refused = ex.InnerException; }
            Check(refused != null && refused.Message.StartsWith(@"Windows denegó el acceso a HKLM\SAM\SAM"), "an access refusal names the key instead of «operación no válida» (" + (refused == null ? "sin error" : refused.Message) + ")");
        }
        finally
        {
            changes.Clear();
            Call("SaveJournal");
            SetField("_hardware", savedHardware);
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(testKey, false);
        }
    }

    // The red «Restablecer todo» link, clicked for real on WinUtil and on Ceito, with real registry writes in
    // a throwaway HKCU key: journaled and unjournaled (1.4.7-style) tweaks go back, the cards and the link follow
    // the re-read state, a tweak Windows refuses is reported with its reason, and no stale entry keeps the link alive.
    private static void CheckResetLinkEndToEnd()
    {
        string testKey = @"Software\BlackwallSelfCheck\" + Guid.NewGuid().ToString("N");
        object savedTweaks = Field("_tweaks"), savedHardware = Field("_hardware"), savedStates = Field("_tweakStates"), savedTab = Field("_activeTab");
        object originalConfirm = Field("_confirmFullReset"), originalReport = Field("_reportFullReset"), originalApply = Field("_confirmApply");
        Func<List<Dictionary<string, object>>> changes = () => (List<Dictionary<string, object>>)Field("_changes");
        List<string> done = null; List<KeyValuePair<string, string>> notDone = null;
        Func<string, Dictionary<string, object>> set = name => new Dictionary<string, object> { { "type", "reg_dword" }, { "hive", "HKCU" }, { "key", testKey }, { "name", name }, { "value", 1 } };
        Func<string, Dictionary<string, object>> remove = name => new Dictionary<string, object> { { "type", "reg_delete" }, { "hive", "HKCU" }, { "key", testKey }, { "name", name } };
        Func<string, string, string, object, Dictionary<string, object>> tweak = (id, category, name, undo) => new Dictionary<string, object> {
            { "id", id }, { "title", "Prueba " + id }, { "shortTitle", "Prueba " + id }, { "summary", "Prueba" }, { "section", category }, { "group", category == "ceito" ? "ceito" : "esenciales" }, { "riskLevel", "low" },
            { "deviceScope", new object[] { "laptop", "desktop" } }, { "applyOps", new object[] { set(name) } }, { "undoOps", new object[] { undo } } };
        // An undo that throws when it runs (like a key Windows refuses), without touching anything outside the test key.
        var refusedUndo = new Dictionary<string, object> { { "type", "reg_value" }, { "hive", "HKCU" }, { "key", testKey }, { "name", "Locked" }, { "kind", "Binary" }, { "value", "no es base64" } };
        var tweaks = new List<Dictionary<string, object>> { tweak("e2e-rendimiento", "winutil", "Perf", remove("Perf")), tweak("e2e-privacidad", "ceito", "Priv", remove("Priv")), tweak("e2e-bloqueado", "winutil", "Locked", refusedUndo) };
        Func<string, ScrollViewer> page = tab => { Call("SwitchTab", tab); if (tab == "ceito") { SetField("_ceitoUnlocked", true); Call("RenderCurrentTab"); } PumpUntil(() => Descendants((DependencyObject)((ScrollViewer)Field("_mainScroll")).Content).Count(e => e.Tag is string && ((string)e.Tag).StartsWith("e2e-")) == (tab == "winutil" ? 2 : 1), 10); return (ScrollViewer)Field("_mainScroll"); };
        Func<string, Button> link = tab => Descendants((DependencyObject)page(tab).Content).OfType<Button>().FirstOrDefault(b => Convert.ToString(b.Content).StartsWith("↩ Restablecer todo"));
        Func<string, string, string> cardState = (tab, id) => {
            var card = Descendants((DependencyObject)page(tab).Content).First(e => (e.Tag as string) == id);
            return string.Join("|", Descendants(card).OfType<TextBlock>().Select(t => t.Text).Where(t => t.EndsWith("como activo")));
        };
        try
        {
            SetField("_tweaks", tweaks);
            SetField("_hardware", HardwareFixture(true));
            SetField("_tweakSearchText", ""); SetField("_pendingSearchId", null);
            changes().Clear(); Call("SaveJournal");
            SetField("_confirmApply", new Func<string, List<Dictionary<string, object>>, Task<List<Dictionary<string, object>>>>((label, list) => Task.FromResult(list)));
            SetField("_confirmFullReset", new Func<List<Dictionary<string, object>>, int, bool>((list, journaled) => true));
            SetField("_reportFullReset", new Action<List<string>, List<KeyValuePair<string, string>>>((d, n) => { done = d; notDone = n; }));
            // WinUtil through Blackwall (journaled); the other two written behind its back, like 1.4.7 left them.
            Task apply = (Task)Call("ExecuteTweakAsync", tweaks[0], true);
            PumpUntil(() => apply.IsCompleted, 10);
            using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(testKey)) { k.SetValue("Priv", 1, Microsoft.Win32.RegistryValueKind.DWord); k.SetValue("Locked", 1, Microsoft.Win32.RegistryValueKind.DWord); }
            Task refresh = (Task)Call("RefreshTweaksStateNativeAsync");
            PumpUntil(() => refresh.IsCompleted, 20);
            Button first = link("winutil");
            Button ceitoRevert = Descendants(Descendants((DependencyObject)page("ceito").Content).First(e => (e.Tag as string) == "e2e-privacidad")).OfType<Button>().FirstOrDefault(b => Convert.ToString(b.Content) == "Revertir");
            Check(ceitoRevert != null && ceitoRevert.IsEnabled, "a Ceito tweak applied before (here or on another PC) shows up active and can be reverted");
            Check(apply.Status == TaskStatus.RanToCompletion && first != null && Convert.ToString(first.Content) == "↩ Restablecer todo (3)" && cardState("winutil", "e2e-rendimiento") == "Detectado como activo",
                "WinUtil shows the red link with every Blackwall tweak and the card as active (" + (first == null ? "sin enlace" : first.Content) + ")");

            link("winutil").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => notDone != null, 30);
            Task settle = (Task)Call("RefreshTweaksStateNativeAsync");
            PumpUntil(() => settle.IsCompleted, 20);
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(testKey))
                Check(k.GetValue("Perf") == null && k.GetValue("Priv") == null && (int)k.GetValue("Locked") == 1, "the click really resets the registry: journaled and 1.4.7-style values go back, the refused one stays");
            Check(done.OrderBy(x => x).SequenceEqual(new[] { "Prueba e2e-privacidad", "Prueba e2e-rendimiento" }) && notDone.Count == 1 && notDone[0].Key == "Prueba e2e-bloqueado" && notDone[0].Value.Length > 0 && !notDone[0].Value.StartsWith("Sigue detectado"),
                "the result lists what was reset and what was not, with the reason (" + string.Join("; ", notDone.Select(p => p.Key + ": " + p.Value)) + ")");
            Check(cardState("winutil", "e2e-rendimiento") == "No detectado como activo" && cardState("ceito", "e2e-privacidad") == "No detectado como activo" && cardState("winutil", "e2e-bloqueado") == "Detectado como activo",
                "the cards on both pages follow the re-read state");
            Button second = link("ceito");
            Check(second != null && Convert.ToString(second.Content) == "↩ Restablecer todo (1)" && changes().All(c => Convert.ToString(c["id"]) == "e2e-bloqueado#restablecer"),
                "afterwards the link counts only what is left, not the reset's own records (" + (second == null ? "sin enlace" : second.Content) + "; " + string.Join(", ", changes().Select(c => c["id"])) + ")");

            // Fixed: the same link on Ceito finishes the job and disappears.
            tweaks[2]["undoOps"] = new object[] { remove("Locked") };
            done = null; notDone = null;
            second.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => notDone != null, 30);
            Task last = (Task)Call("RefreshTweaksStateNativeAsync");
            PumpUntil(() => last.IsCompleted, 20);
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(testKey))
                Check(k.GetValue("Locked") == null && notDone.Count == 0 && changes().Count == 0 && link("ceito") == null && link("winutil") == null,
                    "clicked again from Ceito it finishes, the journal is empty and the link is gone on both pages");
        }
        finally
        {
            SetField("_confirmFullReset", originalConfirm);
            SetField("_reportFullReset", originalReport);
            SetField("_confirmApply", originalApply);
            SetField("_tweaks", savedTweaks);
            SetField("_hardware", savedHardware);
            SetField("_tweakStates", savedStates);
            changes().Clear(); Call("SaveJournal");
            Call("SwitchTab", savedTab);
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(testKey, false);
        }
    }

    // «Reparar plan de energía» against a simulated powercfg (no real plan is touched): every plan is exported first,
    // a failed export changes nothing, the fresh Balanced copy becomes active, nothing of the user's is deleted, the
    // journal only ever deletes Blackwall's own plan, «Revertir todo» goes back and «Restablecer todo» leaves it alone.
    private static void CheckPowerPlanRepair()
    {
        const string balanced = "381b4222-f694-41f0-9685-ff5bb260df2e", high = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", mine = "11111111-2222-3333-4444-555555555555";
        string repair = (string)_type.GetField("RepairSchemeGuid", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        var plans = new List<string> { balanced, high, mine };
        string active = mine;
        string failExport = null;
        bool failRename = false;
        var calls = new List<string>();
        // Maximum processor state per plan, AC then DC: this Balanced was left capped at 50 % by some other tool.
        var maxState = new Dictionary<string, int[]> { { balanced, new[] { 50, 50 } }, { high, new[] { 100, 100 } }, { mine, new[] { 100, 100 } } };
        Func<string, string> fake = args =>
        {
            calls.Add(args);
            var guids = System.Text.RegularExpressions.Regex.Matches(args, "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}").Cast<System.Text.RegularExpressions.Match>().Select(m => m.Value).ToList();
            if (args == "/list") return string.Join("\n", plans.Select(p => "GUID del plan de energía: " + p + "  (Plan)" + (p == active ? " *" : "")));
            if (args == "/getactivescheme") return "GUID del plan de energía: " + active + "  (Plan)";
            if (args == "/setactive scheme_current") return "";
            if (args.StartsWith("/export ")) { if (guids[0] != failExport) File.WriteAllText(args.Split('"')[1], "pow " + guids[0]); return ""; }
            if (args.StartsWith("/duplicatescheme ")) { if (!plans.Contains(guids[0]) || plans.Contains(guids[1])) throw new InvalidOperationException("duplicate"); plans.Add(guids[1]); maxState[guids[1]] = (int[])maxState[guids[0]].Clone(); return ""; }
            if (args.StartsWith("/setacvalueindex ") || args.StartsWith("/setdcvalueindex "))
            {
                var p = args.Split(' ');
                if (p.Length != 5 || p[2] != "SUB_PROCESSOR" || p[3] != "PROCTHROTTLEMAX" || !plans.Contains(guids[0])) throw new InvalidOperationException("powercfg inesperado: " + args);
                maxState[guids[0]][args.StartsWith("/setac") ? 0 : 1] = int.Parse(p[4]);
                return "";
            }
            if (args.StartsWith("/import "))
            {
                string file = args.Split('"')[1];
                if (!File.Exists(file) || File.ReadAllText(file) != "pow " + guids.Last() || plans.Contains(guids.Last())) throw new InvalidOperationException("no se pudo importar");
                plans.Add(guids.Last()); maxState[guids.Last()] = new[] { 100, 100 };
                return "";
            }
            if (args.StartsWith("/changename ") && failRename) throw new InvalidOperationException("changename falló");
            if (args.StartsWith("/qh ") && args.EndsWith(" SUB_PROCESSOR PROCTHROTTLEMAX"))
                return "Índice de configuración de corriente alterna actual: 0x" + maxState[guids[0]][0].ToString("x8") + "\nÍndice de configuración de corriente continua actual: 0x" + maxState[guids[0]][1].ToString("x8");
            if (args.StartsWith("/changename ")) return "";
            if (args.StartsWith("/setactive ")) { if (!plans.Contains(guids[0])) throw new InvalidOperationException("no existe"); active = guids[0]; return ""; }
            if (args.StartsWith("/delete ")) { if (guids[0] == active || !plans.Contains(guids[0])) throw new InvalidOperationException("no se puede borrar"); plans.Remove(guids[0]); return ""; }
            throw new InvalidOperationException("powercfg inesperado: " + args);
        };
        object savedPowercfg = Field("_powercfg"), savedTweaks = Field("_tweaks"), savedHardware = Field("_hardware"), savedConfirm = Field("_confirmFullReset");
        Func<List<Dictionary<string, object>>> changes = () => (List<Dictionary<string, object>>)Field("_changes");
        string backups = (string)Call("get_PowerPlansFolder");
        try
        {
            SetField("_powercfg", fake);
            changes().Clear(); Call("SaveJournal");

            failExport = high;
            Task broken = (Task)Call("RepairPowerPlanCoreAsync");
            PumpUntil(() => broken.IsCompleted, 10);
            Check(broken.IsFaulted && active == mine && plans.Count == 3 && changes().Count == 0 && !calls.Any(c => c.StartsWith("/duplicatescheme")),
                "a plan that cannot be backed up stops the repair before anything changes");

            // Any optimization that changes a power plan backs up every plan first, once per session; no copy, no change.
            Func<string, Dictionary<string, object>> powerTweak = id => new Dictionary<string, object> { { "id", id }, { "title", id }, { "shortTitle", id }, { "section", "winutil" }, { "riskLevel", "low" },
                { "deviceScope", new object[] { "laptop", "desktop" } }, { "applyOps", new object[] { new Dictionary<string, object> { { "type", "powercfg" }, { "args", "/setactive scheme_current" } } } }, { "undoOps", new object[0] } };
            SetField("_hardware", HardwareFixture(true));
            SetField("_powerPlansBackup", null);
            Task refused = (Task)Call("ExecuteTweakAsync", powerTweak("energia-a"), true);
            PumpUntil(() => refused.IsCompleted, 10);
            Check(refused.IsFaulted && refused.Exception.InnerException.Message.StartsWith("No se cambió nada: no se pudo guardar la copia de tus planes de energía") && changes().Count == 0 && !calls.Contains("/setactive scheme_current"),
                "a power optimization is not applied when the plans cannot be backed up first");
            failExport = null; calls.Clear();
            foreach (string id in new[] { "energia-a", "energia-b" })
            {
                Task applied = (Task)Call("ExecuteTweakAsync", powerTweak(id), true);
                PumpUntil(() => applied.IsCompleted, 10);
                Check(applied.Status == TaskStatus.RanToCompletion, "power optimization applies after the backup: " + id);
            }
            string sessionBackup = (string)Field("_powerPlansBackup");
            Check(sessionBackup != null && calls.Count(c => c.StartsWith("/export")) == 3 && calls.FindIndex(c => c.StartsWith("/export")) < calls.IndexOf("/setactive scheme_current")
                && new[] { balanced, high, mine }.All(g => File.Exists(Path.Combine(sessionBackup, g + ".pow"))),
                "every plan is backed up once per session, before the first power change (" + calls.Count(c => c.StartsWith("/export")) + " copias)");
            changes().Clear(); Call("SaveJournal");

            calls.Clear();
            Task first = (Task)Call("RepairPowerPlanCoreAsync");
            PumpUntil(() => first.IsCompleted, 10);
            string folder = first.Status == TaskStatus.RanToCompletion ? ((Task<string>)first).Result : null;
            var entry = changes().SingleOrDefault();
            var ops = entry == null ? new List<string>() : ((System.Collections.IEnumerable)entry["restoreOps"]).Cast<Dictionary<string, object>>().Select(o => Convert.ToString(o["args"])).ToList();
            Check(folder != null && new[] { balanced, high, mine }.All(g => File.Exists(Path.Combine(folder, g + ".pow")))
                && calls.FindIndex(c => c.StartsWith("/export")) < calls.FindIndex(c => c.StartsWith("/duplicatescheme")),
                "every plan is exported before anything changes (" + (folder ?? (first.Exception == null ? "?" : first.Exception.InnerException.Message)) + ")");
            Check(active == repair && plans.Contains(mine) && plans.Contains(high) && plans.Contains(balanced) && ops.SequenceEqual(new[] { "/setactive " + mine, "/delete " + repair }),
                "the fresh Balanced copy is active, no user plan is deleted and the journal knows the way back (" + string.Join(" · ", ops) + ")");
            Check(maxState.ContainsKey(repair) && maxState[repair].SequenceEqual(new[] { 100, 100 }) && maxState[balanced].SequenceEqual(new[] { 50, 50 }),
                "the repaired plan lifts a hidden processor cap to 100 % on AC and battery, without touching Windows' Balanced plan ("
                + (maxState.ContainsKey(repair) ? string.Join("/", maxState[repair]) : "sin copia") + ")");
            Func<string, bool> allowed = args => (bool)Call("IsAllowedRestoreOp", new Dictionary<string, object> { { "type", "powercfg" }, { "args", args } });
            Check(allowed("/delete " + repair) && !allowed("/delete " + mine) && !allowed("/delete " + balanced), "the journal may delete Blackwall's repaired plan, never a user's plan");

            Task again = (Task)Call("RepairPowerPlanCoreAsync");
            PumpUntil(() => again.IsCompleted, 10);
            var againOps = ((System.Collections.IEnumerable)changes().Single()["restoreOps"]).Cast<Dictionary<string, object>>().Select(o => Convert.ToString(o["args"])).ToList();
            Check(again.Status == TaskStatus.RanToCompletion && active == repair && plans.Count(p => p == repair) == 1 && againOps[0] == "/setactive " + mine,
                "repairing again keeps one repaired plan and still remembers the user's original plan");
            Check(File.Exists(Path.Combine(((Task<string>)again).Result, repair + ".pow")), "repairing again backs up Blackwall's own repaired plan before replacing it");

            // «Restablecer todo» is about optimizations: it leaves the repair alone.
            SetField("_tweaks", new List<Dictionary<string, object>>());
            SetField("_hardware", HardwareFixture(true));
            SetField("_confirmFullReset", new Func<List<Dictionary<string, object>>, int, bool>((list, journaled) => { throw new InvalidOperationException("no debería preguntar"); }));
            Task reset = (Task)Call("ResetEverythingAsync");
            PumpUntil(() => reset.IsCompleted, 20);
            Check(reset.Status == TaskStatus.RanToCompletion && changes().Count == 1 && active == repair, "Restablecer todo does not undo the power repair");

            Task back = (Task)Call("RevertAllRecordedChangesAsync");
            PumpUntil(() => back.IsCompleted, 10);
            Check(back.Status == TaskStatus.RanToCompletion && active == mine && !plans.Contains(repair) && changes().Count == 0,
                "Revertir todo reactivates the user's plan and removes only Blackwall's copy");

            // A plan that is gone by the time it must be reactivated comes back from the latest backup.
            active = balanced; plans.Remove(mine); calls.Clear();
            Call("RunTweakOperation", new Dictionary<string, object> { { "type", "powercfg" }, { "args", "/setactive " + mine } });
            Check(plans.Contains(mine) && active == mine && calls.Any(c => c.StartsWith("/import ") && c.EndsWith(" " + mine)),
                "reactivating a plan that no longer exists imports it from the backup first (" + string.Join(" · ", calls) + ")");
            string lost = "99999999-8888-7777-6666-555555555555";
            Exception missing = null;
            try { Call("RunTweakOperation", new Dictionary<string, object> { { "type", "powercfg" }, { "args", "/setactive " + lost } }); }
            catch (Exception ex) { missing = ex is TargetInvocationException ? ex.InnerException : ex; }
            Check(missing != null && missing.Message.Contains(lost) && active == mine, "a plan with no backup is reported by name and the active plan stays");

            // A repair that fails half way puts back the plan it had replaced and the plan that was active.
            Task repaired = (Task)Call("RepairPowerPlanCoreAsync");
            PumpUntil(() => repaired.IsCompleted, 10);
            failRename = true;
            Task half = (Task)Call("RepairPowerPlanCoreAsync");
            PumpUntil(() => half.IsCompleted, 10);
            failRename = false;
            Check(half.IsFaulted && plans.Contains(repair) && active == repair, "a repair that fails half way restores the previous repaired plan and keeps it active");

            // Repair → Máximo rendimiento → repair again → Revertir todo ends on the user's original plan, with no Blackwall plan left.
            const string ultimate = "b1ac0a11-5e7f-4d2a-9c3e-0000000001a5";
            plans.Add(ultimate); maxState[ultimate] = new[] { 100, 100 }; active = ultimate;
            changes().Add(new Dictionary<string, object> { { "id", "desktop_ultimate_performance_plan" }, { "title", "Máximo rendimiento" }, { "at", "2026-11-04 10:00" }, { "restoreOps", new List<Dictionary<string, object>> {
                new Dictionary<string, object> { { "type", "powercfg" }, { "args", "/setactive " + repair } },
                new Dictionary<string, object> { { "type", "powercfg" }, { "args", "/delete " + ultimate } } } } });
            Call("SaveJournal");
            Task third = (Task)Call("RepairPowerPlanCoreAsync");
            PumpUntil(() => third.IsCompleted, 10);
            Task unwind = (Task)Call("RevertAllRecordedChangesAsync");
            PumpUntil(() => unwind.IsCompleted, 10);
            Check(third.Status == TaskStatus.RanToCompletion && unwind.Status == TaskStatus.RanToCompletion && active == mine && !plans.Contains(repair) && !plans.Contains(ultimate) && changes().Count == 0,
                "repairing between other power changes still unwinds to the user's original plan (activo: " + active + ")");

            // Reverting a power optimization backs up every plan first too: its undo may delete a plan.
            SetField("_powerPlansBackup", null); calls.Clear();
            Task undo = (Task)Call("ExecuteTweakAsync", powerTweak("energia-c"), false);
            PumpUntil(() => undo.IsCompleted, 10);
            Check(undo.Status == TaskStatus.RanToCompletion && calls.Any(c => c.StartsWith("/export")), "reverting a power optimization backs up the plans first");

            // A backup that saved no plan at all is not a backup.
            var everyPlan = plans.ToList();
            plans.Clear(); SetField("_powerPlansBackup", null);
            Exception empty = null;
            try { Call("BackupPowerPlansOnce"); } catch (Exception ex) { empty = ex is TargetInvocationException ? ex.InnerException : ex; }
            plans.AddRange(everyPlan);
            Check(empty != null && Field("_powerPlansBackup") == null, "a backup that finds no power plan stops instead of passing for a copy");

            // Two backups in the same second never share (and overwrite) a folder.
            SetField("_powerPlansBackup", null);
            string firstCopy = (string)Call("BackupPowerPlansOnce");
            SetField("_powerPlansBackup", null);
            string secondCopy = (string)Call("BackupPowerPlansOnce");
            Check(firstCopy != secondCopy && Directory.Exists(firstCopy) && Directory.Exists(secondCopy), "each backup gets its own folder (" + Path.GetFileName(firstCopy) + " · " + Path.GetFileName(secondCopy) + ")");

            // Restoring hibernation must really succeed: a failure keeps the journal entry instead of passing for done.
            var restoreHibernation = _type.GetMethod("HibernateRestoreOp", BindingFlags.Static | BindingFlags.NonPublic);
            var on = (Dictionary<string, object>)restoreHibernation.Invoke(null, new object[] { 1 });
            var off = (Dictionary<string, object>)restoreHibernation.Invoke(null, new object[] { 0 });
            Check(Convert.ToString(on["args"]) == "/hibernate on" && Convert.ToString(off["args"]) == "/hibernate off" && !Equals(on["allowFailure"], true) && !Equals(off["allowFailure"], true),
                "a failed hibernation restore is reported, not tolerated");

            // Read-back compares the number, not a piece of the text: 1 is not 10.
            var matches = _type.GetMethod("PowerIndexMatches", BindingFlags.Static | BindingFlags.NonPublic);
            Func<string, string, bool> same = (now, expected) => (bool)matches.Invoke(null, new object[] { now, expected });
            Check(same("100", "100") && !same("10", "1") && !same("100", "10") && same("0", "0"), "a power index is verified by its exact value");
        }
        finally
        {
            SetField("_powercfg", savedPowercfg);
            SetField("_confirmFullReset", savedConfirm);
            SetField("_powerPlansBackup", null);
            SetField("_tweaks", savedTweaks);
            SetField("_hardware", savedHardware);
            changes().Clear(); Call("SaveJournal");
            if (Directory.Exists(backups)) Directory.Delete(backups, true);
        }
    }

    // Startup programs: Task Manager's switch is read and written exactly, journaled, undone by «Revertir todo» and never
    // by «Restablecer todo». The program name is made up, so nothing that exists is touched. Cleanup deletes only old files.
    private static void CheckStartupAndCleanup()
    {
        Func<byte[], bool> enabled = b => (bool)_type.GetMethod("StartupEnabled", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { b });
        Check(enabled(null) && enabled(new byte[] { 2, 0, 0, 0 }) && !enabled(new byte[] { 3, 0, 0, 0 }) && !enabled(new byte[] { 7, 0, 0, 0 }), "startup state follows Task Manager's switch (02 on, 03/07 off)");
        string approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        Func<Dictionary<string, object>, bool> allowed = op => (bool)Call("IsAllowedRestoreOp", op);
        Check(allowed(new Dictionary<string, object> { { "type", "reg_value" }, { "hive", "HKCU" }, { "key", approved }, { "name", "Cualquiera" }, { "kind", "Binary" }, { "value", "AgAAAA==" } })
            && !allowed(new Dictionary<string, object> { { "type", "reg_value" }, { "hive", "HKCU" }, { "key", approved }, { "name", "Cualquiera" }, { "kind", "String" }, { "value", "calc.exe" } }),
            "the journal accepts putting back a startup switch, and nothing else in that key");
        Check(((System.Collections.IEnumerable)Call("ReadStartupEntries")).Cast<Dictionary<string, object>>().All(e => e.ContainsKey("enabled") && e.ContainsKey("key")), "startup programs are read from Run, Run32 and the Startup folders");

        string name = "BlackwallSelfCheck-" + Guid.NewGuid().ToString("N");
        object savedEnsure = Field("_ensureRestorePoint");
        Func<List<Dictionary<string, object>>> changes = () => (List<Dictionary<string, object>>)Field("_changes");
        try
        {
            SetField("_ensureRestorePoint", new Func<Task<bool>>(() => Task.FromResult(true)));
            changes().Clear(); Call("SaveJournal");
            var entry = new Dictionary<string, object> { { "name", name }, { "hive", "HKCU" }, { "key", approved } };
            Task off = (Task)Call("SetStartupEnabledAsync", entry, false);
            PumpUntil(() => off.IsCompleted, 10);
            byte[] written;
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(approved)) written = k == null ? null : k.GetValue(name) as byte[];
            var journaled = changes().SingleOrDefault();
            Check(written != null && written.Length == 12 && written[0] == 3 && BitConverter.ToInt64(written, 4) > 0 && journaled != null && Convert.ToString(journaled["id"]).StartsWith("startup:"),
                "disabling writes Task Manager's 12-byte switch with the time and journals it");
            Check(!(bool)_type.GetMethod("IsOptimizationChange", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { journaled }), "a startup choice is not counted or undone by Restablecer todo");
            Task back = (Task)Call("RevertAllRecordedChangesAsync");
            PumpUntil(() => back.IsCompleted, 10);
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(approved)) written = k == null ? null : k.GetValue(name) as byte[];
            Check(back.Status == TaskStatus.RanToCompletion && written == null && changes().Count == 0, "Revertir todo puts the startup switch back exactly as it was (absent)");
        }
        finally
        {
            SetField("_ensureRestorePoint", savedEnsure);
            changes().Clear(); Call("SaveJournal");
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(approved, true)) if (k != null) k.DeleteValue(name, false);
        }

        string dir = Path.Combine(Path.GetTempPath(), "BlackwallSelfCheck-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        try
        {
            string old = Path.Combine(dir, "sub", "viejo.tmp"), fresh = Path.Combine(dir, "nuevo.tmp");
            File.WriteAllBytes(old, new byte[1000]); File.WriteAllBytes(fresh, new byte[500]);
            File.SetLastWriteTime(old, DateTime.Now.AddDays(-3));
            var clean = _type.GetMethod("DeleteOldTempFiles", BindingFlags.Static | BindingFlags.NonPublic);
            long counted = (long)clean.Invoke(null, new object[] { dir, DateTime.Now.AddDays(-1), true });
            bool untouched = File.Exists(old);
            long freed = (long)clean.Invoke(null, new object[] { dir, DateTime.Now.AddDays(-1), false });
            Check(counted == 1000 && untouched && freed == 1000 && !File.Exists(old) && File.Exists(fresh), "cleanup counts first, then deletes only files older than a day");
        }
        finally { Directory.Delete(dir, true); }
    }

    // WinUtil and Ceito: no duplicated values, preferences wait for their button, Ceito never applies, its wizard
    // demands a verified restore point, and profiles only mark cards. System changes are recorded, never run.
    private static void CheckWinUtilSection(string directory)
    {
        var tweaks = (List<Dictionary<string, object>>)Field("_tweaks");
        Func<Dictionary<string, object>, string, bool> flag = (t, k) => t.ContainsKey(k) && Equals(t[k], true);
        Func<object, List<Dictionary<string, object>>> ops = o => ((System.Collections.IEnumerable)o).Cast<Dictionary<string, object>>().ToList();
        Func<Dictionary<string, object>, string> target = o => (Convert.ToString(o["hive"]) + "\\" + Convert.ToString(o["key"]) + "|" + (o.ContainsKey("name") ? Convert.ToString(o["name"]) : "")).ToLowerInvariant();
        var writers = tweaks.Where(t => Convert.ToString(t["section"]) == "winutil")
            .SelectMany(t => ops(t["applyOps"]).Where(o => Convert.ToString(o["type"]).StartsWith("reg_") && Convert.ToString(o["type"]) != "reg_delete_tree").Select(o => new { id = Convert.ToString(t["id"]), target = target(o) }))
            .GroupBy(w => w.target).Where(g => g.Select(w => w.id).Distinct().Count() > 1).Select(g => g.Key + " ← " + string.Join(", ", g.Select(w => w.id).Distinct())).ToList();
        Check(writers.Count == 0, "no two WinUtil tweaks or preferences write the same value (" + string.Join("; ", writers) + ")");
        Check(tweaks.Where(t => flag(t, "preference")).All(t => ops(t["applyOps"]).Count == ops(t["undoOps"]).Count && t.ContainsKey("defaultOn")),
            "every preference states its on and off values and Windows' default");

        // Firmware is out of bounds: no tweak in any section, applied or undone, reaches BIOS, Secure Boot or the boot store.
        var firmware = new System.Text.RegularExpressions.Regex(@"bcdedit|bcdboot|\\SecureBoot|FirmwareEnvironment|InstrumentedBIOS|BiosSetting|manage-bde|fwupd|capsule", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var touching = tweaks.Where(t => ops(t["applyOps"]).Concat(ops(t["undoOps"])).Any(o => o.Values.Any(v => firmware.IsMatch(Convert.ToString(v))))).Select(t => Convert.ToString(t["id"])).ToList();
        Check(touching.Count == 0, "no tweak touches BIOS, Secure Boot or the boot configuration (" + string.Join(", ", touching) + ")");
        var describe = _type.GetMethod("DescribeSecureBootCerts", BindingFlags.Static | BindingFlags.NonPublic);
        Func<bool, string, int, string> certs = (on, status, error) => (string)describe.Invoke(null, new object[] { on, status, error });
        Check(certs(true, "Updated", 0) == "Actualizados (2023)" && certs(true, "NotStarted", 0).StartsWith("Pendientes") && certs(true, "InProgress", 0).StartsWith("En curso")
            && certs(false, "NotStarted", 0).StartsWith("No aplica") && certs(true, null, 0) == "Sin datos de Windows todavía" && certs(true, "InProgress", unchecked((int)0x80070005)).EndsWith("error 0x80070005"),
            "the Secure Boot 2023 certificate status is read and explained, never changed");

        object savedExecute = Field("_executeTweakAction"), savedEnsure = Field("_ensureRestorePoint"), savedCheck = Field("_checkRestorePoint"), savedHardware = Field("_hardware");
        object savedNotify = Field("_notifyPreference"), savedExplorer = Field("_restartExplorer"), savedConfirmRestart = Field("_confirmRestart"), savedShutdown = Field("_runShutdown");
        var notified = new List<string>(); var shutdowns = new List<string>(); int explorerRestarts = 0;
        SetField("_notifyPreference", new Action<string>(id => notified.Add(id)));
        SetField("_restartExplorer", new Func<Task>(() => { explorerRestarts++; return Task.FromResult(0); }));
        SetField("_confirmRestart", new Func<bool>(() => true));
        SetField("_runShutdown", new Func<string, bool>(args => { shutdowns.Add(args); return true; }));
        Func<Dictionary<string, bool>> states = () => (Dictionary<string, bool>)Field("_tweakStates");
        var savedStates = new Dictionary<string, bool>(states());
        var ran = new List<string>();
        try
        {
            SetField("_hardware", HardwareFixture(true));
            SetField("_executeTweakAction", new Func<Dictionary<string, object>, bool, Task>((t, apply) => { ran.Add(Convert.ToString(t["id"]) + (apply ? "+" : "-")); return Task.FromResult(0); }));
            SetField("_ensureRestorePoint", new Func<Task<bool>>(() => Task.FromResult(true)));
            MarkLoaded("tweaks");

            // Preferences: switches only queue; nothing runs until «Aplicar preferencias».
            var dark = tweaks.First(t => Convert.ToString(t["id"]) == "pref_dark_mode");
            states()["pref_dark_mode"] = false;
            var pending = (Dictionary<string, bool>)Field("_pendingPreferences");
            pending.Clear();
            var card = (FrameworkElement)Call("BuildPreferencesCard", new List<Dictionary<string, object>> { dark });
            var toggle = Descendants(card).OfType<Button>().First(b => Convert.ToString(System.Windows.Automation.AutomationProperties.GetName(b)).StartsWith("Tema oscuro"));
            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(pending.Count == 1 && pending["pref_dark_mode"] && ran.Count == 0, "a preference switch only queues the change");
            card = (FrameworkElement)Call("BuildPreferencesCard", new List<Dictionary<string, object>> { dark });
            Check(Descendants(card).OfType<Button>().Any(b => Convert.ToString(b.Content) == "Aplicar preferencias (1)"), "pending preferences show one apply button with their count");
            var allPrefs = tweaks.Where(t => flag(t, "preference") && (bool)Call("TweakAvailable", t)).ToList();
            Snapshot(directory, "native-preferences.png", 1100, 980, (FrameworkElement)Call("BuildPreferencesCard", allPrefs));
            Task applyPrefs = (Task)Call("ApplyPendingPreferencesAsync", false);
            PumpUntil(() => applyPrefs.IsCompleted, 20);
            Check(applyPrefs.Status == TaskStatus.RanToCompletion && ran.SequenceEqual(new[] { "pref_dark_mode#on+" }) && pending.Count == 0, "applying runs each queued preference once, journaled as its own change (" + string.Join(", ", ran) + ")");
            Check(notified.SequenceEqual(new[] { "pref_dark_mode" }) && shutdowns.Count == 0, "an instant preference is announced to Windows right away, with no restart");
            Check(tweaks.Where(t => flag(t, "preference")).All(t => new[] { "instant", "explorer", "restart" }.Contains(Convert.ToString(t["applyMode"]))), "every preference says when Windows shows it");

            // «Aplicar y reiniciar ahora»: a change Windows did not keep never restarts the PC (the recorded apply wrote nothing).
            var longPaths = tweaks.First(t => Convert.ToString(t["id"]) == "pref_long_paths");
            states()["pref_long_paths"] = false;
            pending["pref_long_paths"] = true;
            Task unverified = (Task)Call("ApplyPendingPreferencesAsync", true);
            PumpUntil(() => unverified.IsCompleted, 20);
            Check(shutdowns.Count == 0 && !(bool)Field("_restartScheduled"), "if a preference does not verify, the PC is not restarted");

            // A real round trip in a throwaway HKCU key: applied, re-read, then the restart is scheduled and can be cancelled.
            string prefKey = @"Software\BlackwallSelfCheck\" + Guid.NewGuid().ToString("N");
            var fixture = new Dictionary<string, object> { { "id", "pref_selfcheck" }, { "title", "Prueba" }, { "summary", "Prueba" }, { "section", "winutil" }, { "group", "preferencias" }, { "preference", true }, { "defaultOn", false }, { "applyMode", "restart" }, { "riskLevel", "low" },
                { "applyOps", new object[] { new Dictionary<string, object> { { "type", "reg_dword" }, { "hive", "HKCU" }, { "key", prefKey }, { "name", "V" }, { "value", 1 } } } },
                { "undoOps", new object[] { new Dictionary<string, object> { { "type", "reg_dword" }, { "hive", "HKCU" }, { "key", prefKey }, { "name", "V" }, { "value", 0 } } } } };
            tweaks.Add(fixture);
            try
            {
                SetField("_executeTweakAction", savedExecute);
                pending.Clear(); pending["pref_selfcheck"] = true;
                Task restart = (Task)Call("ApplyPendingPreferencesAsync", true);
                PumpUntil(() => restart.IsCompleted, 20);
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(prefKey))
                    Check(k != null && (int)k.GetValue("V") == 1 && shutdowns.Count == 1 && shutdowns[0].StartsWith("/r /t 20") && (bool)Field("_restartScheduled"), "applied and verified, then the PC restart is scheduled (" + string.Join(", ", shutdowns) + ")");
                Call("CancelRestart");
                Check(shutdowns.Last() == "/a" && !(bool)Field("_restartScheduled"), "the scheduled restart can be cancelled from Blackwall");
            }
            finally
            {
                tweaks.Remove(fixture);
                ((List<Dictionary<string, object>>)Field("_changes")).Clear(); Call("SaveJournal");
                Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(prefKey, false);
                SetField("_executeTweakAction", new Func<Dictionary<string, object>, bool, Task>((t, apply) => { ran.Add(Convert.ToString(t["id"]) + (apply ? "+" : "-")); return Task.FromResult(0); }));
                pending.Clear();
            }
            states()["pref_dark_mode"] = true;
            Check(!(bool)Call("IsTweakOn", dark) && !(bool)_type.GetMethod("IsOptimizationChange", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { new Dictionary<string, object> { { "id", "pref_dark_mode#on" } } }),
                "a preference never counts as a Blackwall optimization: Restablecer todo leaves it alone");

            // Ceito: revert-only, from the engine and from the card.
            var ceito = tweaks.First(t => Convert.ToString(t["section"]) == "ceito" && !flag(t, "retired") && (bool)Call("TweakAvailable", t));
            SetField("_executeTweakAction", savedExecute);
            Task refused = (Task)Call("ExecuteTweakAsync", ceito, true);
            PumpUntil(() => refused.IsCompleted, 10);
            Check(refused.IsFaulted && refused.Exception.InnerException.Message.StartsWith("Ceito está en pausa"), "the engine refuses to apply a Ceito tweak");
            states()[Convert.ToString(ceito["id"])] = false;
            var paused = Descendants((DependencyObject)Call("BuildTweakCard", ceito)).OfType<Button>().First(b => Convert.ToString(b.Content) == "En pausa");
            Check(!paused.IsEnabled, "a Ceito card that is not applied cannot be applied");

            // Ceito wizard: every visit, a verified restore point and a confirmation are required.
            SetField("_checkRestorePoint", new Func<Task<string>>(() => Task.FromResult("none")));
            Call("SwitchTab", "ceito");
            Func<string> pageText = () => string.Join(" | ", Descendants((DependencyObject)((ScrollViewer)Field("_mainScroll")).Content).OfType<TextBlock>().Select(t => t.Text));
            Func<string, Button> button = label => Descendants((DependencyObject)((ScrollViewer)Field("_mainScroll")).Content).OfType<Button>().FirstOrDefault(b => Convert.ToString(b.Content) == label);
            Check(pageText().Contains("Ceito es experimental") && !button("Atrás").IsEnabled, "entering Ceito starts with the experimental warning");
            Snapshot(directory, "native-ceito-wizard-1.png", 1180, 760);
            button("Siguiente").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => Field("_ceitoRestoreStatus") != null, 10);
            Call("RenderCurrentTab");
            Check(pageText().Contains("No hay un punto de restauración verificado") && !button("Siguiente").IsEnabled && button("Crear punto de restauración ahora") != null, "without a verified restore point Ceito cannot be entered");
            SetField("_checkRestorePoint", new Func<Task<string>>(() => Task.FromResult("ok")));
            SetField("_ceitoRestoreStatus", null);
            Call("RenderCurrentTab");
            PumpUntil(() => (string)Field("_ceitoRestoreStatus") == "ok", 10);
            Call("RenderCurrentTab");
            var confirm = Descendants((DependencyObject)((ScrollViewer)Field("_mainScroll")).Content).OfType<CheckBox>().First();
            Check(!button("Siguiente").IsEnabled && confirm.IsEnabled, "a verified point still needs the explicit confirmation");
            confirm.IsChecked = true;
            confirm.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Check(button("Siguiente").IsEnabled, "confirming enables the next step");
            Snapshot(directory, "native-ceito-wizard-2.png", 1180, 760);
            button("Siguiente").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(pageText().Contains("Continúas bajo tu propio riesgo") && button("Entrar a Ceito") != null, "the last step states the risk and the safety advice");
            button("Entrar a Ceito").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check((bool)Field("_ceitoUnlocked") && pageText().Contains("Ceito · en pausa"), "after the wizard the Ceito section opens, revert-only");
            Snapshot(directory, "native-ceito.png", 1180, 760);
            Call("SwitchTab", "winutil");
            Call("SwitchTab", "ceito");
            Check(!(bool)Field("_ceitoUnlocked") && pageText().Contains("Ceito es experimental"), "leaving and coming back shows the warning again");

            // «N opciones no aplican» unfolds in place: the page is not rebuilt, so nothing jumps.
            Call("SwitchTab", "winutil");
            object pageBefore = ((ScrollViewer)Field("_mainScroll")).Content;
            var why = Descendants((DependencyObject)pageBefore).OfType<Button>().FirstOrDefault(b => Convert.ToString(b.Content).EndsWith("a tu equipo"));
            if (why != null) why.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(why != null && Object.ReferenceEquals(pageBefore, ((ScrollViewer)Field("_mainScroll")).Content) && Convert.ToString(why.Content) == "Ocultar motivos", "showing why options are hidden unfolds in place without rebuilding the page");
            why.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            // Profiles: choosing one only marks cards; the card shows «Se aplicará» with a 2 px outline.
            var activity = tweaks.First(t => Convert.ToString(t["id"]) == "debloat_activity_history");
            states()["debloat_activity_history"] = false;
            Call("SelectProfile", "Recomendado");
            var selection = (HashSet<string>)Field("_profileSelection");
            var marked = (Border)Call("BuildTweakCard", activity);
            Check(selection.Contains("debloat_activity_history") && marked.BorderThickness.Left == 2 && Descendants(marked).OfType<TextBlock>().Any(t => t.Text == "◆ Se aplicará") && ran.Count == 2,
                "choosing a profile marks its cards without applying anything");
            Call("SwitchTab", "winutil");
            Snapshot(directory, "native-winutil-profile.png", 1180, 900);
            Call("SelectProfile", "Recomendado");
            Check(selection.Count == 0 && Field("_profileSelectionName") == null, "choosing the same profile again clears the marks");
            states()["debloat_activity_history"] = true;
            var partial = (HashSet<string>)Field("_tweakPartial");
            partial.Add("debloat_activity_history");
            var half = (Border)Call("BuildTweakCard", activity);
            partial.Remove("debloat_activity_history");
            var active = (Border)Call("BuildTweakCard", activity);
            Check(Descendants(active).OfType<TextBlock>().Any(t => t.Text == "✓ Activo") && active.BorderThickness.Left == 2 && Descendants(half).OfType<TextBlock>().Any(t => t.Text == "◐ En parte"),
                "active and partially applied tweaks are outlined and labelled, not just a coloured dot");
        }
        finally
        {
            SetField("_executeTweakAction", savedExecute);
            SetField("_ensureRestorePoint", savedEnsure);
            SetField("_checkRestorePoint", savedCheck);
            SetField("_notifyPreference", savedNotify);
            SetField("_restartExplorer", savedExplorer);
            SetField("_confirmRestart", savedConfirmRestart);
            SetField("_runShutdown", savedShutdown);
            SetField("_restartScheduled", false);
            SetField("_hardware", savedHardware);
            ((HashSet<string>)Field("_profileSelection")).Clear();
            SetField("_profileSelectionName", null);
            ((Dictionary<string, bool>)Field("_pendingPreferences")).Clear();
            SetField("_tweakStates", new Dictionary<string, bool>(savedStates));
            Call("SwitchTab", "hardware");
        }
    }

    private static void CheckFullReset()
    {
        object savedHardware = Field("_hardware");
        object originalExecute = Field("_executeTweakAction"), originalConfirm = Field("_confirmFullReset"), originalReport = Field("_reportFullReset");
        var reverted = new List<string>();
        int journaledSeen = -1;
        try
        {
            SetField("_hardware", HardwareFixture(true));
            var changes = (List<Dictionary<string, object>>)Field("_changes");
            changes.Clear();
            changes.Add(new Dictionary<string, object> { { "id", "reset-test" }, { "title", "Prueba" }, { "at", "hoy" }, { "restoreOps", new List<Dictionary<string, object>> {
                new Dictionary<string, object> { { "type", "powercfg" }, { "args", "/setactive scheme_current" } } } } });
            changes.Add(new Dictionary<string, object> { { "id", "foreign" }, { "title", "Ajena" }, { "at", "hoy" }, { "restoreOps", new List<Dictionary<string, object>> {
                new Dictionary<string, object> { { "type", "reg_value" }, { "hive", "HKLM" }, { "key", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run" }, { "name", "Malware" }, { "kind", "String" }, { "value", "x" } } } } });
            Call("SaveJournal");
            SetField("_executeTweakAction", new Func<Dictionary<string, object>, bool, Task>((t, apply) => { if (!apply) reverted.Add(Convert.ToString(t["id"])); return Task.FromResult(0); }));
            SetField("_confirmFullReset", new Func<List<Dictionary<string, object>>, int, bool>((list, journaled) => { journaledSeen = journaled; return true; }));
            SetField("_reportFullReset", new Action<List<string>, List<KeyValuePair<string, string>>>((done, notDone) => { }));
            Task reset = (Task)Call("ResetEverythingAsync");
            PumpUntil(() => reset.IsCompleted);
            var detected = ((List<Dictionary<string, object>>)Field("_tweaks")).Where(t => (bool)Call("IsTweakOn", t)).Select(t => Convert.ToString(t["id"])).OrderBy(x => x).ToList();
            Check(reset.Status == TaskStatus.RanToCompletion && journaledSeen == 1 && ((List<Dictionary<string, object>>)Field("_changes")).Count == 0,
                "Restablecer todo replays the valid journal first and never the foreign entry (" + journaledSeen + " of 2)");
            Check(reverted.OrderBy(x => x).SequenceEqual(detected), "Restablecer todo then reverts every tweak still detected (" + reverted.Count + ")");
        }
        finally
        {
            SetField("_executeTweakAction", originalExecute);
            SetField("_confirmFullReset", originalConfirm);
            SetField("_reportFullReset", originalReport);
            SetField("_hardware", savedHardware);
            ((List<Dictionary<string, object>>)Field("_changes")).Clear();
            Call("SaveJournal");
        }
    }

    private static string OpType(Dictionary<string, object> op) { return op.ContainsKey("type") ? Convert.ToString(op["type"]) : ""; }

    private static void CheckEmbeddedCatalog()
    {
        Check(((ICollection)Field("_tweaks")).Count >= 42, "current catalog includes concise presentation fields");
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

    private static void CheckParticles()
    {
        Type particleType = _type.Assembly.GetType("Blackwall.GraphiteNetwork", true);
        var network = (FrameworkElement)Activator.CreateInstance(particleType);
        Check(!network.IsHitTestVisible, "particle surface never captures clicks or wheel events");
        var points = (Point[])particleType.GetField("_points", Private).GetValue(network);
        network.Measure(new Size(640, 400)); network.Arrange(new Rect(0, 0, 640, 400)); network.UpdateLayout();
        Func<byte[]> render = () => { network.UpdateLayout(); var bitmap = new RenderTargetBitmap(640, 400, 96, 96, PixelFormats.Pbgra32); bitmap.Render(network); var bytes = new byte[640 * 400 * 4]; bitmap.CopyPixels(bytes, 640 * 4, 0); return bytes; };
        byte[] idle = render();
        particleType.GetMethod("SetPointer").Invoke(network, new object[] { (Point?)new Point(points[0].X * 640 + 8, points[0].Y * 400) });
        byte[] moved = render();
        Check(!idle.SequenceEqual(moved), "pointer proximity changes rendered particle positions");
        Point[] savedPoints = (Point[])points.Clone();
        for (int i = 0; i < points.Length; i++) points[i] = new Point(0, 0);
        points[0] = new Point(0.25, 0.5);
        particleType.GetMethod("SetPointer").Invoke(network, new object[] { null });
        byte[] withoutLink = render();
        particleType.GetMethod("SetPointer").Invoke(network, new object[] { (Point?)new Point(288, 200) });
        byte[] withLink = render();
        Check(Enumerable.Range(0, 9).Any(i => withoutLink[((199 + i / 3) * 640 + 223 + i % 3) * 4 + 3] != withLink[((199 + i / 3) * 640 + 223 + i % 3) * 4 + 3]), "cursor draws a connection to a nearby particle");
        Array.Copy(savedPoints, points, points.Length);
        Point before = points[0];
        particleType.GetMethod("SetPointer").Invoke(network, new object[] { (Point?)new Point(100, 100) });
        Check((bool)particleType.GetProperty("PointerActive").GetValue(network, null), "particle surface receives cursor position");
        particleType.GetMethod("SetRunning").Invoke(network, new object[] { true });
        var visualBefore = (Point)particleType.GetField("_visualPointer", Private).GetValue(network);
        particleType.GetMethod("SetPointer").Invoke(network, new object[] { (Point?)new Point(500, 300) });
        Check((Point)particleType.GetField("_visualPointer", Private).GetValue(network) == visualBefore, "cursor position does not jump instantly while particles animate");
        PumpUntil(() => (Point)particleType.GetField("_visualPointer", Private).GetValue(network) != visualBefore);
        Check((Point)particleType.GetField("_visualPointer", Private).GetValue(network) != new Point(500, 300), "cursor reaction approaches the target gradually");
        PumpUntil(() => points[0] != before);
        Check((bool)particleType.GetProperty("Running").GetValue(network, null), "one shared timer moves particles while enabled");
        particleType.GetMethod("SetRunning").Invoke(network, new object[] { false });
        Check(!(bool)particleType.GetProperty("Running").GetValue(network, null) && !(bool)particleType.GetProperty("PointerActive").GetValue(network, null), "stopping motion detaches the timer and pointer");
        particleType.GetMethod("SetRunning").Invoke(network, new object[] { true });
        network.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        Check(!(bool)particleType.GetProperty("Running").GetValue(network, null), "unloading the background stops its shared timer");
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
        var outward = new MouseWheelEventArgs(Mouse.PrimaryDevice, 999, 120) { RoutedEvent = Mouse.MouseWheelEvent };
        smoothType.GetMethod("OnMouseWheel", Private).Invoke(scroll, new object[] { outward });
        Check(!outward.Handled, "wheel input at the upper edge can reach an enclosing scroll viewer");
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
        Check((double)smoothType.GetField("_responseMilliseconds", Private).GetValue(scroll) == 24.0, "small touchpad deltas keep their existing response");
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
        Check((double)smoothType.GetField("_responseMilliseconds", Private).GetValue(scroll) == 60.0, "mouse wheel notches use a gentler response");
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
