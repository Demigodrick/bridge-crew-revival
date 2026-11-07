using System;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine.VR;

namespace StbcRevival
{
    internal enum VrDeviceChoice { Auto, SteamVR, Oculus }

    /// <summary>
    /// [VR] Device: which VR runtime the game starts. Once loaded, the game (InitialLoader's SelectVRMode)
    /// always asks Unity for Oculus first and SteamVR only if that fails, whatever the -vrmode launch option
    /// started. Headsets that pass themselves off as Oculus without the Oculus runtime (a Quest through
    /// Virtual Desktop) crash natively in the game's 2016 Oculus plugin there: Unity shuts SteamVR down,
    /// loads Oculus and falls over. SteamVR puts SteamVR first; Auto does that when the game was launched
    /// with -vrmode openvr or when no Oculus runtime is installed; Oculus keeps the game's own order.
    /// </summary>
    internal static class VrDevice
    {
        private static FieldInfo state;   // the iterator's state (0 = not started)
        private static FieldInfo wantVr;  // false with the non_vr launch option
        private static FieldInfo devices; // the device list it asks for
        private static FieldInfo current; // what it yields

        // Safe to patch from plugin Awake (and it must be: the game picks the device early in its first
        // scene): the selection touches no Photon or network types (see Patches.ApplyDeferred).
        internal static void Patch(Harmony harmony)
        {
            string why;
            if (!SteamVRFirst(out why))
            {
                Plugin.Log.LogInfo("VR device: the game's order, Oculus first (" + why + ")");
                return;
            }
            var select = AccessTools.TypeByName("InitialLoader")?.GetNestedTypes(BindingFlags.NonPublic)
                .FirstOrDefault(t => t.Name.StartsWith("<SelectVRMode>"));
            var moveNext = select == null ? null : AccessTools.Method(select, "MoveNext");
            state = select == null ? null : AccessTools.Field(select, "DBNIHIBHOLC");
            wantVr = select == null ? null : AccessTools.Field(select, "EONPCBMAPBK");
            devices = select == null ? null : AccessTools.Field(select, "DIHFOIJGJBK");
            current = select == null ? null : AccessTools.Field(select, "ILLBDLPMFPB");
            if (moveNext == null || state?.FieldType != typeof(int) || wantVr?.FieldType != typeof(bool)
                || devices?.FieldType != typeof(string[]) || current == null)
            {
                Plugin.Log.LogWarning("VR device: the game's VR selection wasn't found; it picks as it always did");
                return;
            }
            harmony.Patch(moveNext, prefix: new HarmonyMethod(typeof(VrDevice), nameof(Select_Prefix)));
            Plugin.Log.LogInfo("VR device: SteamVR first, then Oculus (" + why + ")");
        }

        private static bool SteamVRFirst(out string why)
        {
            switch (Plugin.VrDevice.Value)
            {
                case VrDeviceChoice.SteamVR: why = "[VR] Device = SteamVR"; return true;
                case VrDeviceChoice.Oculus: why = "[VR] Device = Oculus"; return false;
            }
            var args = Environment.GetCommandLineArgs();
            int at = Array.FindIndex(args, a => a.Equals("-vrmode", StringComparison.OrdinalIgnoreCase));
            if (at >= 0 && at + 1 < args.Length && args[at + 1].Equals("openvr", StringComparison.OrdinalIgnoreCase))
            {
                why = "launched with -vrmode openvr";
                return true;
            }
            string runtime = OculusRuntime();
            if (runtime == null)
            {
                why = "no Oculus runtime installed";
                return true;
            }
            why = "Oculus runtime at " + runtime;
            return false;
        }

        // Where the Oculus runtime (LibOVRRT64_1.dll, which the game's Oculus plugin loads) is, or null.
        // The Oculus/Meta app installs it in System32; OculusBase is the app's own folder. Without it the
        // plugin has nothing to talk to, and with a headset that answers as Oculus it crashes instead of
        // failing over to SteamVR.
        private static string OculusRuntime()
        {
            const string dll = "LibOVRRT64_1.dll";
            try
            {
                string path = Path.Combine(Environment.SystemDirectory, dll);
                if (File.Exists(path)) return path;
                string baseDir = Environment.GetEnvironmentVariable("OculusBase");
                if (!string.IsNullOrEmpty(baseDir))
                {
                    path = Path.Combine(Path.Combine(Path.Combine(baseDir, "Support"), "oculus-runtime"), dll);
                    if (File.Exists(path)) return path;
                }
                return null;
            }
            catch (Exception e)
            {
                // Can't tell: keep the game's order, as before.
                return "(couldn't check: " + e.GetType().Name + ")";
            }
        }

        // The first step of SelectVRMode, with the list the other way round; the rest (enabling VR once a
        // device is loaded) is the game's.
        private static bool Select_Prefix(object __instance, ref bool __result)
        {
            if ((int)state.GetValue(__instance) != 0 || !(bool)wantVr.GetValue(__instance)) return true;
            var order = new[] { "OpenVR", "Oculus" };
            devices.SetValue(__instance, order);
            VRSettings.LoadDeviceByName(order);
            current.SetValue(__instance, null);
            state.SetValue(__instance, 1);
            __result = true;
            return false;
        }
    }
}
