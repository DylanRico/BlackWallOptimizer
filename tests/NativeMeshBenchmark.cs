using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

// Background mesh while the cursor moves over the app (pointer updates at ~125 Hz, like a real mouse):
// how often the mesh actually advances, frame pacing, and process CPU (includes WPF's render thread, not GPU time).
internal static class NativeMeshBenchmark
{
    const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;

    [STAThread]
    private static void Main(string[] args)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Type type = Assembly.LoadFrom(Path.GetFullPath(args[0])).GetType("Blackwall.MainWindow", true);
        var window = (Window)Activator.CreateInstance(type, new object[] { Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[0])), "benchmark-unused-preferences.json") });
        ((DispatcherTimer)type.GetField("_scriptMonitorTimer", Flags).GetValue(window)).Stop();
        object loaded = type.GetField("_loadedModules", Flags).GetValue(window);
        foreach (string module in new[] { "hardware", "tweaks", "apps", "monitor", "safety" }) loaded.GetType().GetMethod("Add").Invoke(loaded, new object[] { module });
        type.GetField("_activeTab", Flags).SetValue(window, args.Length > 1 ? args[1] : "optimization");
        type.GetMethod("RenderCurrentTab", Flags).Invoke(window, null);
        var root = (FrameworkElement)window.Content; window.Content = null; root.Resources.MergedDictionaries.Add(window.Resources);
        object network = type.GetField("_network", Flags).GetValue(window);
        Type netType = network.GetType();
        var points = (Array)netType.GetField("_points", Flags).GetValue(network);
        using (var host = new HwndSource(new HwndSourceParameters("Blackwall mesh benchmark") { Width = 1366, Height = 768, PositionX = -12000, PositionY = -12000, WindowStyle = unchecked((int)0x90000000) }))
        {
            host.RootVisual = root;
            if (args.Length > 2 && args[2] == "cache-probe")
                ((UIElement)type.GetField("_mainScroll", Flags).GetValue(window)).CacheMode = new BitmapCache { SnapsToDevicePixels = true };
            if (args.Length > 2 && args[2] == "no-content")
                ((UIElement)type.GetField("_mainScroll", Flags).GetValue(window)).Visibility = Visibility.Hidden;
            Pump(500);
            netType.GetMethod("SetRunning").Invoke(network, new object[] { true });
            Pump(500);
            foreach (bool moving in new[] { false, true })
            {
                var intervals = new List<double>(); int frames = 0, meshFrames = 0;
                object lastPoint = points.GetValue(0);
                var clock = Stopwatch.StartNew(); double last = 0; TimeSpan lastRender = TimeSpan.MinValue;
                EventHandler rendered = (s, e) =>
                {
                    var r = (RenderingEventArgs)e; if (r.RenderingTime == lastRender) return; lastRender = r.RenderingTime;
                    double now = clock.Elapsed.TotalMilliseconds; if (last > 0) intervals.Add(now - last); last = now; frames++;
                    object p = points.GetValue(0); if (!p.Equals(lastPoint)) { meshFrames++; lastPoint = p; }
                };
                // A mouse delivers moves at Input priority; the mesh must keep up while they arrive.
                var mouse = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(8) };
                double angle = 0;
                mouse.Tick += (s, e) => { angle += 0.05; netType.GetMethod("SetPointer").Invoke(network, new object[] { (Point?)new Point(683 + Math.Cos(angle) * 300, 384 + Math.Sin(angle) * 200) }); };
                Process process = Process.GetCurrentProcess(); process.Refresh(); TimeSpan cpu = process.TotalProcessorTime;
                CompositionTarget.Rendering += rendered;
                if (moving) mouse.Start(); else netType.GetMethod("SetPointer").Invoke(network, new object[] { null });
                Pump(4000);
                mouse.Stop(); CompositionTarget.Rendering -= rendered;
                process.Refresh();
                intervals.Sort();
                double seconds = clock.Elapsed.TotalSeconds;
                Console.WriteLine("{\"cursor\":\"" + (moving ? "moving" : "idle") + "\",\"renderFps\":" + F(frames / seconds) + ",\"meshFps\":" + F(meshFrames / seconds) + ",\"frameP95Ms\":" + F(intervals.Count == 0 ? 0 : intervals[(int)Math.Ceiling(intervals.Count * .95) - 1]) + ",\"frameMaxMs\":" + F(intervals.Count == 0 ? 0 : intervals.Last()) + ",\"cpuPercent\":" + F((process.TotalProcessorTime - cpu).TotalMilliseconds / (seconds * 10)) + "}");
            }
            netType.GetMethod("SetRunning").Invoke(network, new object[] { false });
        }
        window.Close(); app.Shutdown();
    }

    static void Pump(int milliseconds)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < milliseconds)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame); Thread.Sleep(1);
        }
    }
    static string F(double v) { return v.ToString("0.#", CultureInfo.InvariantCulture); }
}
