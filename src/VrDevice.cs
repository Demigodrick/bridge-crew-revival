using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
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
    /// with -vrmode openvr or when the Oculus runtime can't be loaded; Oculus keeps the game's own order.
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
            string runtime;
            if (!OculusRuntimeLoads(out runtime))
            {
                why = runtime;
                return true;
            }
            why = "Oculus runtime loads: " + runtime;
            return false;
        }

        private const string RuntimeDll = "LibOVRRT64_1.dll";

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint SearchPathW(string path, string fileName, string extension, uint bufferLength,
            StringBuilder buffer, IntPtr filePart);

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryW(string fileName);

        [DllImport("kernel32")]
        private static extern bool FreeLibrary(IntPtr module);

        // Whether the game's Oculus plugin (OVRPlugin 1.10) can load the Oculus runtime; what describes it either
        // way. The plugin looks for LibOVRRT64_1.dll in %LIBOVR_DLL_DIR%, then on Windows' search path (the
        // game's folder, System32, PATH...), checks its signature and LoadLibraryW's it. When nothing is found
        // it fails cleanly and the game moves on to SteamVR. But when the file is there and won't load (a
        // broken or partly removed Oculus/Meta app, a missing dependency), its loader reports success, with its
        // error code still at the 0 it starts at, and the plugin calls ovr_Initialize through the null pointer
        // it never filled in: an access violation at address 0 in OVRPlugin.dll. So load it here first, the same
        // way, and put SteamVR first unless it loads. (It's let go again; the plugin loads it for itself.)
        private static bool OculusRuntimeLoads(out string what)
        {
            try
            {
                string path = null;
                string dir = Environment.GetEnvironmentVariable("LIBOVR_DLL_DIR");
                if (!string.IsNullOrEmpty(dir))
                {
                    string inDir = Path.Combine(dir, RuntimeDll);
                    if (File.Exists(inDir)) path = inDir;
                }
                if (path == null)
                {
                    var found = new StringBuilder(1024);
                    uint length = SearchPathW(null, RuntimeDll, null, (uint)found.Capacity, found, IntPtr.Zero);
                    if (length > 0 && length < found.Capacity) path = found.ToString();
                }
                if (path == null)
                {
                    what = "no Oculus runtime installed";
                    return false;
                }
                IntPtr module = LoadLibraryW(path);
                if (module == IntPtr.Zero)
                {
                    int error = Marshal.GetLastWin32Error();
                    what = "the Oculus runtime at " + path + " doesn't load (Windows error " + error + ")";
                    return false;
                }
                FreeLibrary(module);
                what = path;
                return true;
            }
            catch (Exception e)
            {
                // Can't tell: keep the game's order, as before.
                what = "couldn't check (" + e.GetType().Name + ": " + e.Message + ")";
                return true;
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
