using System;
using System.Collections;
using System.IO;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using Preferences = SVR.Network.IOGFEJEOLBA;

namespace StbcRevival
{
    /// <summary>
    /// [Import] FetchUbisoftSave: a one-shot run that copies the player's Ubisoft cloud save to the local save.
    /// The installer sets it and launches the game. For that run the mod leaves the Ubisoft login on; the
    /// game asks Ubisoft for its "preferences" record as soon as it's created and gets either the record or
    /// nothing back (MFKKMDCIEJA.OANAMJIDBOO). The mod then writes the local save, records the outcome in
    /// BepInEx/config/stbc.revival.import.txt for the installer, switches the option off and quits the game.
    /// </summary>
    internal static class UbisoftSaveImport
    {
        private const float TimeoutSeconds = 300f;
        internal static string ResultPath => Path.Combine(Paths.ConfigPath, "stbc.revival.import.txt");

        internal static bool Active => Plugin.FetchUbisoftSave.Value;

        private static float quitAt = -1f;
        private static bool done;

        internal static void Patch(Harmony harmony)
        {
            if (!Active) return;
            Plugin.Log.LogInfo("Ubisoft save import: this run logs in to Ubisoft to copy your save, then quits");
            // With the Ubisoft login on, this copy of the game hands over to Ubisoft Connect, which starts a
            // new one that inherits this process's environment. Doorstop (BepInEx's loader) marks the
            // environment once it has loaded and then skips any process carrying the mark, so the new copy
            // would start without the mod. Clear the mark so it loads there too.
            Environment.SetEnvironmentVariable("DOORSTOP_INITIALIZED", null);
            Environment.SetEnvironmentVariable("DOORSTOP_DISABLE", null);
            harmony.Patch(AccessTools.Method(typeof(SVR.Network.MFKKMDCIEJA), "OANAMJIDBOO"), // online search result
                postfix: new HarmonyMethod(typeof(UbisoftSaveImport), nameof(SearchResult_Postfix)));
        }

        private static void SearchResult_Postfix(SVR.Network.MFKKMDCIEJA __instance, Hashtable MIACDBOANPI)
        {
            if (!Active || done || !(__instance is Preferences prefs)) return;
            if (MIACDBOANPI == null)
                Finish("none", "Your Ubisoft account has no Bridge Crew save to import.");
            else if (LocalSave.SaveNow(prefs) is int count)
                Finish("ok", $"Imported {count} saved setting(s) from Ubisoft.");
            else
                Finish("failed", "The Ubisoft save arrived but couldn't be written to disk; see the log.");
        }

        internal static void Update()
        {
            if (!Active && quitAt < 0f) return;
            if (!done && Time.realtimeSinceStartup > TimeoutSeconds)
                Finish("failed", "No answer from Ubisoft within 5 minutes. Is Ubisoft Connect running and signed in?");
            if (quitAt >= 0f && Time.realtimeSinceStartup >= quitAt)
            {
                quitAt = -1f;
                Plugin.Log.LogInfo("Ubisoft save import: quitting the game");
                // In Ubisoft mode the game's shutdown can stall (Ubisoft Connect sign-out), leaving it at the
                // menu. Everything is saved by now, so end the process if the normal quit hasn't after 10 s.
                new System.Threading.Thread(() =>
                {
                    System.Threading.Thread.Sleep(10000);
                    Plugin.Log.LogInfo("Ubisoft save import: the game didn't close; ending it");
                    System.Diagnostics.Process.GetCurrentProcess().Kill();
                }) { IsBackground = true }.Start();
                Application.Quit();
            }
        }

        private static void Finish(string status, string message)
        {
            done = true;
            Plugin.Log.LogInfo($"Ubisoft save import: {status}: {message}");
            try { File.WriteAllText(ResultPath, status + "\n" + message + "\n"); }
            catch (Exception e) { Plugin.Log.LogWarning("Ubisoft save import: couldn't write result: " + e.Message); }
            Plugin.FetchUbisoftSave.Value = false; // one run only; BepInEx saves the config
            RoomBrowser.Show(message + " The game will close in a few seconds.");
            quitAt = Time.realtimeSinceStartup + 5f;
        }
    }
}
