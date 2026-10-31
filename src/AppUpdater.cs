using System;
using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MonitorControl
{
    public sealed class UpdateRelease
    {
        public Version Version;
        public string Tag;
        public string InstallerName;
        public string InstallerUrl;
        public string ChecksumUrl;
        public string InstallerDigest;
        public string ReleaseUrl;
        public long InstallerSize;
    }

    // Parsing and validation are independent of the network and installer execution.
    public static class UpdateProtocol
    {
        public const long MaximumInstallerBytes = 200L * 1024 * 1024;
        public static bool TryVersion(string text, out Version version)
        {
            version = null;
            var match = Regex.Match(text ?? "", @"^v?(\d+)\.(\d+)\.(\d+)(?:\.(\d+))?$", RegexOptions.IgnoreCase);
            if (!match.Success) return false;
            int major, minor, build, revision = 0;
            if (!Int32.TryParse(match.Groups[1].Value, out major) || !Int32.TryParse(match.Groups[2].Value, out minor) || !Int32.TryParse(match.Groups[3].Value, out build)
                || (match.Groups[4].Success && !Int32.TryParse(match.Groups[4].Value, out revision))) return false;
            version = new Version(major, minor, build, revision); return true;
        }
        public static Version Normalize(Version version)
        { return new Version(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision)); }
        public static bool IsNewer(Version available, Version current) { return Normalize(available) > Normalize(current); }
        public static string VersionText(Version version)
        { return version.Major + "." + version.Minor + "." + Math.Max(0, version.Build); }

        public static UpdateRelease ParseRelease(string json)
        {
            var serializer = new JavaScriptSerializer { MaxJsonLength = 2 * 1024 * 1024, RecursionLimit = 32 };
            var root = serializer.DeserializeObject(json) as System.Collections.Generic.Dictionary<string, object>;
            if (root == null) throw new InvalidDataException("릴리스 정보를 읽지 못했습니다.");
            if (Flag(root, "draft") || Flag(root, "prerelease")) throw new InvalidDataException("정식 공개 릴리스가 아닙니다.");
            string tag = Value(root, "tag_name"); Version version;
            if (!Regex.IsMatch(tag, @"^v?\d+\.\d+\.\d+$", RegexOptions.IgnoreCase) || !TryVersion(tag, out version)) throw new InvalidDataException("지원하지 않는 버전 형식입니다.");
            string number = VersionText(version);
            string installerName = "LumaDock-" + number + ".exe";
            string prefix = "/SCOTT0318/LumaDock/releases/download/" + tag + "/";
            var result = new UpdateRelease { Version = version, Tag = tag, InstallerName = installerName,
                ReleaseUrl = "https://github.com/SCOTT0318/LumaDock/releases/tag/" + tag };
            object raw;
            var assets = root.TryGetValue("assets", out raw) ? raw as IEnumerable : null;
            if (assets == null) throw new InvalidDataException("설치 파일이 없는 릴리스입니다.");
            foreach (var item in assets)
            {
                var asset = item as System.Collections.Generic.Dictionary<string, object>; if (asset == null) continue;
                string name = Value(asset, "name");
                if (name != installerName && name != "SHA256SUMS.txt") continue;
                string state = Value(asset, "state");
                if (state.Length > 0 && state != "uploaded") throw new InvalidDataException("업로드가 완료되지 않은 파일입니다.");
                string url = Value(asset, "browser_download_url");
                if (!TrustedAsset(url, prefix + name)) throw new InvalidDataException("신뢰할 수 없는 다운로드 경로입니다.");
                if (name == installerName)
                {
                    if (result.InstallerUrl != null) throw new InvalidDataException("설치 파일이 중복되었습니다.");
                    long size; if (!Int64.TryParse(Value(asset, "size"), out size) || size <= 0 || size > MaximumInstallerBytes) throw new InvalidDataException("설치 파일 크기가 올바르지 않습니다.");
                    result.InstallerUrl = url; result.InstallerSize = size;
                    string digest = Value(asset, "digest");
                    if (digest.Length > 0 && !Regex.IsMatch(digest, @"^sha256:[0-9a-fA-F]{64}$")) throw new InvalidDataException("설치 파일 체크섬이 올바르지 않습니다.");
                    result.InstallerDigest = digest.Length == 0 ? null : digest.Substring(7).ToLowerInvariant();
                }
                else
                {
                    if (result.ChecksumUrl != null) throw new InvalidDataException("체크섬 파일이 중복되었습니다.");
                    result.ChecksumUrl = url;
                }
            }
            if (result.InstallerUrl == null || result.ChecksumUrl == null) throw new InvalidDataException("설치 파일 또는 체크섬이 없습니다.");
            return result;
        }
        private static string Value(System.Collections.Generic.Dictionary<string, object> root, string name)
        { object value; return root.TryGetValue(name, out value) && value != null ? Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) : ""; }
        private static bool Flag(System.Collections.Generic.Dictionary<string, object> root, string name)
        { object value; return root.TryGetValue(name, out value) && value is bool && (bool)value; }
        public static bool TrustedAsset(string url, string expectedPath)
        {
            Uri uri;
            return Uri.TryCreate(url, UriKind.Absolute, out uri) && uri.Scheme == "https" && uri.IsDefaultPort && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 && uri.AbsolutePath == expectedPath;
        }
        public static bool TrustedDownloadHost(Uri uri)
        {
            return uri != null && uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
                (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase));
        }
        public static string ManifestChecksum(string manifest, string fileName)
        {
            string found = null;
            foreach (string line in (manifest ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var match = Regex.Match(line.Trim(), @"^([0-9a-fA-F]{64})\s+\*?(.+)$");
                if (!match.Success || match.Groups[2].Value != fileName) continue;
                string hash = match.Groups[1].Value.ToLowerInvariant();
                if (found != null && found != hash) throw new InvalidDataException("서로 다른 체크섬이 중복되었습니다.");
                found = hash;
            }
            if (found == null) throw new InvalidDataException("설치 파일의 체크섬을 찾지 못했습니다.");
            return found;
        }
        public static string FileChecksum(string path)
        {
            using (var stream = File.OpenRead(path)) using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
        public static string QuoteArgument(string value)
        {
            var result = new StringBuilder("\""); int slashes = 0;
            foreach (char ch in value ?? "")
            {
                if (ch == '\\') { slashes++; continue; }
                result.Append('\\', ch == '"' ? slashes * 2 + 1 : slashes); result.Append(ch); slashes = 0;
            }
            result.Append('\\', slashes * 2); return result.Append('"').ToString();
        }
    }

    public sealed class AppUpdater : IDisposable
    {
        private readonly Window owner;
        private readonly bool demo;
        private readonly Action requestExit;
        private readonly string stagingDirectory;
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private Window dialog;
        private bool checking, disposed;
        public Version CurrentVersion { get { return UpdateProtocol.Normalize(Assembly.GetExecutingAssembly().GetName().Version); } }

        public AppUpdater(Window parent, bool demoMode, Action exit, string stagingDirectory = null)
        {
            owner = parent; demo = demoMode; requestExit = exit;
            this.stagingDirectory = Path.GetFullPath(stagingDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LumaDock", "Updates"));
        }

        public async Task CheckAsync(bool silent = false)
        {
            if (disposed) return;
            if (dialog != null) { if (!silent) { dialog.Show(); dialog.Activate(); } return; }
            if (checking) return;
            if (demo) { if (!silent) ShowDialog(null, "데모에서는 업데이트를 확인하지 않습니다."); return; }
            checking = true;
            try
            {
                string json = await ReadTextAsync("https://api.github.com/repos/SCOTT0318/LumaDock/releases/latest", 2 * 1024 * 1024, lifetime.Token);
                var release = UpdateProtocol.ParseRelease(json);
                if (disposed) return;
                bool newer = UpdateProtocol.IsNewer(release.Version, CurrentVersion);
                if (newer || !silent) ShowDialog(newer ? release : null, newer ? "새 버전을 사용할 수 있습니다." : "최신 버전입니다.");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!silent && !disposed) ShowDialog(null, "업데이트 확인 실패\n" + FriendlyError(ex)); }
            finally { checking = false; }
        }

        private void ShowDialog(UpdateRelease release, string message)
        {
            if (disposed || dialog != null) return;
            var window = new Window { Title = "업데이트 · LumaDock", Width = 520, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = owner.IsVisible ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
                Background = Color("#0E171F"), Foreground = Color("#E7F1F6"), FontFamily = new FontFamily("Segoe UI, Malgun Gothic"), Icon = AppIdentity.WindowIcon() };
            if (owner.IsVisible) window.Owner = owner;
            AppIdentity.DarkTitlebar(window);
            var content = owner.Content as FrameworkElement; if (content != null) window.Resources.MergedDictionaries.Add(content.Resources);
            dialog = window;
            var root = new StackPanel { Margin = new Thickness(28) }; window.Content = root;
            var title = Label("업데이트", 24, "#EBF4F8"); title.FontWeight = FontWeights.SemiBold; root.Children.Add(title);
            var versions = Label("현재 " + UpdateProtocol.VersionText(CurrentVersion) + (release == null ? "" : "    →    " + UpdateProtocol.VersionText(release.Version)), 13, "#8CE8D3");
            versions.Margin = new Thickness(0, 17, 0, 16); root.Children.Add(versions);
            var state = Label(message, 12, "#A8BFCD"); state.TextWrapping = TextWrapping.Wrap; state.LineHeight = 21; root.Children.Add(state);
            var progress = new ProgressBar { Height = 5, Minimum = 0, Maximum = 100, Foreground = Color("#8CE8D3"), Background = Color("#293B47"), BorderThickness = new Thickness(0), Margin = new Thickness(0, 18, 0, 0), Visibility = Visibility.Collapsed };
            root.Children.Add(progress);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 25, 0, 0) }; root.Children.Add(actions);
            var cancel = new Button { Content = "닫기", Padding = new Thickness(15, 9, 15, 9) }; actions.Children.Add(cancel);
            CancellationTokenSource downloadCancellation = null;
            cancel.Click += delegate { if (downloadCancellation != null) downloadCancellation.Cancel(); window.Close(); };
            window.Closed += delegate { if (downloadCancellation != null) downloadCancellation.Cancel(); if (dialog == window) dialog = null; };
            if (release != null)
            {
                var notes = new Button { Content = "릴리스 보기", Padding = new Thickness(15, 9, 15, 9), Margin = new Thickness(8, 0, 0, 0) };
                notes.Click += delegate { Process.Start(new ProcessStartInfo(release.ReleaseUrl) { UseShellExecute = true }); }; actions.Children.Add(notes);
                var install = new Button { Content = "다운로드 및 설치", Padding = new Thickness(16, 9, 16, 9), Margin = new Thickness(8, 0, 0, 0) };
                var style = window.TryFindResource("PrimaryButton") as Style; if (style != null) install.Style = style; actions.Children.Add(install);
                install.Click += async delegate {
                    if (downloadCancellation != null) return;
                    downloadCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    install.IsEnabled = false; notes.IsEnabled = false; cancel.Content = "취소"; progress.Visibility = Visibility.Visible; state.Text = "다운로드 중…";
                    try
                    {
                        var reporter = new Progress<double>(p => { if (!disposed) { progress.Value = p; state.Text = "다운로드 " + (int)p + "%"; } });
                        string installer = await DownloadAsync(release, reporter, downloadCancellation.Token);
                        if (disposed) return;
                        state.Text = "설치 프로그램을 준비하는 중…";
                        LaunchHelper(installer, release);
                        requestExit();
                    }
                    catch (OperationCanceledException) { if (dialog == window) { state.Text = "다운로드 취소됨"; install.IsEnabled = true; notes.IsEnabled = true; } }
                    catch (Exception ex) { if (dialog == window) { state.Text = "업데이트 실패\n" + FriendlyError(ex); install.IsEnabled = true; notes.IsEnabled = true; } }
                    finally { downloadCancellation.Dispose(); downloadCancellation = null; cancel.Content = "닫기"; }
                };
            }
            window.Show();
        }

        private async Task<string> DownloadAsync(UpdateRelease release, IProgress<double> progress, CancellationToken token)
        {
            string directory = Path.Combine(stagingDirectory, UpdateProtocol.VersionText(release.Version)); Directory.CreateDirectory(directory);
            string manifest = await ReadTextAsync(release.ChecksumUrl, 64 * 1024, token);
            string expected = UpdateProtocol.ManifestChecksum(manifest, release.InstallerName);
            if (release.InstallerDigest != null && expected != release.InstallerDigest) throw new InvalidDataException("GitHub와 체크섬 파일의 값이 다릅니다.");
            string target = Path.Combine(directory, release.InstallerName), partial = target + "." + Guid.NewGuid().ToString("N") + ".partial";
            try
            {
                using (var file = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                    await TransferAsync(release.InstallerUrl, file, release.InstallerSize, token, progress);
                token.ThrowIfCancellationRequested();
                string actual = await Task.Run(() => UpdateProtocol.FileChecksum(partial), token);
                if (new FileInfo(partial).Length != release.InstallerSize || actual != expected) throw new InvalidDataException("설치 파일 검증에 실패했습니다. 다시 시도하세요.");
                if (File.Exists(target)) File.Delete(target);
                File.Move(partial, target); return target;
            }
            finally { if (File.Exists(partial)) File.Delete(partial); }
        }

        private void LaunchHelper(string installer, UpdateRelease release)
        {
            string directory = Path.GetDirectoryName(installer);
            string helper = Path.Combine(directory, "LumaDock.UpdateHelper-" + Guid.NewGuid().ToString("N") + ".exe");
            string source = Assembly.GetExecutingAssembly().Location; File.Copy(source, helper);
            if (File.Exists(source + ".config")) File.Copy(source + ".config", helper + ".config");
            string destination = InstalledDirectory(Path.GetDirectoryName(source));
            string args = "--apply-update " + Process.GetCurrentProcess().Id + " " + UpdateProtocol.QuoteArgument(installer) + " " + UpdateProtocol.QuoteArgument(destination)
                + " " + UpdateProtocol.QuoteArgument(UpdateProtocol.FileChecksum(installer)) + " " + UpdateProtocol.QuoteArgument(source);
            Process.Start(new ProcessStartInfo(helper, args) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory });
        }

        public static bool TryApplyUpdate(string[] args)
        {
            if (args.Length == 0 || args[0] != "--apply-update") return false;
            string original = null; bool originalValidated = false;
            try
            {
                if (args.Length != 6) throw new ArgumentException("잘못된 업데이트 요청입니다.");
                int parent; if (!Int32.TryParse(args[1], out parent) || parent <= 0 || parent == Process.GetCurrentProcess().Id) throw new ArgumentException("잘못된 앱 프로세스입니다.");
                string installer = Path.GetFullPath(args[2]); string target = args[3]; string expected = args[4]; original = Path.GetFullPath(args[5]);
                string helperDirectory = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
                if (!String.Equals(Path.GetDirectoryName(installer), helperDirectory, StringComparison.OrdinalIgnoreCase)
                    || !Regex.IsMatch(Path.GetFileName(installer), @"^LumaDock-\d+\.\d+\.\d+\.exe$") || !Regex.IsMatch(expected, @"^[0-9a-f]{64}$")) throw new InvalidDataException("잘못된 설치 파일 경로입니다.");
                if (!String.Equals(Path.GetFileName(original), "LumaDock.exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(original)) throw new InvalidDataException("원래 앱 경로를 찾지 못했습니다.");
                originalValidated = true;
                if (target.Length > 0 && !String.Equals(InstalledDirectory(target), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("설치 위치를 확인하지 못했습니다.");
                try { using (var process = Process.GetProcessById(parent)) if (!process.WaitForExit(120000)) throw new InvalidOperationException("앱 종료를 기다리는 시간이 초과되었습니다."); }
                catch (ArgumentException) { /* Parent already exited before the helper started. */ }
                if (UpdateProtocol.FileChecksum(installer) != expected) throw new InvalidDataException("설치 파일이 변경되었습니다. 업데이트를 다시 확인하세요.");
                string arguments = "/SP-" + (target.Length == 0 ? "" : " /DIR=" + UpdateProtocol.QuoteArgument(target));
                Process.Start(new ProcessStartInfo(installer, arguments) { UseShellExecute = true, WorkingDirectory = helperDirectory });
            }
            catch (Exception ex)
            {
                MessageBox.Show("업데이트를 시작하지 못했습니다.\n" + FriendlyError(ex), "LumaDock", MessageBoxButton.OK, MessageBoxImage.Error);
                if (originalValidated && File.Exists(original)) try { Process.Start(new ProcessStartInfo(original) { UseShellExecute = true }); } catch (Win32Exception) { }
            }
            return true;
        }

        public static string InstalledDirectory(string directory)
        {
            string path = Path.GetFullPath(directory);
            return File.Exists(Path.Combine(path, "LumaDock.exe")) && Directory.GetFiles(path, "unins*.exe").Length > 0 ? path : "";
        }

        private static async Task<string> ReadTextAsync(string url, int limit, CancellationToken token)
        {
            using (var memory = new MemoryStream()) { await TransferAsync(url, memory, limit, token, null); return new UTF8Encoding(false, true).GetString(memory.ToArray()).TrimStart('\uFEFF'); }
        }
        private static async Task TransferAsync(string url, Stream destination, long limit, CancellationToken token, IProgress<double> progress)
        {
            Uri address = new Uri(url);
            for (int redirect = 0; redirect < 6; redirect++)
            {
                if (!UpdateProtocol.TrustedDownloadHost(address)) throw new InvalidDataException("신뢰할 수 없는 다운로드 서버입니다.");
                var request = (HttpWebRequest)WebRequest.Create(address); request.AllowAutoRedirect = false; request.UserAgent = "LumaDock-Updater"; request.Timeout = 30000; request.ReadWriteTimeout = 30000;
                request.Accept = address.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase) ? "application/vnd.github+json" : "*/*";
                using (token.Register(request.Abort))
                {
                    HttpWebResponse response;
                    try { response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false); }
                    catch (WebException) { token.ThrowIfCancellationRequested(); throw; }
                    using (response)
                    {
                        int status = (int)response.StatusCode;
                        if (status >= 300 && status < 400)
                        {
                            string location = response.Headers[HttpResponseHeader.Location];
                            if (String.IsNullOrWhiteSpace(location)) throw new InvalidDataException("다운로드 경로가 없습니다.");
                            address = new Uri(address, location); continue;
                        }
                        if (response.StatusCode != HttpStatusCode.OK) throw new WebException("다운로드 서버가 응답하지 않았습니다.");
                        if (response.ContentLength > limit) throw new InvalidDataException("다운로드 파일이 너무 큽니다.");
                        using (var input = response.GetResponseStream())
                        {
                            var buffer = new byte[65536]; long total = 0; int count;
                            while ((count = await input.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) > 0)
                            {
                                token.ThrowIfCancellationRequested(); total += count;
                                if (total > limit) throw new InvalidDataException("다운로드 파일이 너무 큽니다.");
                                await destination.WriteAsync(buffer, 0, count, token).ConfigureAwait(false);
                                if (progress != null) progress.Report(Math.Min(100, total * 100.0 / limit));
                            }
                        }
                        return;
                    }
                }
            }
            throw new InvalidDataException("다운로드 경로가 너무 많이 변경되었습니다.");
        }
        private static string FriendlyError(Exception ex)
        {
            if (ex is WebException) return "네트워크 연결 또는 GitHub 응답을 확인하세요.";
            var win32 = ex as Win32Exception; if (win32 != null && win32.NativeErrorCode == 1223) return "설치가 취소되었습니다.";
            return ex.Message;
        }
        private static SolidColorBrush Color(string value) { return (SolidColorBrush)new BrushConverter().ConvertFromString(value); }
        private static TextBlock Label(string text, double size, string color) { return new TextBlock { Text = text, FontSize = size, Foreground = Color(color) }; }
        public void Dispose() { if (disposed) return; disposed = true; lifetime.Cancel(); if (dialog != null) dialog.Close(); lifetime.Dispose(); }
    }
}
