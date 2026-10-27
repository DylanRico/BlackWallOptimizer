using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

// Cost of the dimmed backdrop behind the update dialog while the UI underneath keeps repainting
// (as the RAM/process refresh does). Process CPU includes WPF's in-process render thread; it is not GPU time.
internal static class NativeBackdropBenchmark
{
    const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;

    [STAThread]
    private static void Main(string[] args)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Type type = Assembly.LoadFrom(Path.GetFullPath(args[0])).GetType("Blackwall.MainWindow", true);
        var window = (Window)Activator.CreateInstance(type, new object[] { Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[0])), "benchmark-unused-preferences.json") });
        ((DispatcherTimer)type.GetField("_scriptMonitorTimer", Flags).GetValue(window)).Stop();
        type.GetField("_activeTab", Flags).SetValue(window, "optimization");
        object loaded = type.GetField("_loadedModules", Flags).GetValue(window);
        loaded.GetType().GetMethod("Add").Invoke(loaded, new object[] { "tweaks" });
        type.GetMethod("RenderCurrentTab", Flags).Invoke(window, null);
        var root = (Border)type.GetField("_rootBorder", Flags).GetValue(window);
        var status = (TextBlock)type.GetField("_statusText", Flags).GetValue(window);
        window.Content = null;
        MethodInfo backdrop = type.GetMethod("SetModalBackdrop", Flags);
        using (var host = new HwndSource(new HwndSourceParameters("Blackwall backdrop benchmark") { Width = 1400, Height = 880, PositionX = -12000, PositionY = -12000, WindowStyle = unchecked((int)0x90000000) }))
        {
            host.RootVisual = root;
            root.UpdateLayout();
            string plain = Phase(status, 1500);
            // Builds without SetModalBackdrop are measured with the exact backdrop they shipped (opacity 0.55 + blur 3).
            if (backdrop != null) backdrop.Invoke(window, new object[] { true });
            else { root.Opacity = 0.55; root.Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 3 }; }
            string dimmed = Phase(status, 1500);
            Console.WriteLine("{\"renderTier\":" + (RenderCapability.Tier >> 16) + ",\"backdrop\":\"" + (backdrop != null ? "scrim" : "blur") + "\",\"plain\":" + plain + ",\"dimmed\":" + dimmed + "}");
        }
        window.Close(); app.Shutdown();
    }

    private static string Phase(TextBlock status, int milliseconds)
    {
        var intervals = new List<double>();
        var clock = Stopwatch.StartNew();
        double last = 0; int updates = 0;
        TimeSpan renderTime = TimeSpan.MinValue;
        EventHandler rendered = (s, e) =>
        {
            var frame = (RenderingEventArgs)e;
            if (frame.RenderingTime == renderTime) return;
            renderTime = frame.RenderingTime;
            double now = clock.Elapsed.TotalMilliseconds;
            if (last > 0) intervals.Add(now - last);
            last = now;
            status.Text = "Actualizando " + (++updates); // keeps a region under the backdrop dirty every frame
        };
        Process process = Process.GetCurrentProcess();
        process.Refresh();
        TimeSpan cpu = process.TotalProcessorTime;
        CompositionTarget.Rendering += rendered;
        try
        {
            while (clock.ElapsedMilliseconds < milliseconds)
            {
                var frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame); Thread.Sleep(1);
            }
        }
        finally { CompositionTarget.Rendering -= rendered; }
        process.Refresh();
        double cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds;
        intervals.Sort();
        double p95 = intervals.Count == 0 ? 0 : intervals[(int)Math.Ceiling(intervals.Count * 0.95) - 1];
        return "{\"frames\":" + intervals.Count + ",\"renderP95Ms\":" + p95.ToString("0.###", CultureInfo.InvariantCulture) + ",\"cpuMs\":" + cpuMs.ToString("0", CultureInfo.InvariantCulture) + "}";
    }
}
