using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Principal;
using System.Threading;

// A windowless probe of the shell handoff. It never opens Blackwall or installs an update.
internal static class NativeShellCheck
{
    private static bool Elevated()
    {
        using (var identity = WindowsIdentity.GetCurrent())
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--probe")
        {
            File.WriteAllText(args[1], Elevated() ? "elevated" : "standard");
            return 0;
        }
        string report = Path.Combine(args[1], "shell-handoff.txt");
        try
        {
            string result = Path.Combine(args[1], "shell-probe-" + Guid.NewGuid().ToString("N") + ".txt");
            var program = Assembly.LoadFrom(Path.GetFullPath(args[0])).GetType("Blackwall.Program", true);
            program.GetMethod("LaunchUnelevated", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { Process.GetCurrentProcess().MainModule.FileName, "--probe \"" + result + "\"" });
            var watch = Stopwatch.StartNew();
            while (!File.Exists(result) && watch.ElapsedMilliseconds < 10000) Thread.Sleep(50);
            if (!File.Exists(result) || File.ReadAllText(result) != "standard") throw new InvalidOperationException("The interactive shell did not launch a standard-token probe.");
            File.WriteAllText(report, "PASS: interactive shell launched a standard-token probe; parentElevated=" + Elevated() + ". This does not accept UAC or perform an OTA update.");
            File.Delete(result);
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(report, "FAIL: " + ex);
            return 1;
        }
    }
}
