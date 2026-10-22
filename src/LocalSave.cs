using System;
using System.Collections;
using System.IO;
using BepInEx;
using HarmonyLib;
using SVR.Network;
using UnityEngine;
using Json = SVR.Network.Utilities.MHCFEEJDKJM;   // the game's JSON helper (JsonToString / StringToJson)
using Preferences = SVR.Network.IOGFEJEOLBA;     // OnlineSave's "preferences" entity

namespace StbcRevival
{
    /// <summary>
    /// [Game] LocalSave: keep the game's saved settings on disk.
    /// Bridge Crew keeps its save data (avatar appearance, options, progress flags: everything behind
    /// SaveFile's AutoDirtySaveVariables) in one "preferences" table that is only ever written to Ubisoft's
    /// online save service; its local save is empty. With Ubisoft gone it resets every launch.
    /// This mirrors that table to BepInEx/config/stbc.revival.save.json: loaded when the table is created
    /// (or on the first frame, if it already exists), written a moment after any change and on quit.
    /// </summary>
    internal static class LocalSave
    {
        private static string FilePath => Path.Combine(Paths.ConfigPath, "stbc.revival.save.json");

        private static readonly AccessTools.FieldRef<Preferences, Hashtable> Table =
            AccessTools.FieldRefAccess<Preferences, Hashtable>("FDJOFPBNIFF");

        private static Preferences loadedInto;
        private static float saveAt = -1f;
        private static float nextCheck;
        private static string lastWritten;

        internal static void Patch(Harmony harmony)
        {
            if (!Plugin.LocalSave.Value) return;
            harmony.Patch(AccessTools.Constructor(typeof(Preferences)), postfix: new HarmonyMethod(typeof(LocalSave), nameof(Created_Postfix)));
            harmony.Patch(AccessTools.Method(typeof(Preferences), nameof(Preferences.Set)), postfix: new HarmonyMethod(typeof(LocalSave), nameof(Set_Postfix)));
        }

        private static void Created_Postfix(Preferences __instance) => Load(__instance);

        private static void Set_Postfix() => saveAt = Time.realtimeSinceStartup + 2f;

        internal static void Update()
        {
            if (!Plugin.LocalSave.Value) return;
            var prefs = Current();
            if (prefs == null) return;
            if (loadedInto != prefs) Load(prefs);
            float now = Time.realtimeSinceStartup;
            // Some values (e.g. the banned-rooms table) are changed in place without Set; check now and then.
            if ((saveAt >= 0f && now >= saveAt) || now >= nextCheck)
            {
                saveAt = -1f;
                nextCheck = now + 30f;
                Save(prefs);
            }
        }

        internal static void OnQuit()
        {
            if (!Plugin.LocalSave.Value) return;
            var prefs = Current();
            if (prefs != null) Save(prefs);
        }

        // The singleton's backing field, not its Instance getter: that creates the object if it doesn't
        // exist yet, which would change the game's start-up order.
        private static readonly System.Reflection.FieldInfo OnlineSaveInstance =
            AccessTools.Field(typeof(SVR.Singleton_Behaviour_Persistent<OnlineSave>), "JKOEGLOEGKD");

        private static Preferences Current()
        {
            var online = OnlineSaveInstance?.GetValue(null) as OnlineSave;
            return online != null ? online.IOGFEJEOLBA : null;
        }

        private static void Load(Preferences prefs)
        {
            loadedInto = prefs;
            try
            {
                if (!File.Exists(FilePath))
                {
                    Plugin.Log.LogInfo($"Local save: none yet ({FilePath} is created on the first change)");
                    return;
                }
                var json = File.ReadAllText(FilePath);
                var saved = Json.StringToJson<Hashtable>(json);
                if (saved == null) return;
                var table = Table(prefs);
                foreach (DictionaryEntry e in saved) table[e.Key] = e.Value;
                lastWritten = json;
                Plugin.Log.LogInfo($"Local save: loaded {saved.Count} saved setting(s)");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Local save: couldn't read {FilePath}: {e.Message}");
            }
        }

        /// <summary>Write the table now, even if unchanged; returns how many settings were written, or null.</summary>
        internal static int? SaveNow(Preferences prefs) => Save(prefs, force: true) ? Table(prefs).Count : (int?)null;

        private static bool Save(Preferences prefs, bool force = false)
        {
            try
            {
                var json = Json.JsonToString(Table(prefs));
                if (string.IsNullOrEmpty(json)) return false;
                if (json == lastWritten && !force) return true;
                var temp = FilePath + ".tmp";
                File.WriteAllText(temp, json);
                if (File.Exists(FilePath)) File.Copy(FilePath, FilePath + ".bak", true);
                File.Copy(temp, FilePath, true);
                File.Delete(temp);
                if (lastWritten == null) Plugin.Log.LogInfo($"Local save: writing settings to {FilePath}");
                lastWritten = json;
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Local save: couldn't write {FilePath}: {e.Message}");
                return false;
            }
        }
    }
}
