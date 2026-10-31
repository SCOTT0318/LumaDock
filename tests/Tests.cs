using System;
using System.Linq;
using MonitorControl;

internal static class Tests
{
    private static int count;
    private static void Check(bool condition, string description)
    { if (!condition) throw new Exception(description); count++; Console.WriteLine("PASS " + description); }
    public static int Main()
    {
        try
        {
            var caps = CapabilityParser.Parse("(prot(monitor) model(Test) vcp(10 12 60(01 0F 11 12) 62 1A))");
            Check(caps.Count == 5, "Enum values do not become feature codes");
            Check(caps[0x60].SequenceEqual(new uint[] { 1, 15, 17, 18 }), "Hex enum choices preserve monitor order");
            Check(!caps.ContainsKey(0x0F), "Nested input value is not a feature");
            Check(CapabilityParser.Parse("vcp(10 60(11 12)").Count == 0, "Truncated capability report is rejected");
            Check(CapabilityParser.Parse(null).Count == 0, "Unavailable capabilities are handled");
            var compact = CapabilityParser.Parse("VCP(10 60(0f 11)62 1a FF zz 1234 60(11 12))");
            Check(compact.Count == 5 && compact[0x60].Count == 3, "Compact, duplicate, uppercase and invalid tokens are handled");
            var table = CapabilityParser.Parse("vcp(10 60(01(02) 11) 12)");
            Check(table.Count == 3 && table[0x60].Count == 0, "Nested table payload never becomes writable enum choices");
            var range = new MonitorFeature { Minimum = 20, Maximum = 220 };
            Check(range.FromPercent(50) == 120 && range.Percent(120) == 50, "Nonzero native ranges map correctly to percentages");
            Check(range.FromPercent(-10) == 20 && range.FromPercent(200) == 220, "Percentage input is clamped");
            Check(new MonitorFeature { Minimum = 8, Maximum = 8 }.Percent(8) == 0, "Zero-width ranges do not divide by zero");
            Check(MonitorService.NormalizeId(@"\\?\DISPLAY#DEL1234#5&ABC&UID100#{GUID}") == MonitorService.NormalizeId(@"DISPLAY\DEL1234\5&ABC&UID100_0"), "Windows device interface and WMI instance IDs match exactly");
            Check(MonitorService.NormalizeId(@"DISPLAY\DEL1234\5&ABC&UID100_0") != MonitorService.NormalizeId(@"DISPLAY\DEL1234\5&ABC&UID101_0"), "Identical models on different connections keep separate identities");
            using (var service = new MonitorService(true))
            {
                var monitors = service.ScanAsync().GetAwaiter().GetResult();
                Check(monitors.Count == 3 && monitors.All(x => x.Demo), "Demo discovery never creates physical handles");
                Check(monitors[0].Features.Count == 9 && monitors[1].Features.Count == 4 && monitors[2].Features.Count == 1, "Different monitors expose different menus");
                var brightness = monitors[0].Features.First(x => x.Definition.Code == 0x10);
                Check(brightness.Current == 72, "Discovery does not change initial brightness");
                Check(service.SetAsync(monitors[0], brightness, 43).GetAwaiter().GetResult() == 43, "A supported value is applied to its selected monitor");
                Check(monitors[1].Features.First(x => x.Definition.Code == 0x10).Current == 72, "Changing one monitor does not change another");
                bool invalid = false;
                try { service.SetAsync(monitors[0], brightness, 101).GetAwaiter().GetResult(); } catch (ArgumentOutOfRangeException) { invalid = true; }
                Check(invalid && brightness.Current == 43, "Out-of-range writes are rejected without changing state");
                var input = monitors[0].Features.First(x => x.Definition.Code == 0x60);
                invalid = false;
                try { service.SetAsync(monitors[0], input, 0x99).GetAwaiter().GetResult(); } catch (ArgumentOutOfRangeException) { invalid = true; }
                Check(invalid, "Unreported input sources cannot be written");
                service.ScanAsync().GetAwaiter().GetResult();
                bool stale = false;
                try { service.SetAsync(monitors[0], brightness, 20).GetAwaiter().GetResult(); } catch (InvalidOperationException) { stale = true; }
                Check(stale && monitors[0].Disposed, "Rescan invalidates old handles and rejects stale writes");
            }
            var closed = new MonitorService(true); closed.Dispose();
            bool disposed = false;
            try { closed.ScanAsync().GetAwaiter().GetResult(); } catch (ObjectDisposedException) { disposed = true; }
            Check(disposed, "Closed services cannot reopen monitor connections");
            Console.WriteLine(count + " checks passed. No physical monitor values were changed."); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
