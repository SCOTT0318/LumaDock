using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace MonitorControl
{
    // Pure state: changes emitted by this app and duplicate native reports must not
    // recursively become another brightness adjustment.
    public sealed class BrightnessDeltaTracker
    {
        private readonly Dictionary<string, int> values = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<ExpectedValue>> expected = new Dictionary<string, List<ExpectedValue>>(StringComparer.OrdinalIgnoreCase);
        private DateTime suppressUntil;
        private sealed class ExpectedValue { public int Value; public DateTime Until; }

        public void Seed(string instance, int brightness)
        { if (!String.IsNullOrWhiteSpace(instance) && brightness >= 0 && brightness <= 100) values[instance.Trim()] = brightness; }

        public void Expect(string instance, int brightness, DateTime now)
        {
            if (String.IsNullOrWhiteSpace(instance) || brightness < 0 || brightness > 100) return;
            instance = instance.Trim(); List<ExpectedValue> list;
            if (!expected.TryGetValue(instance, out list)) expected[instance] = list = new List<ExpectedValue>();
            list.RemoveAll(x => x.Until < now);
            if (list.Count >= 32) list.RemoveAt(0);
            list.Add(new ExpectedValue { Value = brightness, Until = now.AddSeconds(2) });
        }

        public void Suppress(DateTime until) { if (until > suppressUntil) suppressUntil = until; }

        public int Observe(string instance, int brightness, DateTime now)
        {
            if (String.IsNullOrWhiteSpace(instance) || brightness < 0 || brightness > 100) return 0;
            instance = instance.Trim(); int previous;
            bool known = values.TryGetValue(instance, out previous);
            values[instance] = brightness;
            List<ExpectedValue> list;
            bool selfWrite = false;
            if (expected.TryGetValue(instance, out list))
            {
                list.RemoveAll(x => x.Until < now);
                int match = list.FindIndex(x => x.Value == brightness);
                if (match >= 0) { selfWrite = true; list.RemoveAt(match); }
                if (list.Count == 0) expected.Remove(instance);
            }
            return !known || selfWrite || now < suppressUntil ? 0 : brightness - previous;
        }
    }

    public sealed class BrightnessNativeFusion
    {
        private DateTime rawUntil, lastWindows;
        private int lastWindowsSign;
        public int FromRaw(int direction, DateTime now)
        {
            bool continuingRaw = now < rawUntil;
            rawUntil = now.AddMilliseconds(600);
            // Usually Raw Input precedes WMI. Also cover the opposite ordering for
            // the first report, without suppressing subsequent real key presses.
            if (!continuingRaw && now - lastWindows < TimeSpan.FromMilliseconds(100) && lastWindowsSign == Math.Sign(direction)) return 0;
            return Math.Sign(direction);
        }
        public int FromWindows(int delta, DateTime now)
        {
            if (delta == 0 || now < rawUntil) return 0;
            lastWindows = now; lastWindowsSign = Math.Sign(delta); return delta;
        }
    }

    public sealed class BrightnessUsageTracker
    {
        private readonly Dictionary<string, int> previous = new Dictionary<string, int>();
        public int Observe(string reportKey, IEnumerable<ushort> usages)
        {
            bool up = usages.Contains((ushort)0x6F), down = usages.Contains((ushort)0x70);
            int state = (up ? 1 : 0) | (down ? 2 : 0), old;
            if (!previous.TryGetValue(reportKey, out old)) old = 0;
            previous[reportKey] = state;
            if (up && down) return 0;
            if (up && (old & 1) == 0) return 1;
            if (down && (old & 2) == 0) return -1;
            return 0;
        }
        public void RemoveDevice(string prefix)
        { foreach (string key in previous.Keys.Where(x => x.StartsWith(prefix, StringComparison.Ordinal)).ToArray()) previous.Remove(key); }
        public void Clear() { previous.Clear(); }
    }

    public sealed class BrightnessInput : IDisposable
    {
        private const int IncreaseId = 0x4C41, DecreaseId = 0x4C42;
        private readonly Window host;
        private readonly Action<int> onDelta;
        private readonly Action<string> status;
        private readonly bool demo;
        private readonly object sync = new object();
        private readonly BrightnessUsageTracker usages = new BrightnessUsageTracker();
        private readonly Dictionary<IntPtr, HidDescriptor> descriptors = new Dictionary<IntPtr, HidDescriptor>();
        private BrightnessDeltaTracker tracker = new BrightnessDeltaTracker();
        private BrightnessNativeFusion fusion = new BrightnessNativeFusion();
        private ManagementEventWatcher watcher;
        private HwndSource source;
        private bool enabled, followWindows, useHotkeys, disposed;
        private int step = 5, generation;
        private ModifierKeys modifiers = ModifierKeys.Control | ModifierKeys.Alt;
        private Key increase = Key.Up, decrease = Key.Down;
        public bool HotkeysAvailable { get; private set; }
        public bool RawInputAvailable { get; private set; }
        public bool WindowsEventsAvailable { get; private set; }
        // Valid only inside onDelta, null for Raw Input and registered hotkeys.
        public string WindowsSourceInstance { get; private set; }
        public int? WindowsSourceBrightness { get; private set; }
        public string SourceKind { get; private set; }

        public BrightnessInput(Window window, Action<int> onDeltaPercent, Action<string> report, bool demoMode)
        {
            if (window == null) throw new ArgumentNullException("window");
            host = window; onDelta = onDeltaPercent ?? delegate { }; status = report ?? delegate { }; demo = demoMode;
            host.SourceInitialized += OnSourceInitialized; host.Closed += OnClosed;
            if (new WindowInteropHelper(host).Handle != IntPtr.Zero) EnsureSource();
        }

        public void Configure(bool isEnabled, bool followNativeWindows, int stepPercent, ModifierKeys shortcutModifiers, Key increaseKey, Key decreaseKey, bool enableHotkeys = true)
        {
            host.Dispatcher.VerifyAccess();
            if (disposed) return;
            enabled = isEnabled; followWindows = followNativeWindows; useHotkeys = enableHotkeys;
            step = Math.Max(1, Math.Min(25, stepPercent)); modifiers = shortcutModifiers; increase = increaseKey; decrease = decreaseKey;
            ManagementEventWatcher old;
            int current;
            lock (sync) { current = ++generation; old = watcher; watcher = null; tracker = new BrightnessDeltaTracker(); fusion = new BrightnessNativeFusion(); WindowsEventsAvailable = false; }
            RetireWatcher(old);
            RemoveRegistrations(); usages.Clear();
            if (!enabled || demo) return;
            EnsureSource(); RegisterInputs();
            if (followWindows) StartWindowsWatcher(current);
        }

        public void SuppressWindowsEcho(TimeSpan duration)
        { lock (sync) tracker.Suppress(DateTime.UtcNow.AddMilliseconds(Math.Max(0, Math.Min(5000, duration.TotalMilliseconds)))); }

        public void ExpectWindowsBrightness(string instance, int brightnessPercent)
        { lock (sync) tracker.Expect(instance, brightnessPercent, DateTime.UtcNow); }

        public static bool ValidShortcut(ModifierKeys shortcutModifiers, Key up, Key down)
        {
            ModifierKeys allowed = ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift;
            return shortcutModifiers != ModifierKeys.None && (shortcutModifiers & ~allowed) == 0 && up != down
                && ValidKey(up) && ValidKey(down);
        }
        private static bool ValidKey(Key key)
        {
            return key != Key.None && key != Key.System && key != Key.F12 && key != Key.LeftAlt && key != Key.RightAlt
                && key != Key.LeftCtrl && key != Key.RightCtrl && key != Key.LeftShift && key != Key.RightShift
                && key != Key.LWin && key != Key.RWin && KeyInterop.VirtualKeyFromKey(key) > 0;
        }

        private void OnSourceInitialized(object sender, EventArgs e) { if (!disposed && !demo) { EnsureSource(); RegisterInputs(); } }
        private void EnsureSource()
        {
            if (source != null || demo) return;
            IntPtr handle = new WindowInteropHelper(host).Handle;
            if (handle == IntPtr.Zero) return;
            source = HwndSource.FromHwnd(handle); if (source != null) source.AddHook(WindowMessage);
        }

        private void RegisterInputs()
        {
            if (!enabled || source == null || disposed || demo) return;
            if (useHotkeys)
            {
                if (!ValidShortcut(modifiers, increase, decrease)) Report("유효한 밝기 단축키를 선택하세요.");
                else
                {
                    uint flags = (uint)modifiers | 0x4000u;
                    bool up = RegisterHotKey(source.Handle, IncreaseId, flags, (uint)KeyInterop.VirtualKeyFromKey(increase));
                    bool down = up && RegisterHotKey(source.Handle, DecreaseId, flags, (uint)KeyInterop.VirtualKeyFromKey(decrease));
                    HotkeysAvailable = up && down;
                    if (!HotkeysAvailable)
                    { UnregisterHotKey(source.Handle, IncreaseId); UnregisterHotKey(source.Handle, DecreaseId); Report("밝기 단축키가 다른 앱에서 사용 중입니다."); }
                }
            }
            if (followWindows)
            {
                var device = new RawDevice { UsagePage = 0x0C, Usage = 0x01, Flags = 0x0100 | 0x2000, Target = source.Handle };
                RawInputAvailable = RegisterRawInputDevices(new[] { device }, 1, (uint)Marshal.SizeOf(typeof(RawDevice)));
                if (!RawInputAvailable) Report("밝기 키 입력을 등록하지 못했습니다. 단축키를 사용할 수 있습니다.");
            }
        }

        private void RemoveRegistrations()
        {
            if (source != null && !demo)
            {
                UnregisterHotKey(source.Handle, IncreaseId); UnregisterHotKey(source.Handle, DecreaseId);
                if (RawInputAvailable)
                {
                    var device = new RawDevice { UsagePage = 0x0C, Usage = 0x01, Flags = 1, Target = IntPtr.Zero };
                    RegisterRawInputDevices(new[] { device }, 1, (uint)Marshal.SizeOf(typeof(RawDevice)));
                }
            }
            HotkeysAvailable = RawInputAvailable = false;
            foreach (var descriptor in descriptors.Values) descriptor.Dispose(); descriptors.Clear();
        }

        private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (disposed || !enabled) return IntPtr.Zero;
            if (message == 0x0312 && HotkeysAvailable)
            {
                int id = wParam.ToInt32();
                if (id == IncreaseId || id == DecreaseId) { handled = true; Emit(id == IncreaseId ? step : -step, "hotkey", null, null); }
            }
            else if (message == 0x00FF && RawInputAvailable)
            {
                try { ReadRawInput(lParam); }
                catch (ArgumentException) { }
                catch (OverflowException) { }
                // Keep WM_INPUT unhandled so the default window procedure performs
                // the required foreground Raw Input cleanup.
            }
            else if (message == 0x00FE && wParam.ToInt64() == 2)
            {
                HidDescriptor descriptor;
                if (descriptors.TryGetValue(lParam, out descriptor)) { descriptors.Remove(lParam); descriptor.Dispose(); }
                usages.RemoveDevice(lParam.ToInt64().ToString() + "/");
            }
            return IntPtr.Zero;
        }

        private void ReadRawInput(IntPtr input)
        {
            uint headerSize = (uint)Marshal.SizeOf(typeof(RawHeader)), size = 0;
            if (GetRawInputData(input, 0x10000003, IntPtr.Zero, ref size, headerSize) == UInt32.MaxValue || size < headerSize + 8 || size > 1024 * 1024) return;
            IntPtr buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                uint copied = GetRawInputData(input, 0x10000003, buffer, ref size, headerSize);
                if (copied == UInt32.MaxValue || copied < headerSize + 8 || size < headerSize + 8) return;
                var header = (RawHeader)Marshal.PtrToStructure(buffer, typeof(RawHeader));
                if (header.Type != 2 || header.Device == IntPtr.Zero) return;
                uint reportSize = (uint)Marshal.ReadInt32(buffer, (int)headerSize), count = (uint)Marshal.ReadInt32(buffer, (int)headerSize + 4);
                if (reportSize == 0 || reportSize > 4096 || count == 0 || count > 256 || (ulong)reportSize * count > size - headerSize - 8) return;
                var descriptor = Descriptor(header.Device);
                if (descriptor == null || descriptor.ReportLength != reportSize) return;
                byte[] report = new byte[reportSize];
                for (uint index = 0; index < count; index++)
                {
                    Marshal.Copy(IntPtr.Add(buffer, checked((int)(headerSize + 8 + index * reportSize))), report, 0, report.Length);
                    ushort[] active = new ushort[128]; uint length = (uint)active.Length;
                    if (HidP_GetUsages(0, 0x0C, 0, active, ref length, descriptor.Data, report, reportSize) != 0x00110000 || length > active.Length) continue;
                    int direction = usages.Observe(header.Device.ToInt64() + "/" + report[0], active.Take((int)length));
                    if (direction == 0) continue;
                    lock (sync) direction = fusion.FromRaw(direction, DateTime.UtcNow);
                    if (direction != 0) Emit(direction * step, "raw", null, null);
                    if (disposed || !enabled || descriptor.Data == IntPtr.Zero) break;
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        private HidDescriptor Descriptor(IntPtr handle)
        {
            HidDescriptor descriptor;
            if (descriptors.TryGetValue(handle, out descriptor)) return descriptor;
            uint size = 0;
            if (GetRawInputDeviceInfo(handle, 0x20000005, IntPtr.Zero, ref size) == UInt32.MaxValue || size == 0 || size > 65536) return null;
            IntPtr data = Marshal.AllocHGlobal((int)size);
            if (GetRawInputDeviceInfo(handle, 0x20000005, data, ref size) == UInt32.MaxValue) { Marshal.FreeHGlobal(data); return null; }
            HidCaps caps;
            if (HidP_GetCaps(data, out caps) != 0x00110000 || caps.UsagePage != 0x0C || caps.Usage != 1 || caps.InputReportByteLength == 0)
            { Marshal.FreeHGlobal(data); return null; }
            descriptor = new HidDescriptor { Data = data, ReportLength = caps.InputReportByteLength };
            descriptors.Add(handle, descriptor); return descriptor;
        }

        private void StartWindowsWatcher(int currentGeneration)
        {
            Task.Run(delegate {
                ManagementEventWatcher next = null;
                try
                {
                    var baseline = new BrightnessDeltaTracker(); int count = 0;
                    using (var search = new ManagementObjectSearcher(@"root\wmi", "SELECT InstanceName, CurrentBrightness FROM WmiMonitorBrightness WHERE Active = TRUE"))
                    using (var results = search.Get())
                    foreach (ManagementObject row in results)
                    using (row) { baseline.Seed(Convert.ToString(row["InstanceName"]), Convert.ToInt32(row["CurrentBrightness"])); count++; }
                    if (count == 0) return;
                    next = new ManagementEventWatcher(new ManagementScope(@"root\wmi"), new WqlEventQuery("SELECT * FROM WmiMonitorBrightnessEvent"));
                    next.EventArrived += delegate(object sender, EventArrivedEventArgs e) {
                        try
                        {
                            if (e.NewEvent["Active"] != null && !Convert.ToBoolean(e.NewEvent["Active"])) return;
                            string instance = Convert.ToString(e.NewEvent["InstanceName"]);
                            int value = Convert.ToInt32(e.NewEvent["Brightness"]), delta;
                            lock (sync)
                            {
                                if (disposed || !enabled || !followWindows || generation != currentGeneration) return;
                                delta = tracker.Observe(instance, value, DateTime.UtcNow);
                                delta = fusion.FromWindows(delta, DateTime.UtcNow);
                            }
                            if (delta != 0) Dispatch(delegate { if (generation == currentGeneration && enabled && followWindows) Emit(delta, "windows", instance, value); });
                        }
                        catch (ManagementException) { }
                        catch (InvalidCastException) { }
                    };
                    lock (sync)
                    {
                        if (disposed || generation != currentGeneration) { next.Dispose(); return; }
                        tracker = baseline;
                    }
                    next.Start();
                    lock (sync)
                    {
                        if (disposed || generation != currentGeneration) { RetireWatcher(next); return; }
                        watcher = next; WindowsEventsAvailable = true;
                    }
                }
                catch (ManagementException) { if (next != null) RetireWatcher(next); }
                catch (UnauthorizedAccessException) { if (next != null) RetireWatcher(next); Report("Windows 밝기 이벤트에 접근할 수 없습니다."); }
                catch (COMException) { if (next != null) RetireWatcher(next); }
                catch (InvalidOperationException) { if (next != null) RetireWatcher(next); }
            });
        }

        private void Emit(int delta, string kind, string instance, int? brightness)
        {
            if (disposed || !enabled || delta == 0) return;
            SourceKind = kind; WindowsSourceInstance = instance; WindowsSourceBrightness = brightness;
            try { onDelta(delta); }
            catch (Exception ex) { Report("밝기 조절 실패 · " + ex.Message); }
            finally { SourceKind = null; WindowsSourceInstance = null; WindowsSourceBrightness = null; }
        }
        private void Report(string message) { Dispatch(delegate { if (!disposed) status(message); }); }
        private void Dispatch(Action action)
        {
            if (host.Dispatcher.HasShutdownStarted || host.Dispatcher.HasShutdownFinished) return;
            if (host.Dispatcher.CheckAccess()) action(); else host.Dispatcher.BeginInvoke(action);
        }
        private static void RetireWatcher(ManagementEventWatcher retired)
        {
            if (retired == null) return;
            Task.Run(delegate { try { retired.Stop(); } catch (ManagementException) { } catch (COMException) { } catch (InvalidOperationException) { } finally { retired.Dispose(); } });
        }
        private void OnClosed(object sender, EventArgs e) { Dispose(); }
        public void Dispose()
        {
            if (disposed) return;
            host.Dispatcher.VerifyAccess();
            ManagementEventWatcher old;
            lock (sync) { disposed = true; generation++; old = watcher; watcher = null; WindowsEventsAvailable = false; }
            host.SourceInitialized -= OnSourceInitialized; host.Closed -= OnClosed;
            RemoveRegistrations();
            if (source != null) { source.RemoveHook(WindowMessage); source = null; }
            RetireWatcher(old);
        }

        private sealed class HidDescriptor : IDisposable
        {
            public IntPtr Data; public uint ReportLength;
            public void Dispose() { if (Data != IntPtr.Zero) { Marshal.FreeHGlobal(Data); Data = IntPtr.Zero; } }
        }
        [StructLayout(LayoutKind.Sequential)] private struct RawDevice { public ushort UsagePage, Usage; public uint Flags; public IntPtr Target; }
        [StructLayout(LayoutKind.Sequential)] private struct RawHeader { public uint Type, Size; public IntPtr Device, WParam; }
        [StructLayout(LayoutKind.Sequential)] private struct HidCaps
        {
            public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices, NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices, NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
        }
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RegisterRawInputDevices(RawDevice[] devices, uint count, uint size);
        [DllImport("user32.dll", SetLastError = true)] private static extern uint GetRawInputData(IntPtr raw, uint command, IntPtr data, ref uint size, uint headerSize);
        [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW", SetLastError = true)] private static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, IntPtr data, ref uint size);
        [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr preparsed, out HidCaps caps);
        [DllImport("hid.dll")] private static extern int HidP_GetUsages(int reportType, ushort usagePage, ushort collection, [Out] ushort[] usageList, ref uint usageLength, IntPtr preparsed, byte[] report, uint reportLength);
    }
}
