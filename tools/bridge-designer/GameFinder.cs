using System.Runtime.Versioning;
using System.Text.RegularExpressions;

/// <summary>
/// Where Star Trek: Bridge Crew is: the folder this program is in or one above it (the installers put it in the
/// game's folder), else the game in Steam's libraries, found as the installers find it: Steam's own folder (from the
/// registry on Windows), its libraryfolders.vdf, and the game's appmanifest there.
/// </summary>
static class GameFinder
{
    const int AppId = 527100;

    public static bool IsGame(string dir) =>
        !string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, "stbc_Data", "globalgamemanagers"));

    public static string Find()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (IsGame(dir.FullName)) return dir.FullName;
        foreach (var library in Libraries())
        {
            try
            {
                string manifest = Path.Combine(library, "steamapps", $"appmanifest_{AppId}.acf");
                string installDir = File.Exists(manifest) ? Value(File.ReadAllText(manifest), "installdir") : null;
                foreach (var name in new[] { installDir, "Star Trek Bridge Crew" })
                {
                    if (name == null) continue;
                    string dir = Path.Combine(library, "steamapps", "common", name);
                    if (IsGame(dir)) return dir;
                }
            }
            catch { } // a library on a drive that's gone
        }
        return null;
    }

    static IEnumerable<string> Libraries()
    {
        var roots = new List<string>();
        if (OperatingSystem.IsWindows()) roots.AddRange(RegistryRoots());
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
            roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
        else
            roots.AddRange(new[]
            {
                Path.Combine(home, ".local/share/Steam"), Path.Combine(home, ".steam/steam"), Path.Combine(home, ".steam/root"),
                Path.Combine(home, ".var/app/com.valvesoftware.Steam/.local/share/Steam"), // Flatpak
                Path.Combine(home, "snap/steam/common/.local/share/Steam"),
            });
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
            if (seen.Add(Path.GetFullPath(root))) yield return root;
            string vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;
            string text;
            try { text = File.ReadAllText(vdf); } catch { continue; }
            // "path"  "D:\\SteamLibrary"
            foreach (Match m in Regex.Matches(text, "^\\s*\"path\"\\s*\"(.*)\"\\s*$", RegexOptions.Multiline))
            {
                string path = m.Groups[1].Value.Replace("\\\\", "\\");
                if (Directory.Exists(path) && seen.Add(Path.GetFullPath(path))) yield return path;
            }
        }
    }

    [SupportedOSPlatform("windows")]
    static IEnumerable<string> RegistryRoots()
    {
        var found = new List<string>();
        foreach (var (hive, key) in new[]
        {
            (Microsoft.Win32.Registry.CurrentUser, @"Software\Valve\Steam"),
            (Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam"),
            (Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\Valve\Steam"),
        })
        {
            try
            {
                using var k = hive.OpenSubKey(key);
                foreach (var name in new[] { "SteamPath", "InstallPath" })
                    if (k?.GetValue(name) is string v && v.Length > 0) found.Add(v.Replace('/', '\\'));
            }
            catch { }
        }
        return found;
    }

    static string Value(string vdf, string key)
    {
        var m = Regex.Match(vdf, $"^\\s*\"{key}\"\\s*\"(.*)\"\\s*$", RegexOptions.Multiline);
        return m.Success ? m.Groups[1].Value : null;
    }
}
