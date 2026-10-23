using System;
using System.Collections;
using UnityEngine;

namespace StbcRevival
{
    /// <summary>
    /// [Updates] CheckForUpdates: once per launch, after reaching the shuttle, read the latest version number
    /// from the website (version.txt, a single line written by tools/release.sh). If it's newer than this
    /// mod, the game's own popup offers to open the website and close the game; nothing is downloaded.
    /// Any problem (site down, no internet, an unexpected reply, a slow answer) is logged and otherwise
    /// ignored, so it never gets in the way of playing.
    /// </summary>
    internal static class UpdateCheck
    {
        private const string DefaultUrl = "https://demigodrick.github.io/bridge-crew-revival/version.txt";
        internal const string Website = "https://demigodrick.github.io/bridge-crew-revival/";
        private const float TimeoutSeconds = 15f;

        private const float SettleSeconds = 10f;

        private static bool started, offered;
        private static string newer;
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
            if (newer != null && !offered && Time.realtimeSinceStartup - onShuttleSince >= SettleSeconds) Offer();
        }

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
                }
                else
                    Plugin.Log.LogInfo($"Update check: up to date ({Plugin.Version}; latest {latest})");
            }
            finally
            {
                www.Dispose();
            }
        }

        private static void Offer()
        {
            offered = true;
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
