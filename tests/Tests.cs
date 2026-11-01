using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
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
            CheckWindowsBackend();
            CheckParallelBrightness();
            CheckBrightnessFailures();
            CheckHdmiSafety();
            CheckHdmiRecovery();
            Console.WriteLine(count + " checks passed. No physical monitor values were changed."); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static MonitorFeature Brightness(MonitorDevice device)
    { return device.Features.Single(x => x.Definition.Code == 0x10); }

    private static void CheckWindowsBackend()
    {
        var panel = new MonitorDevice { Id = @"ACM1234\ONE|0" };
        var other = new MonitorDevice { Id = @"ACM1234\TWO|0" };
        foreach (var monitor in new[] { panel, other })
        {
            monitor.Features.Add(new MonitorFeature { Definition = FeatureCatalog.All[0], Backend = "HighLevel", Maximum = 100, Current = 70 });
            monitor.Features.Add(new MonitorFeature { Definition = FeatureCatalog.All[1], Maximum = 100, Current = 50 });
        }
        var devices = new List<MonitorDevice> { panel, other };
        MonitorService.ApplyWmiBrightness(devices, @"DISPLAY\ACM1234\ONE_0", 60, new byte[] { 100, 50, 50, 0, 255 });
        Check(devices.Count == 2 && Brightness(panel).Backend == "WMI" && Brightness(panel).Current == 60, "Windows-controlled panels use WMI even if DDC brightness was also discovered");
        Check(panel.Features.Count == 2 && panel.Features[1].Current == 50 && Brightness(other).Backend == "HighLevel", "Preferring native brightness preserves contrast and identical monitors on other connections");
        Check(Brightness(panel).Choices.SequenceEqual(new uint[] { 0, 50, 100 }), "Native brightness levels are valid, unique and ordered");
    }

    private static void CheckParallelBrightness()
    {
        var transport = new FakeTransport();
        using (var service = new MonitorService(true, transport))
        using (var firstStarted = new ManualResetEventSlim())
        using (var secondStarted = new ManualResetEventSlim())
        using (var releaseFirst = new ManualResetEventSlim())
        using (var singleStarted = new ManualResetEventSlim())
        {
            var devices = service.ScanAsync().GetAwaiter().GetResult();
            transport.BeforeWrite = delegate(MonitorDevice device, uint value) {
                if (device == devices[0] && value == 40)
                { firstStarted.Set(); if (!releaseFirst.Wait(5000)) throw new TimeoutException("Blocked fake monitor was not released."); }
                if (device == devices[1]) secondStarted.Set();
                if (device == devices[0] && value == 55) singleStarted.Set();
            };
            var batch = service.SetBrightnessAsync(new Dictionary<MonitorDevice, int> { { devices[0], 40 }, { devices[1], 40 } });
            Task<uint> single = null;
            try
            {
                Check(firstStarted.Wait(5000) && secondStarted.Wait(5000), "The second monitor starts its write while the first monitor is still blocked");
                single = service.SetAsync(devices[0], Brightness(devices[0]), 55);
                Check(!singleStarted.Wait(100), "A separate control cannot interleave writes on a monitor inside a brightness batch");
            }
            finally { releaseFirst.Set(); }
            Check(batch.GetAwaiter().GetResult().All(x => x.Error == null), "Independent brightness writes and confirmations both succeed");
            Check(single.GetAwaiter().GetResult() == 55 && Brightness(devices[1]).Current == 40 && transport.Reads == 3, "Single writes resume after the batch and brightness uses one confirmation per write");

            firstStarted.Reset(); releaseFirst.Reset();
            var old = devices[0];
            batch = service.SetBrightnessAsync(new Dictionary<MonitorDevice, int> { { old, 40 } });
            Task<List<MonitorDevice>> scan = null;
            try
            {
                Check(firstStarted.Wait(5000), "A batch starts before a concurrent rescan");
                scan = service.ScanAsync();
                Check(!scan.Wait(100) && !old.Disposed, "Rescanning waits for in-flight brightness writes before destroying physical handles");
            }
            finally { releaseFirst.Set(); }
            Check(batch.GetAwaiter().GetResult().All(x => x.Error == null) && scan.GetAwaiter().GetResult().Count == 3 && old.Disposed, "Rescanning completes safely once the batch releases its connections");
            var stale = service.SetBrightnessAsync(new Dictionary<MonitorDevice, int> { { old, 30 } }).GetAwaiter().GetResult();
            Check(stale.Single().Error is InvalidOperationException, "Batches reject stale monitor models after reconnection");
        }
    }

    private static void CheckBrightnessFailures()
    {
        var transport = new FakeTransport();
        using (var service = new MonitorService(true, transport))
        {
            var devices = service.ScanAsync().GetAwaiter().GetResult();
            var disconnected = devices[0];
            transport.BeforeWrite = delegate(MonitorDevice device, uint value) { if (device == disconnected) throw new Win32Exception(31, "Disconnected HDMI"); };
            var results = service.SetBrightnessAsync(new Dictionary<MonitorDevice, int> { { devices[0], 35 }, { devices[1], 35 } }).GetAwaiter().GetResult();
            Check(results.Single(x => x.Device == devices[0]).Error is Win32Exception && results.Single(x => x.Device == devices[1]).Error == null && Brightness(devices[1]).Current == 35, "A disconnected HDMI monitor does not stop another monitor from changing brightness");
            int writes = transport.Writes;
            results = service.SetBrightnessAsync(new Dictionary<MonitorDevice, int> { { devices[0], 30 } }).GetAwaiter().GetResult();
            Check(results.Single().Error is InvalidOperationException && transport.Writes == writes && Brightness(devices[0]).Current == 72, "A failed HDMI connection receives no further commands until it is rescanned");
            devices = service.ScanAsync().GetAwaiter().GetResult();
            Check(service.SetAsync(devices[0], Brightness(devices[0]), 35).GetAwaiter().GetResult() == 35, "A fresh connection can be controlled again after rescanning");
            transport.Readable = false;
            int reads = transport.Reads;
            results = service.SetBrightnessAsync(new Dictionary<MonitorDevice, int> { { devices[1], 25 } }).GetAwaiter().GetResult();
            Check(results.Single().Error != null && transport.Reads == reads + 1 && Brightness(devices[1]).Current == 72, "Unreadable brightness is reported without repeated DDC queries or pretending it was confirmed");
        }
    }

    private sealed class FakeTransport : IMonitorTransport
    {
        private readonly ConcurrentDictionary<MonitorFeature, uint> values = new ConcurrentDictionary<MonitorFeature, uint>();
        internal Action<MonitorDevice, uint> BeforeWrite;
        internal int Writes, Reads;
        internal bool Readable = true;
        public void Write(MonitorDevice device, MonitorFeature feature, uint value)
        {
            Interlocked.Increment(ref Writes);
            if (BeforeWrite != null) BeforeWrite(device, value);
            values[feature] = value;
        }
        public bool TryRead(MonitorDevice device, MonitorFeature feature, out uint value)
        { Interlocked.Increment(ref Reads); return values.TryGetValue(feature, out value) && Readable; }
    }

    private static void CheckHdmiSafety()
    {
        var device = new MonitorDevice { Id = "hdmi-pacing", IsHdmi = true, SafeDdc = true, Demo = true };
        int calls = 0;
        MonitorService.DdcCall(device, "probe", delegate { calls++; return true; });
        var clock = Stopwatch.StartNew();
        MonitorService.DdcCall(device, "probe", delegate { calls++; return true; });
        Check(calls == 2 && clock.ElapsedMilliseconds >= 135, "HDMI control calls leave a settling interval between commands");
        device.ControlFaulted = true;
        Check(!MonitorService.DdcCall(device, "probe", delegate { calls++; return true; }) && calls == 2, "Quarantined HDMI calls never reach the native transport");
        device.Name = "alias"; device.HardwareName = "hardware";
        device.Features.Add(new MonitorFeature { Definition = FeatureCatalog.All[0], Maximum = 100, Current = 45 });
        var fresh = new MonitorDevice { Id = device.Id, IsHdmi = true, SafeDdc = true };
        var old = new Dictionary<string, MonitorDevice> { { device.Id, device } };
        Check(MonitorService.RestoreConnectionState(fresh, old, true) && fresh.ControlFaulted && Brightness(fresh).Current == 45 && fresh.Name == "hardware", "Even explicit rescanning preserves HDMI quarantine without confusing aliases with hardware names");
        fresh.Features[0].Current = 20;
        Check(Brightness(device).Current == 45, "Rebinding HDMI state does not share mutable feature values with stale models");
        Check(!MonitorService.RestoreConnectionState(new MonitorDevice { Id = device.Id, IsHdmi = false }, old, false), "A changed connector does not inherit another HDMI channel's quarantine");
        device.ControlFaulted = false;
        Check(MonitorService.RestoreConnectionState(new MonitorDevice { Id = device.Id, IsHdmi = true, SafeDdc = true }, old, false), "Automatic display notifications reuse known HDMI controls without querying the hardware again");
        Check(!MonitorService.RestoreConnectionState(new MonitorDevice { Id = device.Id, IsHdmi = true, SafeDdc = false }, old, false), "Changing HDMI safety mode triggers fresh capability discovery");
        Check(Marshal.SizeOf(typeof(WindowsDisplayRecovery.DisplayPath)) == 72 && Marshal.SizeOf(typeof(WindowsDisplayRecovery.DisplayMode)) == 64, "Display configuration snapshots match the Windows native structure sizes");
    }

    private static void CheckHdmiRecovery()
    {
        var transport = new FakeTransport(); var recovery = new FakeRecovery();
        using (var service = new MonitorService(true, transport, recovery))
        {
            var monitor = service.ScanAsync().GetAwaiter().GetResult()[0];
            monitor.IsHdmi = true; monitor.SafeDdc = true; monitor.Connection = "HDMI"; monitor.ControlFaulted = true;
            monitor = service.ScanAsync().GetAwaiter().GetResult()[0];
            int writes = transport.Writes;
            bool rejected = false;
            try { service.SetAsync(monitor, Brightness(monitor), 40).GetAwaiter().GetResult(); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected && transport.Writes == writes && monitor.ControlFaulted, "Rescanning a failed HDMI connection does not silently resume brightness traffic");
            service.RecoverHdmiAsync(monitor, true).GetAwaiter().GetResult();
            Check(recovery.Validations == 1 && recovery.Resets == 0 && !monitor.Disposed && monitor.ControlFaulted, "Validation checks recovery without resetting outputs or clearing quarantine");
            recovery.Fail = true;
            rejected = false;
            try { service.RecoverHdmiAsync(monitor).GetAwaiter().GetResult(); } catch (InvalidOperationException) { rejected = true; }
            monitor = service.ScanAsync(false).GetAwaiter().GetResult()[0];
            Check(rejected && monitor.ControlFaulted, "A failed signal reset keeps HDMI control quarantined after rebinding");
            recovery.Fail = false;
            service.RecoverHdmiAsync(monitor).GetAwaiter().GetResult();
            rejected = false;
            try { service.SetAsync(monitor, Brightness(monitor), 40).GetAwaiter().GetResult(); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected && monitor.Disposed && recovery.Resets == 2 && transport.Writes == writes, "Signal recovery invalidates old handles so queued brightness writes cannot replay afterward");
            monitor = service.ScanAsync(false).GetAwaiter().GetResult()[0];
            Check(!monitor.ControlFaulted && service.SetAsync(monitor, Brightness(monitor), 40).GetAwaiter().GetResult() == 40, "Fresh HDMI handles resume control after an explicit successful reinitialization");
        }
    }

    private sealed class FakeRecovery : IDisplayRecovery
    {
        internal int Validations, Resets;
        internal bool Fail;
        public void Reinitialize(MonitorDevice monitor, bool validateOnly)
        {
            if (validateOnly) { Validations++; return; }
            Resets++;
            if (Fail) throw new InvalidOperationException("Fake driver refused the supplied configuration");
        }
    }
}
