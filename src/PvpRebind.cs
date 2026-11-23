using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Sector.Spacecraft;
using UnityEngine;

namespace StbcRevival
{
    /// <summary>
    /// PvP, a game crewing ship B: the first player ship to arrive is ship A, and the game must take it as its player
    /// ship (nobody is seated before a player ship exists, and ship B spawns only once everyone is seated). Ship B
    /// arrives later and is announced too, but what the game set up only once kept ship A: the crew order system's
    /// crew (CommandManager._crew), the crew menus' crew (CrewMenuItem's cache), and possibly more. Two seconds after
    /// your own ship B binds, ship A's parts are swapped for ship B's matching ones:
    ///   - in those two caches;
    ///   - in everything on your bridge and your own console set (the other ship's console set stays ship A's), in
    ///     fields that can only mean your ship (the player ship, its AI crew, its console ship interfaces);
    /// and anything elsewhere still holding ship A is logged once ("[rebind] ... kept"), not changed: sector objects,
    /// enemy AI and the like rightly keep both ships.
    /// </summary>
    internal static class PvpRebind
    {
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        private static PlayerSpacecraft from, to;
        private static float at = -1f;
        private static readonly Dictionary<Type, FieldInfo[]> fieldsOf = new Dictionary<Type, FieldInfo[]>();
        private static readonly FieldInfo OrdersCrew = AccessTools.Field(typeof(AI.CommandManager), "FBEMCAHNKLA");               // _crew
        private static readonly FieldInfo OrdersInstance = AccessTools.Field(typeof(AI.CommandManager), "<MFBACMFBGOL>k__BackingField");
        private static readonly FieldInfo MenuCrew = AccessTools.Field(AccessTools.TypeByName("AI.CHJEFCBPHGN"), "OAEMHOPHIBJ"); // CrewMenuItem's crew

        /// <summary>Your ship bound after another one was: swap the other's parts for yours shortly.</summary>
        internal static void Rebound(PlayerSpacecraft previous, PlayerSpacecraft ship)
        {
            if (previous == null || ship == null || previous == ship) return;
            from = previous;
            to = ship;
            at = Time.unscaledTime + 2f;
        }

        internal static void Forget() { from = to = null; at = -1f; }

        internal static void Update()
        {
            if (at < 0f || Time.unscaledTime < at) return;
            at = -1f;
            if (from == null || to == null) return;
            try { Run(); }
            catch (Exception e) { Plugin.Log.LogWarning("PvP: [rebind] " + e.Message); }
        }

        private static void Run()
        {
            string fromName = from.ALPAOOKFIEE?.BaseName ?? from.name, toName = to.ALPAOOKFIEE?.BaseName ?? to.name;
            var swapped = new List<string>();
            var kept = new HashSet<string>();

            // The two caches the game fills once.
            var ownCrew = to.GetComponent<AI.USSAegisCrew>();
            var orders = OrdersInstance?.GetValue(null);
            if (orders != null && OrdersCrew != null && ownCrew != null && IsOnShip(OrdersCrew.GetValue(orders), from))
            { OrdersCrew.SetValue(orders, ownCrew); swapped.Add("crew orders' crew"); }
            if (MenuCrew != null && ownCrew != null && IsOnShip(MenuCrew.GetValue(null), from))
            { MenuCrew.SetValue(null, ownCrew); swapped.Add("crew menus' crew"); }

            // Everything else that still holds ship A or one of its parts.
            foreach (var behaviour in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>())
            {
                if (behaviour == null || IsOnShip(behaviour, from) || IsOnShip(behaviour, to)) continue;
                string scene = behaviour.gameObject.scene.name ?? "";
                bool mine = (scene.StartsWith("Bridge_scene", StringComparison.Ordinal) || scene.IndexOf("Console", StringComparison.Ordinal) >= 0)
                            && PvpConsoles.SetOf(behaviour) != PvpPrototype.Letter(from);
                for (var type = behaviour.GetType(); type != null && type != typeof(MonoBehaviour); type = type.BaseType)
                    foreach (var field in FieldsOf(type))
                    {
                        object value;
                        try { value = field.GetValue(behaviour); } catch { continue; }
                        if (!IsOnShip(value, from)) continue;
                        string what = $"{type.Name}.{field.Name}";
                        if (!mine) { kept.Add($"{what} ({(scene.Length == 0 ? "no scene" : scene)})"); continue; }
                        if (!MeansYourShip(field.FieldType)) { kept.Add($"{what} (may be ship A as a target)"); continue; }
                        var twin = Twin(value, field.FieldType);
                        if (twin == null) { kept.Add($"{what} (no match on {toName})"); continue; }
                        field.SetValue(behaviour, twin);
                        swapped.Add(what);
                    }
            }
            Plugin.Log.LogInfo($"PvP: [rebind] {fromName} -> {toName}: {(swapped.Count == 0 ? "nothing to swap" : $"swapped {swapped.Count}: " + string.Join(", ", swapped.Distinct().ToArray()))}");
            if (kept.Count > 0) Plugin.Log.LogInfo($"PvP: [rebind] still holding {fromName}, left as they are: {string.Join(", ", kept.Take(40).ToArray())}");
        }

        // Only fields that can only mean "your ship" are swapped: a target, a map marker or any sector body may
        // rightly be ship A, the enemy.
        private static bool MeansYourShip(Type type) =>
            type == typeof(PlayerSpacecraft) || type.Name == "IPlayerSpacecraft" || typeof(AI.USSAegisCrew).IsAssignableFrom(type)
            || typeof(ConsoleShipInterface).IsAssignableFrom(type);

        private static FieldInfo[] FieldsOf(Type type)
        {
            if (fieldsOf.TryGetValue(type, out var list)) return list;
            list = type.GetFields(Fields).Where(f => !f.IsLiteral && !f.IsInitOnly
                && (typeof(Component).IsAssignableFrom(f.FieldType) || f.FieldType == typeof(GameObject) || f.FieldType.IsInterface)).ToArray();
            fieldsOf[type] = list;
            return list;
        }

        private static bool IsOnShip(object value, PlayerSpacecraft ship)
        {
            if (ship == null) return false;
            var t = value is Component c && c != null ? c.transform : value is GameObject g && g != null ? g.transform : null;
            return t != null && (t == ship.transform || t.IsChildOf(ship.transform));
        }

        // Ship B's part matching one of ship A's: the same type, in the same place among that type's parts.
        private static object Twin(object value, Type fieldType)
        {
            if (value is GameObject go)
            {
                var path = PathFrom(from.transform, go.transform);
                var found = path == null ? null : (path.Length == 0 ? to.transform : to.transform.Find(path));
                return found == null ? null : found.gameObject;
            }
            if (!(value is Component part)) return null;
            var ofType = from.GetComponentsInChildren(part.GetType(), true);
            int index = Array.IndexOf(ofType, part);
            var theirs = to.GetComponentsInChildren(part.GetType(), true);
            if (index < 0 || ofType.Length != theirs.Length) return null;
            var twin = theirs[index];
            return fieldType.IsInstanceOfType(twin) ? twin : null;
        }

        private static string PathFrom(Transform root, Transform t)
        {
            var parts = new List<string>();
            for (; t != null && t != root; t = t.parent) parts.Insert(0, t.name);
            return t == root ? string.Join("/", parts.ToArray()) : null;
        }
    }
}
