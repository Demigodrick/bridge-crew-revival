using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Sector.Spacecraft;
using UnityEngine;

namespace StbcRevival
{
    /// <summary>
    /// PvP test: keeps each ship's goings-on to that ship. The game was made for one player ship, so its event
    /// bus (CIFDOFLFGEA, dispatched in FFEMIIJDFMK.EGPBPJOLJGC) says nothing about which ship an order is for:
    /// a helm's course (Crew.NJAILKFJGJF) reached both ships' engines, an engineer's power routing both warp
    /// cores, and the other set's consoles raised contacts and hails on your bridge. Here:
    ///   - Each piece of code runs "for" a ship (the context): a console of either set while it updates or takes
    ///     its player's state, a ship part while it takes console state, and an event listener of either ship
    ///     while it handles an event. Listeners belong to the ship they're part of, or to the set of consoles
    ///     they're in; anything else (the bridge, the mission, music) belongs to no ship.
    ///   - An event sent for one ship skips the other ship's listeners; one sent by the other ship's code reaches
    ///     only that ship's (not the bridge's: its captain console's "Hull down to" warning). Impulse and warp phase events name
    ///     their ship, and go to that ship's listeners only (bridge-wide ones count as your ship's: no warp
    ///     effects on your viewscreen when the other ship warps).
    ///   - What code for the other ship says to the bridge (console messages, crew and computer voice) is
    ///     dropped: it's about the other ship. Each message is logged once.
    /// Only while the PvP test runs with both ships bound.
    /// </summary>
    internal static class PvpRouting
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        /// <summary>The ship the running code is for ("A", "B"), or null when it isn't known.</summary>
        internal static string Context { get; private set; }

        // The context, and with it the ship view the handles show (PvpScope).
        private static void SetContext(string ship)
        {
            Context = ship;
            PvpScope.Switch(ship);
        }

        private static FieldInfo listeners, dispatching, deferred;
        private static MethodInfo cleanup;
        private static readonly Dictionary<Type, FieldInfo> innerListener = new Dictionary<Type, FieldInfo>();
        private static readonly Dictionary<Type, FieldInfo> shipField = new Dictionary<Type, FieldInfo>(); // phase events only
        private static readonly Dictionary<object, string> ownerOf = new Dictionary<object, string>();
        private static readonly HashSet<string> keptOff = new HashSet<string>();
        private static float nextForget;

        private static bool Routing => PvpPrototype.Active && PvpPrototype.Bound != null;
        private static string Mine => PvpShips.Mine;

        internal static void Patch(Harmony harmony)
        {
            var bus = AccessTools.TypeByName("FFEMIIJDFMK");
            var dispatch = bus == null ? null : AccessTools.Method(bus, "EGPBPJOLJGC");
            listeners = bus == null ? null : AccessTools.Field(bus, "KPLOCHIOGLH");
            dispatching = bus == null ? null : AccessTools.Field(bus, "PHDHIABPFNK");
            deferred = bus == null ? null : AccessTools.Field(bus, "AODEJDMMCEH");
            var gameEvent = AccessTools.TypeByName("DGHCMKEKBIA");
            cleanup = gameEvent == null ? null : AccessTools.Method(gameEvent, "Cleanup");
            if (dispatch == null || listeners == null || dispatching == null || deferred == null || cleanup == null)
                Plugin.Log.LogWarning("PvP: the event bus wasn't found; events for one ship reach both");
            else harmony.Patch(dispatch, prefix: new HarmonyMethod(typeof(PvpRouting), nameof(Dispatch_Prefix)));

            foreach (var name in new[] { "Sector.LIPGLFKKOKL", "Sector.HHMLMPCBLOB" }) // impulse phase, warp phase
            {
                var type = AccessTools.TypeByName(name);
                var field = type == null ? null : AccessTools.GetDeclaredFields(type).FirstOrDefault(f => !f.IsStatic && typeof(BaseSpacecraft).IsAssignableFrom(f.FieldType));
                if (field == null) Plugin.Log.LogWarning($"PvP: the phase event {name}'s ship wasn't found");
                else shipField[type] = field;
            }

            // Consoles: while they update or take their player's state. Ship parts: while they take console state.
            var console = new HarmonyMethod(typeof(PvpRouting), nameof(Console_Prefix));
            var part = new HarmonyMethod(typeof(PvpRouting), nameof(Part_Prefix));
            var leave = new HarmonyMethod(typeof(PvpRouting), nameof(Leave_Finalizer));
            foreach (var type in new[] { typeof(ConsoleCaptain), typeof(ConsoleHelm), typeof(ConsoleTactical), typeof(ConsoleEngineering) })
            {
                var update = AccessTools.Method(type, "UpdateManually");
                if (update == null) Plugin.Log.LogWarning($"PvP: {type.Name}.UpdateManually not found");
                else harmony.Patch(update, prefix: console, finalizer: leave);
            }
            harmony.Patch(AccessTools.Method(typeof(ConsoleBase), "ConsoleDeserialize"), prefix: console, finalizer: leave);
            // Orders sent from a console's own parts outside its update (clicks, its UI's updates) and a ship's
            // status effect (a nebula's): the helm's course and warp target, the engineer's power routing.
            var helm = AccessTools.TypeByName("Crew.ConsoleHelmShipInterface");
            foreach (var name in new[] { "set_POITarget", "set_WarpTarget", "SetTarget" })
                PatchEntry(harmony, helm == null ? null : AccessTools.Method(helm, name), part, leave, "ConsoleHelmShipInterface." + name);
            PatchEntry(harmony, AccessTools.Method(typeof(BaseSpacecraft), "KMHNOKFDFAB"), part, leave, "BaseSpacecraft.KMHNOKFDFAB");
            var graph = AccessTools.TypeByName("Sector.Spacecraft.Subsystems.CIMNOCCODAC");
            gridGraph = AccessTools.Field(typeof(EngineerPowerGrid), "CFPJPPMLGKH");
            var graphEntry = new HarmonyMethod(typeof(PvpRouting), nameof(Graph_Prefix));
            foreach (var name in new[] { "SendDistributionUpdate", "ADKNCMHHNMK", "KLICDPIAFHF" })
                PatchEntry(harmony, graph == null || gridGraph == null ? null : AccessTools.Method(graph, name), graphEntry, leave, "power graph " + name);
            PatchEntry(harmony, AccessTools.Method(typeof(AI.USSAegisCrew), "BatchUpdate"), part, leave, "USSAegisCrew.BatchUpdate");
            int parts = 0;
            foreach (var type in typeof(ConsoleBase).Assembly.GetTypes())
            {
                if (!typeof(Component).IsAssignableFrom(type) || typeof(ConsoleBase).IsAssignableFrom(type)) continue;
                var method = type.GetMethod("ConsoleDeserialize", All | BindingFlags.DeclaredOnly);
                if (method == null || method.IsAbstract || method.GetParameters().Length != 2) continue;
                harmony.Patch(method, prefix: part, finalizer: leave);
                parts++;
            }

            // What the other ship's code says to the bridge.
            var mute = new HarmonyMethod(typeof(PvpRouting), nameof(Voice_Prefix));
            foreach (var name in new[] { "PlayVO", "PlayVO_Networked" })
            {
                var play = AccessTools.Method(typeof(VOPlaybackManager), name);
                if (play == null) Plugin.Log.LogWarning($"PvP: VOPlaybackManager.{name} not found");
                else harmony.Patch(play, prefix: mute);
            }
            Plugin.Log.LogInfo($"PvP: events routed by ship ({parts} ship parts' console state)");
        }

        /// <summary>The other ship's code is running: what it says to the bridge isn't about your ship.</summary>
        internal static bool Muted => Routing && Context != null && Context != Mine;

        /// <summary>A console message from the other ship's code is dropped (logged once).</summary>
        internal static bool KeepOff(string text)
        {
            if (!Muted) return false;
            if (keptOff.Add(text ?? "")) Plugin.Log.LogInfo($"PvP: ship {Context}'s console message '{text}' kept off your bridge");
            return true;
        }

        private static bool Voice_Prefix() => !Muted;

        // ---- Context ------------------------------------------------------------------------------

        private static void Console_Prefix(ConsoleBase __instance, out string __state)
        {
            __state = Context;
            if (Routing) SetContext(PvpConsoles.SetOf(__instance) ?? Context);
        }

        private static void Part_Prefix(Component __instance, out string __state)
        {
            __state = Context;
            if (Routing) SetContext(Owner(__instance) ?? Context);
        }

        private static void PatchEntry(Harmony harmony, MethodBase method, HarmonyMethod prefix, HarmonyMethod finalizer, string what)
        {
            if (method == null) Plugin.Log.LogWarning($"PvP: {what} not found; its orders may reach both ships");
            else harmony.Patch(method, prefix: prefix, finalizer: finalizer);
        }

        // The power graph is a plain object: its ship is that of the engineering console's grid holding it.
        private static FieldInfo gridGraph;
        private static readonly Dictionary<object, Component> gridOf = new Dictionary<object, Component>();
        private static float nextGrids;

        private static void Graph_Prefix(object __instance, out string __state)
        {
            __state = Context;
            if (!Routing) return;
            if (!gridOf.TryGetValue(__instance, out var grid) && Time.unscaledTime > nextGrids)
            {
                nextGrids = Time.unscaledTime + 1f;
                gridOf.Clear();
                foreach (var each in Resources.FindObjectsOfTypeAll<EngineerPowerGrid>())
                {
                    var held = each == null ? null : gridGraph.GetValue(each);
                    if (held != null) gridOf[held] = each;
                }
                gridOf.TryGetValue(__instance, out grid);
            }
            SetContext(Owner(grid) ?? Context);
        }

        private static Exception Leave_Finalizer(string __state, Exception __exception)
        {
            if (Context != __state) SetContext(__state);
            return __exception;
        }

        /// <summary>The ship a game object belongs to: the player ship it's part of, or its console set.</summary>
        private static string Owner(object target)
        {
            if (!(target is Component component) || component == null) return null;
            if (Time.unscaledTime > nextForget)
            {
                ownerOf.Clear(); // objects come and go, and the sets change as consoles load
                nextForget = Time.unscaledTime + 2f;
            }
            if (ownerOf.TryGetValue(component, out var owner)) return owner;
            owner = null;
            for (var t = component.transform; t != null && owner == null; t = t.parent)
            {
                var ship = t.GetComponent<PlayerSpacecraft>();
                if (ship != null) owner = PvpPrototype.Letter(ship);
            }
            owner = owner ?? PvpConsoles.SetOf(component);
            ownerOf[component] = owner;
            return owner;
        }

        // ---- Events -------------------------------------------------------------------------------

        // As the game's dispatch (FFEMIIJDFMK.EGPBPJOLJGC), listener by listener, skipping the other ship's.
        private static bool Dispatch_Prefix(object __instance, object __0)
        {
            if (!Routing || __0 == null) return true;
            var type = __0.GetType();
            if (listeners.GetValue(__instance) is IDictionary map && map.Contains(type) && map[type] is IList list)
            {
                string eventShip = null;
                if (shipField.TryGetValue(type, out var field) && field.GetValue(__0) is PlayerSpacecraft ship) eventShip = PvpPrototype.Letter(ship);
                string sender = Context;
                dispatching.SetValue(__instance, type);
                for (int i = 0; i < list.Count; i++)
                {
                    if (!(list[i] is Delegate wrapper)) continue;
                    string owner = Owner(Listener(wrapper)?.Target);
                    if (eventShip != null ? (owner ?? Mine) != eventShip : sender != null && (owner ?? Mine) != sender) continue;
                    SetContext(owner ?? sender);
                    try
                    {
                        wrapper.DynamicInvoke(__0);
                    }
                    catch (Exception)
                    {
                        // the game ignores a listener's exception too
                    }
                    finally
                    {
                        SetContext(sender);
                    }
                }
                dispatching.SetValue(__instance, null);
                if (deferred.GetValue(__instance) is Queue<Action> queue)
                    while (queue.Count > 0) queue.Dequeue()();
            }
            cleanup.Invoke(__0, null);
            return false;
        }

        // The bus wraps each listener in a closure; the listener is the closure's delegate field.
        private static Delegate Listener(Delegate wrapper)
        {
            var closure = wrapper.Target;
            if (closure == null) return wrapper;
            var type = closure.GetType();
            if (!innerListener.TryGetValue(type, out var field))
            {
                field = type.GetFields(All).FirstOrDefault(f => typeof(Delegate).IsAssignableFrom(f.FieldType));
                innerListener[type] = field;
            }
            return field?.GetValue(closure) as Delegate ?? wrapper;
        }
    }
}
