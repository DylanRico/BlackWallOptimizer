using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Windows;

internal static class NativePerformanceFixture
{
    internal static void Prepare(Type type, Window window)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var caps = new Dictionary<string, object>();
        foreach (string key in new[] { "battery", "dedicatedGpu", "nvidia", "amdGpu", "intelGpu", "hags", "modernStandby", "s3", "hibernation", "systemSsd", "win11", "recallPolicy", "edge", "brave" }) caps[key] = true;
        caps["laptop"] = false; caps["desktop"] = true; caps["integratedOnly"] = false; caps["systemHdd"] = false; caps["win10"] = false;
        type.GetField("_hardware", flags).SetValue(window, new Dictionary<string, object> { { "chassis", new Dictionary<string, object> { { "isLaptop", false } } }, { "capabilities", caps }, { "gpus", new List<object>() } });
        var apps = new List<Dictionary<string, object>>();
        foreach (Dictionary<string, object> entry in (IEnumerable)type.GetField("_apps", flags).GetValue(window))
        {
            var copy = new Dictionary<string, object>(entry);
            copy["iconUrl"] = "";
            apps.Add(copy);
        }
        type.GetField("_apps", flags).SetValue(window, apps);
    }

    internal static void RequireCards(Type type, Window window)
    {
        if (((IDictionary)type.GetField("_tweakCardUpdaters", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window)).Count == 0)
            throw new InvalidOperationException("WinUtil fixture has no cards.");
    }
}
