using System;
using System.IO;
using Microsoft.Win32;

namespace MonitorControl
{
    // Kept behind a one-value interface so regression tests never edit Windows startup.
    public interface IStartupValueStore
    {
        string Read();
        void Write(string command);
        void Delete();
    }

    public sealed class StartupRegistrationService
    {
        private readonly IStartupValueStore store;
        public StartupRegistrationService(IStartupValueStore valueStore)
        { if (valueStore == null) throw new ArgumentNullException("valueStore"); store = valueStore; }

        public bool IsEnabled(string executablePath)
        {
            string path = StartupRegistration.NormalizeExecutable(executablePath);
            string arguments;
            return ReferencesExecutable(store.Read(), path, out arguments) && arguments == "--background";
        }

        public void SetEnabled(bool enabled, string executablePath)
        {
            string command = StartupRegistration.BuildCommand(executablePath);
            string path = StartupRegistration.NormalizeExecutable(executablePath);
            string current = store.Read(), arguments;
            bool owned = ReferencesExecutable(current, path, out arguments);
            if (enabled)
            {
                if (owned && arguments == "--background") return;
                // A foreign value with the same name must never be overwritten silently.
                if (!String.IsNullOrWhiteSpace(current) && !owned)
                    throw new InvalidOperationException("다른 경로의 LumaDock 시작 항목이 있습니다. Windows 시작 앱 설정에서 확인해 주세요.");
                store.Write(command);
            }
            else if (owned) store.Delete();
        }

        private static bool ReferencesExecutable(string command, string expectedPath, out string arguments)
        {
            arguments = "";
            if (String.IsNullOrWhiteSpace(command)) return false;
            command = command.Trim();
            string candidate;
            if (command[0] == '"')
            {
                int end = command.IndexOf('"', 1);
                if (end < 0) return false;
                candidate = command.Substring(1, end - 1);
                // A closing quote must be followed by whitespace or the command end.
                if (end + 1 < command.Length && !Char.IsWhiteSpace(command[end + 1])) return false;
                arguments = command.Substring(end + 1).Trim();
            }
            else
            {
                int end = 0;
                while (end < command.Length && !Char.IsWhiteSpace(command[end])) end++;
                candidate = command.Substring(0, end);
                arguments = command.Substring(end).Trim();
            }
            try { return String.Equals(StartupRegistration.NormalizeExecutable(candidate), expectedPath, StringComparison.OrdinalIgnoreCase); }
            catch (ArgumentException) { return false; }
            catch (NotSupportedException) { return false; }
            catch (PathTooLongException) { return false; }
        }
    }

    public static class StartupRegistration
    {
        public const string ValueName = "LumaDock";
        public const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public static string BuildCommand(string executablePath)
        {
            string command = "\"" + NormalizeExecutable(executablePath) + "\" --background";
            // Run keys have a documented 260-character command line limit.
            if (command.Length > 260) throw new ArgumentException("자동 실행 경로가 너무 깁니다.", "executablePath");
            return command;
        }

        public static void SetEnabled(bool enabled, string executablePath)
        { new StartupRegistrationService(new WindowsStartupValueStore()).SetEnabled(enabled, executablePath); }

        public static bool IsEnabled(string executablePath)
        { return new StartupRegistrationService(new WindowsStartupValueStore()).IsEnabled(executablePath); }

        internal static string NormalizeExecutable(string executablePath)
        {
            if (String.IsNullOrWhiteSpace(executablePath)) throw new ArgumentException("실행 파일 경로가 필요합니다.", "executablePath");
            foreach (char ch in executablePath)
                if (ch < 32 || ch == '"' || ch == '*' || ch == '?' || ch == '<' || ch == '>' || ch == '|')
                    throw new ArgumentException("실행 파일 경로가 올바르지 않습니다.", "executablePath");
            bool drive = executablePath.Length >= 3 && Char.IsLetter(executablePath[0]) && executablePath[1] == ':' && (executablePath[2] == '\\' || executablePath[2] == '/');
            bool unc = executablePath.StartsWith(@"\\", StringComparison.Ordinal);
            if (!drive && !unc) throw new ArgumentException("절대 실행 파일 경로가 필요합니다.", "executablePath");
            if (executablePath.IndexOf(':', drive ? 2 : 0) >= 0) throw new ArgumentException("실행 파일 경로가 올바르지 않습니다.", "executablePath");
            string path;
            try { path = Path.GetFullPath(executablePath); }
            catch (PathTooLongException ex) { throw new ArgumentException("자동 실행 경로가 너무 깁니다.", "executablePath", ex); }
            if (!String.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("EXE 실행 파일 경로가 필요합니다.", "executablePath");
            return path;
        }

        private sealed class WindowsStartupValueStore : IStartupValueStore
        {
            private static RegistryKey OpenCurrentUser()
            { return RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Registry32); }

            public string Read()
            {
                using (var user = OpenCurrentUser())
                using (var run = user.OpenSubKey(RegistryPath, false))
                {
                    if (run == null) return null;
                    object value = run.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    if (value == null) return null;
                    var text = value as string;
                    if (text == null) throw new InvalidOperationException("LumaDock 시작 항목의 형식이 올바르지 않습니다.");
                    return text;
                }
            }

            public void Write(string command)
            {
                using (var user = OpenCurrentUser())
                using (var run = user.CreateSubKey(RegistryPath)) run.SetValue(ValueName, command, RegistryValueKind.String);
            }

            public void Delete()
            {
                using (var user = OpenCurrentUser())
                using (var run = user.OpenSubKey(RegistryPath, true))
                    if (run != null) run.DeleteValue(ValueName, false);
            }
        }
    }
}
