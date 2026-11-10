using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using SVR.Network;
using UnityEngine;

namespace StbcRevival
{
    /// <summary>
    /// [Avatar] Rank and NamePlates.
    ///   - Rank: the game already has ranks, earned with experience (Ensign to Captain): each player's is a Photon
    ///     player property ("CurrentPlayerRank", NovaPlayerInfo.FPCLMHFIGLD) the game sets in the briefing room,
    ///     shown on the uniform (pips on the TNG collar, braid on the others: BodyCustomization.SetRankType) and as an
    ///     icon in the briefing room. Here you can wear a lower one than you've earned (-1: the earned one; a higher
    ///     choice is held to the earned one): the property is set to it, so crewmates see it too, with or without the
    ///     mod. Every character's uniform follows its
    ///     player's rank as it changes (the game only sets it as the character is made).
    ///   - Name plates: rank and name over each crewmate's head ("Lt. Cmdr. Yannök"), in LCARS orange, facing you.
    /// </summary>
    internal static class Ranks
    {
        internal static readonly string[] Names = { "Ensign", "Lieutenant Junior Grade", "Lieutenant", "Lieutenant Commander", "Commander", "Captain" };
        private static readonly string[] Short = { "Ens.", "Lt. j.g.", "Lt.", "Lt. Cmdr.", "Cmdr.", "Capt." };
        private static readonly string[] ShortDe = { "Fähnrich", "Lt. j.g.", "Lt.", "Lt. Cmdr.", "Cmdr.", "Captain" };

        private static readonly FieldInfo WornRank = AccessTools.Field(typeof(BodyCustomization), "OMJKMLCLMNI");
        private static float nextCheck;
        private static int lastChosen = -2;

        internal static int Chosen => Mathf.Clamp(Plugin.Rank.Value, -1, Names.Length - 1);

        // The rank worn for an earned one: the chosen one, no higher than earned.
        private static int Wear(int earned) => Chosen < 0 ? earned : Math.Min(Chosen, earned);

        // The menu's next choice: earned, then Ensign up to the earned rank (all six while it isn't known), then earned.
        internal static int NextChoice()
        {
            int top = Earned();
            if (top < 0) top = Names.Length - 1;
            return Chosen >= top ? -1 : Chosen + 1;
        }

        internal static void Patch(Harmony harmony)
        {
            var setter = AccessTools.PropertySetter(typeof(NovaPlayerInfo), "FPCLMHFIGLD");
            if (setter == null) { Plugin.Log.LogWarning("Ranks: NovaPlayerInfo's rank property not found; the chosen rank won't be worn"); return; }
            // A failed patch here mustn't stop the patches after it (the bridge walk's among them).
            try { harmony.Patch(setter, prefix: new HarmonyMethod(typeof(Ranks), nameof(SetRank_Prefix))); }
            catch (Exception e) { Plugin.Log.LogWarning("Ranks: patching the rank property: " + e.Message); }
        }

        // The game setting the local player's rank (the earned one, in the briefing room): the chosen one instead.
        // (The setter's parameter isn't called "value" in the game's assembly: taken by position.)
        private static void SetRank_Prefix(NovaPlayerInfo __instance, ref int __0)
        {
            if (Chosen >= 0 && IsLocal(__instance)) __0 = Wear(__0);
        }

        private static bool IsLocal(NovaPlayerInfo info)
        {
            try { return info != null && info == NovaPlayerManager.IEFBKOMBFGO?.AJPNCBOKCLC; } catch { return false; }
        }

        // The rank earned with experience, or -1 if the save can't be read (yet).
        internal static int Earned()
        {
            try { return Mathf.Clamp(NetworkCore.IEFBKOMBFGO.CMDONECIBMP.LocalPlayerLevel.CurrentTitle, 0, Names.Length - 1); }
            catch { return -1; }
        }

        internal static string ShortName(int rank)
        {
            var names = Translation.German ? ShortDe : Short;
            return names[Mathf.Clamp(rank, 0, names.Length - 1)];
        }

        internal static void Update()
        {
            if (Time.unscaledTime < nextCheck) return;
            nextCheck = Time.unscaledTime + 1f;
            try
            {
                var local = NovaPlayerManager.IEFBKOMBFGO?.AJPNCBOKCLC;
                if (local != null && local.PKIBEAIHJNE != null && NDIOGIGMPBH.PGJDPPDBGBL) // in a room
                {
                    int earned = Earned(), want = Wear(earned);
                    // The chosen rank on (or, chosen back to "earned", the earned one back on); nothing while the
                    // earned one can't be read, rather than a wrong rank for the whole crew.
                    if (earned >= 0 && (Chosen >= 0 || lastChosen >= 0) && local.FPCLMHFIGLD != want)
                    {
                        local.FPCLMHFIGLD = want;
                        Plugin.Log.LogInfo($"Ranks: wearing {Names[want]}{(want == earned ? " (earned)" : "")}");
                    }
                    if (earned >= 0) lastChosen = Chosen;
                }
                // Every character's uniform with its player's rank.
                foreach (var info in Walking.Bridge.Players())
                {
                    if (info == null || info.VisualPawnObject == null || info.PKIBEAIHJNE == null) continue;
                    int rank = Mathf.Clamp(info.FPCLMHFIGLD, 0, Names.Length - 1);
                    foreach (var body in info.VisualPawnObject.GetComponentsInChildren<BodyCustomization>(true))
                        if (WornRank == null || !(WornRank.GetValue(body) is FJHJPAIGAEO worn) || (int)worn != rank)
                            body.SetRankType((FJHJPAIGAEO)rank);
                }
            }
            catch (Exception e) { Plugin.Log.LogDebug("Ranks: " + e.Message); }
        }
    }

    /// <summary>Rank and name over each crewmate's head (not your own), turned to the camera.</summary>
    internal static class NamePlates
    {
        private sealed class Plate { public GameObject Go; public TextMesh Text; public NovaPlayerInfo Info; public Transform Head; public bool Looked; public string Shown; }

        private static readonly Dictionary<NovaPlayerInfo, Plate> plates = new Dictionary<NovaPlayerInfo, Plate>();
        private static Font font;
        private static float nextScan;
        private static Runner runner;
        private static readonly Color Lcars = new Color(1f, 0.62f, 0.25f);

        internal static void Tick()
        {
            if (!Plugin.NamePlates.Value) { Clear(); return; }
            if (runner == null)
            {
                var go = new GameObject("Revival name plates");
                UnityEngine.Object.DontDestroyOnLoad(go);
                runner = go.AddComponent<Runner>();
            }
            if (Time.unscaledTime < nextScan) return;
            nextScan = Time.unscaledTime + 0.5f;
            var seen = new HashSet<NovaPlayerInfo>();
            var local = NovaPlayerManager.IEFBKOMBFGO?.AJPNCBOKCLC;
            foreach (var info in Walking.Bridge.Players())
            {
                if (info == null || info == local || info.PKIBEAIHJNE == null || info.VisualPawnObject == null) continue;
                seen.Add(info);
                if (!plates.TryGetValue(info, out var plate) || plate.Go == null) plates[info] = plate = Make(info);
                string text = $"{Ranks.ShortName(info.FPCLMHFIGLD)} {Name(info)}";
                if (text != plate.Shown) { plate.Text.text = text; plate.Shown = text; }
                if (plate.Head == null && !plate.Looked)
                {
                    plate.Head = FindHead(info.VisualPawnObject);
                    plate.Looked = true;
                }
            }
            var gone = new List<NovaPlayerInfo>();
            foreach (var pair in plates) if (pair.Key == null || !seen.Contains(pair.Key)) gone.Add(pair.Key);
            foreach (var info in gone) { if (plates[info].Go != null) UnityEngine.Object.Destroy(plates[info].Go); plates.Remove(info); }
        }

        private static string Name(NovaPlayerInfo info)
        {
            try { return info.PKIBEAIHJNE.GPICMMEIAPM ?? "?"; } catch { return "?"; }
        }

        private static Plate Make(NovaPlayerInfo info)
        {
            if (font == null) font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            var go = new GameObject("Name plate");
            UnityEngine.Object.DontDestroyOnLoad(go);
            var text = go.AddComponent<TextMesh>();
            text.font = font;
            text.richText = false; // names come from other players: no <size=...> filling your view
            text.fontSize = 64;
            text.characterSize = 0.008f;
            text.anchor = TextAnchor.LowerCenter;
            text.alignment = TextAlignment.Center;
            text.color = Lcars;
            go.GetComponent<MeshRenderer>().sharedMaterial = Material();
            return new Plate { Go = go, Text = text, Info = info };
        }

        // The font's own material draws through walls (its shader always passes the depth test). The UI shader
        // takes the depth test as a property, and reads the font's alpha-only texture as uGUI's Text does (white
        // plus the texture's alpha, tinted by the text colour). Falls back to the font's material.
        private static Material material;
        private static Material Material()
        {
            if (material != null) return material;
            var shader = Shader.Find("UI/Default");
            if (shader == null) return material = font.material;
            material = new Material(shader) { mainTexture = font.material.mainTexture };
            material.SetInt("unity_GUIZTestMode", (int)UnityEngine.Rendering.CompareFunction.LessEqual);
            material.SetVector("_TextureSampleAdd", new Vector4(1f, 1f, 1f, 0f));
            material.renderQueue = 3000;
            return material;
        }

        // The head bone of a humanoid rig, else a child called "Head", else none (the plate then sits over the pawn).
        private static Transform FindHead(GameObject pawn)
        {
            var anim = pawn.GetComponentInChildren<Animator>();
            if (anim != null && anim.isHuman)
            {
                var bone = anim.GetBoneTransform(HumanBodyBones.Head);
                if (bone != null) return bone;
            }
            foreach (var t in pawn.GetComponentsInChildren<Transform>(true))
                if (t.name.EndsWith("Head", StringComparison.OrdinalIgnoreCase)) return t;
            return null;
        }

        private static void Clear()
        {
            foreach (var plate in plates.Values) if (plate.Go != null) UnityEngine.Object.Destroy(plate.Go);
            plates.Clear();
        }

        // Over the head, facing the camera, shown while the character is (after everything has moved).
        private sealed class Runner : MonoBehaviour
        {
            private void LateUpdate()
            {
                var cam = Camera.main;
                foreach (var plate in plates.Values)
                {
                    if (plate.Go == null) continue;
                    bool show = plate.Info != null && plate.Info.VisualPawnObject != null && plate.Info.VisualPawnObject.activeInHierarchy;
                    if (plate.Go.activeSelf != show) plate.Go.SetActive(show);
                    if (!show) continue;
                    Vector3 up = plate.Info.PawnObject != null ? plate.Info.PawnObject.transform.up : Vector3.up;
                    plate.Go.transform.position = plate.Head != null ? plate.Head.position + up * 0.28f
                        : plate.Info.VisualPawnObject.transform.position + up * 1.95f;
                    if (cam != null) plate.Go.transform.rotation = Quaternion.LookRotation(plate.Go.transform.position - cam.transform.position, up);
                }
            }
        }
    }
}
