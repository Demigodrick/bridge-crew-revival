using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace StbcRevival
{
    /// <summary>
    /// The player's AI crew (the bots at stations no player sits at; USSAegisCrew). Its three trees (helm, tactical,
    /// engineering) are built in code each time the ship arrives in a system (SetUp), and each only runs while a bot holds
    /// that station, so these changes never touch a player's own station. Host only, like all the crew AI.
    ///
    /// - Helm holds weapons range (CrewAI.HelmHoldsRange): with weapons cleared to fire and a ship as its destination,
    ///   the game's helm ran at full throttle straight at it, overshooting and ramming. Now it closes at full speed to
    ///   phaser range, eases off to a stop by half that range with its nose on the target, and backs away at half the
    ///   ordered speed inside 30 % of it. (Circling was left out: it puts the target off the beam, where the phaser arcs
    ///   may not reach.) Replaces the full-throttle SetHelmThrottle (JLFCNDMPFBN) beside the NavMarker throttle steps in
    ///   BuildHelmTree (LNFHNLIBBHI) with a wrapper that runs the game's node at another scale.
    /// - Quicker torpedoes (CrewAI.QuickTorpedoes): the tactical bot waited 1.5 s with its hand on the button before each
    ///   torpedo (now 0.4 s), and only fired torpedoes while the crew had a destination order (IsSlotValid(Destination)),
    ///   so "fire torpedoes" without a move order did nothing. The destination is now needed only for depth charges,
    ///   which aim at it. BuildTacticalTree (BCCLPBBHKIF); the waits apply from the next system after a change.
    /// - Engineering sets power itself (CrewAI.AutoPower): the engineering bot only rerouted power when the captain
    ///   ordered it. Now it picks the captain's own reroute profiles: Offense in a fight, Defense when the shields fall
    ///   below 30 % (back to Offense above 70 %), Balanced once no enemy has been within 25 km for 10 s; at most one change
    ///   every 15 s. A captain's power order holds until the situation changes (a fight starts or ends). Runs beside
    ///   BuildEngineeringTree's (LHDPDMDADMA) root; the order goes through PowerRouteMenuItem's Execute (CKGHIIHKKDC), so
    ///   the bot does it by hand and says so, as when the captain orders it.
    /// </summary>
    internal static class CrewAi
    {
        private const float StopShare = 0.5f;        // stopped by this share of phaser range (full speed beyond the range)
        private const float BackOffShare = 0.3f;     // backing away inside this share
        private const float BackOffThrottle = 0.5f;  // of the ordered speed
        private const float DefaultRange = 10000f;   // metres, when the console's phaser range can't be read
        private const float TorpedoDelay = 0.4f;     // seconds the hand rests on the button (the game's 1.5)
        private const float CombatRange = 25000f, CalmAfter = 10f, PowerEvery = 15f, PowerCheckEvery = 2f;
        private const float DefenseBelow = 0.3f, OffenseAbove = 0.7f;

        private static readonly Dictionary<Type, FieldInfo[]> childFields = new Dictionary<Type, FieldInfo[]>();
        private static readonly AccessTools.FieldRef<AI.JLFCNDMPFBN, float> ThrottleScale = AccessTools.FieldRefAccess<AI.JLFCNDMPFBN, float>("OOGEJCBJMKO");
        private static readonly AccessTools.FieldRef<AI.GMOMNCADEJC, AI.GBEFAHLEGIO> ValidSlot = AccessTools.FieldRefAccess<AI.GMOMNCADEJC, AI.GBEFAHLEGIO>("KKBOJCMFALF");
        private static readonly AccessTools.FieldRef<AI.KNMMMJOOBHC, float> WaitSeconds = AccessTools.FieldRefAccess<AI.KNMMMJOOBHC, float>("NDGBBEEOPAF");
        private static readonly AccessTools.FieldRef<AI.KNMMMJOOBHC, AI.JEHDNFHCICK> WaitType = AccessTools.FieldRefAccess<AI.KNMMMJOOBHC, AI.JEHDNFHCICK>("CBBOKGPJINC");
        private static readonly AccessTools.FieldRef<ConsoleTactical, int> PhaserRange = AccessTools.FieldRefAccess<ConsoleTactical, int>("ELHMOBNJEIA");
        private static MethodInfo powerExecute;
        private static Func<Sector.FAMOMDAAPCH, float> shieldHealth, shieldMax;
        private static bool loggedRange, loggedHelm, loggedTorpedo, loggedPower;

        internal static void Patch(Harmony harmony)
        {
            int ok = 0;
            HarmonyMethod Me(string name) => new HarmonyMethod(typeof(CrewAi), name);
            void Postfix(string method, string patch)
            {
                var m = AccessTools.DeclaredMethod(typeof(AI.USSAegisCrew), method);
                if (m == null) { Plugin.Log.LogWarning($"Crew AI: USSAegisCrew.{method} wasn't found"); return; }
                harmony.Patch(m, postfix: Me(patch));
                ok++;
            }
            Postfix("LNFHNLIBBHI", nameof(Helm_Postfix));        // BuildHelmTree
            Postfix("BCCLPBBHKIF", nameof(Tactical_Postfix));    // BuildTacticalTree
            Postfix("LHDPDMDADMA", nameof(Engineering_Postfix)); // BuildEngineeringTree
            powerExecute = AccessTools.DeclaredMethod(typeof(AI.GJKEHBHBCPK), "CKGHIIHKKDC"); // PowerRouteMenuItem.Execute
            if (powerExecute != null) harmony.Patch(powerExecute, prefix: Me(nameof(PowerOrder_Prefix)));
            var health = AccessTools.Method(typeof(Sector.FAMOMDAAPCH), "get_ShieldHealth");
            var max = AccessTools.Method(typeof(Sector.FAMOMDAAPCH), "get_ShieldMaxHealth");
            if (health != null && max != null)
            {
                shieldHealth = AccessTools.MethodDelegate<Func<Sector.FAMOMDAAPCH, float>>(health);
                shieldMax = AccessTools.MethodDelegate<Func<Sector.FAMOMDAAPCH, float>>(max);
            }
            Plugin.Log.LogInfo($"Crew AI: {ok} of 3 trees patched (helm holds range {On(Plugin.CrewHelmHoldsRange)}, quick torpedoes {On(Plugin.CrewQuickTorpedoes)}, automatic power {On(Plugin.CrewAutoPower)})");
        }

        private static string On(BepInEx.Configuration.ConfigEntry<bool> entry) => entry.Value ? "on" : "off";

        // ---- Tree walking ----------------------------------------------------------------------------------------------

        // The fields of a node type holding child nodes (one node, or an array).
        private static FieldInfo[] Children(Type type)
        {
            if (childFields.TryGetValue(type, out var found)) return found;
            var list = new List<FieldInfo>();
            for (var t = type; t != null && t != typeof(object); t = t.BaseType)
                foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (f.FieldType == typeof(AI.GGCBOJKCOMJ) || f.FieldType == typeof(AI.GGCBOJKCOMJ[])) list.Add(f);
            return childFields[type] = list.ToArray();
        }

        private static AI.GGCBOJKCOMJ[] Kids(AI.GGCBOJKCOMJ node)
        {
            foreach (var f in Children(node.GetType()))
                if (f.FieldType == typeof(AI.GGCBOJKCOMJ[])) return f.GetValue(node) as AI.GGCBOJKCOMJ[];
            return null;
        }

        // Every node of the tree, depth first (each node once).
        private static void Walk(AI.GGCBOJKCOMJ node, Action<AI.GGCBOJKCOMJ> visit, HashSet<AI.GGCBOJKCOMJ> seen = null)
        {
            if (node == null) return;
            seen = seen ?? new HashSet<AI.GGCBOJKCOMJ>();
            if (!seen.Add(node)) return;
            visit(node);
            foreach (var f in Children(node.GetType()))
            {
                if (f.GetValue(node) is AI.GGCBOJKCOMJ[] many) { foreach (var child in many) Walk(child, visit, seen); }
                else Walk(f.GetValue(node) as AI.GGCBOJKCOMJ, visit, seen);
            }
        }

        // ---- Shared reads ----------------------------------------------------------------------------------------------

        private static Dictionary<AI.IAHDEGHOPAK, AI.OJADMPIBCFG> Orders => AI.CommandManager.JOMJCMPJGEK?.AACGBEMCAGH;

        private static bool Order(AI.IAHDEGHOPAK key) => Orders != null && Orders.TryGetValue(key, out var v) && v != null && v.bVal;

        private static SystemTransform Me(AI.MNIJEDINGOI bb) => (bb?.Owner as AI.USSAegisCrew)?.AHMPFHDGMKJ?.ALPAOOKFIEE;

        private static float Range()
        {
            int range = 0;
            try { var tactical = Bridge.KHFJCHHMFHN; if (tactical != null) range = PhaserRange(tactical); } catch { }
            float metres = range >= 1000 && range <= 60000 ? range : DefaultRange;
            if (!loggedRange)
            {
                loggedRange = true;
                Plugin.Log.LogInfo($"Crew AI: phaser range reads {range}; holding range from {metres:0} m");
            }
            return metres;
        }

        // ---- Helm: hold weapons range ----------------------------------------------------------------------------------

        private static void Helm_Postfix(AI.GGCBOJKCOMJ __result)
        {
            int throttles = 0;
            try
            {
                Walk(__result, node =>
                {
                    var kids = Kids(node);
                    if (kids == null || kids.Length != 2) return;
                    // Selector(Sequence(OrderMoveModeIs, ...), SetHelmThrottle(1)): the full throttle used while firing.
                    if (node is AI.EPMNKCFGGAG && kids[1] is AI.JLFCNDMPFBN throttle && kids[0] is AI.JMDIFJIGIBC steps
                        && Kids(steps) is AI.GGCBOJKCOMJ[] first && first.Length > 0 && first[0] is AI.JOLNDBFGKCM)
                    {
                        kids[1] = new HoldRangeThrottle(throttle);
                        throttles++;
                    }
                });
            }
            catch (Exception e) { Plugin.Log.LogWarning("Crew AI: helm tree: " + e.Message); }
            if (!loggedHelm)
            {
                loggedHelm = true;
                Plugin.Log.LogInfo($"Crew AI: helm tree: {throttles} throttle step(s) hold weapons range");
            }
        }

        // The ship the helm is closing on while weapons are cleared to fire, and how far it is; null when the helm is
        // doing anything else (moving to a place, evading, weapons held, the setting off).
        private static AI.AgentShip Engaged(AI.MNIJEDINGOI bb, out double distance, out SystemTransform me, out SystemTransform them)
        {
            distance = 0; me = null; them = null;
            if (!Plugin.CrewHelmHoldsRange.Value) return null;
            var orders = Orders;
            if (orders == null) return null;
            if (!orders.TryGetValue(AI.IAHDEGHOPAK.MoveMode, out var mode) || mode == null || mode.moveMode != AI.LMHJLBGOFGM.NavMarker) return null;
            if (!(Order(AI.IAHDEGHOPAK.CanFirePhasersOnTarget) || Order(AI.IAHDEGHOPAK.CanFireTorpedoesOnTarget) || Order(AI.IAHDEGHOPAK.CanFocusFire))) return null;
            if (!orders.TryGetValue(AI.IAHDEGHOPAK.Destination, out var dest) || dest?.targetable == null) return null;
            var ship = dest.targetable.AHMPFHDGMKJ as AI.AgentShip;
            if (ship == null || !ship.gameObject.activeInHierarchy) return null;
            me = Me(bb);
            them = ship.ALPAOOKFIEE;
            if (me == null || them == null || me.System != them.System) return null;
            distance = (them.Position - me.Position).magnitude;
            return ship;
        }

        private sealed class HoldRangeThrottle : AI.GGCBOJKCOMJ
        {
            private readonly AI.JLFCNDMPFBN game;
            public HoldRangeThrottle(AI.JLFCNDMPFBN game) { this.game = game; }

            public override void Init(AI.CJJGGNNFNLJ scheduler) { base.Init(scheduler); game.Init(scheduler); }

            protected override AI.LECHLIEBBEM IKLNPCHBLCA(AI.MNIJEDINGOI bb, AI.LECHLIEBBEM childResult)
            {
                float scale = 1f;
                try
                {
                    if (Engaged(bb, out double d, out _, out _) != null)
                    {
                        float range = Range();
                        float moving = Orders.TryGetValue(AI.IAHDEGHOPAK.MoveSpeed, out var speed) && speed != null ? speed.fVal : 0f;
                        if (d < range * BackOffShare && moving > 0f) scale = -BackOffThrottle;              // too close: back away, nose on
                        else if (d < range) scale = Mathf.InverseLerp(range * StopShare, range, (float)d);  // ease off to a stop
                    }
                }
                catch { scale = 1f; }
                float was = ThrottleScale(game);
                ThrottleScale(game) = scale;
                try { return game.ProcessUpdate(bb, AI.LECHLIEBBEM.NONE, false); }
                finally { ThrottleScale(game) = was; }
            }
        }

        // ---- Tactical: quicker torpedoes -------------------------------------------------------------------------------

        private static void Tactical_Postfix(AI.GGCBOJKCOMJ __result)
        {
            int checks = 0, waits = 0;
            bool quick = Plugin.CrewQuickTorpedoes.Value;
            try
            {
                Walk(__result, node =>
                {
                    var kids = Kids(node);
                    if (!(node is AI.JMDIFJIGIBC) || kids == null || !Array.Exists(kids, k => k is AI.HFCKDEECMKF)) return; // a sequence with CanFireTorpedoTube
                    for (int i = 0; i < kids.Length; i++)
                    {
                        if (kids[i] is AI.GMOMNCADEJC valid && ValidSlot(valid) == AI.GBEFAHLEGIO.Destination)
                        {
                            kids[i] = new TorpedoDestination(valid);
                            checks++;
                        }
                        if (!quick) continue;
                        // The wait before firing: a Wait(1.5) in the sequence, or in its selector (the Enterprise-D's is 0.5).
                        if (Shorten(kids[i])) waits++;
                        else if (kids[i] is AI.EPMNKCFGGAG && Kids(kids[i]) is AI.GGCBOJKCOMJ[] options)
                            foreach (var option in options)
                            {
                                if (Shorten(option)) waits++;
                                else if (option is AI.JMDIFJIGIBC && Kids(option) is AI.GGCBOJKCOMJ[] steps)
                                    foreach (var step in steps) if (Shorten(step)) waits++;
                            }
                    }
                });
            }
            catch (Exception e) { Plugin.Log.LogWarning("Crew AI: tactical tree: " + e.Message); }
            if (!loggedTorpedo)
            {
                loggedTorpedo = true;
                Plugin.Log.LogInfo($"Crew AI: tactical tree: {checks} torpedo destination check(s) relaxed, {waits} wait(s) at the button shortened");
            }
        }

        private static bool Shorten(AI.GGCBOJKCOMJ node)
        {
            if (!(node is AI.KNMMMJOOBHC wait) || WaitType(wait) != AI.JEHDNFHCICK.CustomTime || Mathf.Abs(WaitSeconds(wait) - 1.5f) > 0.01f) return false;
            WaitSeconds(wait) = TorpedoDelay;
            return true;
        }

        // The game's IsSlotValid(Destination) before a torpedo: still needed for depth charges, which aim at the
        // destination; otherwise passes.
        private sealed class TorpedoDestination : AI.GGCBOJKCOMJ
        {
            private readonly AI.GMOMNCADEJC game;
            public TorpedoDestination(AI.GMOMNCADEJC game) { this.game = game; }

            public override void Init(AI.CJJGGNNFNLJ scheduler) { base.Init(scheduler); game.Init(scheduler); }

            protected override AI.LECHLIEBBEM IKLNPCHBLCA(AI.MNIJEDINGOI bb, AI.LECHLIEBBEM childResult)
            {
                var result = game.ProcessUpdate(bb, AI.LECHLIEBBEM.NONE, false);
                if (result != AI.LECHLIEBBEM.Fail || !Plugin.CrewQuickTorpedoes.Value) return result;
                try
                {
                    var tactical = Bridge.KHFJCHHMFHN;
                    if (tactical != null && tactical.HNFEOFIENLC) return result; // depth charges
                }
                catch { return result; }
                return AI.LECHLIEBBEM.Success;
            }
        }

        // ---- Engineering: power by the situation -----------------------------------------------------------------------

        private static void Engineering_Postfix(ref AI.GGCBOJKCOMJ __result)
        {
            if (__result == null) return;
            try
            {
                __result = new AI.MOMEJJAOFIM(new AI.GGCBOJKCOMJ[] { __result, new AutoPower() }); // Parallel
                if (!loggedPower)
                {
                    loggedPower = true;
                    Plugin.Log.LogInfo("Crew AI: engineering tree: sets power by the situation when no one orders it");
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("Crew AI: engineering tree: " + e.Message); }
        }

        private enum Situation { Calm, Fight }

        private static bool issuing;                     // our own order going through PowerRouteMenuItem.Execute
        private static Situation? captainChose;          // the situation the captain's last power order was for
        private static Situation situation = Situation.Calm;
        private static AI.OAHAFOIGGMN applied = AI.OAHAFOIGGMN.Balanced;
        private static float lastEnemyNear = -999f, lastChange = -999f, nextCheck;

        private static readonly AccessTools.FieldRef<AI.GJKEHBHBCPK, AI.OAHAFOIGGMN> ProfileOf = AccessTools.FieldRefAccess<AI.GJKEHBHBCPK, AI.OAHAFOIGGMN>("OBIGLGPOLOM");

        private static void PowerOrder_Prefix(AI.GJKEHBHBCPK __instance)
        {
            if (issuing) return;
            captainChose = situation; // held until a fight starts or ends
            try { applied = ProfileOf(__instance); } catch { }
        }

        private sealed class AutoPower : AI.GGCBOJKCOMJ
        {
            // Never finishes: it runs beside the game's engineering tree for as long as the bot holds the station.
            protected override AI.LECHLIEBBEM IKLNPCHBLCA(AI.MNIJEDINGOI bb, AI.LECHLIEBBEM childResult)
            {
                try { Tick(bb); } catch (Exception e) { Plugin.Log.LogWarning("Crew AI: power: " + e.Message); nextCheck = Time.time + 30f; }
                return AI.LECHLIEBBEM.Running;
            }
        }

        private static void Tick(AI.MNIJEDINGOI bb)
        {
            float now = Time.time;
            if (now < nextCheck) return;
            nextCheck = now + PowerCheckEvery;
            if (!Plugin.CrewAutoPower.Value || Loader.JCEFNOJHCKO || powerExecute == null) return; // the original series has no rerouting
            var crew = bb?.Owner as AI.USSAegisCrew;
            var body = crew?.AHMPFHDGMKJ;
            var me = body?.ALPAOOKFIEE;
            if (me == null) return;

            foreach (var ship in EnemyAiFlying.Ships)
            {
                if (ship == null || !ship.gameObject.activeInHierarchy || !EnemyAiTuning.HostileTeam(ship)) continue;
                var them = ship.ALPAOOKFIEE;
                if (them != null && them.System == me.System && (them.Position - me.Position).magnitude < CombatRange) { lastEnemyNear = now; break; }
            }
            var was = situation;
            situation = now - lastEnemyNear < CalmAfter ? Situation.Fight : Situation.Calm;
            if (situation != was && captainChose.HasValue && captainChose.Value != situation) captainChose = null;
            if (captainChose.HasValue || crew.KCKHLJEOBHP) return; // the captain's order stands; rerouting locked

            var want = AI.OAHAFOIGGMN.Balanced;
            if (situation == Situation.Fight)
            {
                float share = 1f;
                var info = body.GetTargetInfo();
                if (info != null && shieldHealth != null && shieldMax(info) > 0f) share = shieldHealth(info) / shieldMax(info);
                want = applied == AI.OAHAFOIGGMN.Defense ? (share > OffenseAbove ? AI.OAHAFOIGGMN.Offense : AI.OAHAFOIGGMN.Defense)
                                                          : (share < DefenseBelow ? AI.OAHAFOIGGMN.Defense : AI.OAHAFOIGGMN.Offense);
            }
            if (want == applied || now - lastChange < PowerEvery) return;
            var item = new AI.GJKEHBHBCPK(FLCHMJJDKDL.Engineering, "Revival automatic power", want); // PowerRouteMenuItem
            issuing = true;
            try { powerExecute.Invoke(item, null); }
            catch (Exception e) { Plugin.Log.LogInfo("Crew AI: power order: " + (e.InnerException ?? e).Message); } // a voice line can fail after the order is set
            finally { issuing = false; }
            Plugin.Log.LogInfo($"Crew AI: engineering routes power for {want} ({situation})");
            applied = want;
            lastChange = now;
        }
    }
}
