using System;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using MonitorControl;

internal static class AppUpdaterTests
{
    private static int count;
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private static void Check(bool condition, string label)
    { if (!condition) throw new Exception(label); count++; Console.WriteLine("PASS " + label); }
    private static string Json(string tag = "v1.2.3", string installerUrl = null, bool draft = false, bool prerelease = false, long size = 1234, string digest = "sha256:" + Hash, bool checksum = true)
    {
        string number = tag.TrimStart('v'); string name = "LumaDock-" + number + ".exe";
        var assets = new System.Collections.Generic.List<object>();
        assets.Add(new { name = name, state = "uploaded", browser_download_url = installerUrl ?? "https://github.com/SCOTT0318/LumaDock/releases/download/" + tag + "/" + name, size = size, digest = digest });
        if (checksum) assets.Add(new { name = "SHA256SUMS.txt", state = "uploaded", browser_download_url = "https://github.com/SCOTT0318/LumaDock/releases/download/" + tag + "/SHA256SUMS.txt", size = 91 });
        return new JavaScriptSerializer().Serialize(new { tag_name = tag, draft = draft, prerelease = prerelease, assets = assets });
    }
    private static bool Rejects(Action action)
    { try { action(); return false; } catch (InvalidDataException) { return true; } }
    public static int Main(string[] args)
    {
        try
        {
            Version version;
            Check(UpdateProtocol.TryVersion("v1.2.3", out version) && version == new Version(1, 2, 3, 0), "Stable release tags normalize to four version components");
            Check(UpdateProtocol.TryVersion("1.0.0.0", out version), "Assembly-style versions are supported");
            Check(!UpdateProtocol.TryVersion("v1.0.0-beta", out version) && !UpdateProtocol.TryVersion("../1.0.0", out version), "Prerelease labels and path-like version strings are rejected");
            Check(!UpdateProtocol.TryVersion("999999999999.0.0", out version), "Overflowing version components are rejected");
            Check(!UpdateProtocol.IsNewer(new Version(1, 0, 0), new Version(1, 0, 0, 0)), "Equivalent numeric versions do not produce false updates");
            Check(UpdateProtocol.IsNewer(new Version(1, 10, 0), new Version(1, 9, 9)), "Update ordering compares numeric rather than lexical versions");
            Check(!UpdateProtocol.IsNewer(new Version(0, 0, 2), new Version(1, 0, 0)), "An older beta-era release cannot downgrade the installed version");
            var release = UpdateProtocol.ParseRelease(Json());
            Check(release.InstallerName == "LumaDock-1.2.3.exe" && release.InstallerSize == 1234 && release.InstallerDigest == Hash, "Installer name, size and GitHub digest are read from matching release assets");
            Check(release.ReleaseUrl == "https://github.com/SCOTT0318/LumaDock/releases/tag/v1.2.3", "Release link stays pinned to the trusted repository");
            Check(Rejects(() => UpdateProtocol.ParseRelease(Json(tag:"v1.2.3.4"))), "Release asset versions require the three-component public naming scheme");
            Check(Rejects(() => UpdateProtocol.ParseRelease(Json(draft:true))) && Rejects(() => UpdateProtocol.ParseRelease(Json(prerelease:true))), "Draft and prerelease records are not treated as stable updates");
            Check(Rejects(() => UpdateProtocol.ParseRelease(Json(installerUrl:"https://example.com/LumaDock-1.2.3.exe"))), "An installer hosted outside the release repository is rejected");
            Check(Rejects(() => UpdateProtocol.ParseRelease(Json(installerUrl:"http://github.com/SCOTT0318/LumaDock/releases/download/v1.2.3/LumaDock-1.2.3.exe"))), "Plain HTTP installer paths are rejected");
            Check(Rejects(() => UpdateProtocol.ParseRelease(Json(installerUrl:"https://github.com/Other/LumaDock/releases/download/v1.2.3/LumaDock-1.2.3.exe"))), "Another repository cannot supply the installer");
            Check(Rejects(() => UpdateProtocol.ParseRelease(Json(installerUrl:"https://github.com/SCOTT0318/LumaDock/releases/download/v1.2.3/LumaDock-1.2.3.exe?redirect=evil"))), "Unexpected asset query strings are rejected");
            Check(Rejects(() => UpdateProtocol.ParseRelease(Json(checksum:false))), "A release without the SHA256 manifest cannot be installed");
            Check(Rejects(() => UpdateProtocol.ParseRelease(Json(size:0))) && Rejects(() => UpdateProtocol.ParseRelease(Json(size:UpdateProtocol.MaximumInstallerBytes+1))), "Empty and oversized installer metadata is rejected");
            Check(Rejects(() => UpdateProtocol.ParseRelease(Json(digest:"sha1:bad"))), "Malformed GitHub asset digests are rejected");
            Check(UpdateProtocol.ParseRelease(Json(digest:null)).InstallerDigest == null, "Older GitHub metadata may omit a digest while still requiring the manifest");
            Check(UpdateProtocol.TrustedDownloadHost(new Uri("https://release-assets.githubusercontent.com/file?token=temporary")), "GitHub release CDN HTTPS redirects are allowed");
            Check(!UpdateProtocol.TrustedDownloadHost(new Uri("https://evilgithubusercontent.com/file")) && !UpdateProtocol.TrustedDownloadHost(new Uri("http://release-assets.githubusercontent.com/file")), "Lookalike domains and insecure redirects are rejected");
            Check(!UpdateProtocol.TrustedDownloadHost(new Uri("https://raw.githubusercontent.com/other/file.exe")), "User-content hosts outside the release CDN cannot become installer redirects");
            string manifest = Hash.ToUpperInvariant() + "  LumaDock-1.2.3.exe\r\n";
            Check(UpdateProtocol.ManifestChecksum(manifest, "LumaDock-1.2.3.exe") == Hash, "SHA256 manifests accept hexadecimal case and CRLF");
            Check(UpdateProtocol.ManifestChecksum(Hash + " *LumaDock-1.2.3.exe\n", "LumaDock-1.2.3.exe") == Hash, "GNU binary checksum notation is supported");
            Check(Rejects(() => UpdateProtocol.ManifestChecksum(Hash + "  other.exe", "LumaDock-1.2.3.exe")), "Checksum entries for a different filename do not authorize this installer");
            Check(Rejects(() => UpdateProtocol.ManifestChecksum(manifest + new string('f',64) + "  LumaDock-1.2.3.exe", "LumaDock-1.2.3.exe")), "Conflicting checksum entries are rejected");
            Check(UpdateProtocol.QuoteArgument("C:\\My Apps\\") == "\"C:\\My Apps\\\\\"", "Command-line quoting preserves trailing directory separators");
            Check(UpdateProtocol.QuoteArgument("") == "\"\"" && UpdateProtocol.QuoteArgument("a\"b") == "\"a\\\"b\"", "Command-line quoting handles empty and quoted arguments");
            string root = Path.Combine(Path.GetFullPath(args[0]), "run-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); string file = Path.Combine(root, "hash-test.txt");
            File.WriteAllText(file, "abc", new UTF8Encoding(false));
            Check(UpdateProtocol.FileChecksum(file) == "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", "Installer checksum uses the standard SHA256 digest");
            string installed = Path.Combine(root, "installed"); Directory.CreateDirectory(installed); File.WriteAllText(Path.Combine(installed, "LumaDock.exe"), "test");
            Check(AppUpdater.InstalledDirectory(installed) == "", "Portable folders are not forced into an install-in-place update");
            File.WriteAllText(Path.Combine(installed, "unins000.exe"), "test");
            Check(AppUpdater.InstalledDirectory(installed) == installed, "Existing Inno installations retain their installation directory");
            Check(!AppUpdater.TryApplyUpdate(new string[0]), "Ordinary startup does not run updater helper logic");
            Console.WriteLine(count + " updater checks passed. No downloads, process launches or installation were performed."); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
