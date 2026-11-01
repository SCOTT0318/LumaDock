using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace MonitorControl
{
    internal static class DisplayDiagnostics
    {
        private static readonly object sync = new object();
        internal static void Write(string message)
        {
            try
            {
                lock (sync)
                {
                    string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LumaDock");
                    Directory.CreateDirectory(folder);
                    string path = Path.Combine(folder, "display.log");
                    if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024)
                    { File.Copy(path, path + ".previous", true); File.WriteAllText(path, ""); }
                    File.AppendAllText(path, DateTimeOffset.Now.ToString("o") + " " + message.Replace('\r', ' ').Replace('\n', ' ') + Environment.NewLine);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal interface IDisplayRecovery
    {
        void Reinitialize(MonitorDevice monitor, bool validateOnly);
    }

    // Query and reapply the exact active configuration. No topology presets,
    // disabling adapters, registry writes, fallback modes or DDC power commands.
    internal sealed class WindowsDisplayRecovery : IDisplayRecovery
    {
        internal const uint QueryActive = 2;
        internal const uint UseSupplied = 0x20, Validate = 0x40, Apply = 0x80, NoOptimization = 0x100;

        [StructLayout(LayoutKind.Sequential)] internal struct Luid { public uint Low; public int High; }
        [StructLayout(LayoutKind.Sequential)] internal struct Rational { public uint Numerator, Denominator; }
        [StructLayout(LayoutKind.Sequential)] internal struct PathSource { public Luid Adapter; public uint Id, ModeIndex, Status; }
        [StructLayout(LayoutKind.Sequential)] internal struct PathTarget
        {
            public Luid Adapter;
            public uint Id, ModeIndex, Technology, Rotation, Scaling;
            public Rational Refresh;
            public uint ScanLine, Available, Status;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct DisplayPath { public PathSource Source; public PathTarget Target; public uint Flags; }
        [StructLayout(LayoutKind.Explicit, Size = 64)] internal struct DisplayMode
        {
            [FieldOffset(0)] public uint Type;
            [FieldOffset(4)] public uint Id;
            [FieldOffset(8)] public Luid Adapter;
            [FieldOffset(16)] private ulong Data0;
            [FieldOffset(24)] private ulong Data1;
            [FieldOffset(32)] private ulong Data2;
            [FieldOffset(40)] private ulong Data3;
            [FieldOffset(48)] private ulong Data4;
            [FieldOffset(56)] private ulong Data5;
            [FieldOffset(16)] public uint SourceWidth;
            [FieldOffset(20)] public uint SourceHeight;
        }
        [StructLayout(LayoutKind.Sequential)] private struct InfoHeader { public uint Type, Size; public Luid Adapter; public uint Id; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct TargetName
        {
            public InfoHeader Header;
            public uint Flags, Technology;
            public ushort Manufacturer, Product;
            public uint Connector;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Name;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DevicePath;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct SourceName
        {
            public InfoHeader Header;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        }
        [StructLayout(LayoutKind.Sequential)] private struct ColorInfo { public InfoHeader Header; public uint Flags, Encoding, BitsPerChannel; }

        internal sealed class ConnectionInfo
        {
            public string Id, Name, Display;
            public uint Technology, Width, Height, RefreshNumerator, RefreshDenominator;
            public uint? BitsPerChannel, ColorEncoding;
            public bool? AdvancedColorEnabled;
        }

        internal static ConnectionInfo[] Inspect()
        {
            var snapshot = Capture();
            return snapshot.Item1.Select(path => {
                var target = new TargetName { Header = new InfoHeader { Type = 2, Size = (uint)Marshal.SizeOf(typeof(TargetName)), Adapter = path.Target.Adapter, Id = path.Target.Id } };
                int error = DisplayConfigGetDeviceInfo(ref target);
                if (error != 0) throw new Win32Exception(error, "모니터 식별 정보를 읽지 못했습니다.");
                var source = new SourceName { Header = new InfoHeader { Type = 1, Size = (uint)Marshal.SizeOf(typeof(SourceName)), Adapter = path.Source.Adapter, Id = path.Source.Id } };
                error = GetSourceName(ref source);
                if (error != 0) throw new Win32Exception(error, "디스플레이 이름을 읽지 못했습니다.");
                var info = new ConnectionInfo { Id = MonitorService.NormalizeId(target.DevicePath), Name = target.Name, Display = source.Name,
                    Technology = path.Target.Technology, RefreshNumerator = path.Target.Refresh.Numerator, RefreshDenominator = path.Target.Refresh.Denominator };
                if (path.Source.ModeIndex < snapshot.Item2.Length)
                { info.Width = snapshot.Item2[path.Source.ModeIndex].SourceWidth; info.Height = snapshot.Item2[path.Source.ModeIndex].SourceHeight; }
                var color = new ColorInfo { Header = new InfoHeader { Type = 9, Size = (uint)Marshal.SizeOf(typeof(ColorInfo)), Adapter = path.Target.Adapter, Id = path.Target.Id } };
                if (GetColorInfo(ref color) == 0) { info.BitsPerChannel = color.BitsPerChannel; info.ColorEncoding = color.Encoding; info.AdvancedColorEnabled = (color.Flags & 2) != 0; }
                return info;
            }).ToArray();
        }

        internal static Tuple<DisplayPath[], DisplayMode[]> Capture()
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                uint pathCount, modeCount;
                int error = GetDisplayConfigBufferSizes(QueryActive, out pathCount, out modeCount);
                if (error != 0) throw new Win32Exception(error, "현재 디스플레이 구성을 읽지 못했습니다.");
                if (pathCount == 0 || pathCount > 256 || modeCount > 1024) throw new InvalidOperationException("복구할 디스플레이 구성이 없습니다.");
                var paths = new DisplayPath[pathCount]; var modes = new DisplayMode[modeCount];
                error = QueryDisplayConfig(QueryActive, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
                if (error == 122) continue; // A hotplug changed the required buffer sizes.
                if (error != 0) throw new Win32Exception(error, "현재 디스플레이 구성을 읽지 못했습니다.");
                return Tuple.Create(paths.Take((int)pathCount).ToArray(), modes.Take((int)modeCount).ToArray());
            }
            throw new InvalidOperationException("연결이 변경 중입니다. 잠시 후 다시 시도하세요.");
        }

        public void Reinitialize(MonitorDevice monitor, bool validateOnly)
        {
            if (monitor == null || !monitor.IsHdmi) throw new InvalidOperationException("HDMI 모니터를 선택하세요.");
            var snapshot = Capture();
            bool exactMatch = false;
            foreach (var path in snapshot.Item1.Where(x => x.Target.Technology == 5))
            {
                var name = new TargetName { Header = new InfoHeader { Type = 2, Size = (uint)Marshal.SizeOf(typeof(TargetName)), Adapter = path.Target.Adapter, Id = path.Target.Id } };
                if (DisplayConfigGetDeviceInfo(ref name) != 0) continue;
                string id = MonitorService.NormalizeId(name.DevicePath);
                if (id.Length > 0 && (String.Equals(monitor.Id, id, StringComparison.OrdinalIgnoreCase) || monitor.Id.StartsWith(id + "|", StringComparison.OrdinalIgnoreCase))) exactMatch = true;
            }
            if (!exactMatch) throw new InvalidOperationException("선택한 HDMI 연결이 변경되었습니다. 다시 검색해 주세요.");
            int error = SetDisplayConfig((uint)snapshot.Item1.Length, snapshot.Item1, (uint)snapshot.Item2.Length, snapshot.Item2, UseSupplied | Validate);
            if (error != 0) throw new Win32Exception(error, "현재 화면 구성을 보존한 복구를 검증하지 못했습니다.");
            if (validateOnly) return;
            error = SetDisplayConfig((uint)snapshot.Item1.Length, snapshot.Item1, (uint)snapshot.Item2.Length, snapshot.Item2, UseSupplied | Apply | NoOptimization);
            if (error != 0) throw new Win32Exception(error, "디스플레이 신호를 다시 초기화하지 못했습니다.");
        }

        [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
        [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags, ref uint paths, [Out] DisplayPath[] pathArray, ref uint modes, [Out] DisplayMode[] modeArray, IntPtr topology);
        [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref TargetName name);
        [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] private static extern int GetSourceName(ref SourceName name);
        [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] private static extern int GetColorInfo(ref ColorInfo color);
        [DllImport("user32.dll")] private static extern int SetDisplayConfig(uint paths, [In] DisplayPath[] pathArray, uint modes, [In] DisplayMode[] modeArray, uint flags);
    }
}
