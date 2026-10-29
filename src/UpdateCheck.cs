using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEngine;

namespace StbcRevival
{
    /// <summary>
    /// [Updates] CheckForUpdates: once per launch, after reaching the shuttle, read the latest version number
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
        private const float DownloadSeconds = 120f;

        private static bool started, offered, downloading;
        private static string newer, installed;
        private static float onShuttleSince = -1f;

        internal static void Update()
        {
            if (!Plugin.CheckForUpdates.Value || UbisoftSaveImport.Active) return;
            bool onShuttle = GameState.Phase() == "GamePhase_MainMenu" && !GameState.Loading()
                             && GameState.ConnectedAndReady() && GameState.RoomName() == null;
            if (!onShuttle)
            {
                onShuttleSince = -1f;
                return;
            }
            if (onShuttleSince < 0f) onShuttleSince = Time.realtimeSinceStartup;
            if (!started)
            {
                started = true;
                Plugin.Instance.StartCoroutine(Fetch());
            }
            // Only on the shuttle (never in a briefing room or mission), and only once it has settled: a popup
            // shown while the shuttle console is still loading goes nowhere.
            if (newer != null && !offered && !downloading && Time.realtimeSinceStartup - onShuttleSince >= SettleSeconds) Offer();
        }

        /// <summary>At startup: the DLLs an update replaced last time.</summary>
        internal static void CleanUp()
        {
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
            var www = new WWW(url + (url.Contains("?") ? "&" : "?") + "t=" + DateTime.UtcNow.Ticks);
            float until = Time.realtimeSinceStartup + TimeoutSeconds;
            while (!www.isDone && Time.realtimeSinceStartup < until) yield return null;
            try
            {
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
            try
            {
                Install(version, sums, got);
                installed = version;
                Plugin.Log.LogInfo($"Update: {version} installed; it runs from the next launch");
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
            var expected = new Dictionary<string, string>();
            foreach (var line in (sums ?? "").Split('\n'))
            {
                var parts = line.Trim().Split(new[] { ' ', '*' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2) expected[parts[1]] = parts[0].ToLowerInvariant();
            }
            foreach (var file in Files)
            {
                if (!expected.TryGetValue(file, out var hash) || got[file] == null || got[file].Length == 0) throw new Exception($"no checksum or no data for {file}");
                string actual;
                using (var sha = SHA256.Create()) actual = BitConverter.ToString(sha.ComputeHash(got[file])).Replace("-", "").ToLowerInvariant();
                if (actual != hash) throw new Exception($"{file} doesn't match its checksum");
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
                    $"Bridge Crew Revival {newer} is available. You have {Plugin.Version}.\n\n" +
                    "Open the website to update? This closes the game; run the installer again from there.",
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
                    $"Bridge Crew Revival has been updated to {installed}. It starts the next time you launch the game.\n\n" +
                    "Close the game now?",
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
