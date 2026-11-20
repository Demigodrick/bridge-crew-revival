using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace StbcRevival
{
    /// <summary>
    /// Fixes for bugs in the game's enemy ship AI. The AI only runs on the host, so these only
    /// matter there. Each fix is a small patch on one method, and each checks the setting when it runs, so turning the
    /// setting off puts the game's own behaviour back (the Snipe and Ram fixes apply to ships spawned after the change).
    /// tools/xref/AI.md (branch ai-map) explains the AI and where each bug was found.
    ///
    /// - Shield recharge (AgentShip.UpdateShieldRecharge, MPENGMGKEPB) and Borg hull regeneration (BorgCube.
    ///   UpdateHullRegeneration, JPABKFBKAIJ) run in a round robin that gives every AI ship one turn every N frames, but
    ///   add rate x one frame's time, so both ran N times too slow. They now add rate x the time since the ship's last turn.
    /// - SetNavRandomAround (LBHEECFIJDG): Random.Next(0, 1) is always 0, so random wandering always turned the same way.
    ///   Now Next(0, 2). Its last-known-location search was centred on the player's real position; now on the player's
    ///   LKL marker (USSAegisCrew.LKLMarker), where the enemies last saw it.
    /// - CheckSideCollision (GOEJFIHHNOE): the "is the other side clear?" ray was cast with a negative length, so it never
    ///   hit anything. Now cast the other way with the right length.
    /// - SetAvoidMarkerFromSideHit (HMOJDEOHAIC): placed the avoidance point from the side hit's position but the forward
    ///   hit's surface normal. Now uses the side hit's own normal.
    /// - SetNavToNearestSearchPoint (EILBKPHAMJM): read SearchPoints[index] after RemoveAt(index): the wrong point, or an
    ///   exception when it was the last one. Now reads the point it just moved to VisitedSearchPoints.
    /// - PickSecondaryTarget (GIHFOIJFEBK): Random.Next(0, count - 1) never picked the last body. Now Next(0, count).
    /// - Ram: its hull condition compared a 0-1 ratio with a percentage (10 by default), so it always passed. Now a
    ///   percentage. (Science C's 100 % means "always" either way.)
    /// - SnipeSubSystem: its aim loop waited on a timer nothing started, so it fired before turning to aim. Now the timer
    ///   is started as the aim loop begins (Romulan sniper scouts).
    /// </summary>
    internal static class EnemyAiFixes
    {
        private static bool On => true; // part of the mod (no longer a setting)

        internal static void Patch(Harmony harmony)
        {
            int ok = 0, of = 0;
            void Try(string what, Func<bool> patch)
            {
                of++;
                try { if (patch()) ok++; else Plugin.Log.LogWarning($"Enemy AI fixes: {what}: not found, left as the game has it"); }
                catch (Exception e) { Plugin.Log.LogWarning($"Enemy AI fixes: {what}: {e.Message}"); }
            }
            Type T(string name) => AccessTools.TypeByName(name);
            MethodInfo Tick(string type) => AccessTools.DeclaredMethod(T(type), "IKLNPCHBLCA"); // the node's OnUpdate
            HarmonyMethod Me(string name) => new HarmonyMethod(typeof(EnemyAiFixes), name);

            Try("shield recharge", () =>
            {
                var m = AccessTools.DeclaredMethod(typeof(AI.AgentShip), "MPENGMGKEPB");
                if (m == null) return false;
                harmony.Patch(m, prefix: Me(nameof(ShieldRecharge_Prefix)), transpiler: Me(nameof(DeltaTime_Transpiler)));
                return true;
            });
            Try("Borg hull regeneration", () =>
            {
                var m = AccessTools.DeclaredMethod(typeof(AI.BorgCube), "JPABKFBKAIJ");
                if (m == null) return false;
                harmony.Patch(m, prefix: Me(nameof(HullRegen_Prefix)), transpiler: Me(nameof(DeltaTime_Transpiler)));
                return true;
            });
            Try("random wandering and LKL search", () => Patched(harmony, Tick("AI.LBHEECFIJDG"), nameof(RandomAround_Transpiler)));
            Try("side collision ray", () => Patched(harmony, Tick("AI.GOEJFIHHNOE"), nameof(SideCollision_Transpiler)));
            Try("side avoidance normal", () => Patched(harmony, Tick("AI.HMOJDEOHAIC"), nameof(SideAvoid_Transpiler)));
            Try("nearest search point", () => Patched(harmony, Tick("AI.EILBKPHAMJM"), nameof(NearestSearchPoint_Transpiler)));
            Try("secondary target pick", () => Patched(harmony, Tick("AI.GIHFOIJFEBK"), nameof(SecondaryTarget_Transpiler)));
            Try("ram hull threshold", () =>
            {
                var m = AccessTools.DeclaredMethod(typeof(AI.Ram), "GetSubtree");
                if (m == null) return false;
                harmony.Patch(m, prefix: Me(nameof(Ram_Prefix)));
                return true;
            });
            Try("sniper aim timer", () =>
            {
                var m = AccessTools.DeclaredMethod(typeof(AI.SnipeSubSystem), "GetSubtree");
                if (m == null || !SnipeTypesFound()) return false;
                harmony.Patch(m, postfix: Me(nameof(Snipe_Postfix)));
                return true;
            });
            Plugin.Log.LogInfo($"Enemy AI fixes: {ok} of {of} patched ({(On ? "on" : "off")})");
        }

        // A transpiler that reports whether it found what it changes: it sets this, and Patched checks it.
        private static bool transpiled;

        private static bool Patched(Harmony harmony, MethodInfo method, string transpiler)
        {
            if (method == null) return false;
            transpiled = false;
            harmony.Patch(method, transpiler: new HarmonyMethod(typeof(EnemyAiFixes), transpiler));
            return transpiled;
        }

        // ---- Shield recharge and Borg regeneration: the time since this ship's last turn, not one frame's -----------

        private static readonly Dictionary<int, float> lastShieldTurn = new Dictionary<int, float>();
        private static readonly Dictionary<int, float> lastRegenTurn = new Dictionary<int, float>();
        private static float turnTime; // the running method's: set by its prefix, read where it used Time.deltaTime
        private const float LongestTurn = 2f; // a longer gap (just spawned, back from far away): one frame, as the game did

        private static void ShieldRecharge_Prefix(AI.AgentShip __instance) => turnTime = SinceLastTurn(lastShieldTurn, __instance);
        private static void HullRegen_Prefix(AI.BorgCube __instance) => turnTime = SinceLastTurn(lastRegenTurn, __instance);

        private static float SinceLastTurn(Dictionary<int, float> last, UnityEngine.Object ship)
        {
            float now = Time.time;
            int id = ship.GetInstanceID();
            float since = last.TryGetValue(id, out var then) ? now - then : -1f;
            last[id] = now;
            return since > 0f && since <= LongestTurn ? since : Time.deltaTime;
        }

        private static float TurnDeltaTime() => On ? turnTime : Time.deltaTime;

        private static IEnumerable<CodeInstruction> DeltaTime_Transpiler(IEnumerable<CodeInstruction> code, MethodBase original)
        {
            var deltaTime = AccessTools.PropertyGetter(typeof(Time), nameof(Time.deltaTime));
            int n = 0;
            foreach (var i in code)
            {
                if (i.Calls(deltaTime)) { i.opcode = OpCodes.Call; i.operand = AccessTools.Method(typeof(EnemyAiFixes), nameof(TurnDeltaTime)); n++; }
                yield return i;
            }
            if (n != 1) Plugin.Log.LogWarning($"Enemy AI fixes: {original.DeclaringType?.Name}.{original.Name}: {n} uses of Time.deltaTime replaced (1 expected)");
        }

        // ---- SetNavRandomAround: turn either way; search round the last known location ------------------------------

        private static int RandomSideMax() => On ? 2 : 1; // Random.Next(0, max): 0 or 1 instead of always 0

        private static Sector.SectorBody LklOrPlayer(AI.MNIJEDINGOI bb)
        {
            var player = bb.PlayerShip;
            if (!On || player == null) return player;
            try
            {
                var crew = player.GetComponent<AI.USSAegisCrew>();
                var marker = crew != null ? crew.JHKIOOMFAJD : null; // LKLMarker
                return marker != null ? marker : player;
            }
            catch { return player; }
        }

        private static IEnumerable<CodeInstruction> RandomAround_Transpiler(IEnumerable<CodeInstruction> code)
        {
            var list = code.ToList();
            var next = AccessTools.Method(typeof(System.Random), nameof(System.Random.Next), new[] { typeof(int), typeof(int) });
            var player = AccessTools.Field(typeof(AI.MNIJEDINGOI), "PlayerShip");
            bool side = false, lkl = false;
            for (int i = 2; i < list.Count; i++)
            {
                // the sign: ldc.i4.0, ldc.i4.1, callvirt Random.Next(int, int)
                if (!side && list[i].Calls(next) && list[i - 1].opcode == OpCodes.Ldc_I4_1 && list[i - 2].opcode == OpCodes.Ldc_I4_0)
                {
                    list[i - 1].opcode = OpCodes.Call;
                    list[i - 1].operand = AccessTools.Method(typeof(EnemyAiFixes), nameof(RandomSideMax));
                    side = true;
                }
            }
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].LoadsField(player)) // bb.PlayerShip (the LKL slot's centre), the method's only use
                {
                    list[i].opcode = OpCodes.Call;
                    list[i].operand = AccessTools.Method(typeof(EnemyAiFixes), nameof(LklOrPlayer));
                    lkl = true;
                }
            }
            if (!side) Plugin.Log.LogWarning("Enemy AI fixes: random wandering's Random.Next(0, 1) not found");
            if (!lkl) Plugin.Log.LogWarning("Enemy AI fixes: the LKL search's PlayerShip not found");
            transpiled = side && lkl;
            return list;
        }

        // ---- CheckSideCollision: cast the "other side" ray the other way, with a positive length -------------------

        private static Vector3 OtherSide(Vector3 direction) => On ? -direction : direction;
        private static float OtherSideLength(float length) => On ? length : -length; // replaces the game's neg

        private static IEnumerable<CodeInstruction> SideCollision_Transpiler(IEnumerable<CodeInstruction> code)
        {
            var list = code.ToList();
            var negs = new List<int>();
            for (int at = 0; at < list.Count; at++) if (list[at].opcode == OpCodes.Neg) negs.Add(at);
            // ... ldloc <direction>, ldloca <hit>, ldloc <length>, neg, ldsfld <mask>, ldc.i4.1, call Physics.Raycast
            if (negs.Count != 1 || negs[0] < 3 || !list[negs[0] - 1].IsLdloc() || list[negs[0] - 2].opcode != OpCodes.Ldloca_S && list[negs[0] - 2].opcode != OpCodes.Ldloca
                || !list[negs[0] - 3].IsLdloc())
                return list;
            int neg = negs[0];
            list[neg].opcode = OpCodes.Call;
            list[neg].operand = AccessTools.Method(typeof(EnemyAiFixes), nameof(OtherSideLength));
            list.Insert(neg - 2, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(EnemyAiFixes), nameof(OtherSide))));
            transpiled = true;
            return list;
        }

        // ---- SetAvoidMarkerFromSideHit: the side hit's own normal -----------------------------------------------------

        private static AI.BDFAFNOMDCJ AvoidNormalHit(AI.MNIJEDINGOI bb) => On && bb.CollisionSideHit != null ? bb.CollisionSideHit : bb.CollisionToNavHit;

        private static IEnumerable<CodeInstruction> SideAvoid_Transpiler(IEnumerable<CodeInstruction> code)
        {
            var toNav = AccessTools.Field(typeof(AI.MNIJEDINGOI), "CollisionToNavHit");
            int n = 0;
            foreach (var i in code)
            {
                if (i.LoadsField(toNav)) { i.opcode = OpCodes.Call; i.operand = AccessTools.Method(typeof(EnemyAiFixes), nameof(AvoidNormalHit)); n++; }
                yield return i;
            }
            transpiled = n == 1;
        }

        // ---- SetNavToNearestSearchPoint: the point it just took, not what took its place ------------------------------

        private static SystemTransform TakenSearchPoint(List<SystemTransform> searchPoints, int index, AI.MNIJEDINGOI bb)
        {
            if (On && bb.VisitedSearchPoints.Count > 0) return bb.VisitedSearchPoints[bb.VisitedSearchPoints.Count - 1];
            return searchPoints[index];
        }

        private static IEnumerable<CodeInstruction> NearestSearchPoint_Transpiler(IEnumerable<CodeInstruction> code)
        {
            var list = code.ToList();
            var search = AccessTools.Field(typeof(AI.MNIJEDINGOI), "SearchPoints");
            int removeAt = list.FindIndex(i => i.operand is MethodInfo m && m.Name == "RemoveAt");
            if (removeAt < 0) return list;
            int n = 0;
            for (int i = removeAt + 1; i + 2 < list.Count; i++)
            {
                // bb (already loaded), ldfld SearchPoints, ldloc <index>, callvirt get_Item  ->  ..., ldarg.1, call TakenSearchPoint
                if (list[i].LoadsField(search) && list[i + 1].IsLdloc() && list[i + 2].operand is MethodInfo get && get.Name == "get_Item")
                {
                    list[i + 2] = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(EnemyAiFixes), nameof(TakenSearchPoint))).MoveLabelsFrom(list[i + 2]);
                    list.Insert(i + 2, new CodeInstruction(OpCodes.Ldarg_1));
                    n++;
                }
            }
            transpiled = n == 2;
            return list;
        }

        // ---- PickSecondaryTarget: every body can be picked ------------------------------------------------------------

        private static int SecondaryPickCount(int count) => On ? count : count - 1;

        private static IEnumerable<CodeInstruction> SecondaryTarget_Transpiler(IEnumerable<CodeInstruction> code)
        {
            var list = code.ToList();
            var next = AccessTools.Method(typeof(System.Random), nameof(System.Random.Next), new[] { typeof(int), typeof(int) });
            int call = list.FindIndex(i => i.Calls(next));
            // ... get_Count, ldc.i4.1, sub, callvirt Random.Next
            if (call < 2 || list[call - 1].opcode != OpCodes.Sub || list[call - 2].opcode != OpCodes.Ldc_I4_1) return list;
            list[call - 2].opcode = OpCodes.Call;
            list[call - 2].operand = AccessTools.Method(typeof(EnemyAiFixes), nameof(SecondaryPickCount));
            list[call - 1].opcode = OpCodes.Nop;
            list[call - 1].operand = null;
            transpiled = true;
            return list;
        }

        // ---- Ram: a percentage, as its default (10) and the Science C's (100) are written -----------------------------

        private static readonly FieldInfo RamHullPercent = AccessTools.Field(typeof(AI.Ram), "_maxHealthPercentAllowed");
        private static readonly HashSet<int> ramsDone = new HashSet<int>();

        private static void Ram_Prefix(AI.Ram __instance)
        {
            // The pattern is a shared asset: convert it once, the first time a ship's tree is built from it.
            if (!On || __instance == null || !ramsDone.Add(__instance.GetInstanceID())) return;
            if (RamHullPercent?.GetValue(__instance) is float threshold && threshold > 1f) RamHullPercent.SetValue(__instance, threshold / 100f);
        }

        // ---- SnipeSubSystem: start the aim timer as the aim loop starts ------------------------------------------------

        private static Type btNode, sequence, repeatUntilFail, inverter, timerElapsed, startTimer;
        private static FieldInfo sequenceChildren, repeatChild, inverterChild, elapsedTimer;

        private static bool SnipeTypesFound()
        {
            btNode = AccessTools.TypeByName("AI.GGCBOJKCOMJ");          // BTNode
            sequence = AccessTools.TypeByName("AI.JMDIFJIGIBC");        // BTSequence
            repeatUntilFail = AccessTools.TypeByName("AI.EHHMPNMMJJL"); // RepeatUntilFail
            inverter = AccessTools.TypeByName("AI.MMIHFPNOHHG");        // Inverter
            timerElapsed = AccessTools.TypeByName("AI.AEBMOLHJLIM");    // TimerElapsed
            startTimer = AccessTools.TypeByName("AI.LODFONJKEBB");      // StartTimer
            sequenceChildren = AccessTools.Field(sequence, "JEGNDMPCDEG");
            repeatChild = AccessTools.Field(repeatUntilFail, "FACKKDKMLGP");
            inverterChild = AccessTools.Field(inverter, "FACKKDKMLGP");
            elapsedTimer = AccessTools.Field(timerElapsed, "IIBCNHJFALG");
            return btNode != null && startTimer != null && sequenceChildren != null && repeatChild != null && inverterChild != null && elapsedTimer != null;
        }

        private static void Snipe_Postfix(object __result)
        {
            if (!On || __result == null) return;
            try
            {
                if (!WrapAimLoop(__result, 0)) Plugin.Log.LogWarning("Enemy AI fixes: the sniper's aim loop wasn't found in its tree");
            }
            catch (Exception e) { Plugin.Log.LogWarning("Enemy AI fixes: sniper aim timer: " + e.Message); }
        }

        // Find, in a sequence's children, the aim loop: RepeatUntilFail(Sequence(..., Inverter(TimerElapsed(t)), ...)),
        // and put StartTimer(t) before it: Sequence(StartTimer(t), loop).
        private static bool WrapAimLoop(object node, int depth)
        {
            if (node == null || depth > 12) return false;
            if (sequence.IsInstanceOfType(node))
            {
                var children = (Array)sequenceChildren.GetValue(node);
                for (int i = 0; i < children.Length; i++)
                {
                    var child = children.GetValue(i);
                    if (AimTimer(child) is object timer)
                    {
                        var start = Activator.CreateInstance(startTimer, timer);
                        var pair = Array.CreateInstance(btNode, 2);
                        pair.SetValue(start, 0);
                        pair.SetValue(child, 1);
                        children.SetValue(Activator.CreateInstance(sequence, new object[] { pair }), i);
                        return true;
                    }
                    if (WrapAimLoop(child, depth + 1)) return true;
                }
                return false;
            }
            foreach (var f in AccessTools.GetDeclaredFields(node.GetType()))
            {
                if (f.IsStatic) continue;
                var value = f.GetValue(node);
                if (value != null && btNode.IsInstanceOfType(value) && WrapAimLoop(value, depth + 1)) return true;
                if (value is Array array && btNode.IsAssignableFrom(array.GetType().GetElementType()))
                    foreach (var item in array) if (WrapAimLoop(item, depth + 1)) return true;
            }
            return false;
        }

        // The timer an aim loop waits on, or null when the node isn't one.
        private static object AimTimer(object node)
        {
            if (node == null || !repeatUntilFail.IsInstanceOfType(node)) return null;
            var body = repeatChild.GetValue(node);
            if (body == null || !sequence.IsInstanceOfType(body)) return null;
            foreach (var step in (Array)sequenceChildren.GetValue(body))
                if (step != null && inverter.IsInstanceOfType(step) && inverterChild.GetValue(step) is object inner && timerElapsed.IsInstanceOfType(inner))
                    return elapsedTimer.GetValue(inner);
            return null;
        }
    }

    /// <summary>
    /// Enemy ships think more often. The game runs every AI ship's behaviour tree (and its
    /// target scan, shields and hacks) in one round-robin group, BehaviorLogic, which runs one task a frame between all of
    /// them: with 20 ships each one decided about every 23rd frame. UpdateScheduler (FNPDNCICIMB) gives each group a
    /// share of its tasks to run a frame (s_updatePercents, NOJNLDJAPIG; BehaviorLogic's is 0, so 0 + 1 task). This sets
    /// BehaviorLogic's share to Share, so each ship has a turn about every 1 / Share frames however many there are. The
    /// group also holds the player's AI crew (three tasks), which think faster too. Shield recharge is time-based once
    /// EnemyAiFixes is on, so more turns don't recharge faster.
    /// </summary>
    internal static class EnemyAiThinkRate
    {
        internal const float Share = 0.25f; // a turn about every 4 frames
        private const int BehaviorLogic = 6; // RoundRobinGroup.BehaviorLogic

        private static float gameShare = -1f;

        internal static void Apply()
        {
            try
            {
                var scheduler = AccessTools.TypeByName("FNPDNCICIMB"); // UpdateScheduler
                var percents = (float[])AccessTools.Field(scheduler, "NOJNLDJAPIG").GetValue(null);
                if (gameShare < 0f) gameShare = percents[BehaviorLogic];
                float share = Share;
                percents[BehaviorLogic] = share; // groups made from now on
                // A group made already keeps the share it was made with: set it there too.
                var groups = (IDictionary)AccessTools.Field(scheduler, "DFHCGBHKIHC").GetValue(null); // s_roundRobinUpdateGroups
                foreach (DictionaryEntry entry in groups)
                {
                    if (Convert.ToInt32(entry.Key) != BehaviorLogic || entry.Value == null) continue;
                    var group = entry.Value;
                    AccessTools.Field(group.GetType(), "Percent").SetValue(group, share);
                    int tasks = ((ICollection)AccessTools.Field(group.GetType(), "CELCDBOGOPP").GetValue(group)).Count; // _tasks
                    AccessTools.Field(group.GetType(), "FALHIPFJAJC").SetValue(group, (int)(share * tasks)); // _tasksPerExecute
                }
                Plugin.Log.LogInfo($"Enemy AI: BehaviorLogic share {share} ({(share > 0f ? $"a turn about every {1f / share:0} frames" : "one task a frame")})");
            }
            catch (Exception e) { Plugin.Log.LogWarning("Enemy AI: think rate not set: " + e.Message); }
        }
    }
}
