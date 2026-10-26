using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
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
        Window window = (Window)Activator.CreateInstance(type, new object[] { Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[0])), "benchmark-unused-preferences.json") });
        try
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            bool lifetime = args.Length > 1 && args[1] == "--lifetime";
            var oldPages = new List<WeakReference>();
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
            long startupCpu = (long)(process.TotalProcessorTime - cpu).TotalMilliseconds;
            foreach (string module in new[] { "tweaks", "apps", "monitor", "safety" }) loaded.GetType().GetMethod("Add").Invoke(loaded, new object[] { module });
            Stopwatch navigation = Stopwatch.StartNew();
            for (int iteration = 0; iteration < (lifetime ? 17 : 5); iteration++)
                foreach (string tab in new[] { "hardware", "optimization", "debloat", "apps", "monitor", "safety" })
                {
                    if (lifetime) oldPages.Add(new WeakReference(ViewerPage(type, window, flags)));
                    type.GetField("_activeTab", flags).SetValue(window, tab);
                    type.GetMethod("RenderCurrentTab", flags).Invoke(window, null);
                    content.Measure(new Size(1400, 880));
                    content.Arrange(new Rect(0, 0, 1400, 880));
                    content.UpdateLayout();
                }
            navigation.Stop();
            type.GetField("_activeTab", flags).SetValue(window, "optimization");
            type.GetMethod("RenderCurrentTab", flags).Invoke(window, null);
            content.Measure(new Size(1400, 880)); content.Arrange(new Rect(0, 0, 1400, 880)); content.UpdateLayout();
            var searchDurations = new List<double>();
            object searchPage = ViewerPage(type, window, flags);
            int replacedPages = 0;
            int collections = GC.CollectionCount(0);
            Stopwatch searching = Stopwatch.StartNew();
            string[] queries = { "a", "au", "aud", "audio", "zzzz-no-results", "" };
            for (int i = 0; i < 108; i++)
            {
                var input = (TextBox)type.GetField("_sectionSearch", flags).GetValue(window);
                Stopwatch update = Stopwatch.StartNew();
                input.Text = queries[i % queries.Length];
                content.UpdateLayout();
                update.Stop();
                searchDurations.Add(update.Elapsed.TotalMilliseconds);
                object current = ViewerPage(type, window, flags);
                if (!Object.ReferenceEquals(searchPage, current)) replacedPages++;
                searchPage = current;
            }
            searching.Stop();
            collections = GC.CollectionCount(0) - collections;
            searchDurations.Sort();
            double searchP95 = searchDurations[(int)Math.Ceiling(searchDurations.Count * 0.95) - 1];
            var viewer = (System.Windows.Controls.ScrollViewer)type.GetField("_mainScroll", flags).GetValue(window);
            Stopwatch scrolling = Stopwatch.StartNew();
            for (int step = 0; step < 100; step++) { viewer.ScrollToVerticalOffset((step % 20) * 120); content.UpdateLayout(); }
            scrolling.Stop();
            // Drain navigation callbacks and finish entrances before sampling steady idle.
            PumpFor(1000);
            TimeSpan idleCpu = process.TotalProcessorTime;
            PumpFor(1000);
            long idleCpuMs = (long)(process.TotalProcessorTime - idleCpu).TotalMilliseconds;
            int retainedPages = 0;
            if (lifetime)
            {
                // Collection is diagnostic-only in the harness, never used to manage production performance.
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                foreach (var page in oldPages) if (page.IsAlive) retainedPages++;
                if (retainedPages > 3) throw new InvalidOperationException("Detached navigation pages retained: " + retainedPages);
            }
            process.Refresh();
            Console.WriteLine("{\"elapsedMs\":" + watch.ElapsedMilliseconds + ",\"cpuMs\":" + startupCpu + ",\"navigationMs\":" + navigation.ElapsedMilliseconds + ",\"searchMs\":" + searching.ElapsedMilliseconds + ",\"searchP95Ms\":" + searchP95.ToString("0.###", CultureInfo.InvariantCulture) + ",\"searchPageReplacements\":" + replacedPages + ",\"searchGen0Collections\":" + collections + ",\"retainedPages\":" + retainedPages + ",\"scrollMs\":" + scrolling.ElapsedMilliseconds + ",\"idleCpuMs\":" + idleCpuMs + ",\"workingSetBytes\":" + process.WorkingSet64 + ",\"privateBytes\":" + process.PrivateMemorySize64 + "}");
        }
        finally { window.Close(); app.Shutdown(); }
    }

    private static object ViewerPage(Type type, Window window, BindingFlags flags)
    {
        return ((ScrollViewer)type.GetField("_mainScroll", flags).GetValue(window)).Content;
    }

    private static void PumpFor(int milliseconds)
    {
            Stopwatch idle = Stopwatch.StartNew();
            while (idle.ElapsedMilliseconds < milliseconds)
            {
                DispatcherFrame frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);
                Thread.Sleep(10);
            }
    }
}
