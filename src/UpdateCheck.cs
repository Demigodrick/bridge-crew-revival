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

        // On the shuttle this long before the popup: the game's popup manager comes with the shuttle's menu (and
        // ShowPopup says when it isn't there yet), so only a moment, for the view to come up from the load.
        private const float SettleSeconds = 1f;
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
            // With AutoUpdate, as soon as the DLLs are in: the ship models may still be checked and fetched after it
            // (closing the game then is fine: the first launch after an in-game update checks them in full).
            if (newer != null && !offered && (!downloading || installed != null)
                && Time.realtimeSinceStartup - onShuttleSince >= SettleSeconds)
                Offer();
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
                if (File.Exists(old)) updatedInGame = true;
                try { if (File.Exists(old)) File.Delete(old); }
                catch (Exception e) { Plugin.Log.LogInfo($"Update: couldn't delete {old}: {e.Message}"); }
            }
        }

        // This launch is the first since the in-game updater replaced the DLLs (it left the old ones as .old). Not
        // for a build put in place by hand, or by an installer (which copies every file from the zip).
        private static bool updatedInGame;

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
                // The first launch after an in-game update, by an updater that didn't check this version's files in
                // full (0.7.46 and older): that check now. It brings any missing native library too.
                bool fullCheck = updatedInGame && !Synced(Plugin.Version);
                if (!downloading && !nativeTried && !fullCheck)
                {
                    nativeTried = true;
                    foreach (var native in NativeFiles)
                        if (!File.Exists(NativePath(native))) Plugin.Instance.StartCoroutine(FetchNative(Plugin.Version, native));
                }
                // Our ship models (BepInEx/ships) and galaxy file (BepInEx/galaxies): any of this version's that's missing, once a launch.
                if (!downloading && !shipsTried)
                {
                    shipsTried = true;
                    Plugin.Instance.StartCoroutine(SyncShips(Plugin.Version, fullCheck));
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
            string sums = null, problem = null;
            yield return FetchSums(version, (text, p) => { sums = text; problem = p; });
            if (problem != null)
            {
                Plugin.Log.LogInfo($"Update: {version}'s SHA256SUMS: {problem}; offering the website instead");
                downloading = false;
                yield break;
            }
            foreach (var file in Files)
            {
                byte[] data = null;
                yield return GetFile(version, "plugins/" + file, file, (b, p) => { data = b; problem = p; });
                if (problem != null)
                {
                    Plugin.Log.LogInfo($"Update: {version}'s {file}: {problem}; offering the website instead");
                    downloading = false;
                    yield break;
                }
                got[file] = data;
            }
            // The native libraries too, if this release has them (older ones don't): each its own try, as they're optional.
            var natives = new Dictionary<string, byte[]>();
            foreach (var file in NativeFiles)
            {
                byte[] data = null;
                yield return GetFile(version, "native/" + file, file, (b, p) => { data = b; problem = p; });
                if (problem == null) natives[file] = data;
                else Plugin.Log.LogInfo($"Update: {version}'s {file}: {problem}; {NativeUse(file)} stays as it is");
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

        // ---- ship models and the second sector ----
        // Our ship classes' models live in BepInEx/ships, the second sector's galaxy file in BepInEx/galaxies (the
        // installers copy them from the zip). A release lists them in SHA256SUMS as "ships/<ship>/<file>" and
        // "galaxies/<file>.json", and they're fetched from the release's zip (ReleaseZip); up to 0.7.46 each was an
        // asset of its own named with dots for the slashes ("ships.Nova.Nova.obj"), still tried when the zip can't
        // be read. Missing ones are fetched at launch; on an
        // update, changed ones too (their hashes worked out on a thread: the Nova's model is 46 MB).

        private static bool shipsTried;
        private const float ShipSeconds = 900f; // a large model on a slow line
        private static string LocalPath(string path) => Path.Combine(Paths.BepInExRootPath, path.Replace('/', Path.DirectorySeparatorChar));

        // A path from SHA256SUMS that's one of ours and safe to write, plain names only: ships/<ship>/<file> or
        // galaxies/<file>.json.
        internal static bool IsAssetPath(string path)
        {
            if (path.Contains("..") || path.Contains("\\")) return false;
            foreach (char c in path) if (!(char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '-' || c == '/')) return false;
            var parts = path.Split('/');
            foreach (var part in parts) if (part.Length == 0) return false;
            if (parts[0] == "ships") return parts.Length == 3;
            if (parts[0] == "galaxies") return parts.Length == 2 && parts[1].EndsWith(".json", StringComparison.Ordinal);
            return false;
        }

        private static IEnumerator SyncShips(string version, bool checkChanged)
        {
            string sums = null;
            yield return FetchSums(version, (text, p) => sums = text);
            if (sums == null) yield break; // offline or no such release: nothing to do this time
            var wanted = new List<KeyValuePair<string, string>>();
            var all = ParseSums(sums);
            foreach (var kv in all) if (IsAssetPath(kv.Key)) wanted.Add(kv);
            // The full check (once a version) takes in the native libraries too: an older updater that brought this
            // version may have left changed ones behind (it fetched each from an asset of its own, which releases
            // no longer have).
            if (checkChanged)
                foreach (var native in NativeFiles)
                    if (all.TryGetValue(native, out var nativeHash)) wanted.Add(new KeyValuePair<string, string>(native, nativeHash));
            if (wanted.Count == 0) { if (checkChanged) MarkSynced(version); yield break; }

            var todo = new List<KeyValuePair<string, string>>();
            foreach (var kv in wanted)
            {
                var local = Local(kv.Key);
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
            if (todo.Count == 0) { if (checkChanged) MarkSynced(version); yield break; }
            Plugin.Log.LogInfo($"Update: fetching {todo.Count} file(s) of {version}: {string.Join(", ", todo.Select(t => t.Key).ToArray())}");
            bool allDone = true;
            foreach (var kv in todo)
            {
                byte[] data = null; string problem = null;
                bool native = Array.IndexOf(NativeFiles, kv.Key) >= 0;
                yield return GetFile(version, native ? "native/" + kv.Key : kv.Key, kv.Key.Replace('/', '.'), (b, p) => { data = b; problem = p; });
                if (problem != null) { allDone = false; Plugin.Log.LogInfo($"Update: {kv.Key}: {problem}; tried again next launch"); continue; }
                if (native)
                {
                    if (!InstallNative(version, sums, kv.Key, data)) allDone = false;
                    data = null;
                    continue;
                }
                try
                {
                    if (data == null || data.Length == 0 || Hash(data) != kv.Value) throw new Exception("it doesn't match its checksum");
                    var local = LocalPath(kv.Key);
                    Directory.CreateDirectory(Path.GetDirectoryName(local));
                    File.WriteAllBytes(local + ".new", data);
                    if (File.Exists(local)) File.Delete(local);
                    File.Move(local + ".new", local);
                    Plugin.Log.LogInfo($"Update: {kv.Key} put in place ({data.Length / 1024} KB); used from the next launch");
                }
                catch (Exception e) { allDone = false; Plugin.Log.LogInfo($"Update: {kv.Key} not installed ({e.Message})"); }
                data = null;
            }
            if (checkChanged && allDone) MarkSynced(version);
        }

        private static string Local(string path) => Array.IndexOf(NativeFiles, path) >= 0 ? NativePath(path) : LocalPath(path);

        // Which version's files were last checked in full (ship models, the sector and the native libraries against
        // that release's SHA256SUMS), by the update that brought it or the first launch after: an updater from 0.7.46
        // or older fetched changed files from assets of their own, which releases no longer have.
        private static string SyncedPath => Path.Combine(Paths.CachePath, "revival-files-checked.txt");

        private static bool Synced(string version)
        {
            try { return File.Exists(SyncedPath) && File.ReadAllText(SyncedPath).Trim() == version; }
            catch { return false; }
        }

        private static void MarkSynced(string version)
        {
            try { Directory.CreateDirectory(Paths.CachePath); File.WriteAllText(SyncedPath, version + "\n"); }
            catch (Exception e) { Plugin.Log.LogInfo("Update: couldn't note the files as checked: " + e.Message); }
        }

        // ---- fetching from a release ----

        // The release's zip, opened once a version (its index read); null after a failure, so the files come from
        // the release's own assets, as before 0.7.47.
        private static readonly Dictionary<string, ReleaseZip> zips = new Dictionary<string, ReleaseZip>();

        private static IEnumerator OpenZip(string version)
        {
            if (zips.ContainsKey(version)) yield break;
            var zip = new ReleaseZip(version);
            zips[version] = zip; // a second caller waits on the same one
            yield return zip.Open();
            if (zip.IsOpen) Plugin.Log.LogInfo($"Update: reading {version}'s files from its zip");
            else
            {
                Plugin.Log.LogInfo($"Update: {version}'s zip couldn't be read ({zip.Problem}); fetching its files one by one");
                zips[version] = null;
            }
        }

        // One file of a release: from its zip (zipPath, its path in the release folder), or from an asset of its own
        // (asset) when the zip can't be read. done(bytes, null) or done(null, problem). Not yet checked against
        // SHA256SUMS: the caller does that.
        private static IEnumerator GetFile(string version, string zipPath, string asset, Action<byte[], string> done)
        {
            yield return OpenZip(version);
            var zip = zips[version];
            while (zip != null && !zip.IsOpen && zip.Problem == null) { yield return null; zip = zips[version]; }
            if (zip != null && zip.IsOpen)
            {
                byte[] data = null; string problem = null;
                yield return zip.Get(zipPath, (b, p) => { data = b; problem = p; });
                if (data != null) { done(data, null); yield break; }
                Plugin.Log.LogInfo($"Update: {zipPath} from {version}'s zip: {problem}; trying it on its own");
            }
            var www = new WWW(Releases + version + "/" + asset);
            float until = Time.realtimeSinceStartup + ShipSeconds;
            while (!www.isDone && Time.realtimeSinceStartup < until) yield return null;
            string failed = !www.isDone ? "no answer" : !string.IsNullOrEmpty(www.error) ? www.error : null;
            byte[] bytes = failed == null ? www.bytes : null;
            www.Dispose();
            if (failed == null && (bytes == null || bytes.Length == 0)) failed = "nothing came back";
            done(failed == null ? bytes : null, failed);
        }

        private static IEnumerator FetchSums(string version, Action<string, string> done)
        {
            var www = new WWW(Releases + version + "/SHA256SUMS");
            float until = Time.realtimeSinceStartup + DownloadSeconds;
            while (!www.isDone && Time.realtimeSinceStartup < until) yield return null;
            string problem = !www.isDone ? "no answer" : !string.IsNullOrEmpty(www.error) ? www.error : null;
            string text = problem == null ? www.text : null;
            www.Dispose();
            done(text, problem);
        }

        // This version's native library, when it's missing: from this version's release, checked against its
        // SHA256SUMS. A release without it (rnnoise.dll before 0.7.36, speexdsp.dll before echo cancellation) just
        // means what it does stays off.
        private static IEnumerator FetchNative(string version, string native)
        {
            string sums = null, problem = null;
            byte[] data = null;
            yield return FetchSums(version, (text, p) => { sums = text; problem = p; });
            if (problem == null) yield return GetFile(version, "native/" + native, native, (b, p) => { data = b; problem = p; });
            if (problem != null)
            {
                Plugin.Log.LogInfo($"Update: {native} is missing, and {version}'s couldn't be fetched ({problem}); {NativeUse(native)} stays off");
                yield break;
            }
            InstallNative(version, sums, native, data);
        }

        // Checked, then put in BepInEx/native: a copy already there (loaded, perhaps) is moved aside to .old first,
        // deleted at the next start.
        private static bool InstallNative(string version, string sums, string file, byte[] data)
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
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogInfo($"Update: {version}'s {file} not installed ({e.Message}); {NativeUse(file)} stays as it is");
                try { File.Delete(path + ".new"); } catch { }
                return false;
            }
        }

        // ShowPopup is false when the game has no popup manager yet: tried again next frame.
        private static void Offer()
        {
            offered = installed != null ? OfferRestart() : OfferWebsite();
            if (offered) Plugin.Log.LogInfo($"Update: popup shown {Time.realtimeSinceStartup - onShuttleSince:F1} s after reaching the shuttle");
        }

        private static bool OfferWebsite()
        {
            try
            {
                return UI.PopupMgr.ShowPopup(
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
                return true; // not again every frame
            }
        }

        private static bool OfferRestart()
        {
            try
            {
                return UI.PopupMgr.ShowPopup(
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
                return true; // not again every frame
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
