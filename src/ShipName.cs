using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace StbcRevival
{
    /// <summary>
    /// [Rooms] ShipName: the host names the crew's ship ("USS Cosmo, NCC-91234"), and the ship's name and registry in
    /// the game's own texts become it for the whole crew with the mod: the captain's console header (USS AEGIS
    /// (NX-1787), on the Enterprise-D USS Enterprise (NCC 1701-D)), "USS Aegis" in the briefing room's mission texts.
    /// The host's name goes on the room as a property ("SHN"); out of a room your own is used. Empty: the game's names.
    /// Set in the Revival settings (the desktop prompt, or SteamVR's keyboard in VR) or in the config.
    /// </summary>
    internal static class ShipName
    {
        private const string Key = "SHN";
        private static float nextCheck;
        private static string current = "";   // "Name, REGISTRY" in use, or ""
        private static string name = "", registry = "";

        /// <summary>The ship name in use (the room's, or your own out of a room) and its registry; "" for the game's.</summary>
        internal static string Name => name;
        internal static string Registry => registry;
        /// <summary>"Name, REGISTRY" as in use, or "".</summary>
        internal static string Current => current;

        private static object Room() => Patches.PhotonNetworkType?.GetMethod("get_room", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);

        private static bool IsHost() => Patches.PhotonNetworkType?.GetMethod("get_isMasterClient", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null) is bool m && m;

        /// <summary>
        /// Names the ship: for the room when in one (the briefing room's box, or the host's settings), and as your own
        /// default when hosting or alone. From the settings in someone else's room only your own default changes.
        /// </summary>
        internal static void Set(string value, bool forRoom)
        {
            value = (value ?? "").Trim();
            var room = Room();
            if (room == null || IsHost()) Plugin.ShipName.Value = value;
            if (room != null && forRoom | IsHost()) PhotonProps.Set(room, Key, value);
            if (room == null || forRoom | IsHost()) Use(value);
            nextCheck = Time.unscaledTime + 1f;
        }

        /// <summary>"USS Cosmo, NCC-91234" (a room's SHN) → "USS Cosmo (NCC-91234)"; "" for none.</summary>
        internal static string Format(string value)
        {
            value = (value ?? "").Trim();
            int comma = value.LastIndexOf(',');
            if (comma < 0) return value;
            string n = value.Substring(0, comma).Trim(), r = value.Substring(comma + 1).Trim();
            return r.Length > 0 ? $"{n} ({r})" : n;
        }

        // The room lists show the ship name: listed in the lobby with the game's own properties (Photon's list; Steam's
        // lobbies carry every short room property already).
        private static object listedFor;
        private static void ListInLobby(object room)
        {
            if (ReferenceEquals(listedFor, room)) return;
            try
            {
                var field = AccessTools.Field(room.GetType(), "KJNGOJCPCDJ");
                var listed = field?.GetValue(room) as string[] ?? new string[0];
                if (!listed.Contains(Key))
                {
                    var set = room.GetType().GetMethod("SetPropertiesListedInLobby", new[] { typeof(string[]) });
                    if (set == null) { listedFor = room; return; }
                    set.Invoke(room, new object[] { listed.Concat(new[] { Key }).ToArray() });
                    Plugin.Log.LogInfo("Ship name: listed in the lobby");
                }
                listedFor = room;
            }
            catch (Exception e) { listedFor = room; Plugin.Log.LogDebug("Ship name: listing in the lobby: " + e.Message); }
        }

        /// <summary>"NCC-91234 · USS Cosmo", or "" for the game's.</summary>
        internal static string Display => name.Length == 0 ? "" : registry.Length > 0 ? registry + " · " + name : name;

        internal static void Patch(Harmony harmony)
        {
            var lookup = AccessTools.Method(typeof(SVR.Localization), "GetLocalizedText", new[] { typeof(string), typeof(int) });
            if (lookup == null) { Plugin.Log.LogWarning("Ship name: the game's text lookup wasn't found"); return; }
            try { harmony.Patch(lookup, postfix: new HarmonyMethod(typeof(ShipName), nameof(Lookup_Postfix))); }
            catch (Exception e) { Plugin.Log.LogWarning("Ship name: patching the text lookup: " + e.Message); }
        }

        /// <summary>The name shown in the settings: the one you set, or "the game's".</summary>
        internal static string Own => (Plugin.ShipName.Value ?? "").Trim();

        internal static void Update()
        {
            if (Time.unscaledTime < nextCheck) return;
            nextCheck = Time.unscaledTime + 1f;
            try
            {
                var photon = Patches.PhotonNetworkType;
                var room = photon?.GetMethod("get_room", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
                string use = Own;
                if (room != null)
                {
                    var props = room.GetType().GetMethod("get_customProperties")?.Invoke(room, null) as IDictionary;
                    string onRoom = props != null && props.Contains(Key) ? props[Key] as string ?? "" : "";
                    bool host = photon.GetMethod("get_isMasterClient", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null) is bool m && m;
                    // The room's name is the one in use. A new room gets the host's; after that the briefing room's
                    // SHIP NAME box changes it (host or captain), for everyone.
                    if (host && (props == null || !props.Contains(Key)) && Own.Length > 0 && PhotonProps.Set(room, Key, Own)) onRoom = Own;
                    if (host) ListInLobby(room);
                    use = onRoom;
                }
                if (use != current) Use(use);
            }
            catch (Exception e) { Plugin.Log.LogDebug("Ship name: " + e.Message); }
        }

        private static void Use(string value)
        {
            current = value ?? "";
            int comma = current.LastIndexOf(',');
            name = (comma >= 0 ? current.Substring(0, comma) : current).Trim();
            registry = comma >= 0 ? current.Substring(comma + 1).Trim() : "";
            Plugin.Log.LogInfo(current.Length == 0 ? "Ship name: the game's" : $"Ship name: {name}{(registry.Length > 0 ? " (" + registry + ")" : "")}");
        }

        // The game's ship names in any text it looks up: the crew's name instead.
        private static void Lookup_Postfix(ref string __result)
        {
            if (name.Length == 0 || string.IsNullOrEmpty(__result) || __result.IndexOf("USS", StringComparison.Ordinal) < 0) return;
            try
            {
                string withRegistry = registry.Length > 0 ? $"{name} ({registry})" : name;
                __result = __result
                    .Replace("USS AEGIS (NX-1787)", withRegistry.ToUpperInvariant())
                    .Replace("USS Enterprise (NCC 1701-D)", withRegistry)
                    .Replace("USS AEGIS", name.ToUpperInvariant())
                    .Replace("USS Aegis", name);
                // Other spellings (e.g. the German "U.S.S. ENTERPRISE (NCC 1701-D)"): name and registry each.
                __result = System.Text.RegularExpressions.Regex.Replace(__result, @"U\.\s?S\.\s?S\.\s?(ENTERPRISE|AEGIS)", m => (m.Groups[1].Value == m.Groups[1].Value.ToUpperInvariant() ? name.ToUpperInvariant() : name), System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (registry.Length > 0) __result = System.Text.RegularExpressions.Regex.Replace(__result, @"NCC[\s\-]1701[\s\-]D|NX[\s\-]1787", registry);
            }
            catch { }
        }

        /// <summary>Asks for a new name (the settings' row): the desktop prompt, or SteamVR's keyboard in VR.</summary>
        internal static void Ask()
        {
            const string what = "Ship name, registry (e.g. USS Cosmo, NCC-91234); empty for the game's";
            Action<string> done = text =>
            {
                Set(text, false);
                Plugin.Log.LogInfo($"Revival settings: ship name \"{Plugin.ShipName.Value}\"");
            };
            if (UnityEngine.VR.VRSettings.enabled && UnityEngine.VR.VRSettings.loadedDeviceName == "OpenVR") VrKeyboard.Open(Translation.T("Ship name"), 40, done);
            else TextPrompt.Ask(Translation.T(what), false, done);
        }
    }
}
