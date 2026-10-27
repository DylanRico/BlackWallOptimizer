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
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

// Section switching in a real WPF host: time the click blocks the UI thread, time to the first frame of the new
// section, and the worst frame gap while the entrance settles. Process CPU includes WPF's render thread; not GPU time.
internal static class NativeNavigationBenchmark
{
    const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;

    [STAThread]
    private static void Main(string[] args)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Type type = Assembly.LoadFrom(Path.GetFullPath(args[0])).GetType("Blackwall.MainWindow", true);
        var window = (Window)Activator.CreateInstance(type, new object[] { Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[0])), "benchmark-unused-preferences.json") });
        ((DispatcherTimer)type.GetField("_scriptMonitorTimer", Flags).GetValue(window)).Stop();
        type.GetField("_reduceMotion", Flags).SetValue(window, false);
        object loaded = type.GetField("_loadedModules", Flags).GetValue(window);
        foreach (string module in new[] { "hardware", "tweaks", "apps", "monitor", "safety" }) /* "hardware" too: no real WMI scan competing for CPU */ loaded.GetType().GetMethod("Add").Invoke(loaded, new object[] { module });
        var root = (FrameworkElement)window.Content;
        window.Content = null;
        MethodInfo switchTab = type.GetMethod("SwitchTab", Flags);
        string[] tabs = { "optimization", "debloat", "apps", "monitor", "safety", "hardware" };
        var block = new List<double>(); var firstFrame = new List<double>(); var worstGap = new List<double>(); var perTab = tabs.ToDictionary(t => t, t => new List<double>()); var layoutMs = tabs.ToDictionary(t => t, t => new List<double>());
        using (var host = new HwndSource(new HwndSourceParameters("Blackwall navigation benchmark") { Width = 1400, Height = 880, PositionX = -12000, PositionY = -12000, WindowStyle = unchecked((int)0x90000000) }))
        {
            host.RootVisual = root;
            Pump(400);
            Process process = Process.GetCurrentProcess();
            process.Refresh();
            TimeSpan cpu = process.TotalProcessorTime;
            for (int round = 0; round < 4; round++)
                foreach (string tab in tabs)
                {
                    var frames = new List<double>();
                    var clock = Stopwatch.StartNew();
                    TimeSpan last = TimeSpan.MinValue;
                    EventHandler rendered = (s, e) => { var r = (RenderingEventArgs)e; if (r.RenderingTime != last) { last = r.RenderingTime; frames.Add(clock.Elapsed.TotalMilliseconds); } };
                    CompositionTarget.Rendering += rendered;
                    switchTab.Invoke(window, new object[] { tab });
                    double blocked = clock.Elapsed.TotalMilliseconds;
                    if (args.Length > 1) { root.UpdateLayout(); if (round > 0) layoutMs[tab].Add(clock.Elapsed.TotalMilliseconds - blocked); }
                    Pump(450);
                    CompositionTarget.Rendering -= rendered;
                    if (round == 0) continue; // first visit includes JIT
                    block.Add(blocked);
                    firstFrame.Add(frames.Count > 0 ? frames[0] : 450);
                    perTab[tab].Add(frames.Count > 0 ? frames[0] : 450);
                    double gap = 0; for (int i = 1; i < frames.Count; i++) gap = Math.Max(gap, frames[i] - frames[i - 1]);
                    worstGap.Add(Math.Max(gap, frames.Count > 0 ? frames[0] : 450));
                }
            process.Refresh();
            Console.WriteLine("{\"switches\":" + block.Count + ",\"blockMedianMs\":" + Med(block) + ",\"blockMaxMs\":" + F(block.Max()) + ",\"firstFrameMedianMs\":" + Med(firstFrame) + ",\"worstGapMedianMs\":" + Med(worstGap) + ",\"worstGapMaxMs\":" + F(worstGap.Max()) + ",\"cpuMs\":" + F((process.TotalProcessorTime - cpu).TotalMilliseconds) + ",\"firstFrameByTab\":{" + string.Join(",", tabs.Select(t => "\"" + t + "\":" + Med(perTab[t]))) + "}" + (args.Length > 1 ? ",\"layoutByTab\":{" + string.Join(",", tabs.Select(t => "\"" + t + "\":" + Med(layoutMs[t]))) + "}" : "") + "}");
        }
        window.Close(); app.Shutdown();
    }

    static void Pump(int milliseconds)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < milliseconds)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame); Thread.Sleep(1);
        }
    }
    static string Med(List<double> v) { var s = v.OrderBy(x => x).ToList(); return F(s[s.Count / 2]); }
    static string F(double v) { return v.ToString("0.#", CultureInfo.InvariantCulture); }
}
