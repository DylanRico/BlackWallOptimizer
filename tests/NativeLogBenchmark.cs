using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

// Cost of logging while the activity screen is open (2 000 lines, laid out and rendered in a real WPF host),
// and of opening that screen afterwards. Works with old (TextBox console) and new (row log) builds.
internal static class NativeLogBenchmark
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
        var root = (FrameworkElement)window.Content;
        window.Content = null;
        MethodInfo append = type.GetMethod("AppendLog", Flags, null, new[] { typeof(string), typeof(string) }, null);
        MethodInfo switchTab = type.GetMethod("SwitchTab", Flags);
        using (var host = new HwndSource(new HwndSourceParameters("Blackwall log benchmark") { Width = 1400, Height = 880, PositionX = -12000, PositionY = -12000, WindowStyle = unchecked((int)0x90000000) }))
        {
            host.RootVisual = root;
            switchTab.Invoke(window, new object[] { args.Length > 1 && args[1] == "closed" ? "safety" : "monitor" });
            Pump(300);
            Process process = Process.GetCurrentProcess(); process.Refresh();
            TimeSpan cpu = process.TotalProcessorTime;
            var clock = Stopwatch.StartNew();
            for (int i = 0; i < 2000; i++)
            {
                append.Invoke(window, new object[] { "Registro · Services\\DiagTrack › Start = 4 (entrada " + i + ")", i % 10 == 0 ? "WARN" : "TWEAK" });
                if (i % 20 == 0) { root.UpdateLayout(); Pump(1); } // like a real burst: the UI keeps rendering between lines
            }
            Pump(200);
            double appendMs = clock.Elapsed.TotalMilliseconds;
            process.Refresh();
            double appendCpu = (process.TotalProcessorTime - cpu).TotalMilliseconds;
            switchTab.Invoke(window, new object[] { "safety" });
            Pump(200);
            clock.Restart();
            switchTab.Invoke(window, new object[] { "monitor" });
            root.UpdateLayout();
            double openMs = clock.Elapsed.TotalMilliseconds;
            process.Refresh();
            Console.WriteLine("{\"append2000Ms\":" + F(appendMs) + ",\"append2000CpuMs\":" + F(appendCpu) + ",\"openWithLogMs\":" + F(openMs) + ",\"privateMb\":" + F(process.PrivateMemorySize64 / 1048576.0) + "}");
        }
        window.Close(); app.Shutdown();
    }

    static void Pump(int milliseconds)
    {
        var clock = Stopwatch.StartNew();
        do
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame); Thread.Sleep(1);
        } while (clock.ElapsedMilliseconds < milliseconds);
    }
    static string F(double v) { return v.ToString("0.#", CultureInfo.InvariantCulture); }
}
