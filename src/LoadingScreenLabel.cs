using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace StbcRevival
{
    /// <summary>
    /// "Revival Mod" on the loading screen: on the game's own loading graphic (a monitor, Oculus; see Update), and in
    /// SteamVR on the compositor's loading skybox. While the game loads, it hands the headset over to the SteamVR
    /// compositor (CameraFade.DoFade: CompositorBringToFront), which shows a skybox of the game's own pictures
    /// (ViveLoadingSkybox, through SteamVR_Skybox.SetOverride) instead of the game's frames; nothing the game draws
    /// is seen then. So the text goes into those pictures: on the way into SetOverride, each of the four side faces
    /// is swapped for a copy with the text drawn on it, below the middle (the copies are made once and kept).
    /// </summary>
    internal static class LoadingScreenLabel
    {
        private const string Label = "Revival Mod";
        private const int Layer = 31; // drawn alone, by its own camera, far from everything
        private static readonly Vector3 Away = new Vector3(0f, -50000f, 0f);
        private static readonly Dictionary<Texture, Texture> stamped = new Dictionary<Texture, Texture>();

        // ---- Without SteamVR (a monitor, Oculus): the game's own loading graphic -------------------------------------
        // The loading graphic is a CanvasGroup on the CameraFade overlay (_loadingGraphicsCanvasGroup), shown and hidden
        // by its alpha. It has one look per bridge (Loading_Aegis, _Enterprise, _Tng), each with a "LOADING" text; the label
        // is a copy of each "LOADING" text, as its sibling just below it, smaller: same font, colour and canvas, and it fades with the rest.
        private static CameraFade overlay;
        private static float nextLook;

        internal static void Update()
        {
            if (overlay != null || Time.realtimeSinceStartup < nextLook) return;
            nextLook = Time.realtimeSinceStartup + 1f;
            var found = UnityEngine.Object.FindObjectOfType<CameraFade>();
            if (found == null) return;
            overlay = found;
            AddToLoadingGraphic(found);
        }

        private static void AddToLoadingGraphic(CameraFade fade)
        {
            var group = AccessTools.Field(typeof(CameraFade), "_loadingGraphicsCanvasGroup")?.GetValue(fade) as CanvasGroup;
            if (group == null)
            {
                Plugin.Log.LogWarning("Loading screen label: the overlay has no loading graphic");
                return;
            }
            Plugin.Log.LogInfo("Loading screen label: the loading graphic is\n" + Describe(group.transform, 0));
            // One per bridge: Loading_Aegis, Loading_Enterprise and Loading_Tng, each with its own LOADING.
            int added = 0;
            foreach (var c in group.GetComponentsInChildren<Component>(true))
            {
                if (!(c is TMPro.TMP_Text) && !(c is UnityEngine.UI.Text)) continue;
                var shown = TextOf(c);
                if (shown == null || !shown.ToUpperInvariant().Contains("LOADING")) continue;
                AddUnder(c);
                added++;
            }
            if (added == 0) Plugin.Log.LogWarning("Loading screen label: no LOADING text in the loading graphic; no label");
        }

        private static void AddUnder(Component loading)
        {
            var original = loading.transform;
            var copy = UnityEngine.Object.Instantiate(original.gameObject);
            copy.name = "RevivalModLabel";
            copy.transform.SetParent(original.parent, false);
            copy.transform.localRotation = original.localRotation;
            copy.transform.localScale = original.localScale;
            // How far down: by the size LOADING is drawn at, not its box, which can be smaller than the text (the TNG
            // one's is 48 high with taller letters overflowing it). Auto-sized text is drawn up to fontSizeMax.
            float size = 40f;
            if (loading is TMPro.TMP_Text shown)
                size = shown.BMAIEFHKPMA ? shown.LOBPGECEJCD : shown.ACEGMMMIHCN; // enableAutoSizing ? fontSizeMax : fontSize
            else if (loading is UnityEngine.UI.Text legacy)
                size = legacy.fontSize;
            float rectDrop = original is RectTransform r && r.rect.height > 0f ? r.rect.height * 0.6f : 0f;
            float drop = Mathf.Max(rectDrop, size * 0.9f);
            copy.transform.localPosition = original.localPosition + original.localRotation * Vector3.down * drop;
            copy.transform.SetSiblingIndex(original.GetSiblingIndex() + 1);
            if (copy.GetComponent<TMPro.TextMeshProUGUI>() is TMPro.TextMeshProUGUI tmp)
            {
                tmp.SetLocalizedText(Label);
                tmp.ACEGMMMIHCN *= 0.55f; // fontSize
                tmp.LOBPGECEJCD *= 0.55f; // fontSizeMax, for auto-sized text
            }
            else if (copy.GetComponent<UnityEngine.UI.Text>() is UnityEngine.UI.Text text)
            {
                text.text = Label;
                text.fontSize = Mathf.Max(8, (int)(text.fontSize * 0.55f));
            }
            Plugin.Log.LogInfo($"Loading screen label: added under '{original.name}' ({loading.GetType().Name}), "
                               + $"at {copy.transform.localPosition} in {original.parent.name} (LOADING drawn at {size:0}, {drop:0} down)");
        }

        private static readonly System.Reflection.MethodInfo TmpText = AccessTools.Method(typeof(TMPro.TMP_Text), "get_text");

        private static string TextOf(Component c) =>
            c is UnityEngine.UI.Text text ? text.text : TmpText?.Invoke(c, null) as string;

        // The loading graphic's objects, their components and places, for placing the label.
        private static string Describe(Transform t, int depth)
        {
            var sb = new System.Text.StringBuilder();
            var rect = t as RectTransform;
            sb.Append(' ', depth * 2).Append(t.name).Append(t.gameObject.activeSelf ? "" : " (off)").Append(" at ").Append(t.localPosition)
              .Append(rect != null ? $" size {rect.rect.size}" : "").Append(" [");
            foreach (var c in t.GetComponents<Component>())
                if (c != null && !(c is Transform)) sb.Append(c.GetType().Name).Append(' ');
            var shown = t.GetComponent<TMPro.TMP_Text>() is TMPro.TMP_Text tmp ? TextOf(tmp) : t.GetComponent<UnityEngine.UI.Text>()?.text;
            sb.Append(']').Append(shown != null ? $" \"{shown}\"" : "").Append('\n');
            if (depth < 5) foreach (Transform child in t) sb.Append(Describe(child, depth + 1));
            return sb.ToString();
        }

        // ---- SteamVR: the compositor's loading skybox ----------------------------------------------------------------
        internal static void Patch(Harmony harmony)
        {
            var setOverride = AccessTools.Method(typeof(SteamVR_Skybox), "SetOverride");
            if (setOverride == null)
            {
                Plugin.Log.LogWarning("Loading screen label: SteamVR_Skybox.SetOverride wasn't found; no label");
                return;
            }
            harmony.Patch(setOverride, prefix: new HarmonyMethod(typeof(LoadingScreenLabel), nameof(SetOverride_Prefix)));
        }

        // Front, back, left, right: the faces seen when looking around; top and bottom stay as they are.
        private static void SetOverride_Prefix(ref Texture __0, ref Texture __1, ref Texture __2, ref Texture __3)
        {
            try
            {
                __0 = Stamped(__0);
                __1 = Stamped(__1);
                __2 = Stamped(__2);
                __3 = Stamped(__3);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Loading screen label: couldn't draw it: " + e.Message);
            }
        }

        private static Texture Stamped(Texture face)
        {
            if (face == null) return null;
            if (stamped.TryGetValue(face, out var copy) && copy != null) return copy;
            if (stamped.ContainsValue(face)) return face; // already one of ours
            copy = Stamp(face);
            stamped[face] = copy;
            return copy;
        }

        private static Texture Stamp(Texture face)
        {
            // An uncompressed copy without mipmaps: what the compositor takes (see SteamVR_Skybox.SetOverride).
            var target = new RenderTexture(face.width, face.height, 24, RenderTextureFormat.ARGB32)
            {
                name = face.name + " (Revival Mod)",
                useMipMap = false,
            };
            target.Create();
            Graphics.Blit(face, target);

            var cameraObject = new GameObject("Revival Mod label camera");
            var textObject = new GameObject("Revival Mod label");
            try
            {
                cameraObject.transform.position = Away + new Vector3(0f, 0f, -10f);
                var camera = cameraObject.AddComponent<Camera>();
                camera.enabled = false;
                camera.orthographic = true;
                camera.orthographicSize = 1f; // the face spans -1..1 up and down
                camera.aspect = (float)face.width / face.height;
                camera.nearClipPlane = 1f;
                camera.farClipPlane = 20f;
                camera.cullingMask = 1 << Layer;
                camera.clearFlags = CameraClearFlags.Nothing;
                camera.targetTexture = target;

                textObject.layer = Layer;
                var font = Resources.GetBuiltinResource<Font>("Arial.ttf");
                var text = textObject.AddComponent<TextMesh>();
                text.font = font;
                text.fontSize = 128;
                text.characterSize = 0.01f;
                text.anchor = TextAnchor.MiddleCenter;
                text.alignment = TextAlignment.Center;
                text.color = new Color(1f, 1f, 1f, 0.85f);
                text.text = Label;
                var renderer = textObject.GetComponent<MeshRenderer>();
                renderer.sharedMaterial = font.material;

                // A quarter of the face wide, a third of the way down from the middle.
                float width = renderer.bounds.size.x;
                float scale = width > 0f ? 0.5f * camera.aspect / width : 1f;
                textObject.transform.localScale = Vector3.one * scale;
                textObject.transform.position = Away + new Vector3(0f, -0.35f, 0f);

                camera.Render();
                Plugin.Log.LogInfo($"Loading screen label: drawn on {face.name} ({face.width}x{face.height})");
            }
            finally
            {
                UnityEngine.Object.Destroy(textObject);
                UnityEngine.Object.Destroy(cameraObject);
            }
            return target;
        }
    }
}
