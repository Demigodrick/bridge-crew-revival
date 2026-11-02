using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace StbcRevival
{
    /// <summary>
    /// Diagnostics for issue #4's hand skin report (since 0.7.20 the hands' skin colour no longer follows the
    /// creator). The shuttle's own avatar (CharacterCustomization.IsMainMenuAvatar: your hands in VR) gets a hand
    /// material for its species (SetVisualSpeciesForMainMenuAvatar), then the skin tone on each of its body parts
    /// that is switched on (UpdateRaceSkinValues → BodyCustomization.SetSkinParams). After either, this logs which
    /// avatar it was, where it lives, each body part's hand material and its skin colour, and how many main menu
    /// avatars there are (a second one, say from the bridge loaded at launch, would take the creator's changes).
    /// Logged when something changed, not every time.
    /// </summary>
    internal static class HandSkinLog
    {
        private static readonly AccessTools.FieldRef<CharacterCustomization, BodyCustomization[]> Bodies =
            AccessTools.FieldRefAccess<CharacterCustomization, BodyCustomization[]>("GPLBJJJBPOL");
        private static readonly AccessTools.FieldRef<CharacterCustomization, bool> IsMainMenuAvatar =
            AccessTools.FieldRefAccess<CharacterCustomization, bool>("IsMainMenuAvatar");
        private static readonly int SkinColor1 = Shader.PropertyToID("_SkinColor1");
        private static readonly Dictionary<int, string> last = new Dictionary<int, string>();

        internal static void Patch(Harmony harmony)
        {
            var postfix = new HarmonyMethod(typeof(HandSkinLog), nameof(Changed_Postfix));
            foreach (var name in new[] { "UpdateRaceSkinValues", "SetVisualSpeciesForMainMenuAvatar" })
            {
                var method = AccessTools.Method(typeof(CharacterCustomization), name);
                if (method == null) Plugin.Log.LogWarning($"Hand skin log: CharacterCustomization.{name} not found");
                else harmony.Patch(method, postfix: postfix);
            }
        }

        private static void Changed_Postfix(CharacterCustomization __instance, MethodBase __originalMethod)
        {
            try
            {
                if (__instance == null || !IsMainMenuAvatar(__instance)) return;
                var text = Describe(__instance);
                int id = __instance.GetInstanceID();
                if (last.TryGetValue(id, out var before) && before == text) return;
                last[id] = text;
                int avatars = Resources.FindObjectsOfTypeAll<CharacterCustomization>()
                    .Count(c => c != null && IsMainMenuAvatar(c) && c.gameObject.scene.IsValid());
                Plugin.Log.LogInfo($"Hand skin: after {__originalMethod.Name}, {text}; main menu avatars: {avatars}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Hand skin log: " + e.Message);
            }
        }

        private static string Describe(CharacterCustomization avatar)
        {
            var go = avatar.gameObject;
            var parts = new List<string>();
            foreach (var body in Bodies(avatar) ?? new BodyCustomization[0])
            {
                if (body == null)
                {
                    parts.Add("(destroyed)");
                    continue;
                }
                var renderer = AccessTools.Field(typeof(BodyCustomization), "PDMECBLMBGB")?.GetValue(body) as SkinnedMeshRenderer;
                var materials = renderer == null ? null : renderer.sharedMaterials;
                int slot = body.HandMaterialPosition;
                var hand = materials != null && slot >= 0 && slot < materials.Length ? materials[slot] : null;
                string skin = hand != null && hand.HasProperty(SkinColor1) ? Colour(hand.GetColor(SkinColor1)) : "none";
                parts.Add($"{body.name} ({(body.isActiveAndEnabled ? "on" : "off")}, scene {body.gameObject.scene.name}): " +
                          $"hand {(hand == null ? "none" : hand.name)}, skin {skin}");
            }
            return $"{go.name} in {go.scene.name} ({(go.activeInHierarchy ? "active" : "inactive")}), " +
                   (parts.Count == 0 ? "no body parts" : string.Join("; ", parts.ToArray()));
        }

        private static string Colour(Color c) => $"({c.r:0.###}, {c.g:0.###}, {c.b:0.###})";
    }
}
