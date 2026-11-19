using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace StbcRevival
{
    /// <summary>
    /// [Debug] SlowCallProbe (a development setting, off by default): times the game's per-object entry points
    /// (Awake, Start, OnEnable, Update, LateUpdate, FixedUpdate, BatchUpdate, BatchLateUpdate) and every coroutine
    /// step (the compiler's iterator MoveNext, the game's and Revival's), and logs any call over 50 ms as
    /// "[diag] slow call". For finding what holds up a frame; patching them all adds a few seconds to the first
    /// frame and a little to every call, so it's not for playing with.
    /// </summary>
    internal static class SlowCallProbe
    {
        private static readonly long Threshold = Stopwatch.Frequency / 20; // 50 ms

        private static readonly HashSet<string> EntryPoints = new HashSet<string>
            { "Awake", "Start", "OnEnable", "Update", "LateUpdate", "FixedUpdate", "BatchUpdate", "BatchLateUpdate", "MoveNext" };

        internal static void Patch(Harmony harmony)
        {
            if (!Plugin.SlowCallProbe.Value) return;
            var clock = Stopwatch.StartNew();
            var prefix = new HarmonyMethod(typeof(SlowCallProbe), nameof(Before));
            var postfix = new HarmonyMethod(typeof(SlowCallProbe), nameof(After));
            int patched = 0, failed = 0;
            foreach (var assembly in new[] { typeof(IntroSequence).Assembly, typeof(SlowCallProbe).Assembly })
            {
                bool own = assembly == typeof(SlowCallProbe).Assembly;
                foreach (var type in Types(assembly))
                {
                    if (type.IsInterface || type.ContainsGenericParameters || type == typeof(SlowCallProbe)) continue;
                    bool iterator = type.Name.StartsWith("<", StringComparison.Ordinal);
                    foreach (var m in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        if (!EntryPoints.Contains(m.Name) || m.IsAbstract || m.ContainsGenericParameters || m.GetParameters().Length > 0) continue;
                        if (m.Name == "MoveNext" ? !iterator : own) continue; // Revival: its coroutines only
                        try
                        {
                            if (m.GetMethodBody() == null) continue;
                            harmony.Patch(m, prefix, postfix);
                            patched++;
                        }
                        catch (Exception)
                        {
                            failed++;
                        }
                    }
                }
            }
            Plugin.Log.LogInfo($"[diag] slow call probe: timing {patched} methods ({failed} couldn't be patched), set up in {clock.ElapsedMilliseconds} ms");
        }

        private static IEnumerable<Type> Types(Assembly assembly)
        {
            try { return assembly.GetTypes(); }
            catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null); }
        }

        private static void Before(out long __state) => __state = Stopwatch.GetTimestamp();

        private static void After(long __state, MethodBase __originalMethod)
        {
            long took = Stopwatch.GetTimestamp() - __state;
            if (took < Threshold) return;
            Plugin.Log.LogInfo($"[diag] slow call: {__originalMethod.DeclaringType?.FullName}.{__originalMethod.Name} took {took * 1000 / Stopwatch.Frequency} ms, " +
                               $"{UnityEngine.Time.realtimeSinceStartup:0.0} s after launch");
        }
    }
}
