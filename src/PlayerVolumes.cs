using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace StbcRevival
{
    /// <summary>
    /// Muting a crewmate (issue #9): the escape menu's player options have a voice volume slider for each player
    /// (PlayerListUI → VoIpManager.SetVolumeForPlayer, which scales their voice chat on your end only), but the
    /// game stops it at 30% (VoIpManager._minPlayerVolume, and the slider's own SliderMinValue). Here it goes down
    /// to zero, which is silence, and each player's volume is remembered by their Steam ID ([Players] VoiceVolumes)
    /// and given back whenever they're in a crew with you again; the game forgets it when they leave.
    /// The Steam ID is the one every mod player shares in their player properties (HostHandoff's "SID"), so
    /// a player without the mod can be turned down for the session but isn't remembered.
    /// </summary>
    internal static class PlayerVolumes
    {
        private const float Muted = 0.02f; // the slider's bottom end, near enough: silence

        private static readonly AccessTools.FieldRef<VoIpManager, float> MinVolume =
            AccessTools.FieldRefAccess<VoIpManager, float>("_minPlayerVolume");
        private static readonly AccessTools.FieldRef<VoIpManager, IDictionary> VoipPlayers =
            AccessTools.FieldRefAccess<VoIpManager, IDictionary>("PFKNMPPKOFP"); // actor number → VoIpManager.PIHAFAACGLJ
        private static readonly AccessTools.FieldRef<PlayerListUI, SliderInteraction> Slider =
            AccessTools.FieldRefAccess<PlayerListUI, SliderInteraction>("_voiceVolumeInteractionSlider");

        // Players whose saved volume has been given back: actor number → Steam ID (a new actor number is a new join).
        private static readonly Dictionary<int, ulong> restored = new Dictionary<int, ulong>();
        private static float nextCheck;

        internal static void Patch(Harmony harmony)
        {
            var set = AccessTools.Method(typeof(VoIpManager), "SetVolumeForPlayer");
            var open = AccessTools.Method(typeof(PlayerListUI), "IIGLKLICKDJ"); // a player picked: their options page
            if (set == null || open == null)
            {
                Plugin.Log.LogWarning("Player volumes: the voice volume methods weren't found; the slider stops at 30% as before");
                return;
            }
            harmony.Patch(set, prefix: new HarmonyMethod(typeof(PlayerVolumes), nameof(Set_Prefix)),
                postfix: new HarmonyMethod(typeof(PlayerVolumes), nameof(Set_Postfix)));
            harmony.Patch(open, prefix: new HarmonyMethod(typeof(PlayerVolumes), nameof(Open_Prefix)));
        }

        // ---- The slider ---------------------------------------------------------------------------

        private static void Open_Prefix(PlayerListUI __instance)
        {
            var slider = Slider(__instance);
            if (slider != null) slider.SliderMinValue = 0f;
        }

        private static void Set_Prefix(VoIpManager __instance, ref float __1)
        {
            MinVolume(__instance) = 0f;
            if (__1 < Muted) __1 = 0f;
        }

        // Every change but our own restoring comes from the slider.
        private static bool restoring;

        private static void Set_Postfix(object __0, float __1)
        {
            if (restoring) return;
            ulong id = SteamId(__0);
            if (id == 0) return;
            var list = Entries();
            float before = list.TryGetValue(id, out var v) ? v : 1f;
            if (__1 >= 0.999f) list.Remove(id);
            else list[id] = __1;
            Save(list);
            if ((before == 0f) != (__1 == 0f))
                Plugin.Log.LogInfo($"Player volumes: {Name(__0)} ({id}) {(__1 == 0f ? "muted" : "unmuted")}");
        }

        // ---- Giving saved volumes back ------------------------------------------------------------

        internal static void Update()
        {
            if (Time.realtimeSinceStartup < nextCheck) return;
            nextCheck = Time.realtimeSinceStartup + 1f;
            var voip = VoIpManager.IEFBKOMBFGO;
            if (voip == null) return;
            var players = VoipPlayers(voip);
            if (players == null) return;
            var saved = Entries();
            foreach (DictionaryEntry kv in players)
            {
                if (!(kv.Key is int actor) || !(kv.Value is VoIpManager.PIHAFAACGLJ info) || info.Player == null) continue;
                ulong id = SteamId(info.Player); // shared a moment after joining, so looked for until it's there
                if (id == 0 || (restored.TryGetValue(actor, out var done) && done == id)) continue;
                restored[actor] = id;
                if (!saved.TryGetValue(id, out var volume)) continue;
                restoring = true;
                try { voip.SetVolumeForPlayer(info.Player, volume); }
                finally { restoring = false; }
                Plugin.Log.LogInfo($"Player volumes: {Name(info.Player)} ({id}) {(volume == 0f ? "muted" : "at " + Mathf.RoundToInt(volume * 100) + "%")}, as you left them");
            }
            if (restored.Count > players.Count)
                foreach (var gone in restored.Keys.Where(a => !players.Contains(a)).ToList()) restored.Remove(gone);
        }

        // ---- The list -----------------------------------------------------------------------------

        // "steamid=volume;steamid=volume", volumes 0 (muted) to 1; players at full volume aren't kept.
        private static Dictionary<ulong, float> Entries()
        {
            var list = new Dictionary<ulong, float>();
            foreach (var item in (Plugin.VoiceVolumes.Value ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = item.IndexOf('=');
                if (eq > 0 && ulong.TryParse(item.Substring(0, eq).Trim(), out var id)
                    && float.TryParse(item.Substring(eq + 1).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var volume))
                    list[id] = Mathf.Clamp01(volume);
            }
            return list;
        }

        private static void Save(Dictionary<ulong, float> list) =>
            Plugin.VoiceVolumes.Value = string.Join(";", list.Select(kv => kv.Key + "=" + kv.Value.ToString("0.##", CultureInfo.InvariantCulture)).ToArray());

        private static ulong SteamId(object player)
        {
            var props = player?.GetType().GetMethod("get_customProperties")?.Invoke(player, null) as IDictionary;
            return props != null && props.Contains(HostHandoff.SteamIdKey) && ulong.TryParse(props[HostHandoff.SteamIdKey] as string, out var id) ? id : 0;
        }

        private static string Name(object player) => player?.GetType().GetMethod("get_name")?.Invoke(player, null) as string ?? "a player";
    }
}
