using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using BepInEx;
using UnityEngine;

namespace StbcRevival
{
    /// <summary>
    /// [Updates] CheckForUpdates: as the game starts (and again on returning to the shuttle after 15 minutes or more), read the latest version number
    /// from the website (version.txt, a single line written by tools/release.sh). If it's newer than this
    /// mod:
    ///   - [Updates] AutoUpdate: that release's StbcRevival.dll and StbcCore.dll are downloaded from GitHub and
    ///     checked against its SHA256SUMS, then put in place of this game's (a loaded DLL can't be overwritten
    ///     but can be renamed: the old ones become .old, deleted at the next start). Both or neither: any
    ///     failure puts the old ones back. The new version runs from the next launch, and a popup offers to
    ///     close the game now.
    ///   - Otherwise, or if that fails, the game's own popup offers to open the website and close the game.
    /// Any problem (site down, no internet, an unexpected reply, a slow answer) is logged and otherwise
    /// ignored, so it never gets in the way of playing.
    /// </summary>
    internal static class UpdateCheck
    {
        private const string DefaultUrl = "https://demigodrick.github.io/bridge-crew-revival/version.txt";
        internal const string Website = "https://demigodrick.github.io/bridge-crew-revival/";
        private const float TimeoutSeconds = 15f;

        private const float SettleSeconds = 10f;
        private const string Releases = "https://github.com/Demigodrick/bridge-crew-revival/releases/download/v";
        private static readonly string[] Files = { "StbcRevival.dll", "StbcCore.dll" };
        // Revival voice's native libraries (not plugins): in BepInEx/native, where the installer puts them. The updater
        // brings them with an update, and fetches this version's when one is missing (a player who only ever updated in
        // game never had it). Optional: without one, what it does is just off.
        private static readonly string[] NativeFiles = { "rnnoise.dll", "speexdsp.dll" };
        private static string NativePath(string file) => Path.Combine(Path.Combine(Paths.BepInExRootPath, "native"), file);
        private static string NativeUse(string file) => file == "rnnoise.dll" ? "noise suppression" : "echo cancellation";
        private static bool nativeTried;
        private const float DownloadSeconds = 120f;

        private const float RecheckSeconds = 15 * 60f; // a session this long asks again on the shuttle

        private static bool started, offered, downloading, fetching;
        private static float lastFetch;
        private static string newer, installed;
        private static float onShuttleSince = -1f;

        internal static void Update()
        {
            if (!Plugin.CheckForUpdates.Value || UbisoftSaveImport.Active) return;
            // Asked as soon as the game starts, so the answer (and with AutoUpdate the download) is in by the time
            // the shuttle is reached, rather than starting there.
            if (!started)
            {
                started = true;
                lastFetch = Time.realtimeSinceStartup;
                Plugin.Instance.StartCoroutine(Fetch());
            }
            bool onShuttle = GameState.Phase() == "GamePhase_MainMenu" && !GameState.Loading()
                             && GameState.ConnectedAndReady() && GameState.RoomName() == null;
            if (!onShuttle)
            {
                onShuttleSince = -1f;
                return;
            }
            if (onShuttleSince < 0f)
            {
                onShuttleSince = Time.realtimeSinceStartup;
                // Back on the shuttle in a long session: ask again, so a release made while playing is offered too.
                if (newer == null && !fetching && Time.realtimeSinceStartup - lastFetch >= RecheckSeconds)
                {
                    lastFetch = Time.realtimeSinceStartup;
                    Plugin.Instance.StartCoroutine(Fetch());
                }
            }
            // Only on the shuttle (never in a briefing room or mission), and only once it has settled: a popup
            // shown while the shuttle console is still loading goes nowhere.
            if (newer != null && !offered && !downloading && Time.realtimeSinceStartup - onShuttleSince >= SettleSeconds) Offer();
        }

        /// <summary>At startup: the DLLs an update replaced last time.</summary>
        internal static void CleanUp()
        {
            foreach (var native in NativeFiles)
            {
                try { if (File.Exists(NativePath(native) + ".old")) File.Delete(NativePath(native) + ".old"); }
                catch (Exception e) { Plugin.Log.LogInfo($"Update: couldn't delete {NativePath(native)}.old: {e.Message}"); }
            }
            foreach (var file in Files)
            {
                var old = Path.Combine(PluginFolder, file) + ".old";
                try { if (File.Exists(old)) File.Delete(old); }
                catch (Exception e) { Plugin.Log.LogInfo($"Update: couldn't delete {old}: {e.Message}"); }
            }
        }

        private static string PluginFolder => Path.GetDirectoryName(typeof(UpdateCheck).Assembly.Location);

        private static IEnumerator Fetch()
        {
            var url = string.IsNullOrEmpty(Plugin.UpdateUrl.Value) ? DefaultUrl : Plugin.UpdateUrl.Value.Trim();
            // A query string keeps caches from serving an old file.
            fetching = true;
            var www = new WWW(url + (url.Contains("?") ? "&" : "?") + "t=" + DateTime.UtcNow.Ticks);
            float until = Time.realtimeSinceStartup + TimeoutSeconds;
            while (!www.isDone && Time.realtimeSinceStartup < until) yield return null;
            try
            {
                fetching = false;
                if (!www.isDone) { Plugin.Log.LogInfo($"Update check: no answer from {url}; skipped"); yield break; }
                if (!string.IsNullOrEmpty(www.error)) { Plugin.Log.LogInfo($"Update check: {url}: {www.error}; skipped"); yield break; }
                var latest = (www.text ?? "").Trim();
                if (!TryParse(latest, out var remote) || !TryParse(Plugin.Version, out var mine))
                {
                    Plugin.Log.LogInfo($"Update check: {url} didn't return a version number; skipped");
                    yield break;
                }
                if (remote > mine)
                {
                    newer = latest;
                    Plugin.Log.LogInfo($"Update check: {latest} is available (this is {Plugin.Version})");
                    if (Plugin.AutoUpdate.Value)
                    {
                        downloading = true;
                        Plugin.Instance.StartCoroutine(Download(latest));
                    }
                }
                else
                    Plugin.Log.LogInfo($"Update check: up to date ({Plugin.Version}; latest {latest})");
                if (!downloading && !nativeTried)
                {
                    nativeTried = true;
                    foreach (var native in NativeFiles)
                        if (!File.Exists(NativePath(native))) Plugin.Instance.StartCoroutine(FetchNative(Plugin.Version, native));
                }
                // Our ship models (BepInEx/ships): any of this version's that's missing, once a launch.
                if (!downloading && !shipsTried)
                {
                    shipsTried = true;
                    Plugin.Instance.StartCoroutine(SyncShips(Plugin.Version, false));
                }
            }
            finally
            {
                www.Dispose();
            }
        }

        private static IEnumerator Download(string version)
        {
            var got = new Dictionary<string, byte[]>();
            string sums = null;
            foreach (var file in new[] { "SHA256SUMS" }.Concat(Files))
            {
                var www = new WWW(Releases + version + "/" + file);
                float until = Time.realtimeSinceStartup + DownloadSeconds;
                while (!www.isDone && Time.realtimeSinceStartup < until) yield return null;
                string problem = !www.isDone ? "no answer" : !string.IsNullOrEmpty(www.error) ? www.error : null;
                if (problem == null)
                {
                    if (file == "SHA256SUMS") sums = www.text;
                    else got[file] = www.bytes;
                }
                www.Dispose();
                if (problem != null)
                {
                    Plugin.Log.LogInfo($"Update: {version}'s {file}: {problem}; offering the website instead");
                    downloading = false;
                    yield break;
                }
            }
            // The native libraries too, if this release has them (older ones don't): each its own try, as they're optional.
            var natives = new Dictionary<string, byte[]>();
            foreach (var file in NativeFiles)
            {
                var www = new WWW(Releases + version + "/" + file);
                float until = Time.realtimeSinceStartup + DownloadSeconds;
                while (!www.isDone && Time.realtimeSinceStartup < until) yield return null;
                if (www.isDone && string.IsNullOrEmpty(www.error)) natives[file] = www.bytes;
                else Plugin.Log.LogInfo($"Update: {version}'s {file}: {(www.isDone ? www.error : "no answer")}; {NativeUse(file)} stays as it is");
                www.Dispose();
            }
            try
            {
                Install(version, sums, got);
                installed = version;
                Plugin.Log.LogInfo($"Update: {version} installed; it runs from the next launch");
                foreach (var kv in natives) InstallNative(version, sums, kv.Key, kv.Value);
            }
            catch (Exception e)
            {
                Plugin.Log.LogInfo($"Update: {version} not installed ({e.Message}); offering the website instead");
            }
            // The ship models: those that are new or changed in this version (an older release has none listed).
            if (installed == version) yield return SyncShips(version, true);
            downloading = false;
        }

        // Checked against the release's SHA256SUMS, then swapped in: both files or neither.
        private static void Install(string version, string sums, Dictionary<string, byte[]> got)
        {
            var expected = ParseSums(sums);
            foreach (var file in Files)
            {
                if (!expected.TryGetValue(file, out var hash) || got[file] == null || got[file].Length == 0) throw new Exception($"no checksum or no data for {file}");
                if (Hash(got[file]) != hash) throw new Exception($"{file} doesn't match its checksum");
            }
            var folder = PluginFolder;
            foreach (var file in Files) File.WriteAllBytes(Path.Combine(folder, file) + ".new", got[file]);
            var moved = new List<string>();
            try
            {
                foreach (var file in Files)
                {
                    var live = Path.Combine(folder, file);
                    if (File.Exists(live + ".old")) File.Delete(live + ".old");
                    File.Move(live, live + ".old");
                    moved.Add(live);
                    File.Move(live + ".new", live);
                }
            }
            catch
            {
                foreach (var live in moved)
                {
                    try
                    {
                        if (File.Exists(live)) File.Delete(live);
                        File.Move(live + ".old", live);
                    }
                    catch (Exception e) { Plugin.Log.LogWarning($"Update: couldn't put {live} back: {e.Message}"); }
                }
                throw;
            }
            finally
            {
                foreach (var file in Files)
                    try { File.Delete(Path.Combine(folder, file) + ".new"); } catch { }
            }
        }

        private static Dictionary<string, string> ParseSums(string sums)
        {
            var expected = new Dictionary<string, string>();
            foreach (var line in (sums ?? "").Split('\n'))
            {
                var parts = line.Trim().Split(new[] { ' ', '*' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2) expected[parts[1]] = parts[0].ToLowerInvariant();
            }
            return expected;
        }

        private static string Hash(byte[] data)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
        }

        // ---- ship models ----
        // Our ship classes' models live in BepInEx/ships (the installers copy them from the zip). A release lists them
        // in SHA256SUMS as "ships/<ship>/<file>", each uploaded as an asset of its own named with dots for the slashes
        // ("ships.Nova.Nova.obj"), as Unity 5.4's .NET can't open the zip. Missing ones are fetched at launch; on an
        // update, changed ones too (their hashes worked out on a thread: the Nova's model is 46 MB).

        private static bool shipsTried;
        private const float ShipSeconds = 900f; // a large model on a slow line
        private static string ShipsRoot => Path.Combine(Paths.BepInExRootPath, "ships");

        // A path from SHA256SUMS that's one of ours and safe to write: ships/<ship>/<file>, plain names only.
        private static bool IsShipPath(string path)
        {
            if (!path.StartsWith("ships/", StringComparison.Ordinal) || path.Contains("..") || path.Contains("\\")) return false;
            var parts = path.Split('/');
            if (parts.Length != 3) return false;
            foreach (char c in path) if (!(char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '-' || c == '/')) return false;
            return parts[1].Length > 0 && parts[2].Length > 0;
        }

        private static IEnumerator SyncShips(string version, bool checkChanged)
        {
            string sums = null;
            {
                var www = new WWW(Releases + version + "/SHA256SUMS");
                float until = Time.realtimeSinceStartup + DownloadSeconds;
                while (!www.isDone && Time.realtimeSinceStartup < until) yield return null;
                if (www.isDone && string.IsNullOrEmpty(www.error)) sums = www.text;
                www.Dispose();
            }
            if (sums == null) yield break; // offline or no such release: nothing to do this time
            var wanted = new List<KeyValuePair<string, string>>();
            foreach (var kv in ParseSums(sums)) if (IsShipPath(kv.Key)) wanted.Add(kv);
            if (wanted.Count == 0) yield break;

            var todo = new List<KeyValuePair<string, string>>();
            foreach (var kv in wanted)
            {
                var local = Path.Combine(ShipsRoot, kv.Key.Substring("ships/".Length).Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(local)) { todo.Add(kv); continue; }
                if (!checkChanged) continue;
                string have = null; bool done = false;
                var thread = new System.Threading.Thread(() =>
                {
                    try { using (var f = File.OpenRead(local)) using (var sha = SHA256.Create()) have = BitConverter.ToString(sha.ComputeHash(f)).Replace("-", "").ToLowerInvariant(); }
                    catch { }
                    done = true;
                }) { IsBackground = true, Priority = System.Threading.ThreadPriority.BelowNormal };
                thread.Start();
                while (!done) yield return null;
                if (have != kv.Value) todo.Add(kv);
            }
            if (todo.Count == 0) yield break;
            Plugin.Log.LogInfo($"Update: fetching {todo.Count} ship file(s) of {version}: {string.Join(", ", todo.Select(t => t.Key).ToArray())}");
            foreach (var kv in todo)
            {
                var www = new WWW(Releases + version + "/" + kv.Key.Replace('/', '.'));
                float until = Time.realtimeSinceStartup + ShipSeconds;
                while (!www.isDone && Time.realtimeSinceStartup < until) yield return null;
                string problem = !www.isDone ? "no answer" : !string.IsNullOrEmpty(www.error) ? www.error : null;
                byte[] data = problem == null ? www.bytes : null;
                www.Dispose();
                if (problem != null) { Plugin.Log.LogInfo($"Update: {kv.Key}: {problem}; tried again next launch"); continue; }
                try
                {
                    if (data == null || data.Length == 0 || Hash(data) != kv.Value) throw new Exception("it doesn't match its checksum");
                    var local = Path.Combine(ShipsRoot, kv.Key.Substring("ships/".Length).Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(local));
                    File.WriteAllBytes(local + ".new", data);
                    if (File.Exists(local)) File.Delete(local);
                    File.Move(local + ".new", local);
                    Plugin.Log.LogInfo($"Update: {kv.Key} put in place ({data.Length / 1024} KB); used from the next launch");
                }
                catch (Exception e) { Plugin.Log.LogInfo($"Update: {kv.Key} not installed ({e.Message})"); }
                data = null;
            }
        }

        // This version's native library, when it's missing: from this version's release, checked against its
        // SHA256SUMS. A release without it (rnnoise.dll before 0.7.36, speexdsp.dll before echo cancellation) just
        // means what it does stays off.
        private static IEnumerator FetchNative(string version, string native)
        {
            string sums = null;
            byte[] data = null;
            foreach (var file in new[] { "SHA256SUMS", native })
            {
                var www = new WWW(Releases + version + "/" + file);
                float until = Time.realtimeSinceStartup + DownloadSeconds;
                while (!www.isDone && Time.realtimeSinceStartup < until) yield return null;
                string problem = !www.isDone ? "no answer" : !string.IsNullOrEmpty(www.error) ? www.error : null;
                if (problem == null) { if (file == native) data = www.bytes; else sums = www.text; }
                www.Dispose();
                if (problem != null)
                {
                    Plugin.Log.LogInfo($"Update: {native} is missing, and {version}'s {file} couldn't be fetched ({problem}); {NativeUse(native)} stays off");
                    yield break;
                }
            }
            InstallNative(version, sums, native, data);
        }

        // Checked, then put in BepInEx/native: a copy already there (loaded, perhaps) is moved aside to .old first,
        // deleted at the next start.
        private static void InstallNative(string version, string sums, string file, byte[] data)
        {
            var path = NativePath(file);
            try
            {
                if (data == null || data.Length == 0) throw new Exception("no data");
                if (!ParseSums(sums).TryGetValue(file, out var hash)) throw new Exception("no checksum for it in SHA256SUMS");
                if (Hash(data) != hash) throw new Exception("it doesn't match its checksum");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path + ".new", data);
                if (File.Exists(path))
                {
                    if (File.Exists(path + ".old")) File.Delete(path + ".old");
                    File.Move(path, path + ".old");
                }
                File.Move(path + ".new", path);
                Plugin.Log.LogInfo($"Update: {version}'s {file} put in {Path.GetDirectoryName(path)}; {NativeUse(file)} from the next launch");
            }
            catch (Exception e)
            {
                Plugin.Log.LogInfo($"Update: {version}'s {file} not installed ({e.Message}); {NativeUse(file)} stays as it is");
                try { File.Delete(path + ".new"); } catch { }
            }
        }

        private static void Offer()
        {
            offered = true;
            if (installed != null)
            {
                OfferRestart();
                return;
            }
            try
            {
                UI.PopupMgr.ShowPopup(
                    Translation.T($"Bridge Crew Revival {newer} is available. You have {Plugin.Version}.\n\n" +
                    "Open the website to update? This closes the game; run the installer again from there."),
                    new UI.LGNBEBILNCL(yes =>
                    {
                        if (!yes) return;
                        Plugin.Log.LogInfo("Update check: opening the website and closing the game");
                        Application.OpenURL(Website);
                        Application.Quit();
                    }),
                    UI.PDHCCBFJODN.Question, true);
            }
            catch (Exception e)
            {
                Plugin.Log.LogInfo("Update check: couldn't show the popup: " + e.Message);
            }
        }

        private static void OfferRestart()
        {
            try
            {
                UI.PopupMgr.ShowPopup(
                    Translation.T($"Bridge Crew Revival has been updated to {installed}. It starts the next time you launch the game.\n\n" +
                    "Close the game now?"),
                    new UI.LGNBEBILNCL(yes =>
                    {
                        if (!yes) return;
                        Plugin.Log.LogInfo("Update: closing the game to start the new version");
                        Application.Quit();
                    }),
                    UI.PDHCCBFJODN.Question, true);
            }
            catch (Exception e)
            {
                Plugin.Log.LogInfo("Update: couldn't show the popup: " + e.Message);
            }
        }

        // "0.6.5" style versions; anything else isn't a version.
        private static bool TryParse(string text, out Version version)
        {
            version = null;
            if (string.IsNullOrEmpty(text) || text.Length > 20) return false;
            foreach (var c in text) if (c != '.' && (c < '0' || c > '9')) return false;
            try { version = new Version(text); return true; }
            catch { return false; }
        }
    }
}
