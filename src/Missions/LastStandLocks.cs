using System;
using HarmonyLib;
using UnityEngine;

namespace StbcRevival.Missions
{
    /// <summary>
    /// Last Stand is fought where you start: the helm can't warp out or impulse to another point in the
    /// system. Applied on every player's machine (whoever is at the helm), from the room's Revival choice.
    /// Ordinary throttle and steering are untouched.
    /// </summary>
    internal static class LastStandLocks
    {
        private static int checkedFrame = -1;
        private static bool locked;

        internal static bool Locked
        {
            get
            {
                if (checkedFrame != Time.frameCount)
                {
                    checkedFrame = Time.frameCount;
                    try { locked = RevivalMissions.Selected == typeof(LastStandSequence); }
                    catch { locked = false; }
                }
                return locked;
            }
        }

        internal static void Patch(Harmony harmony)
        {
            int patched = 0;
            foreach (var getter in new[] { "get_CanWarp", "get_CanImpulse" })
                patched += Try(harmony, getter, postfix: nameof(Cannot_Postfix));
            foreach (var engage in new[] { "OnEngageWarpDown", "OnEngageImpulseDown", "OnEngageWarpImpulse" })
                patched += Try(harmony, engage, prefix: nameof(Engage_Prefix));
            patched += Try(harmony, "OnWarpThrottleValueChanged", prefix: nameof(Throttle_Prefix));
            Plugin.Log.LogInfo($"Last Stand: {patched} of 6 helm travel locks in place");
        }

        private static int Try(Harmony harmony, string method, string prefix = null, string postfix = null)
        {
            var target = AccessTools.Method(typeof(ConsoleHelm), method);
            if (target == null)
            {
                Plugin.Log.LogWarning($"Last Stand: ConsoleHelm.{method} not found; that travel lock is off");
                return 0;
            }
            harmony.Patch(target,
                prefix: prefix == null ? null : new HarmonyMethod(typeof(LastStandLocks), prefix),
                postfix: postfix == null ? null : new HarmonyMethod(typeof(LastStandLocks), postfix));
            return 1;
        }

        private static void Cannot_Postfix(ref bool __result)
        {
            if (__result && Locked) __result = false;
        }

        private static bool Engage_Prefix() => !Locked;

        // The warp throttle (warp, and impulse travel on the Enterprise-D) stays at off.
        private static void Throttle_Prefix(ref float __0)
        {
            if (__0 > 0f && Locked) __0 = 0f;
        }
    }
}
