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
    /// started, and plays without VR when neither loads (or with the non_vr launch option, when it asks for
    /// neither). Asking for Oculus crashes natively in the game's 2016 Oculus plugin when an Oculus runtime
    /// is found but won't load (see OculusRuntimeLoads): with that, a PC crashes at startup whether a
    /// headset is on or not. SteamVR puts SteamVR first; Auto does that when the game was launched with
    /// -vrmode openvr or when the Oculus runtime is missing or won't load; Oculus keeps the game's own order.
    /// Whenever SteamVR goes first, Oculus is only asked for after it if its runtime loads.
    /// </summary>
    internal static class VrDevice
    {
        private static FieldInfo state;   // the iterator's state (0 = not started)
        private static FieldInfo wantVr;  // false with the non_vr launch option
        private static FieldInfo devices; // the device list it asks for
        private static FieldInfo current; // what it yields
        private static string[] order;    // what the game asks for instead

        // Safe to patch from plugin Awake (and it must be: the game picks the device early in its first
        // scene): the selection touches no Photon or network types (see Patches.ApplyDeferred).
        internal static void Patch(Harmony harmony)
        {
            if (Plugin.VrDevice.Value == VrDeviceChoice.Oculus)
            {
                Plugin.Log.LogInfo("VR device: the game's order, Oculus first ([VR] Device = Oculus)");
                return;
            }
            string runtime;
            bool oculusLoads = OculusRuntimeLoads(out runtime);
            string why;
            if (Plugin.VrDevice.Value == VrDeviceChoice.SteamVR) why = "[VR] Device = SteamVR";
            else if (LaunchedForOpenVR()) why = "launched with -vrmode openvr";
            else if (!oculusLoads) why = runtime;
            else
            {
                Plugin.Log.LogInfo("VR device: the game's order, Oculus first (Oculus runtime loads: " + runtime + ")");
                return;
            }
            order = oculusLoads ? new[] { "OpenVR", "Oculus" } : new[] { "OpenVR" };

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
            Plugin.Log.LogInfo(oculusLoads
                ? "VR device: SteamVR first, then Oculus (" + why + ")"
                : "VR device: SteamVR only, no Oculus (" + why + (why == runtime ? "" : "; " + runtime) + ")");
        }

        private static bool LaunchedForOpenVR()
        {
            var args = Environment.GetCommandLineArgs();
            int at = Array.FindIndex(args, a => a.Equals("-vrmode", StringComparison.OrdinalIgnoreCase));
            return at >= 0 && at + 1 < args.Length && args[at + 1].Equals("openvr", StringComparison.OrdinalIgnoreCase);
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
            devices.SetValue(__instance, order);
            VRSettings.LoadDeviceByName(order);
            current.SetValue(__instance, null);
            state.SetValue(__instance, 1);
            __result = true;
            return false;
        }
    }
}
