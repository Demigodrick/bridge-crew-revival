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

            bool inRoom = Publish();
            foreach (var info in UnityEngine.Object.FindObjectsOfType<PlayerInfo>())
                Apply(info.VisualPawnObject, Wants(info, inRoom));
            // The character creator shows its own copy of each preset (CustomizationManager.FFJOIFLJFPF, in
            // the order of NovaPlayerManager.CharacterProfiles), not your player avatar.
            var creator = UnityEngine.Object.FindObjectOfType<CustomizationManager>();
            if (creator != null && CreatorAvatars?.GetValue(creator) is GameObject[] avatars && Profiles() is IList profiles)
                for (int i = 0; i < avatars.Length && i < profiles.Count; i++)
                    if (avatars[i] != null && avatars[i].activeInHierarchy)
                        Apply(avatars[i], Plugin.RomulanFace.Value && FemaleVulcan(profiles[i] as CharacterProfile));
            wearing.RemoveWhere(h => h == null);
        }

        private static readonly FieldInfo CreatorAvatars = AccessTools.Field(typeof(CustomizationManager), "FFJOIFLJFPF");

        private static IList Profiles()
        {
            var instance = AccessTools.Method(AccessTools.TypeByName("NovaPlayerManager"), "get_Instance")?.Invoke(null, null);
            return instance == null ? null : AccessTools.Field(instance.GetType(), "CharacterProfiles")?.GetValue(instance) as IList; // SVR.PlayerManager field
        }

        private static bool FemaleVulcan(CharacterProfile profile) =>
            profile != null && profile.CharacterSpecies.ToString() == "Vulcan" && profile.CharacterGender.ToString() == "Female";

        private static void Apply(GameObject avatar, bool want)
        {
            var face = avatar == null ? null : avatar.GetComponentInChildren<FaceCustomization>(true);
            var head = face == null ? null : face.GetComponent<SkinnedMeshRenderer>();
            if (head == null) return;
            if (want && !IsRomulan(head.sharedMaterial) && Wear(head))
            {
                wearing.Add(head);
                Plugin.Log.LogInfo($"Romulan face: on {avatar.name}");
            }
            else if (!want && wearing.Remove(head))
            {
                face.SetDefaultLook(); // the preset's own face back
                Plugin.Log.LogInfo($"Romulan face: off {avatar.name}");
            }
        }

        // A female Vulcan preset whose player chose it: us from the setting, others from their player property.
        // Out of a room (main menu, character creator) the only avatar is ours; in a room, one with no network
        // player is an AI crew member (Lt. T'Nar at engineering), never changed.
        private static bool Wants(PlayerInfo info, bool inRoom)
        {
            if (!FemaleVulcan(info.Profile)) return false;
            var player = info.PKIBEAIHJNE;
            if (player == null) return !inRoom && Plugin.RomulanFace.Value;
            if (IsLocal(player)) return Plugin.RomulanFace.Value;
            return Properties(player) is IDictionary props && props.Contains(PlayerKey) && props[PlayerKey] is bool on && on;
        }

        // Keep our player property in step with the setting while in a room; says whether we're in one.
        private static bool Publish()
        {
            var room = Patches.PhotonNetworkType?.GetMethod("get_room", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
            if (room == null) return false;
            var local = Patches.PhotonNetworkType.GetMethod("get_player", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
            if (local == null) return true;
            bool on = Plugin.RomulanFace.Value;
            var props = Properties(local) as IDictionary;
            if (props != null && props.Contains(PlayerKey) && props[PlayerKey] is bool current && current == on) return true;
            if (!PhotonProps.Set(local, PlayerKey, on)) return true;
            Plugin.Log.LogInfo($"Romulan face: told the crew {(on ? "on" : "off")}");
            return true;
        }

        private static bool IsLocal(object player) => AccessTools.Field(player.GetType(), "isLocal")?.GetValue(player) is bool local && local;

        private static object Properties(object player) => player.GetType().GetMethod("get_customProperties", All)?.Invoke(player, null);

        // Put the Romulan face on, keeping the hair colour and intensity the creator set on the face it replaces.
        private static bool Wear(SkinnedMeshRenderer head)
        {
            if (!(Find() is Material material)) return false;
            var previous = head.sharedMaterial;
            var worn = new Material(material) { name = "Romulan face", renderQueue = previous != null ? previous.renderQueue : material.renderQueue };
            if (previous != null)
            {
                if (previous.HasProperty("_HairColor") && worn.HasProperty("_HairColor")) worn.SetColor("_HairColor", previous.GetColor("_HairColor"));
                if (previous.HasProperty("_HairIntensity") && worn.HasProperty("_HairIntensity")) worn.SetFloat("_HairIntensity", previous.GetFloat("_HairIntensity"));
            }
            head.material = worn;
            return true;
        }

        internal static void Patch(Harmony harmony)
        {
            // The creator swaps the face material when you change look, head or hair (DNNOHHKPGDA puts a look
            // on, CAJFLMIJGLL the original ones back); put the Romulan face straight back on a face wearing it.
            foreach (var name in new[] { "DNNOHHKPGDA", "CAJFLMIJGLL" })
            {
                var method = AccessTools.Method(typeof(FaceCustomization), name);
                if (method != null) harmony.Patch(method, postfix: new HarmonyMethod(typeof(RomulanFace), nameof(Swapped_Postfix)));
                else Plugin.Log.LogWarning($"Romulan face: FaceCustomization.{name} not found; face changes undo the Romulan face for a second");
            }
        }

        private static void Swapped_Postfix(FaceCustomization __instance)
        {
            var head = __instance.GetComponent<SkinnedMeshRenderer>();
            if (head != null && wearing.Contains(head) && !IsRomulan(head.sharedMaterial)) Wear(head);
        }

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
