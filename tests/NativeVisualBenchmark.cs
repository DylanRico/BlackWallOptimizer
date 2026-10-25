using System;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

// Comparable construction + offscreen render only; no Loaded event or Windows mutations.
internal static class NativeVisualBenchmark
{
    [STAThread]
    public static void Main(string[] args)
    {
        Application app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Process process = Process.GetCurrentProcess();
        TimeSpan cpu = process.TotalProcessorTime;
        Stopwatch watch = Stopwatch.StartNew();
        Type type = Assembly.LoadFrom(args[0]).GetType("Blackwall.MainWindow", true);
        Window window = (Window)Activator.CreateInstance(type);
        try
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            ((DispatcherTimer)type.GetField("_scriptMonitorTimer", flags).GetValue(window)).Stop();
            object loaded = type.GetField("_loadedModules", flags).GetValue(window);
            loaded.GetType().GetMethod("Add").Invoke(loaded, new object[] { "hardware" });
            type.GetMethod("RenderCurrentTab", flags).Invoke(window, null);
            FrameworkElement content = (FrameworkElement)window.Content;
            content.Measure(new Size(1400, 880));
            content.Arrange(new Rect(0, 0, 1400, 880));
            content.UpdateLayout();
            var image = new RenderTargetBitmap(1400, 880, 96, 96, PixelFormats.Pbgra32);
            image.Render(content);
            watch.Stop();
            process.Refresh();
            Console.WriteLine("{\"elapsedMs\":" + watch.ElapsedMilliseconds + ",\"cpuMs\":" + (long)(process.TotalProcessorTime - cpu).TotalMilliseconds + ",\"workingSetBytes\":" + process.WorkingSet64 + ",\"privateBytes\":" + process.PrivateMemorySize64 + "}");
        }
        finally { window.Close(); app.Shutdown(); }
    }
}
