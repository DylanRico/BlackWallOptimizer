using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

// Synthetic input through the real catalog view. Render-event cadence is not a GPU/FPS measurement.
internal static class NativeScrollBenchmark
{
    [STAThread]
    private static void Main(string[] args)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Type type = Assembly.LoadFrom(Path.GetFullPath(args[0])).GetType("Blackwall.MainWindow", true);
        var window = (Window)Activator.CreateInstance(type, new object[] { Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[0])), "benchmark-unused-preferences.json") });
        ((DispatcherTimer)type.GetField("_scriptMonitorTimer", flags).GetValue(window)).Stop();
        type.GetField("_reduceMotion", flags).SetValue(window, true);
        type.GetField("_activeTab", flags).SetValue(window, "optimization");
        type.GetField("_optDeviceScope", flags).SetValue(window, "all");
        object loaded = type.GetField("_loadedModules", flags).GetValue(window);
        loaded.GetType().GetMethod("Add").Invoke(loaded, new object[] { "tweaks" });
        type.GetMethod("RenderCurrentTab", flags).Invoke(window, null);
        var scroll = (ScrollViewer)type.GetField("_mainScroll", flags).GetValue(window);
        ((Panel)scroll.Parent).Children.Remove(scroll);
        scroll.Resources = window.Resources;
        Type smooth = scroll.GetType();
        smooth.GetProperty("ReduceMotion").SetValue(scroll, false, null);
        MethodInfo wheel = smooth.GetMethod("OnMouseWheel", flags);
        using (var host = new HwndSource(new HwndSourceParameters("Blackwall isolated input benchmark") { Width = 1100, Height = 650, PositionX = -12000, PositionY = -12000, WindowStyle = unchecked((int)0x90000000) }))
        {
            host.RootVisual = scroll;
            scroll.UpdateLayout();
            if (scroll.ScrollableHeight <= 0) throw new InvalidOperationException("Catalog fixture does not scroll.");
            var clock = Stopwatch.StartNew();
            var intervals = new List<double>();
            var handlerTimes = new List<double>();
            var jumps = new List<double>();
            double last = 0, previousOffset = 0;
            TimeSpan renderTime = TimeSpan.MinValue;
            EventHandler rendered = (s, e) =>
            {
                var frame = (RenderingEventArgs)e;
                if (frame.RenderingTime == renderTime) return;
                renderTime = frame.RenderingTime;
                double now = clock.Elapsed.TotalMilliseconds;
                if (last > 0) intervals.Add(now - last);
                jumps.Add(Math.Abs(scroll.VerticalOffset - previousOffset));
                previousOffset = scroll.VerticalOffset; last = now;
            };
            CompositionTarget.Rendering += rendered;
            int index = 0;
            var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (s, e) =>
            {
                var input = new MouseWheelEventArgs(Mouse.PrimaryDevice, (int)clock.ElapsedMilliseconds, index < 100 ? -18 : 18) { RoutedEvent = Mouse.MouseWheelEvent };
                var measured = Stopwatch.StartNew(); wheel.Invoke(scroll, new object[] { input }); measured.Stop();
                handlerTimes.Add(measured.Elapsed.TotalMilliseconds);
                if (++index == 160) timer.Stop();
            };
            Process process = Process.GetCurrentProcess();
            TimeSpan cpu = process.TotalProcessorTime;
            timer.Start();
            try
            {
                while (timer.IsEnabled || (bool)smooth.GetField("_scrolling", flags).GetValue(scroll))
                {
                    if (clock.ElapsedMilliseconds > 10000) throw new TimeoutException("Scroll did not settle.");
                    var frame = new DispatcherFrame();
                    Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
                    Dispatcher.PushFrame(frame); Thread.Sleep(1);
                }
            }
            finally { timer.Stop(); CompositionTarget.Rendering -= rendered; }
            if (index != 160 || scroll.VerticalOffset < 0 || scroll.VerticalOffset > scroll.ScrollableHeight) throw new InvalidOperationException("Input was lost or escaped bounds.");
            Console.WriteLine("{\"events\":" + index + ",\"frames\":" + intervals.Count + ",\"renderEventP95Ms\":" + P95(intervals) + ",\"handlerP95Ms\":" + P95(handlerTimes) + ",\"offsetStepP95\":" + P95(jumps) + ",\"elapsedMs\":" + clock.ElapsedMilliseconds + ",\"cpuMs\":" + ((long)(process.TotalProcessorTime - cpu).TotalMilliseconds) + "}");
        }
        window.Close(); app.Shutdown();
    }

    private static string P95(List<double> values)
    {
        values.Sort();
        return (values.Count == 0 ? 0 : values[(int)Math.Ceiling(values.Count * 0.95) - 1]).ToString("0.###", CultureInfo.InvariantCulture);
    }
}
