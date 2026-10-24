using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using PhotonHashtable = ExitGames.Client.Photon.Hashtable;

namespace StbcRevival
{
    /// <summary>
    /// [Avatar] RomulanFace: wear the Romulan captain's face (the hail-screen character) on a female Vulcan
    /// avatar. Her head is the same female head the Vulcan presets use, and a face is just a "look" material
    /// on it (FaceCustomization loads them from Resources/CharacterMaterials), so this swaps that material.
    /// The choice is a player property ("RF"), so crewmates with the mod see it too; checked once a second.
    /// </summary>
    internal static class RomulanFace
    {
        private const string PlayerKey = "RF";
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        private static Material romulan;
        private static bool searchedAll;
        private static float nextCheck;
        private static readonly HashSet<SkinnedMeshRenderer> wearing = new HashSet<SkinnedMeshRenderer>();
        private static readonly FieldInfo LookNames = AccessTools.Field(typeof(FaceCustomization), "LookMaterialNames");
        private static readonly FieldInfo OriginalLook = AccessTools.Field(typeof(FaceCustomization), "_originalLookIdx");

        internal static void Update()
        {
            if (Time.unscaledTime < nextCheck) return;
            nextCheck = Time.unscaledTime + 1f;

            Publish();
            foreach (var info in UnityEngine.Object.FindObjectsOfType<PlayerInfo>())
            {
                var face = info.VisualPawnObject == null ? null : info.VisualPawnObject.GetComponentInChildren<FaceCustomization>(true);
                var head = face == null ? null : face.GetComponent<SkinnedMeshRenderer>();
                if (head == null) continue;
                bool want = Wants(info);
                if (want && Find() is Material material && !IsRomulan(head.sharedMaterial))
                {
                    var worn = new Material(material) { name = "Romulan face", renderQueue = head.sharedMaterial != null ? head.sharedMaterial.renderQueue : material.renderQueue };
                    head.material = worn;
                    wearing.Add(head);
                    Plugin.Log.LogInfo($"Romulan face: on {info.VisualPawnObject.name}");
                }
                else if (!want && wearing.Remove(head))
                {
                    face.SetDefaultLook(); // the preset's own face back
                    Plugin.Log.LogInfo($"Romulan face: off {info.VisualPawnObject.name}");
                }
            }
            wearing.RemoveWhere(h => h == null);
        }

        // A female Vulcan preset whose player chose it: us from the setting, others from their player property.
        // No network player yet (main menu, shuttle on your own) means it's ours.
        private static bool Wants(PlayerInfo info)
        {
            var profile = info.Profile;
            if (profile == null || profile.CharacterSpecies.ToString() != "Vulcan" || profile.CharacterGender.ToString() != "Female") return false;
            var player = info.PKIBEAIHJNE;
            if (player == null || IsLocal(player)) return Plugin.RomulanFace.Value;
            return Properties(player) is IDictionary props && props.Contains(PlayerKey) && props[PlayerKey] is bool on && on;
        }

        // Keep our player property in step with the setting while in a room.
        private static void Publish()
        {
            var local = Patches.PhotonNetworkType?.GetMethod("get_player", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
            if (local == null) return;
            var room = Patches.PhotonNetworkType.GetMethod("get_room", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
            if (room == null) return;
            bool on = Plugin.RomulanFace.Value;
            var props = Properties(local) as IDictionary;
            if (props != null && props.Contains(PlayerKey) && props[PlayerKey] is bool current && current == on) return;
            local.GetType().GetMethod("SetCustomProperties", All)?.Invoke(local, new object[] { new PhotonHashtable { [PlayerKey] = on }, null, false });
            Plugin.Log.LogInfo($"Romulan face: told the crew {(on ? "on" : "off")}");
        }

        private static bool IsLocal(object player) => AccessTools.Field(player.GetType(), "isLocal")?.GetValue(player) is bool local && local;

        private static object Properties(object player) => player.GetType().GetMethod("get_customProperties", All)?.Invoke(player, null);

        private static bool IsRomulan(Material material) => material != null && material.name.StartsWith("Romulan face", StringComparison.Ordinal);

        // The Romulan captain's look material: by her own look names if her character is loaded (the bridge),
        // else the first Romulan material among the character materials.
        private static Material Find()
        {
            if (romulan != null) return romulan;
            foreach (var face in Resources.FindObjectsOfTypeAll<FaceCustomization>())
            {
                if (face.transform.root.name.IndexOf("Romulan", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (!(LookNames?.GetValue(face) is string[] names) || names.Length == 0) continue;
                int index = OriginalLook?.GetValue(face) is int i && i >= 0 && i < names.Length ? i : 0;
                romulan = Resources.Load("CharacterMaterials/" + names[index]) as Material;
                if (romulan != null)
                {
                    Plugin.Log.LogInfo($"Romulan face: using CharacterMaterials/{names[index]} (from {face.transform.root.name})");
                    return romulan;
                }
            }
            if (searchedAll) return null;
            searchedAll = true;
            var materials = Resources.LoadAll<Material>("CharacterMaterials");
            var candidates = materials.Where(m => m.name.IndexOf("Romulan", StringComparison.OrdinalIgnoreCase) >= 0
                                                  || (m.mainTexture != null && m.mainTexture.name.IndexOf("Romulan", StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
            Plugin.Log.LogInfo($"Romulan face: {materials.Length} character materials, Romulan ones: "
                               + (candidates.Count == 0 ? "none" : string.Join(", ", candidates.Select(m => $"{m.name} ({m.mainTexture?.name})").ToArray())));
            romulan = candidates.FirstOrDefault(m => m.mainTexture != null && m.mainTexture.name.StartsWith("Romulan_Head", StringComparison.OrdinalIgnoreCase))
                      ?? candidates.FirstOrDefault();
            return romulan;
        }
    }
}
