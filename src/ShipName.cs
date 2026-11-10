using System;
using System.Collections;
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
                    // The host names the ship for the room; everyone else takes the room's name.
                    if (host && onRoom != Own && PhotonProps.Set(room, Key, Own)) onRoom = Own;
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
            }
            catch { }
        }

        /// <summary>Asks for a new name (the settings' row): the desktop prompt, or SteamVR's keyboard in VR.</summary>
        internal static void Ask()
        {
            const string what = "Ship name, registry (e.g. USS Cosmo, NCC-91234); empty for the game's";
            Action<string> done = text =>
            {
                Plugin.ShipName.Value = (text ?? "").Trim();
                nextCheck = 0f;
                Plugin.Log.LogInfo($"Revival settings: ship name \"{Plugin.ShipName.Value}\"");
            };
            if (UnityEngine.VR.VRSettings.enabled && UnityEngine.VR.VRSettings.loadedDeviceName == "OpenVR") VrKeyboard.Open(Translation.T("Ship name"), 40, done);
            else TextPrompt.Ask(Translation.T(what), false, done);
        }
    }
}
