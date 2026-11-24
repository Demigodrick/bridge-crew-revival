using System;
using HarmonyLib;
using UnityEngine;
using UnityEngine.VR;

namespace StbcRevival
{
    /// <summary>
    /// Keeps the VR eye texture within what the game's engine (Unity 5.4) can make. Both eyes are drawn side by side
    /// into one texture, which failed at 12016 wide (6008 an eye), so an eye is kept to 4000 wide. The game asks for
    /// more than the headset's size (its graphics.config: render scale 1.5 on a fast graphics card), and Virtual
    /// Desktop can report a very large size while it starts (6008×6440 an eye seen from a Quest 3, half that a few
    /// seconds later): 1.5 times that fails ("RenderTexture.Create failed: requested size is too large"), SteamVR's
    /// plugin switches itself off for good, and the game never reads the controllers again (issue 43).
    ///  - Whenever the game sets its render scale (GraphicsConfig.SetRenderScale from graphics.config, and
    ///    RenderScaleControl.SetRenderScale, which sets its default step when the shuttle's scene starts), it's
    ///    lowered at once, in the same frame, to what fits.
    ///  - Every few seconds, the scale follows the size the headset reports: lowered when it no longer fits, raised
    ///    back towards the game's when it does again. The eye texture's size lags a change of scale by a frame or
    ///    so, so nothing is worked out from it until a change has settled.
    /// </summary>
    internal static class VrRenderCap
    {
        // Both eyes side by side in one texture (the game draws in single pass): 8192 wide at most here (a Quest 3
        // through Virtual Desktop failed at 6008 an eye, render scale 1), so an eye up to 4000 wide.
        private const int MaxEyeWidth = 4000, MaxEyeHeight = 8000;
        private const float SettleSeconds = 3f;
        private static float wanted = -1f;  // the game's render scale, when it set one
        // The render scale the eye texture was last seen made at: Unity's own 1 until a check has seen it settle (a
        // scale set since then reaches the texture a frame later; the next check, a few seconds on, puts that right).
        private static float settled = 1f;
        private static float nextCheck;

        internal static void Patch(Harmony harmony)
        {
            int patched = 0;
            foreach (var name in new[] { "GraphicsConfig", "RenderScaleControl" })
            {
                var type = AccessTools.TypeByName(name);
                if (type == null) continue;
                foreach (var m in type.GetMethods(AccessTools.all))
                    if (m.Name == "DLECOECACJE" && m.DeclaringType == type) // SetRenderScale (float, and RenderScaleControl's by step)
                    {
                        harmony.Patch(m, postfix: new HarmonyMethod(typeof(VrRenderCap), nameof(SetRenderScale_Postfix)));
                        patched++;
                    }
            }
            if (patched == 0) Plugin.Log.LogWarning("VR render size: the game's render scale settings weren't found");
        }

        // What the game just set, then no more than fits.
        private static void SetRenderScale_Postfix()
        {
            if (!VRSettings.enabled) return;
            float set = VRSettings.renderScale;
            if (set > 0f) wanted = set;
            float fits = Fits(set);
            if (fits <= 0f || set <= fits) return;
            VRSettings.renderScale = fits;
            nextCheck = Time.realtimeSinceStartup + SettleSeconds;
            Plugin.Log.LogInfo($"VR render size: the game set render scale {set:0.##}; {fits:0.##} instead, the most the game's engine can draw "
                               + $"for this headset's eye size ({BaseSize()})");
        }

        internal static void Update()
        {
            if (Time.realtimeSinceStartup < nextCheck || !VRSettings.enabled) return;
            nextCheck = Time.realtimeSinceStartup + SettleSeconds;
            float scale = VRSettings.renderScale;
            settled = scale; // unchanged for SettleSeconds: the eye texture is made at it
            float fits = Fits();
            if (fits <= 0f) return;
            float target = Math.Min(wanted > 0f ? wanted : Math.Max(scale, 1f), fits);
            if (Math.Abs(target - scale) < 0.02f) return;
            if (target > scale && scale >= (wanted > 0f ? wanted : 1f)) return; // only back up to the game's own
            VRSettings.renderScale = target;
            Plugin.Log.LogInfo($"VR render size: render scale {scale:0.##} → {target:0.##} (eye size {BaseSize()} at scale 1"
                               + (target < scale ? "; lower Virtual Desktop's quality or SteamVR's render resolution to avoid this)" : ")"));
        }

        // The largest render scale that fits this headset's eye size, from the eye texture now and the scale it's at;
        // 0 when VR isn't running.
        private static float Fits() => Fits(VRSettings.renderScale);

        // From the eye texture's size and the scale it was made at: the scale settled at the last check (a scale set
        // this frame hasn't reached the texture yet), else the one given.
        private static float Fits(float now)
        {
            if (!VRSettings.enabled) return 0f;
            float scale = settled > 0f ? settled : now;
            if (scale <= 0f) return 0f;
            int w = VRSettings.eyeTextureWidth, h = VRSettings.eyeTextureHeight;
            if (w <= 0 || h <= 0 || scale <= 0f) return 0f;
            float baseW = w / scale, baseH = h / scale;
            return (float)Math.Floor(Math.Min(MaxEyeWidth / baseW, MaxEyeHeight / baseH) * 100f) / 100f;
        }

        private static string BaseSize()
        {
            float scale = settled;
            return scale > 0f ? $"{(int)(VRSettings.eyeTextureWidth / scale)}×{(int)(VRSettings.eyeTextureHeight / scale)}" : "?";
        }
    }
}
