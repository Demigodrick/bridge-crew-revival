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
        // Revival voice's noise suppression (native, not a plugin): in BepInEx/native, where the installer puts it. The
        // updater brings it with an update, and fetches this version's when it's missing (a player who only ever
        // updated in game never had it). Optional: without it noise suppression is just off.
        private const string NativeFile = "rnnoise.dll";
        private static string NativePath => Path.Combine(Path.Combine(Paths.BepInExRootPath, "native"), NativeFile);
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
            try { if (File.Exists(NativePath + ".old")) File.Delete(NativePath + ".old"); }
            catch (Exception e) { Plugin.Log.LogInfo($"Update: couldn't delete {NativePath}.old: {e.Message}"); }
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
                if (!downloading && !nativeTried && !File.Exists(NativePath))
                {
                    nativeTried = true;
                    Plugin.Instance.StartCoroutine(FetchNative(Plugin.Version));
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
            // The noise suppression library too, if this release has it (older ones don't): its own try, as it's optional.
            byte[] native = null;
            {
                var www = new WWW(Releases + version + "/" + NativeFile);
                float until = Time.realtimeSinceStartup + DownloadSeconds;
                while (!www.isDone && Time.realtimeSinceStartup < until) yield return null;
                if (www.isDone && string.IsNullOrEmpty(www.error)) native = www.bytes;
                else Plugin.Log.LogInfo($"Update: {version}'s {NativeFile}: {(www.isDone ? www.error : "no answer")}; noise suppression stays as it is");
                www.Dispose();
            }
            try
            {
                Install(version, sums, got);
                installed = version;
                Plugin.Log.LogInfo($"Update: {version} installed; it runs from the next launch");
                if (native != null) InstallNative(version, sums, native);
            }
            catch (Exception e)
            {
                Plugin.Log.LogInfo($"Update: {version} not installed ({e.Message}); offering the website instead");
            }
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

        // This version's noise suppression library, when it's missing: from this version's release, checked against
        // its SHA256SUMS. A release without it (before 0.7.36) just means it stays off.
        private static IEnumerator FetchNative(string version)
        {
            string sums = null;
            byte[] data = null;
            foreach (var file in new[] { "SHA256SUMS", NativeFile })
            {
                var www = new WWW(Releases + version + "/" + file);
                float until = Time.realtimeSinceStartup + DownloadSeconds;
                while (!www.isDone && Time.realtimeSinceStartup < until) yield return null;
                string problem = !www.isDone ? "no answer" : !string.IsNullOrEmpty(www.error) ? www.error : null;
                if (problem == null) { if (file == NativeFile) data = www.bytes; else sums = www.text; }
                www.Dispose();
                if (problem != null)
                {
                    Plugin.Log.LogInfo($"Update: {NativeFile} is missing, and {version}'s {file} couldn't be fetched ({problem}); noise suppression stays off");
                    yield break;
                }
            }
            InstallNative(version, sums, data);
        }

        // Checked, then put in BepInEx/native: a copy already there (loaded, perhaps) is moved aside to .old first,
        // deleted at the next start.
        private static void InstallNative(string version, string sums, byte[] data)
        {
            try
            {
                if (data == null || data.Length == 0) throw new Exception("no data");
                if (!ParseSums(sums).TryGetValue(NativeFile, out var hash)) throw new Exception("no checksum for it in SHA256SUMS");
                if (Hash(data) != hash) throw new Exception("it doesn't match its checksum");
                var path = NativePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path + ".new", data);
                if (File.Exists(path))
                {
                    if (File.Exists(path + ".old")) File.Delete(path + ".old");
                    File.Move(path, path + ".old");
                }
                File.Move(path + ".new", path);
                Plugin.Log.LogInfo($"Update: {version}'s {NativeFile} put in {Path.GetDirectoryName(path)}; noise suppression from the next launch");
            }
            catch (Exception e)
            {
                Plugin.Log.LogInfo($"Update: {version}'s {NativeFile} not installed ({e.Message}); noise suppression stays as it is");
                try { File.Delete(NativePath + ".new"); } catch { }
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
