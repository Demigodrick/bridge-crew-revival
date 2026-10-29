using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Sector;
using Sector.Spacecraft;
using StbcRevival.Missions;
using UnityEngine;
using Team = Sector.DGGJOECIFAF;
using Category = Sector.HHHDDNLEDBN;

namespace StbcRevival
{
    /// <summary>
    /// Player vs player, phase 1 (the PvP test mission): two player ships in one sector, each player's game on
    /// its own. The game assumes one player ship; these patches keep a second one from taking over:
    ///   - Binding: every machine announces a newly spawned player ship (SectorSpawnManager.CJDNEDMKCJJ sends
    ///     "player ship spawned", INPOODEDFLG) and some fifteen systems (bridge, consoles, camera, nav map...)
    ///     take the last one announced as theirs. The second ship ("PvP B") is announced as an ordinary ship
    ///     instead, unless you're on it (PvpShips: the first player to join after the host), when it's announced after the first.
    ///   - The other ship's own crew logic (USSAegisCrew, which acts through the bridge's consoles, so on your
    ///     ship) is switched off, as are its replies to ship-wide events (subsystem messages, mission warps,
    ///     probe launches) and its throttle's hold on the helm's slider.
    ///   - Teams: hostility is "not Federation", so the second ship is Klingon; players on it see the teams
    ///     swapped (their own ship Federation, the first one Klingon). The host's simulation sees the real
    ///     teams (the host is on the first ship), and the teams sent to other players are always the real ones.
    ///   - The targeting panel's category: another player ship is an enemy, yours is yours.
    ///   - Map icons: both player ships are targetable by every station (the mission sets it, for the other
    ///     crew's maps), but your own ship's icon still can't be selected on yours.
    ///   - "Is it a player ship?" checks that mean "is it my ship" (the map's own-ship icon, which can't be
    ///     selected; the viewscreen's target display; the engine and warp core's player-only sounds, such as
    ///     the impulse loop, played to the whole bridge) are true only for your own ship (PlayerShipChecks).
    ///   - One-per-game parts of a player ship (PlayerSpacecraftSoundManager, which music and crew sounds go
    ///     through, and the like) take over when a ship is made (Awake): the other ship's are handed back to
    ///     yours, so they don't go silent when it's destroyed.
    ///   - The tactical target panel shows shields and hull only for AI ships: also for the other player ship.
    ///   - A destroyed player ship doesn't explode (only AI ships do, AgentShip.OnHideShip): the other one does
    ///     here, with its ShipFX's explosion, and disappears.
    /// Only while the PvP test mission runs.
    /// </summary>
    internal static class PvpPrototype
    {
        internal const string ShipBName = "PvP B";

        /// <summary>The player ship this game is on, or null outside the PvP test.</summary>
        internal static PlayerSpacecraft Bound { get; private set; }

        private static TargetInfoComponent boundInfo;
        private static bool serializing;
        private static int activeFrame = -1;
        private static bool active;
        private static readonly Dictionary<TargetableComponent, PlayerSpacecraft> shipOf = new Dictionary<TargetableComponent, PlayerSpacecraft>();
        private static readonly HashSet<PlayerSpacecraft> others = new HashSet<PlayerSpacecraft>();
        private static readonly HashSet<PlayerSpacecraft> exploded = new HashSet<PlayerSpacecraft>();
        private const BindingFlags Statics = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        private static readonly AccessTools.FieldRef<TargetInfoComponent, Team> TeamField =
            AccessTools.FieldRefAccess<TargetInfoComponent, Team>("<BEJOMHEMMLD>k__BackingField"); // behind get_TeamID

        // The ship's replies to ship-wide events: subsystem damaged / destroyed / repaired / at minimum or
        // maximum power (bridge messages), probe launch, warp-effect end, mission warp.
        private static readonly string[] ShipEventHandlers =
        {
            "HCCIGJCMAKL", "BOAPPLHKHFP", "OPMBGEHIDBD", "NEBNOAFFOJP", "NDBKDBOMAJB", "NEPBADGDJBJ", "AEIHFBMCKMD", "GCLCHPJHNNK",
        };

        /// <summary>The PvP test mission is running (or about to): checked once a frame.</summary>
        internal static bool Active
        {
            get
            {
                if (activeFrame != Time.frameCount)
                {
                    activeFrame = Time.frameCount;
                    try { active = RevivalMissions.Selected == typeof(PvpTestSequence); }
                    catch { active = false; }
                }
                return active;
            }
        }

        private static bool OnShipB => PvpShips.Mine == "B";

        internal static Team RawTeam(TargetInfoComponent info) => TeamField(info);

        internal static void Patch(Harmony harmony)
        {
            var announce = AccessTools.Method(typeof(SectorSpawnManager), "CJDNEDMKCJJ", new[] { typeof(SectorBody) });
            if (announce == null)
            {
                Plugin.Log.LogWarning("PvP: SectorSpawnManager's ship announcement wasn't found; PvP test off");
                return;
            }
            harmony.Patch(announce, prefix: new HarmonyMethod(typeof(PvpPrototype), nameof(Announce_Prefix)));
            foreach (var name in ShipEventHandlers)
            {
                var handler = AccessTools.Method(typeof(PlayerSpacecraft), name);
                if (handler == null) Plugin.Log.LogWarning($"PvP: PlayerSpacecraft.{name} not found");
                else harmony.Patch(handler, prefix: new HarmonyMethod(typeof(PvpPrototype), nameof(OwnShipOnly_Prefix)));
            }
            var throttle = AccessTools.Method(typeof(PlayerSpacecraft), "Multicast_SetThrottleAndPower");
            if (throttle != null)
                harmony.Patch(throttle, prefix: new HarmonyMethod(typeof(PvpPrototype), nameof(Throttle_Prefix)),
                    postfix: new HarmonyMethod(typeof(PvpPrototype), nameof(Throttle_Postfix)));
            var team = AccessTools.PropertyGetter(typeof(TargetInfoComponent), "DGGJOECIFAF");
            var serialize = AccessTools.Method(typeof(TargetInfoComponent), "OnPhotonSerializeView");
            if (team != null && serialize != null)
            {
                harmony.Patch(team, postfix: new HarmonyMethod(typeof(PvpPrototype), nameof(Team_Postfix)));
                harmony.Patch(serialize, prefix: new HarmonyMethod(typeof(PvpPrototype), nameof(Serialize_Prefix)),
                    finalizer: new HarmonyMethod(typeof(PvpPrototype), nameof(Serialize_Finalizer)));
            }
            var category = AccessTools.Method(typeof(TargetableComponent), "GetCategory");
            if (category != null) harmony.Patch(category, postfix: new HarmonyMethod(typeof(PvpPrototype), nameof(Category_Postfix)));
            var selectable = AccessTools.Method(typeof(MapIcon), "ACAGOGEPCCE");
            if (selectable != null) harmony.Patch(selectable, postfix: new HarmonyMethod(typeof(PvpPrototype), nameof(Selectable_Postfix)));
            var heading = AccessTools.Method(typeof(NavMapIcon), "GICLGFHINDB");
            if (heading == null) Plugin.Log.LogWarning("PvP: NavMapIcon.GICLGFHINDB not found; the other ship's map icon turns with yours");
            else harmony.Patch(heading, postfix: new HarmonyMethod(typeof(PvpPrototype), nameof(Heading_Postfix)));
            PatchPlayerShipChecks(harmony);
            PvpConsoles.Patch(harmony);
            var consoleAI = AccessTools.Method(typeof(AI.USSAegisCrew), "IsConsoleAI");
            if (consoleAI == null) Plugin.Log.LogWarning("PvP: USSAegisCrew.IsConsoleAI not found");
            else harmony.Patch(consoleAI, postfix: new HarmonyMethod(typeof(PvpPrototype), nameof(ConsoleAI_Postfix)));
            foreach (var name in new[] { "OPGMKEGJFIP", "FBNPCAFBGCH" }) // the target's details: TNG / Aegis, original series
            {
                var panel = AccessTools.Method(typeof(TacticalTargetUI), name);
                if (panel == null) Plugin.Log.LogWarning($"PvP: TacticalTargetUI.{name} not found");
                else harmony.Patch(panel, postfix: new HarmonyMethod(typeof(PvpPrototype), nameof(TargetPanel_Postfix)));
            }
        }

        // A player ship spawned: the first ship always binds (as the game does); the second binds only for
        // players on it, and for everyone else is announced as an ordinary ship (next frame's end, as the game's).
        private static bool Announce_Prefix(SectorSpawnManager __instance, SectorBody __0)
        {
            if (!(__0 is PlayerSpacecraft ship) || !Active) return true;
            string name = ship.ALPAOOKFIEE?.BaseName ?? "";
            bool shipB = name.StartsWith(ShipBName, StringComparison.Ordinal);
            if (!shipB || OnShipB)
            {
                Bind(ship);
                Plugin.Log.LogInfo($"PvP: on {(shipB ? ShipBName : "the first player ship")} ('{name}')");
                return true;
            }
            Plugin.Log.LogInfo($"PvP: '{name}' is the other player's ship");
            Detach(ship);
            __instance.StartCoroutine(AnnounceAsBody(ship));
            return false;
        }

        private static IEnumerator AnnounceAsBody(SectorBody body)
        {
            yield return new WaitForEndOfFrame();
            if (body == null) yield break;
            CIFDOFLFGEA.Send(new JJKIHDENJCK(body));
            if (body is PlayerSpacecraft other && Letter(other) == "B") PvpConsoles.BindCopies(other);
            HandBackSingletons(body as PlayerSpacecraft);
            yield return null; // and once more after Start
            HandBackSingletons(body as PlayerSpacecraft);
        }

        // The other ship's components that made themselves the game's one instance (a static of their own type,
        // set in Awake): that instance is your ship's again.
        private static void HandBackSingletons(PlayerSpacecraft other)
        {
            if (other == null || Bound == null || other == Bound) return;
            var handed = new List<string>();
            foreach (var part in other.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (part == null) continue;
                var own = Bound.GetComponentInChildren(part.GetType(), true);
                if (own == null) continue;
                for (var type = part.GetType(); type != null && type != typeof(MonoBehaviour); type = type.BaseType)
                {
                    foreach (var property in type.GetProperties(Statics))
                    {
                        try
                        {
                            if (!property.PropertyType.IsInstanceOfType(part) || property.GetIndexParameters().Length > 0) continue;
                            var setter = property.GetSetMethod(true);
                            if (setter == null || !ReferenceEquals(property.GetValue(null, null), part)) continue;
                            setter.Invoke(null, new object[] { own });
                            handed.Add(property.DeclaringType.Name);
                        }
                        catch (Exception) { } // a getter that can't run now: not a singleton we need
                    }
                    foreach (var field in type.GetFields(Statics))
                    {
                        if (field.IsLiteral || field.IsInitOnly || !field.FieldType.IsInstanceOfType(part) || !ReferenceEquals(field.GetValue(null), part)) continue;
                        field.SetValue(null, own);
                        handed.Add(field.DeclaringType.Name);
                    }
                }
            }
            if (handed.Count > 0) Plugin.Log.LogInfo($"PvP: handed back to your ship: {string.Join(", ", handed.ToArray())}");
        }

        private static void Bind(PlayerSpacecraft ship)
        {
            if (Bound != null && Bound != ship) Detach(Bound);
            Bound = ship;
            others.Remove(ship);
            boundInfo = ship.GetTargetInfo() as TargetInfoComponent;
            shipOf.Clear();
            var crew = ship.GetComponent<AI.USSAegisCrew>();
            if (crew != null) crew.enabled = true;
        }

        // Its crew logic off (OnDisable drops its event listeners and its updates).
        private static void Detach(PlayerSpacecraft ship)
        {
            var crew = ship.GetComponent<AI.USSAegisCrew>();
            if (crew != null) crew.enabled = false;
            others.Add(ship);
        }

        /// <summary>Each frame: the other ship explodes when its hull is gone.</summary>
        internal static void Update()
        {
            if (!Active)
            {
                if (others.Count > 0 || exploded.Count > 0)
                {
                    others.Clear();
                    exploded.Clear();
                }
                if (hiddenAvatars.Count > 0) ShowAllAvatars();
                return;
            }
            PvpShips.Update();
            PvpConsoles.Update();
            if (Time.unscaledTime >= nextAvatarCheck)
            {
                nextAvatarCheck = Time.unscaledTime + 0.5f;
                HideOtherShipsAvatars();
            }
            foreach (var ship in others)
            {
                if (ship == null || exploded.Contains(ship)) continue;
                var info = ship.GetTargetInfo();
                if (info == null || info.GBNCJMFFIDI > 0f) continue;
                exploded.Add(ship);
                Explode(ship);
            }
        }

        // The AI crew's consoles, by station (Captain has none: the AI never captains).
        private static readonly Dictionary<FLCHMJJDKDL, FieldInfo> crewConsoles = new Dictionary<FLCHMJJDKDL, FieldInfo>
        {
            [FLCHMJJDKDL.Tactical] = AccessTools.Field(typeof(AI.USSAegisCrew), "GELDBDOEIBN"),
            [FLCHMJJDKDL.Helm] = AccessTools.Field(typeof(AI.USSAegisCrew), "IJFDAJGLKLF"),
            [FLCHMJJDKDL.Engineering] = AccessTools.Field(typeof(AI.USSAegisCrew), "LMFOFJEALLE"),
        };

        /// <summary>"A" or "B": which of the two player ships this is.</summary>
        internal static string Letter(PlayerSpacecraft ship) =>
            (ship?.ALPAOOKFIEE?.BaseName ?? "").StartsWith(ShipBName, StringComparison.Ordinal) ? "B" : "A";

        // Stations are shared: one console per station on every game's bridge, holding whichever player chose
        // it, whatever their ship. For a ship's AI crew, a station held by a player on the other ship is empty,
        // so its bot works it (IsConsoleAI: no player, or a bot, and a bot assigned).
        private static void ConsoleAI_Postfix(AI.USSAegisCrew __instance, FLCHMJJDKDL __0, ref bool __result)
        {
            if (__result || !Active || !crewConsoles.TryGetValue(__0, out var field) || field == null) return;
            if (!(field.GetValue(__instance) is ConsoleBase console) || console.BJLCFFKCFFN == null || console.LPMMGGIAOCL == null) return;
            var player = console.FKKJLMLCKJI;
            if (player == null || player.AOMGCCGDJJC) return;
            var crewShip = __instance.GetComponent<PlayerSpacecraft>();
            if (PvpShips.Of(PvpShips.Player(player.NJNHJKEPFAK)) != Letter(crewShip)) __result = true;
        }

        private static readonly HashSet<NovaPlayerInfo> hiddenAvatars = new HashSet<NovaPlayerInfo>();
        private static float nextAvatarCheck;

        // Every game has one bridge, where every player in the room is seated at their station: players on the
        // other ship aren't drawn on yours (NJNHJKEPFAK: their actor number; AOLEJMABHPE: you; AOMGCCGDJJC: a bot).
        private static void HideOtherShipsAvatars()
        {
            if (Bound == null) return;
            string mine = PvpShips.Mine;
            foreach (var player in UnityEngine.Object.FindObjectsOfType<NovaPlayerInfo>())
            {
                if (player == null || player.AOLEJMABHPE || player.AOMGCCGDJJC) continue;
                bool other = PvpShips.Of(PvpShips.Player(player.NJNHJKEPFAK)) != mine;
                if (other) SetAvatarVisible(player, false); // every time: the game may switch renderers back on
                if (other == hiddenAvatars.Contains(player)) continue;
                if (!other) SetAvatarVisible(player, true);
                if (other) hiddenAvatars.Add(player);
                else hiddenAvatars.Remove(player);
                Plugin.Log.LogInfo($"PvP: {player.FAJABLKBGHN} (actor {player.NJNHJKEPFAK}) is {(other ? "on the other ship: hidden" : "on your ship: shown")}");
            }
        }

        private static void ShowAllAvatars()
        {
            foreach (var player in hiddenAvatars)
                if (player != null) SetAvatarVisible(player, true);
            hiddenAvatars.Clear();
        }

        private static void SetAvatarVisible(NovaPlayerInfo player, bool visible)
        {
            foreach (var pawn in new[] { player.VisualPawnObject, player.PawnObject })
                if (pawn != null)
                    foreach (var renderer in pawn.GetComponentsInChildren<Renderer>(true)) renderer.enabled = visible;
        }

        // As an AI ship's (AgentShip.OnHideShip, ShipFX.OnExplodingAction): its explosion where it was, then
        // nothing of it left to see or hit.
        private static void Explode(PlayerSpacecraft ship)
        {
            var fx = ship.GetComponentInChildren<ShipFX>(true);
            var model = fx == null ? null : AccessTools.Field(typeof(ShipFX), "_explosionModel").GetValue(fx) as GameObject;
            string source = "its own";
            if (model == null)
            {
                // The player ship's prefab may have none (the game never blows it up from outside).
                var stand = Resources.Load<GameObject>("Sector/Ships/Ship - D5 Cruiser");
                var standFx = stand == null ? null : stand.GetComponentInChildren<ShipFX>(true);
                model = standFx == null ? null : AccessTools.Field(typeof(ShipFX), "_explosionModel").GetValue(standFx) as GameObject;
                source = "a D5's";
            }
            // Where the ship is seen: the middle of its visible model (its transform may be elsewhere, in the
            // simulation's own space), facing the way its biggest piece does.
            var visible = ship.GetComponentsInChildren<Renderer>(false).Where(r => r.enabled && !(r is ParticleSystemRenderer)).ToArray();
            Vector3 position;
            Quaternion rotation;
            if (visible.Length > 0)
            {
                var bounds = visible[0].bounds;
                foreach (var r in visible) bounds.Encapsulate(r.bounds);
                position = bounds.center;
                rotation = visible.OrderByDescending(r => r.bounds.size.sqrMagnitude).First().transform.rotation;
            }
            else
            {
                var at = fx != null ? fx.transform : ship.transform;
                position = at.position;
                rotation = at.rotation;
            }
            foreach (var renderer in ship.GetComponentsInChildren<Renderer>(true)) renderer.enabled = false;
            foreach (var collider in ship.GetComponentsInChildren<Collider>(true)) collider.enabled = false;
            DropContact(ship);
            if (model == null)
            {
                Plugin.Log.LogInfo($"PvP: {ship.name} destroyed, no explosion found");
                return;
            }
            var boom = (GameObject)UnityEngine.Object.Instantiate(model, position, rotation);
            var space = SimulationSpace.BALGCFOKIPN;
            if (space != null) boom.transform.SetParent(space, true); // moves with the floating origin, as AI ships' do
            var debris = boom.GetComponent<ExplodedShipFX>();
            if (debris != null) debris.enabled = true;
            var camera = Camera.main;
            Plugin.Log.LogInfo($"PvP: {ship.ALPAOOKFIEE?.BaseName ?? ship.name} exploded ({source} explosion, ship FX {(fx != null ? "found" : "none")}) at {position}"
                + $" from {visible.Length} visible parts, {(camera != null ? Vector3.Distance(camera.transform.position, position).ToString("F0") : "?")} from the main camera,"
                + $" in {(space != null ? space.name : "the scene root")}");
        }

        // Off the sensors, as a removed body (SectorMap.LIPOCCLEEGM: out of its system's detectables and
        // targetables, so off every map, with the game's "lost contact").
        private static void DropContact(PlayerSpacecraft ship)
        {
            try
            {
                var map = UnityEngine.Object.FindObjectOfType<SectorMap>();
                var drop = AccessTools.Method(typeof(SectorMap), "LIPOCCLEEGM", new[] { typeof(SectorBody) });
                if (map == null || drop == null)
                {
                    Plugin.Log.LogWarning("PvP: the sector map wasn't found; the destroyed ship stays on the maps");
                    return;
                }
                drop.Invoke(map, new object[] { ship });
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("PvP: couldn't take the destroyed ship off the maps: " + (e.InnerException ?? e).Message);
            }
        }

        private static bool IsOther(PlayerSpacecraft ship) => Active && Bound != null && ship != Bound;

        private static bool OwnShipOnly_Prefix(PlayerSpacecraft __instance) => !IsOther(__instance);

        // Multicast_SetThrottleAndPower also moves the helm's throttle slider: not for the other ship.
        private static void Throttle_Prefix(PlayerSpacecraft __instance, out float __state)
        {
            __state = -1f;
            if (IsOther(__instance) && Bridge.AGAFPEFMEGB != null) __state = Bridge.AGAFPEFMEGB.KJLEFDENMNI.OJKPBPCBPMA;
        }

        private static void Throttle_Postfix(float __state)
        {
            if (__state >= 0f && Bridge.AGAFPEFMEGB != null) Bridge.AGAFPEFMEGB.KJLEFDENMNI.OJKPBPCBPMA = __state;
        }

        // On the second ship, its team and the Federation swap places (never while being sent).
        private static void Team_Postfix(ref Team __result)
        {
            if (serializing || boundInfo == null || !Active) return;
            var own = TeamField(boundInfo);
            if (own == Team.Federation) return;
            if (__result == own) __result = Team.Federation;
            else if (__result == Team.Federation) __result = own;
        }

        private static void Serialize_Prefix() => serializing = true;

        private static Exception Serialize_Finalizer(Exception __exception)
        {
            serializing = false;
            return __exception;
        }

        // Types whose "is PlayerSpacecraft" checks mean "is it the ship I'm on".
        private static readonly string[] PlayerShipChecks =
        {
            "MapIcon", "NavMapIcon", "UI.ViewscreenTargetingDisplay",
            "Sector.Spacecraft.Subsystems.SimpleEngineClient", "Sector.Spacecraft.Subsystems.SimpleWarpCoreClient",
        };

        private static readonly MethodInfo OwnShipOrNullMethod = AccessTools.Method(typeof(PvpPrototype), nameof(OwnShipOrNull));

        // Every method of those types with such a check: its "isinst PlayerSpacecraft" becomes OwnShipOrNull.
        private static void PatchPlayerShipChecks(Harmony harmony)
        {
            var transpiler = new HarmonyMethod(typeof(PvpPrototype), nameof(PlayerShipCheck_Transpiler));
            int patched = 0;
            foreach (var name in PlayerShipChecks)
            {
                var type = AccessTools.TypeByName(name);
                if (type == null)
                {
                    Plugin.Log.LogWarning($"PvP: {name} not found");
                    continue;
                }
                foreach (var method in AccessTools.GetDeclaredMethods(type))
                {
                    if (method.IsAbstract || method.ContainsGenericParameters || !ChecksPlayerShip(method)) continue;
                    try
                    {
                        harmony.Patch(method, transpiler: transpiler);
                        patched++;
                    }
                    catch (Exception e)
                    {
                        Plugin.Log.LogWarning($"PvP: {name}.{method.Name}: {e.Message}");
                    }
                }
            }
            Plugin.Log.LogInfo($"PvP: {patched} player-ship checks now mean your own ship");
        }

        private static bool ChecksPlayerShip(MethodBase method)
        {
            try
            {
                return PatchProcessor.ReadMethodBody(method).Any(op => op.Key == OpCodes.Isinst && (op.Value as Type) == typeof(PlayerSpacecraft));
            }
            catch
            {
                return false;
            }
        }

        private static IEnumerable<CodeInstruction> PlayerShipCheck_Transpiler(IEnumerable<CodeInstruction> code)
        {
            foreach (var instruction in code)
            {
                if (instruction.opcode == OpCodes.Isinst && (instruction.operand as Type) == typeof(PlayerSpacecraft))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = OwnShipOrNullMethod;
                }
                yield return instruction;
            }
        }

        /// <summary>"as PlayerSpacecraft", except that in the PvP test another player's ship isn't one.</summary>
        public static PlayerSpacecraft OwnShipOrNull(object body)
        {
            var ship = body as PlayerSpacecraft;
            return ship != null && IsOther(ship) ? null : ship;
        }

        // Both player ships are targetable by every station (for the other crew's maps): not your own on yours.
        private static void Selectable_Postfix(MapIcon __instance, ref bool __result)
        {
            if (__result && Active && Bound != null && __instance.AJHKBDCEKPH == Bound) __result = false;
        }

        // The nav map turns an icon by its ship's heading unless it's a player ship (yours sits under the map's
        // own rotation, which is your heading): the other player ship turns by its own, as an AI ship does.
        private static void Heading_Postfix(MapIcon __instance, ref bool __result)
        {
            if (!__result && __instance.AJHKBDCEKPH is PlayerSpacecraft ship && IsOther(ship)) __result = true;
        }

        // The target panel's shields and hull, as for an AI ship: the ship's target info (shield points of the
        // shield bubble, hull points), not the shield subsystem's own health (the system, not the bubble).
        private static void TargetPanel_Postfix(TacticalTargetUI __instance)
        {
            if (!Active || Bound == null || !(__instance._selectedTarget?.AHMPFHDGMKJ is PlayerSpacecraft ship) || !IsOther(ship)) return;
            if (!(ship.GetTargetInfo() is TargetInfoComponent info)) return;
            float shield = info.ENKDCAEBMIH <= 0f ? 0f : info.ICOKADECALG / info.ENKDCAEBMIH;
            float hull = info.EHMGJDHIMCJ <= 0f ? 0f : info.GBNCJMFFIDI / info.EHMGJDHIMCJ;
            PanelShows = $"shields {shield * 100f:F0}%, hull {hull * 100f:F0}%";
            bool original = (bool)AccessTools.Field(typeof(TacticalTargetUI), "_originalBridgeMode").GetValue(__instance);
            if (original) SetPanelText(__instance, "_tosTargetShieldDisplay", null, shield);
            else SetPanelText(__instance, "_shieldValueText", "_shieldValueTextTNG", shield);
            SetPanelText(__instance, "_hullValueText", "_hullValueTextTNG", hull);
        }

        /// <summary>What the target panel last showed for the other ship, for the PvP test's log.</summary>
        internal static string PanelShows { get; private set; } = "nothing yet";

        private static MethodInfo setText;

        // KHDFGIINMKJ.SetText(TextMeshProUGUI, string, TextMeshPro): the panel's text, Aegis or TNG.
        private static void SetPanelText(TacticalTargetUI panel, string field, string tngField, float fraction)
        {
            if (setText == null)
                setText = AccessTools.GetDeclaredMethods(AccessTools.TypeByName("KHDFGIINMKJ"))
                    .FirstOrDefault(m => m.Name == "SetText" && m.GetParameters().Length == 3 && m.GetParameters()[1].ParameterType == typeof(string));
            var text = AccessTools.Field(typeof(TacticalTargetUI), field)?.GetValue(panel);
            var tng = tngField == null ? null : AccessTools.Field(typeof(TacticalTargetUI), tngField)?.GetValue(panel);
            if (setText == null || (text == null && tng == null)) return;
            float percent = fraction * 100f;
            if (percent > 0f && percent < 1f) percent = 1f;
            setText.Invoke(null, new[] { text, $"{percent:F0} %", tng });
        }

        private static void Category_Postfix(TargetableComponent __instance, ref Category __result)
        {
            if (!Active || Bound == null) return;
            if (!shipOf.TryGetValue(__instance, out var ship))
                shipOf[__instance] = ship = __instance.GetComponentInParent<PlayerSpacecraft>();
            if (ship != null) __result = ship == Bound ? Category.Self : Category.Enemy;
        }
    }
}
