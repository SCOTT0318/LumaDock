using System;
using MonitorControl;

internal static class StartupRegistrationTests
{
    private static int count;
    private sealed class MemoryStore : IStartupValueStore
    {
        public string Value;
        public int Writes, Deletes, Reads;
        public string Read() { Reads++; return Value; }
        public void Write(string command) { Writes++; Value = command; }
        public void Delete() { Deletes++; Value = null; }
    }
    private static void Check(bool condition, string description)
    { if (!condition) throw new Exception(description); count++; Console.WriteLine("PASS " + description); }
    private static void Invalid(string path, string description)
    {
        bool rejected = false;
        try { StartupRegistration.BuildCommand(path); } catch (ArgumentException) { rejected = true; }
        Check(rejected, description);
    }
    public static int Main()
    {
        try
        {
            const string exe = @"C:\LumaDock\LumaDock.exe";
            const string command = "\"C:\\LumaDock\\LumaDock.exe\" --background";
            Check(StartupRegistration.BuildCommand(exe) == command, "Startup commands quote the executable and request background launch");
            Check(StartupRegistration.BuildCommand(@"C:\프로그램 폴더\LumaDock\LumaDock.exe") == "\"C:\\프로그램 폴더\\LumaDock\\LumaDock.exe\" --background", "Spaces and Unicode remain inside the quoted executable path");
            Check(StartupRegistration.BuildCommand(@"C:/Apps/../LumaDock/LumaDock.exe") == command, "Absolute paths normalize separators and parent segments");
            Check(StartupRegistration.BuildCommand(@"\\server\shared folder\LumaDock.exe") == "\"\\\\server\\shared folder\\LumaDock.exe\" --background", "Absolute UNC installation paths are quoted correctly");
            Invalid(null, "Missing executable paths are rejected");
            Invalid(" ", "Blank executable paths are rejected");
            Invalid(@"LumaDock.exe", "Relative executable paths cannot become startup entries");
            Invalid(@"\LumaDock\LumaDock.exe", "Drive-relative rooted paths are rejected");
            Invalid(@"C:LumaDock.exe", "Drive-relative paths without a separator are rejected");
            Invalid("C:\\LumaDock\\LumaDock.exe\" --extra", "Embedded quote and argument injection is rejected");
            Invalid("C:\\LumaDock\\Luma\0Dock.exe", "NUL characters are rejected");
            Invalid("C:\\LumaDock\\Luma\nDock.exe", "Control characters are rejected");
            Invalid(@"C:\LumaDock\*.exe", "Wildcard executable paths are rejected");
            Invalid(@"C:\LumaDock\LumaDock.cmd", "Non-EXE files cannot be registered");
            Invalid(@"C:\LumaDock\LumaDock.exe:other.exe", "Alternate stream executable paths are rejected");
            Invalid("C:\\" + new string('a', 250) + "\\LumaDock.exe", "Commands over the documented Run-key limit are rejected");

            var store = new MemoryStore();
            var registration = new StartupRegistrationService(store);
            Check(store.Reads == 0 && store.Writes == 0 && store.Deletes == 0, "Constructing registration performs no registry-style operations");
            Check(!registration.IsEnabled(exe) && store.Writes == 0, "Checking a missing entry does not create one");
            registration.SetEnabled(true, exe);
            Check(store.Value == command && store.Writes == 1, "Enabling writes the exact background command");
            Check(registration.IsEnabled(exe), "The exact background command is recognized as enabled");
            registration.SetEnabled(true, exe);
            Check(store.Writes == 1, "Repeated startup synchronization does not rewrite an unchanged Run entry");
            registration.SetEnabled(false, exe);
            Check(store.Value == null && store.Deletes == 1, "Disabling removes this executable's own entry");
            registration.SetEnabled(false, exe);
            Check(store.Deletes == 1, "Disabling an absent entry performs no deletion");
            store.Value = "\"C:\\Other\\Other.exe\" --background";
            registration.SetEnabled(false, exe);
            Check(store.Value.Contains("Other.exe") && store.Deletes == 1, "Disabling preserves a foreign executable with the same value name");
            bool conflict = false;
            try { registration.SetEnabled(true, exe); } catch (InvalidOperationException) { conflict = true; }
            Check(conflict && store.Value.Contains("Other.exe") && store.Writes == 1, "Enabling does not overwrite a foreign startup value");
            Check(!registration.IsEnabled(exe), "Foreign executable commands are never reported as enabled");
            store.Value = "\"c:\\lumadock\\LUMADOCK.EXE\" --background";
            Check(registration.IsEnabled(exe), "Executable ownership uses Windows case-insensitive path matching");
            store.Value = command + " --extra";
            Check(!registration.IsEnabled(exe), "Extra startup arguments are not mistaken for the expected command");
            registration.SetEnabled(false, exe);
            Check(store.Value == null, "An owned executable can be disabled even if its arguments differ");
            store.Value = "\"C:\\LumaDock\\LumaDock.exe\" --BACKGROUND";
            Check(!registration.IsEnabled(exe), "Case-sensitive background flags match the app command-line parser");
            registration.SetEnabled(true, exe);
            Check(store.Value == command, "An owned legacy command is repaired to the canonical background argument");
            store.Value = exe + " --background";
            Check(registration.IsEnabled(exe), "An unquoted path without spaces still resolves to the exact owned executable");
            int writes = store.Writes;
            registration.SetEnabled(true, exe);
            Check(store.Writes == writes, "A semantically unchanged startup entry is not rewritten during execution");
            store.Value = "\"C:\\LumaDock\\LumaDock.exe\"evil --background";
            registration.SetEnabled(false, exe);
            Check(store.Value.EndsWith("evil --background"), "Malformed quote boundaries cannot authorize deletion");
            store.Value = "\"C:\\LumaDock-old\\LumaDock.exe\" --background";
            registration.SetEnabled(false, exe);
            Check(store.Value.Contains("LumaDock-old"), "A different installation path is not removed by this installation");
            Console.WriteLine(count + " startup registration checks passed. No real Run registry keys were read or changed."); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
