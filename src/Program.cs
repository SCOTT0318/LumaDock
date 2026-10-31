using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows;
using System.Threading;

namespace MonitorControl
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                if (AppUpdater.TryApplyUpdate(args)) return 0;
                bool demo = args.Contains("--demo") || args.Contains("--render-demo");
                string diagnose = Argument(args, "--diagnose");
                string preview = Argument(args, "--render-demo");
                if (diagnose != null)
                {
                    using (var service = new MonitorService(demo))
                    {
                        var monitors = service.ScanAsync().GetAwaiter().GetResult();
                        var report = new {
                            ReadOnly = true, Demo = demo, CapturedAt = DateTimeOffset.Now.ToString("o"),
                            Monitors = monitors.Select(m => new {
                                m.Name, m.Display, m.Resolution, m.Primary, m.Notice, m.Left, m.Top, m.Width, m.Height, m.PositionLabel,
                                Transport = m.WmiInstance != null ? "WMI" : "DDC/CI",
                                Features = m.Features.Select(f => new {
                                    Code = "0x" + f.Definition.Code.ToString("X2"), f.Definition.Name,
                                    Minimum = f.Definition.Discrete ? (uint?)null : f.Minimum, f.Current,
                                    Maximum = f.Definition.Discrete ? (uint?)null : f.Maximum, f.Backend, f.Writable, f.Choices
                                }).ToArray(),
                                ReportedCodes = m.Reported.Keys.OrderBy(x => x).Select(x => "0x" + x.ToString("X2")).ToArray(),
                                RawCapabilities = m.Capabilities
                            }).ToArray()
                        };
                        string path = Path.GetFullPath(diagnose);
                        Directory.CreateDirectory(Path.GetDirectoryName(path));
                        File.WriteAllText(path, new JavaScriptSerializer().Serialize(report), new UTF8Encoding(false));
                    }
                    return 0;
                }
                var app = new Application();
                app.DispatcherUnhandledException += delegate(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e) {
                    Log(e.Exception); e.Handled = true;
                    MessageBox.Show(e.Exception.Message, "LumaDock", MessageBoxButton.OK, MessageBoxImage.Error);
                };
                if (demo) return app.Run(new MainWindow(true, preview));
                bool created;
                using (var instance = new Mutex(true, @"Local\LumaDock.App", out created))
                {
                    bool background = args.Contains("--background");
                    if (!created) { if (!background) AppIdentity.ShowExisting(); return 0; }
                    try
                    {
                        var window = new MainWindow(false, null, background);
                        if (window.StartupToTray)
                        {
                            app.MainWindow = window;
                            window.StartBackground();
                            return app.Run();
                        }
                        return app.Run(window);
                    }
                    finally { instance.ReleaseMutex(); }
                }
            }
            catch (Exception ex)
            {
                Log(ex);
                if (!args.Contains("--diagnose") && !args.Contains("--render-demo")) MessageBox.Show(ex.Message, "LumaDock", MessageBoxButton.OK, MessageBoxImage.Error);
                return 1;
            }
        }

        private static string Argument(string[] args, string name)
        {
            int index = Array.IndexOf(args, name);
            if (index < 0) return null;
            if (index + 1 >= args.Length || args[index + 1].StartsWith("--")) throw new ArgumentException(name + " requires an output path.");
            return args[index + 1];
        }

        private static void Log(Exception ex)
        {
            try
            {
                string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LumaDock");
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, "error.log"), DateTimeOffset.Now + " " + ex + Environment.NewLine, Encoding.UTF8);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
