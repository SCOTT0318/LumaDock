using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MonitorControl
{
    public sealed class MonitorDevice : IDisposable
    {
        public string Id;
        public string Name;
        public string HardwareName;
        public int Left, Top, Width, Height;
        public string PositionLabel;
        public string Display;
        public string Resolution;
        public bool Primary;
        public bool Demo;
        public bool IsHdmi;
        public bool SafeDdc;
        public string Connection = "";
        public int ControlRequests { get; internal set; }
        public string Capabilities = "";
        public string Notice = "";
        public string WmiInstance;
        public List<MonitorFeature> Features = new List<MonitorFeature>();
        public Dictionary<byte, List<uint>> Reported = new Dictionary<byte, List<uint>>();
        internal IntPtr Handle;
        internal bool Disposed;
        internal bool ControlFaulted;
        internal int LastDdcError;
        internal long LastDdcCompleted;
        internal readonly object DdcSync = new object();
        public void Dispose()
        {
            if (Disposed) return;
            Disposed = true;
            if (Handle != IntPtr.Zero) { NativeMethods.DestroyPhysicalMonitor(Handle); Handle = IntPtr.Zero; }
        }
    }

    public sealed class BrightnessChangeResult
    {
        public MonitorDevice Device { get; internal set; }
        public Exception Error { get; internal set; }
    }

    internal interface IMonitorTransport
    {
        void Write(MonitorDevice device, MonitorFeature feature, uint value);
        bool TryRead(MonitorDevice device, MonitorFeature feature, out uint value);
    }

    public sealed class MonitorService : IDisposable
    {
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        private readonly bool demo;
        private readonly IMonitorTransport transport;
        private readonly IDisplayRecovery recovery;
        private List<MonitorDevice> devices = new List<MonitorDevice>();
        private bool disposed;
        public event Action<MonitorDevice, MonitorFeature> FeatureChanged;
        public event Action<MonitorDevice, MonitorFeature, uint> FeatureChanging;
        public bool HdmiSafeMode { get; set; }
        public MonitorService(bool demoMode) : this(demoMode, null) { }
        internal MonitorService(bool demoMode, IMonitorTransport monitorTransport) : this(demoMode, monitorTransport, null) { }
        internal MonitorService(bool demoMode, IMonitorTransport monitorTransport, IDisplayRecovery displayRecovery)
        { demo = demoMode; transport = monitorTransport ?? new MonitorTransport(); recovery = displayRecovery ?? new WindowsDisplayRecovery(); HdmiSafeMode = true; }

        public Task<List<MonitorDevice>> ScanAsync(bool refreshValues = true)
        {
            return Task.Run(async delegate {
                await gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (disposed) throw new ObjectDisposedException("MonitorService");
                    // Invalidate old models and release handles while no writes can run.
                    var previous = devices.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
                    foreach (var device in devices) device.Dispose();
                    devices.Clear();
                    if (demo) devices = DemoDevices();
                    else ScanNative(devices, previous, HdmiSafeMode, refreshValues);
                    if (demo) foreach (var device in devices)
                    {
                        MonitorDevice old;
                        if (previous.TryGetValue(device.Id, out old))
                        { device.IsHdmi = old.IsHdmi; device.SafeDdc = old.IsHdmi && HdmiSafeMode; device.Connection = old.Connection; }
                        RestoreConnectionState(device, previous, refreshValues);
                    }
                    InitializeMetadata(devices);
                    if (!demo) foreach (var device in devices) DisplayDiagnostics.Write("scan refresh=" + refreshValues + " " + device.Id + " " + device.Connection + " safe=" + device.SafeDdc + " quarantined=" + device.ControlFaulted + " requests=" + device.ControlRequests);
                    return new List<MonitorDevice>(devices);
                }
                finally { gate.Release(); }
            });
        }

        private static void ScanNative(List<MonitorDevice> found, IDictionary<string, MonitorDevice> previous, bool safeHdmi, bool refreshValues)
        {
            var connections = ReadConnections();
            NativeMethods.MonitorCallback callback = delegate(IntPtr logical, IntPtr hdc, ref NativeMethods.Rect rect, IntPtr data) {
                var info = new NativeMethods.MonitorInfo();
                info.Size = Marshal.SizeOf(info);
                if (!NativeMethods.GetMonitorInfo(logical, ref info)) return true;
                var display = new NativeMethods.DisplayDevice();
                display.Size = Marshal.SizeOf(display);
                // Interface IDs contain the full physical instance, allowing an exact WMI match.
                NativeMethods.EnumDisplayDevices(info.Device, 0, ref display, 1);
                string deviceId = NormalizeId(display.Id);
                string displayName = info.Device.Replace(@"\\.\", "");
                string resolution = (info.Monitor.Right - info.Monitor.Left) + " × " + (info.Monitor.Bottom - info.Monitor.Top);
                string fallbackId = previous.Keys.FirstOrDefault(x => x == deviceId || x.StartsWith(deviceId + "|", StringComparison.OrdinalIgnoreCase)) ?? (deviceId.Length == 0 ? info.Device : deviceId);
                uint technology;
                if (!connections.TryGetValue(deviceId, out technology))
                    technology = previous.Values.Any(x => x.IsHdmi && (x.Id == deviceId || x.Id.StartsWith(deviceId + "|", StringComparison.OrdinalIgnoreCase))) ? 5u : UInt32.MaxValue;
                var quarantined = previous.Values.Where(x => x.IsHdmi && x.ControlFaulted && (x.Id == deviceId || x.Id.StartsWith(deviceId + "|", StringComparison.OrdinalIgnoreCase))).ToList();
                if (technology == 5 && quarantined.Count > 0)
                {
                    foreach (var old in quarantined)
                    {
                        var paused = new MonitorDevice { Id = old.Id, Name = old.HardwareName, Display = displayName, Resolution = resolution, Primary = (info.Flags & 1) != 0,
                            Left = info.Monitor.Left, Top = info.Monitor.Top, Width = info.Monitor.Right - info.Monitor.Left, Height = info.Monitor.Bottom - info.Monitor.Top, IsHdmi = true, Connection = "HDMI", SafeDdc = safeHdmi };
                        RestoreConnectionState(paused, previous, refreshValues); found.Add(paused);
                    }
                    return true;
                }
                uint count;
                if (!NativeMethods.GetNumberOfPhysicalMonitorsFromHMONITOR(logical, out count) || count == 0 || count > 64)
                {
                    found.Add(new MonitorDevice { Id = fallbackId, Name = String.IsNullOrEmpty(display.Description) ? displayName : display.Description,
                        Display = displayName, Resolution = resolution, Primary = (info.Flags & 1) != 0,
                        Left = info.Monitor.Left, Top = info.Monitor.Top, Width = info.Monitor.Right - info.Monitor.Left, Height = info.Monitor.Bottom - info.Monitor.Top,
                        IsHdmi = technology == 5, Connection = technology == 5 ? "HDMI" : "", SafeDdc = technology == 5 && safeHdmi,
                        ControlFaulted = technology == 5,
                        Notice = "이 연결에서는 모니터 제어 채널을 찾지 못했습니다." });
                    return true;
                }
                var physical = new NativeMethods.PhysicalMonitor[count];
                if (!NativeMethods.GetPhysicalMonitorsFromHMONITOR(logical, count, physical))
                {
                    foreach (var handle in physical) if (handle.Handle != IntPtr.Zero) NativeMethods.DestroyPhysicalMonitor(handle.Handle);
                    found.Add(new MonitorDevice { Id = fallbackId, Name = displayName, Display = displayName, Resolution = resolution,
                        Primary = (info.Flags & 1) != 0,
                        Left = info.Monitor.Left, Top = info.Monitor.Top, Width = info.Monitor.Right - info.Monitor.Left, Height = info.Monitor.Bottom - info.Monitor.Top,
                        IsHdmi = technology == 5, Connection = technology == 5 ? "HDMI" : "", SafeDdc = technology == 5 && safeHdmi,
                        ControlFaulted = technology == 5,
                        Notice = "모니터 연결을 열지 못했습니다. 다시 검색해 주세요." });
                    return true;
                }
                for (int i = 0; i < physical.Length; i++)
                {
                    var device = new MonitorDevice { Handle = physical[i].Handle, Id = (deviceId.Length == 0 ? info.Device : deviceId) + "|" + i,
                        Name = physical[i].Description, Display = displayName, Resolution = resolution, Primary = (info.Flags & 1) != 0,
                        Left = info.Monitor.Left, Top = info.Monitor.Top, Width = info.Monitor.Right - info.Monitor.Left, Height = info.Monitor.Bottom - info.Monitor.Top };
                    // Ownership transfers immediately, including failures in capability discovery.
                    found.Add(device);
                    device.IsHdmi = technology == 5;
                    device.Connection = device.IsHdmi ? "HDMI" : technology == 10 || technology == 11 ? "DisplayPort" : "디스플레이";
                    device.SafeDdc = device.IsHdmi && safeHdmi;
                    bool reuse = RestoreConnectionState(device, previous, refreshValues);
                    if (reuse) continue;
                    try { Discover(device); }
                    catch (Exception ex) { device.Notice = "지원 기능 조회 실패: " + ex.Message; }
                }
                return true;
            };
            if (!NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "모니터 목록을 읽지 못했습니다.");
            AddWmiBrightness(found);
            AddWmiNames(found);
        }

        private static Dictionary<string, uint> ReadConnections()
        {
            var result = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var connection in WindowsDisplayRecovery.Inspect()) result[connection.Id] = connection.Technology;
                if (result.Count > 0) return result;
            }
            catch (Win32Exception) { }
            catch (InvalidOperationException) { }
            try
            {
                using (var search = new ManagementObjectSearcher(@"root\wmi", "SELECT InstanceName, VideoOutputTechnology FROM WmiMonitorConnectionParams WHERE Active = TRUE"))
                using (var rows = search.Get())
                foreach (ManagementObject row in rows)
                using (row) result[NormalizeId(Convert.ToString(row["InstanceName"]))] = Convert.ToUInt32(row["VideoOutputTechnology"]);
            }
            catch (ManagementException) { }
            catch (UnauthorizedAccessException) { }
            catch (COMException) { }
            return result;
        }

        internal static bool RestoreConnectionState(MonitorDevice device, IDictionary<string, MonitorDevice> previous, bool refreshValues)
        {
            MonitorDevice old;
            if (!previous.TryGetValue(device.Id, out old) || !old.IsHdmi || !device.IsHdmi) return false;
            if (!old.ControlFaulted && old.Features.Count == 0) return false;
            // Neither automatic nor manual discovery should re-open a quarantined
            // HDMI control channel. Only an actual reconnect or explicit recovery can.
            if (!old.ControlFaulted && (refreshValues || old.SafeDdc != device.SafeDdc)) return false;
            device.ControlFaulted = old.ControlFaulted;
            device.LastDdcCompleted = old.LastDdcCompleted;
            device.LastDdcError = old.LastDdcError;
            device.Name = String.IsNullOrWhiteSpace(old.HardwareName) ? old.Name : old.HardwareName;
            device.WmiInstance = old.WmiInstance;
            device.Capabilities = device.SafeDdc ? "" : old.Capabilities;
            device.Reported = device.SafeDdc ? new Dictionary<byte, List<uint>>() : old.Reported.ToDictionary(x => x.Key, x => new List<uint>(x.Value));
            device.Features = old.Features.Where(x => !device.SafeDdc || x.Definition.Basic).Select(x => new MonitorFeature { Definition = x.Definition, Minimum = x.Minimum, Maximum = x.Maximum, Current = x.Current, Backend = x.Backend, Choices = new List<uint>(x.Choices) }).ToList();
            device.Notice = old.ControlFaulted ? "HDMI 통신 중지 · 트레이 메뉴의 HDMI 신호 복구를 실행하세요." : old.Notice;
            return true;
        }

        internal static bool DdcCall(MonitorDevice device, string operation, Func<bool> call)
        {
            lock (device.DdcSync)
            {
                if (device.ControlFaulted) return false;
                if (device.SafeDdc && device.LastDdcCompleted != 0)
                {
                    double elapsed = (Stopwatch.GetTimestamp() - device.LastDdcCompleted) * 1000.0 / Stopwatch.Frequency;
                    if (elapsed < 150) Thread.Sleep((int)Math.Ceiling(150 - elapsed));
                }
                long started = Stopwatch.GetTimestamp();
                device.ControlRequests++;
                bool ok = call();
                int error = ok ? 0 : Marshal.GetLastWin32Error();
                device.LastDdcCompleted = Stopwatch.GetTimestamp(); device.LastDdcError = error;
                if (!device.Demo && (device.IsHdmi || !ok || operation.StartsWith("set", StringComparison.Ordinal)))
                    DisplayDiagnostics.Write(device.Id + " " + device.Connection + " " + operation + " success=" + ok + " error=" + error + " ms=" + ((device.LastDdcCompleted - started) * 1000 / Stopwatch.Frequency));
                return ok;
            }
        }

        private static void AddWmiNames(List<MonitorDevice> found)
        {
            try
            {
                using (var search = new ManagementObjectSearcher(@"root\wmi", "SELECT InstanceName, UserFriendlyName FROM WmiMonitorID WHERE Active = TRUE"))
                using (var results = search.Get())
                foreach (ManagementObject row in results)
                using (row)
                {
                    string id = NormalizeId(Convert.ToString(row["InstanceName"]));
                    var name = row["UserFriendlyName"] as ushort[];
                    if (name == null || id.Length == 0) continue;
                    string friendly = new string(name.Where(x => x != 0).Select(x => (char)x).ToArray()).Trim();
                    if (friendly.Length == 0) continue;
                    foreach (var device in found.Where(x => x.Id == id || x.Id.StartsWith(id + "|", StringComparison.OrdinalIgnoreCase)))
                        if (String.IsNullOrWhiteSpace(device.Name) || device.Name == device.Display || device.Name == "내장 디스플레이" || device.Name.IndexOf("Generic", StringComparison.OrdinalIgnoreCase) >= 0)
                            device.Name = friendly;
                }
            }
            catch (ManagementException) { }
            catch (UnauthorizedAccessException) { }
            catch (COMException) { }
        }

        private static void InitializeMetadata(List<MonitorDevice> found)
        {
            var primary = found.FirstOrDefault(x => x.Primary && x.Width > 0) ?? found.FirstOrDefault(x => x.Width > 0);
            foreach (var monitor in found)
            {
                monitor.HardwareName = monitor.Name;
                if (monitor.Primary) { monitor.PositionLabel = "주 모니터"; continue; }
                if (primary == null || monitor.Width <= 0 || monitor.Height <= 0) { monitor.PositionLabel = "위치 정보 없음"; continue; }
                double dx = monitor.Left + monitor.Width / 2.0 - primary.Left - primary.Width / 2.0;
                double dy = monitor.Top + monitor.Height / 2.0 - primary.Top - primary.Height / 2.0;
                var parts = new List<string>();
                if (Math.Abs(dx) > primary.Width * .25) parts.Add(dx < 0 ? "왼쪽" : "오른쪽");
                if (Math.Abs(dy) > primary.Height * .25) parts.Add(dy < 0 ? "위쪽" : "아래쪽");
                monitor.PositionLabel = parts.Count == 0 ? "화면 겹침" : String.Join(" ", parts);
            }
        }

        public Task RefreshBrightnessAsync(bool nativeOnly = false)
        {
            return Task.Run(async delegate {
                var changed = new List<KeyValuePair<MonitorDevice, MonitorFeature>>();
                await gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (disposed) return;
                    foreach (var monitor in devices)
                    {
                        var feature = monitor.Features.FirstOrDefault(x => x.Definition.Code == 0x10);
                        if (feature == null || monitor.Demo) continue;
                        if (nativeOnly && feature.Backend != "WMI") continue;
                        try
                        {
                            if (monitor.ControlFaulted) continue;
                            uint actual;
                            bool read = transport.TryRead(monitor, feature, out actual);
                            if (!read && monitor.IsHdmi) MarkControlFault(monitor, "brightness-refresh");
                            if (read && actual != feature.Current)
                            { feature.Current = actual; changed.Add(new KeyValuePair<MonitorDevice, MonitorFeature>(monitor, feature)); }
                        }
                        catch (ManagementException) { }
                        catch (COMException) { }
                    }
                }
                finally { gate.Release(); }
                var notify = FeatureChanged;
                if (notify != null) foreach (var pair in changed) notify(pair.Key, pair.Value);
            });
        }

        internal static string NormalizeId(string value)
        {
            if (String.IsNullOrEmpty(value)) return "";
            value = value.ToUpperInvariant();
            if (value.StartsWith(@"\\?\", StringComparison.Ordinal))
            {
                string[] parts = value.Substring(4).Split('#');
                if (parts.Length >= 3 && (parts[0] == "DISPLAY" || parts[0] == "MONITOR")) value = parts[1] + "\\" + parts[2];
            }
            value = value.Replace("MONITOR\\", "").Replace("DISPLAY\\", "");
            return Regex.Replace(value, @"_\d+$", "");
        }

        private static void Discover(MonitorDevice device)
        {
            // Basic controls are probed independently: capability reports are often incomplete.
            foreach (var definition in FeatureCatalog.All.Where(x => x.Basic))
            {
                uint minimum = 0, current = 0, maximum = 0;
                Func<bool> probe = delegate {
                    return DdcCall(device, "probe-" + definition.Code.ToString("X2"), delegate { return definition.Code == 0x10
                        ? NativeMethods.GetMonitorBrightness(device.Handle, out minimum, out current, out maximum)
                        : NativeMethods.GetMonitorContrast(device.Handle, out minimum, out current, out maximum); });
                };
                bool success = device.SafeDdc ? probe() : Retry(probe);
                if (success && maximum > minimum && current >= minimum && current <= maximum)
                    device.Features.Add(new MonitorFeature { Definition = definition, Minimum = minimum, Current = current, Maximum = maximum, Backend = "HighLevel" });
                else
                {
                    uint type;
                    if (DdcCall(device, "probe-vcp-" + definition.Code.ToString("X2"), delegate { return NativeMethods.GetVCPFeatureAndVCPFeatureReply(device.Handle, definition.Code, out type, out current, out maximum); }) && maximum > 0 && current <= maximum)
                        device.Features.Add(new MonitorFeature { Definition = definition, Current = current, Maximum = maximum });
                }
            }
            if (device.SafeDdc)
            {
                device.Notice = "HDMI 안정 모드 · 밝기·명암만 제어하고 추가 기능 조회를 생략합니다.";
                if (device.Features.Count == 0) { device.ControlFaulted = true; device.Notice = "HDMI 통신 중지 · 트레이 메뉴의 HDMI 신호 복구를 실행하세요."; }
                return;
            }
            uint length = 0;
            if (DdcCall(device, "capabilities-length", delegate { return NativeMethods.GetCapabilitiesStringLength(device.Handle, out length); }) && length > 0 && length <= 65536)
            {
                var buffer = new StringBuilder((int)length);
                if (DdcCall(device, "capabilities", delegate { return NativeMethods.CapabilitiesRequestAndCapabilitiesReply(device.Handle, buffer, length); })) device.Capabilities = buffer.ToString();
            }
            device.Reported = CapabilityParser.Parse(device.Capabilities);
            var model = Regex.Match(device.Capabilities, @"\bmodel\(([^)]+)\)", RegexOptions.IgnoreCase);
            if (model.Success) device.Name = model.Groups[1].Value.Trim();
            if (String.IsNullOrWhiteSpace(device.Name)) device.Name = device.Display;
            foreach (var definition in FeatureCatalog.All.Where(x => !x.Basic))
            {
                if (!device.Reported.ContainsKey(definition.Code)) continue;
                uint type = 0, current = 0, maximum = 0;
                if (!DdcCall(device, "probe-vcp-" + definition.Code.ToString("X2"), delegate { return NativeMethods.GetVCPFeatureAndVCPFeatureReply(device.Handle, definition.Code, out type, out current, out maximum); })) continue;
                if (definition.Discrete || (maximum > 0 && current <= maximum))
                    device.Features.Add(new MonitorFeature { Definition = definition, Current = current, Maximum = maximum,
                        Choices = new List<uint>(device.Reported[definition.Code]) });
            }
            if (device.Features.Count == 0) device.Notice = "제어 가능한 기능을 읽지 못했습니다. 모니터의 OSD에서 DDC/CI가 켜져 있는지 확인해 주세요.";
            else if (device.Capabilities.Length == 0) device.Notice = "지원 기능 목록을 받지 못해, 직접 조회에 성공한 기본 항목만 표시합니다.";
        }

        private static void AddWmiBrightness(List<MonitorDevice> found)
        {
            try
            {
                using (var search = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM WmiMonitorBrightness WHERE Active = TRUE"))
                using (var results = search.Get())
                foreach (ManagementObject row in results)
                using (row)
                {
                    ApplyWmiBrightness(found, Convert.ToString(row["InstanceName"]), Convert.ToUInt32(row["CurrentBrightness"]), row["Level"] as byte[]);
                }
            }
            catch (ManagementException) { /* External-only systems commonly have no WMI class. */ }
            catch (UnauthorizedAccessException) { }
            catch (COMException) { }
        }

        internal static void ApplyWmiBrightness(List<MonitorDevice> found, string instance, uint brightness, byte[] levels)
        {
            string id = NormalizeId(instance);
            if (id.Length == 0 || brightness > 100) return;
            // Prefer Windows for a panel it controls, even when DDC discovery also
            // succeeded. Otherwise firmware and this app can adjust it twice.
            var candidates = found.Where(x => x.Id == id || x.Id.StartsWith(id + "|", StringComparison.OrdinalIgnoreCase)).ToList();
            MonitorDevice device;
            if (candidates.Count == 1) device = candidates[0];
            else if (candidates.Count == 0)
            {
                device = new MonitorDevice { Id = id, Name = "내장 디스플레이", Display = "WMI", Resolution = "Windows 밝기 제어" };
                found.Add(device);
            }
            else return;
            device.WmiInstance = instance;
            device.ControlFaulted = false;
            device.Features.RemoveAll(x => x.Definition.Code == 0x10);
            device.Features.Insert(0, new MonitorFeature { Definition = FeatureCatalog.All[0], Current = brightness, Maximum = 100,
                Backend = "WMI", Choices = levels == null ? new List<uint>() : levels.Where(x => x <= 100).Select(x => (uint)x).Distinct().OrderBy(x => x).ToList() });
            device.Notice = "이 화면의 밝기는 Windows 밝기 제어를 사용합니다.";
        }

        public Task<uint> SetAsync(MonitorDevice device, MonitorFeature feature, uint value)
        {
            var operation = Task.Run(async delegate {
                await gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    return await SetCoreAsync(device, feature, value).ConfigureAwait(false);
                }
                finally { gate.Release(); }
            });
            return PublishChangeAsync(operation, device, feature);
        }

        public Task RecoverHdmiAsync(MonitorDevice monitor, bool validateOnly = false)
        {
            return Task.Run(async delegate {
                await gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (disposed || monitor == null || monitor.Disposed || !devices.Contains(monitor) || !monitor.IsHdmi)
                        throw new InvalidOperationException("연결된 HDMI 모니터를 선택하세요.");
                    if (validateOnly) { if (!demo || !(recovery is WindowsDisplayRecovery)) recovery.Reinitialize(monitor, true); return; }
                    // Invalidate every queued write before reinitializing the driver.
                    // The next scan replaces these models and opens fresh handles.
                    foreach (var device in devices) device.Dispose();
                    monitor.ControlFaulted = true;
                    if (!demo) DisplayDiagnostics.Write(monitor.Id + " recovery-start");
                    if (!demo || !(recovery is WindowsDisplayRecovery)) recovery.Reinitialize(monitor, false);
                    if (!demo) await Task.Delay(1500).ConfigureAwait(false);
                    monitor.ControlFaulted = false; monitor.LastDdcCompleted = Stopwatch.GetTimestamp();
                    if (!demo) DisplayDiagnostics.Write(monitor.Id + " recovery-reinitialization-requested");
                }
                catch (Exception ex)
                {
                    if (!demo) DisplayDiagnostics.Write("recovery-failed " + ex.Message);
                    throw;
                }
                finally { gate.Release(); }
            });
        }

        private static void MarkControlFault(MonitorDevice monitor, string operation)
        {
            monitor.ControlFaulted = true;
            if (monitor.IsHdmi) monitor.Notice = "HDMI 통신 중지 · 트레이 메뉴의 HDMI 신호 복구를 실행하세요.";
            if (!monitor.Demo) DisplayDiagnostics.Write(monitor.Id + " quarantined " + operation + " error=" + monitor.LastDdcError);
        }

        // Hold the connection lifetime gate for the whole batch, but allow separate
        // physical monitors to receive their writes without waiting for each other's
        // readback. Scans, single writes and disposal cannot invalidate these handles.
        public Task<List<BrightnessChangeResult>> SetBrightnessAsync(IDictionary<MonitorDevice, int> percentages)
        {
            if (percentages == null) throw new ArgumentNullException("percentages");
            var requests = percentages.ToArray();
            return Task.Run(async delegate {
                await gate.WaitAsync().ConfigureAwait(false);
                List<BrightnessChangeResult> results = null;
                try
                {
                    var tasks = requests.Select(request => Task.Run(async delegate {
                        var result = new BrightnessChangeResult { Device = request.Key };
                        MonitorFeature feature = null;
                        try
                        {
                            feature = request.Key.Features.FirstOrDefault(x => x.Definition.Code == 0x10);
                            if (feature == null) throw new InvalidOperationException("밝기를 변경할 수 없는 모니터입니다.");
                            await SetCoreAsync(request.Key, feature, feature.FromPercent(request.Value)).ConfigureAwait(false);
                        }
                        catch (Exception ex) { result.Error = ex; }
                        return result;
                    })).ToArray();
                    results = (await Task.WhenAll(tasks).ConfigureAwait(false)).ToList();
                    return results;
                }
                finally
                {
                    gate.Release();
                    if (results != null) foreach (var result in results)
                    {
                        var feature = result.Device.Features.FirstOrDefault(x => x.Definition.Code == 0x10);
                        if (feature != null) PublishChange(result.Device, feature);
                    }
                }
            });
        }

        private async Task<uint> SetCoreAsync(MonitorDevice device, MonitorFeature feature, uint value)
        {
            if (disposed || device == null || device.Disposed || !devices.Contains(device)) throw new InvalidOperationException("모니터 연결이 변경되었습니다. 다시 검색해 주세요.");
            if (!device.Features.Contains(feature) || !feature.Writable) throw new InvalidOperationException("변경할 수 없는 항목입니다.");
            if (feature.Definition.Discrete)
            { if (!feature.Choices.Contains(value)) throw new ArgumentOutOfRangeException("value"); }
            else if (value < feature.Minimum || value > feature.Maximum) throw new ArgumentOutOfRangeException("value");
            if (feature.Backend == "WMI" && feature.Choices.Count > 0) value = feature.Choices.OrderBy(x => Math.Abs((long)x - value)).First();
            if (device.ControlFaulted) throw new InvalidOperationException(device.IsHdmi ? "HDMI 통신을 중지했습니다. 트레이 메뉴의 HDMI 신호 복구를 실행하세요." : "모니터 통신을 중지했습니다. 연결을 확인한 뒤 다시 검색해 주세요.");
            if (feature.Current == value) return value;
            try
            {
                var changing = FeatureChanging;
                if (changing != null) changing(device, feature, value);
                transport.Write(device, feature, value);
                // Switching inputs can sever DDC immediately. Demo transports also
                // need no hardware settling delay.
                if (feature.Definition.Code == 0x60) { feature.Current = value; return value; }
                bool brightness = feature.Definition.Code == 0x10;
                uint actual = feature.Current;
                bool confirmed = false;
                for (int attempt = 0; attempt < (brightness || device.SafeDdc ? 1 : 3); attempt++)
                {
                    if (!device.Demo) await Task.Delay(brightness ? 200 : 100).ConfigureAwait(false);
                    uint read;
                    if (transport.TryRead(device, feature, out read))
                    {
                        confirmed = true; actual = read;
                        if (actual == value) break;
                    }
                }
                if (!confirmed)
                {
                    if (feature.Backend != "WMI") MarkControlFault(device, "readback");
                    throw new InvalidOperationException("요청을 보냈지만 적용 값을 확인하지 못했습니다. 연결을 확인한 뒤 다시 검색해 주세요.");
                }
                feature.Current = actual;
                if (actual != value) throw new InvalidOperationException("모니터가 요청한 값을 적용하지 않았습니다. 화면 모드나 OSD 설정을 확인해 주세요.");
                return actual;
            }
            catch (Win32Exception) { MarkControlFault(device, "write"); throw; }
        }

        private async Task<uint> PublishChangeAsync(Task<uint> operation, MonitorDevice device, MonitorFeature feature)
        {
            try { return await operation.ConfigureAwait(false); }
            finally
            {
                PublishChange(device, feature);
            }
        }

        private void PublishChange(MonitorDevice device, MonitorFeature feature)
        {
            var notify = FeatureChanged;
            if (notify != null) notify(device, feature);
        }

        private sealed class MonitorTransport : IMonitorTransport
        {
            public void Write(MonitorDevice device, MonitorFeature feature, uint value)
            {
                if (device.Demo) { feature.Current = value; return; }
                if (feature.Backend == "WMI") { WriteWmi(device.WmiInstance, value); return; }
                Func<bool> write = delegate { return DdcCall(device, "set-" + feature.Definition.Code.ToString("X2") + " value=" + value, delegate {
                    if (feature.Backend == "HighLevel") return feature.Definition.Code == 0x10
                        ? NativeMethods.SetMonitorBrightness(device.Handle, value) : NativeMethods.SetMonitorContrast(device.Handle, value);
                    return NativeMethods.SetVCPFeature(device.Handle, feature.Definition.Code, value);
                }); };
                // A failed DDC write may already have reached the monitor. Repeating
                // brightness writes immediately can overload an unstable connection.
                bool ok = feature.Definition.Code == 0x10 || device.SafeDdc ? write() : Retry(write);
                if (!ok) throw new Win32Exception(device.LastDdcError, "모니터가 변경 요청에 응답하지 않았습니다.");
            }

            public bool TryRead(MonitorDevice device, MonitorFeature feature, out uint value)
            {
                if (device.Demo) { value = feature.Current; return true; }
                return ReadNative(device, feature, out value);
            }
        }

        private static void WriteWmi(string instance, uint value)
        {
            using (var search = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM WmiMonitorBrightnessMethods WHERE Active = TRUE"))
            using (var results = search.Get())
            foreach (ManagementObject row in results)
            using (row)
            {
                if (!String.Equals(Convert.ToString(row["InstanceName"]), instance, StringComparison.OrdinalIgnoreCase)) continue;
                using (var args = row.GetMethodParameters("WmiSetBrightness"))
                {
                    args["Timeout"] = (uint)0;
                    args["Brightness"] = (byte)value;
                    using (var response = row.InvokeMethod("WmiSetBrightness", args, null))
                        if (response == null || Convert.ToUInt32(response["ReturnValue"]) != 0) throw new InvalidOperationException("Windows 밝기 변경에 실패했습니다.");
                    return;
                }
            }
            throw new InvalidOperationException("내장 디스플레이 연결을 찾지 못했습니다.");
        }

        private static bool ReadNative(MonitorDevice device, MonitorFeature feature, out uint value)
        {
            uint actual = 0, minimum = 0, maximum = 0, type = 0;
            if (feature.Backend == "WMI")
            {
                using (var search = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM WmiMonitorBrightness WHERE Active = TRUE"))
                using (var results = search.Get())
                foreach (ManagementObject row in results)
                using (row)
                    if (String.Equals(Convert.ToString(row["InstanceName"]), device.WmiInstance, StringComparison.OrdinalIgnoreCase))
                    { value = Convert.ToUInt32(row["CurrentBrightness"]); return true; }
                value = 0; return false;
            }
            Func<bool> read = delegate { return DdcCall(device, "get-" + feature.Definition.Code.ToString("X2"), delegate {
                if (feature.Backend == "HighLevel") return feature.Definition.Code == 0x10
                    ? NativeMethods.GetMonitorBrightness(device.Handle, out minimum, out actual, out maximum)
                    : NativeMethods.GetMonitorContrast(device.Handle, out minimum, out actual, out maximum);
                return NativeMethods.GetVCPFeatureAndVCPFeatureReply(device.Handle, feature.Definition.Code, out type, out actual, out maximum);
            }); };
            bool ok = feature.Definition.Code == 0x10 || device.SafeDdc ? read() : Retry(read);
            value = actual;
            return ok && (feature.Definition.Discrete || (actual >= feature.Minimum && actual <= feature.Maximum));
        }

        private static bool Retry(Func<bool> action)
        {
            if (action()) return true;
            Thread.Sleep(90);
            return action();
        }

        private static List<MonitorDevice> DemoDevices()
        {
            var one = DemoDevice("demo-1", "DELL U2723QE", "DISPLAY1", "3840 × 2160", true, "(vcp(10 12 60(0f 11 12) 62 14(05 08 0B) 16 18 1A 87))");
            var two = DemoDevice("demo-2", "LG ULTRAGEAR", "DISPLAY2", "2560 × 1440", false, "(vcp(10 12 60(0f 11) 87))");
            var three = DemoDevice("demo-3", "내장 디스플레이", "DISPLAY3", "1920 × 1200", false, "(vcp(10))");
            one.Width = 3840; one.Height = 2160;
            two.Left = 3840; two.Width = 2560; two.Height = 1440;
            three.Top = 2160; three.Width = 1920; three.Height = 1200;
            return new List<MonitorDevice> { one, two, three };
        }

        private static MonitorDevice DemoDevice(string id, string name, string display, string resolution, bool primary, string caps)
        {
            var device = new MonitorDevice { Id = id, Name = name, Display = display, Resolution = resolution, Primary = primary, Demo = true, Capabilities = caps, Reported = CapabilityParser.Parse(caps) };
            foreach (var definition in FeatureCatalog.All)
                if (device.Reported.ContainsKey(definition.Code)) device.Features.Add(new MonitorFeature { Definition = definition, Maximum = 100,
                    Current = definition.Code == 0x60 ? 0x0Fu : definition.Code == 0x14 ? 5u : definition.Code == 0x10 ? 72u : definition.Code == 0x12 ? 65u : definition.Code == 0x62 ? 30u : 50u,
                    Choices = new List<uint>(device.Reported[definition.Code]) });
            return device;
        }

        public void Dispose()
        {
            gate.Wait();
            try { disposed = true; foreach (var device in devices) device.Dispose(); devices.Clear(); }
            finally { gate.Release(); }
        }
    }
}
