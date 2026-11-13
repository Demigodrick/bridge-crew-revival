using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace StbcRevival
{
    /// <summary>
    /// Lists of some of the game's objects (the players, the character creator, the shuttle's menus and news pages),
    /// kept as the game makes them, so features that look for them every second needn't search the whole scene.
    /// Object.FindObjectsOfType looks through every object there is: on a bridge, over 120,000, about 10-20 ms a
    /// search. Several features did that once a second, in the same frame, which showed as a stutter while walking.
    ///
    /// Each type's constructor gets a postfix that keeps the new object (Unity makes components with their
    /// constructor, sometimes off the main thread while loading, so it's only kept there, under a lock). A lookup
    /// gives the ones still there, active and in the game (not prefabs: see All); destroyed ones drop out.
    /// Subclasses' constructors are patched too (NovaPlayerInfo's), as the runtime may build a base constructor into
    /// them. Once a scene has been loaded a few seconds, each list is checked against one search of the scene: one
    /// that missed anything takes them in, and one that misses again is dropped, and that type searched for as before (said in the log). The same if a
    /// type's constructor can't be patched.
    /// </summary>
    internal static class Tracked
    {
        private static readonly object gate = new object();
        private static readonly Dictionary<Type, HashSet<UnityEngine.Object>> made = new Dictionary<Type, HashSet<UnityEngine.Object>>();
        private static readonly HashSet<Type> patched = new HashSet<Type>();

        /// <summary>The types kept: a subclass's objects are kept with its base type (NovaPlayerInfo with PlayerInfo).</summary>
        private static readonly Type[] Types =
        {
            typeof(PlayerInfo), typeof(CustomizationManager), typeof(ConsoleMainMenu), typeof(NewsPage), typeof(InviteActionUI),
        };

        /// <summary>Subclasses whose constructors are patched as well.</summary>
        private static readonly Type[] Subclasses = { typeof(NovaPlayerInfo) };

        internal static void Patch(Harmony harmony)
        {
            var postfix = new HarmonyMethod(typeof(Tracked), nameof(Made));
            foreach (var type in Types)
            {
                try
                {
                    var ctor = AccessTools.Constructor(type, Type.EmptyTypes);
                    if (ctor == null) { Plugin.Log.LogWarning($"Tracked: {type.Name} has no constructor to follow; it's searched for as before"); continue; }
                    lock (gate) made[type] = new HashSet<UnityEngine.Object>(SameObject.Instance);
                    harmony.Patch(ctor, postfix: postfix);
                    foreach (var sub in Subclasses)
                        if (type.IsAssignableFrom(sub)) harmony.Patch(AccessTools.Constructor(sub, Type.EmptyTypes), postfix: postfix);
                    lock (gate) patched.Add(type);
                }
                catch (Exception e) { Plugin.Log.LogWarning($"Tracked: {type.Name}: {e.Message}; it's searched for as before"); lock (gate) patched.Remove(type); }
            }
        }

        private static void Made(object __instance)
        {
            if (!(__instance is UnityEngine.Object o)) return;
            var t = o.GetType();
            lock (gate)
                foreach (var kv in made)
                    if (kv.Key.IsAssignableFrom(t)) { kv.Value.Add(o); return; }
        }

        private static readonly List<UnityEngine.Object> gone = new List<UnityEngine.Object>();
        private static int checkedScene = -1;
        private static float checkAt = -1f;

        /// <summary>Every frame: a few seconds into each scene, each list is checked once against a search of the scene.</summary>
        internal static void Update()
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            int id = scene.IsValid() ? scene.GetHashCode() ^ (scene.GetRootGameObjects().Length > 0 ? scene.GetRootGameObjects()[0].GetInstanceID() : 0) : 0;
            if (id != checkedScene) { checkedScene = id; checkAt = Time.unscaledTime + 5f; return; }
            if (checkAt < 0f || Time.unscaledTime < checkAt) return;
            checkAt = -1f;
            Check<PlayerInfo>(); Check<CustomizationManager>(); Check<ConsoleMainMenu>(); Check<NewsPage>(); Check<InviteActionUI>();
        }

        private static readonly HashSet<UnityEngine.Object> assets = new HashSet<UnityEngine.Object>(SameObject.Instance);
        private static readonly HashSet<Type> missedBefore = new HashSet<Type>();

        // A list that missed objects takes them in (and says which, and why it might have missed them); one that
        // misses again in a later scene is dropped, and that type searched for as before.
        private static void Check<T>() where T : Component
        {
            Type key = typeof(T);
            lock (gate) if (!patched.Contains(key)) return;
            var kept = new HashSet<UnityEngine.Object>(SameObject.Instance);
            foreach (var c in All<T>()) kept.Add(c);
            var missed = new List<string>();
            var found = UnityEngine.Object.FindObjectsOfType<T>();
            var real = new HashSet<UnityEngine.Object>(SameObject.Instance);
            foreach (var o in found) real.Add(o);
            foreach (var o in kept)
                if (!real.Contains(o))
                {
                    // Not in the scene (shouldn't happen: All holds back what it isn't sure of). Taken out for good.
                    lock (gate) { assets.Add(o); made[key].Remove(o); live.Remove(o); }
                    Plugin.Log.LogInfo($"Tracked: {o.GetType().Name} on {((Component)o).gameObject.name} isn't in the scene (a prefab); left out of the list");
                }
            foreach (var o in found)
            {
                if (kept.Contains(o)) continue;
                bool known;
                lock (gate) { known = made[key].Contains(o); made[key].Add(o); }
                var go = o.gameObject;
                missed.Add($"{o.GetType().Name} on {go.name} (scene '{go.scene.name}' valid {go.scene.IsValid()}, active {go.activeInHierarchy}, "
                    + (known ? "kept but left out of the list" : "never seen being made") + ")");
            }
            if (missed.Count == 0) return;
            bool again;
            lock (gate) { again = !missedBefore.Add(key); if (again) patched.Remove(key); }
            Plugin.Log.LogWarning($"Tracked: the list of {key.Name} missed {missed.Count}: {string.Join("; ", missed.ToArray())}"
                + (again ? "; it missed before, so they're searched for as before" : "; the missed ones are in the list now"));
        }

        // Objects whose scene isn't valid are either kept between scenes (your own player) or prefabs and other
        // assets; on this Unity (5.4) nothing about them tells which (both: no name, handle 0), and a prefab must
        // never be handed out: touching its Transform crashes the game. So such an object is held back until a search
        // of the scene says which it is, once, when it first turns up active (not every lookup).
        private static readonly HashSet<UnityEngine.Object> live = new HashSet<UnityEngine.Object>(SameObject.Instance);
        private static readonly List<UnityEngine.Object> unsure = new List<UnityEngine.Object>();

        /// <summary>The T objects in the loaded scenes that are active, as FindObjectsOfType&lt;T&gt;() gives them.</summary>
        internal static List<T> All<T>() where T : Component
        {
            var result = new List<T>();
            Type key = null;
            foreach (var t in Types) if (t.IsAssignableFrom(typeof(T))) { key = t; break; }
            bool kept;
            lock (gate) kept = key != null && patched.Contains(key);
            if (!kept) { result.AddRange(UnityEngine.Object.FindObjectsOfType<T>()); return result; }
            lock (gate)
            {
                gone.Clear(); unsure.Clear();
                foreach (var o in made[key])
                {
                    if (o == null) { gone.Add(o); continue; } // destroyed
                    var go = ((Component)o).gameObject;
                    if (go.scene.IsValid() || assets.Contains(o) || live.Contains(o)) continue;
                    if (go.activeInHierarchy) unsure.Add(o);
                }
                foreach (var o in gone) { made[key].Remove(o); live.Remove(o); }
            }
            if (unsure.Count > 0)
            {
                // One search says which are in the game; the rest are assets (they're active, so a search would find them).
                var found = new HashSet<UnityEngine.Object>(SameObject.Instance);
                foreach (var o in UnityEngine.Object.FindObjectsOfType(key)) found.Add(o);
                lock (gate)
                    foreach (var o in unsure)
                    {
                        if (found.Contains(o)) live.Add(o);
                        else { assets.Add(o); made[key].Remove(o); }
                    }
            }
            lock (gate)
                foreach (var o in made[key])
                {
                    if (o == null || !(o is T c)) continue;
                    var go = c.gameObject;
                    if (!go.activeInHierarchy) continue; // switched off
                    if (go.scene.IsValid() || live.Contains(o)) result.Add(c);
                }
            return result;
        }

        /// <summary>
        /// The same object, by reference: Unity's own comparison counts a destroyed object, or one still being made
        /// (in its constructor, before it has an instance ID), as equal to null and to each other.
        /// </summary>
        private sealed class SameObject : IEqualityComparer<UnityEngine.Object>
        {
            public static readonly SameObject Instance = new SameObject();
            public bool Equals(UnityEngine.Object a, UnityEngine.Object b) => ReferenceEquals(a, b);
            public int GetHashCode(UnityEngine.Object o) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);
        }

        /// <summary>The first such object, as FindObjectOfType&lt;T&gt;(), or null.</summary>
        internal static T First<T>() where T : Component
        {
            var all = All<T>();
            return all.Count > 0 ? all[0] : null;
        }
    }
}
