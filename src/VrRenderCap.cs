using System;
using UnityEngine;
using UnityEngine.VR;

namespace StbcRevival
{
    /// <summary>
    /// Keeps the VR eye texture within what the game's engine (Unity 5.4) can make. A headset that asks for a very large
    /// render size (Virtual Desktop or SteamVR's render resolution turned well up: 6008×6440 seen from a Quest 3) gets
    /// "RenderTexture.Create failed: requested size is too large" on the way to the shuttle, SteamVR's plugin switches
    /// itself off for good, and the game never reads the controllers again (an error every frame, and "connect your
    /// controller"). When the eye texture is over 4096 high (8192 wide: both eyes side by side), the render scale is
    /// lowered until it fits, checked a few times a second (the size the runtime asks for can change). Lower sizes are
    /// left as they are.
    /// </summary>
    internal static class VrRenderCap
    {
        private const int MaxWidth = 8192, MaxHeight = 4096;
        private const float CheckSeconds = 0.25f;
        private static float nextCheck;

        internal static void Update()
        {
            if (Time.realtimeSinceStartup < nextCheck) return;
            nextCheck = Time.realtimeSinceStartup + CheckSeconds;
            if (!VRSettings.enabled) return;
            int w = VRSettings.eyeTextureWidth, h = VRSettings.eyeTextureHeight;
            float scale = VRSettings.renderScale;
            if (w <= 0 || h <= 0 || scale <= 0f || (w <= MaxWidth && h <= MaxHeight)) return;
            float fit = Math.Min((float)MaxWidth / w, (float)MaxHeight / h) * 0.99f;
            float want = scale * fit;
            VRSettings.renderScale = want;
            Plugin.Log.LogInfo($"VR render size: eye texture {w}×{h} is more than the game's engine can make; render scale {scale:0.###} → {want:0.###} "
                               + $"(about {(int)(w * fit)}×{(int)(h * fit)}). Lower Virtual Desktop's quality or SteamVR's render resolution to avoid this.");
        }
    }
}
