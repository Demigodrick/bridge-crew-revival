using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace StbcRevival.Galaxy
{
    /// <summary>
    /// What a crew needs to agree on a galaxy region, in Photon properties:
    ///   - player "GH": each player's own galaxy file hash ("" for none), set all the time, so it goes with the join;
    ///   - room "GH" (host): the host's file hash, "" when the host has no region; room "GN": its name;
    ///   - room "GA" (host): the decision for the mission being flown: the region's hash when it's on, "off" when off,
    ///     "" between missions (cleared off the bridge). A player joining a mission under way follows it
    ///     (GalaxyRegion.Decide), and leaves the crew with a message if the crew flies a region they can't build:
    ///     their Trench wouldn't have the region's systems, so system ids past the Trench wouldn't exist for them, and
    ///     SectorIds after a Trench system with additions would differ (a desync), as with other mod mismatches (ModVersionCheck).
    /// Every PC decides from the same inputs (room GH, every player's GH, the room's mission) at the map hook.
    /// </summary>
    internal static class GalaxyRoom
    {
        internal const string HashKey = "GH";
        internal const string NameKey = "GN";
        internal const string ActiveKey = "GA";
        internal const string Off = "off";
        private const string P = "Galaxy room: ";
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private const BindingFlags Static = BindingFlags.Public | BindingFlags.Static;

        private static float nextUpdate;
        private static string checkedRoom;
        private static float joinedAt = -1;

        // The own file's hash, re-read when the config value or the file changes.
        private static string hashedPath;
        private static DateTime hashedTime;
        private static long hashedLength = -1;
        private static string ownHash = "";
        private static string ownName;
        private static string ownText;
        private static string pendingLeave; // a crew we must leave, with the message why (left from Update)

        /// <summary>The name inside this PC's galaxy file, or null.</summary>
        internal static string OwnName
        {
            get
            {
                var hash = OwnHash; // re-reads the file when it changed
                return hash == "" || string.IsNullOrEmpty(ownName) ? null : ownName;
            }
        }

        /// <summary>This PC's galaxy file hash, "" when no region is set or its file is missing.</summary>
        internal static string OwnHash
        {
            get
            {
                try
                {
                    var path = GalaxyRegion.FilePath();
                    if (path == null || !File.Exists(path))
                    {
                        hashedPath = path;
                        hashedLength = -1;
                        ownHash = "";
                        ownText = null;
                        return ownHash;
                    }
                    var info = new FileInfo(path);
                    if (path != hashedPath || info.LastWriteTimeUtc != hashedTime || info.Length != hashedLength)
                    {
                        hashedPath = path;
                        hashedTime = info.LastWriteTimeUtc;
                        hashedLength = info.Length;
                        string json = File.ReadAllText(path);
                        ownHash = GalaxyFile.Hash(json);
                        ownText = json;
                        try { ownName = GalaxyFile.Parse(json).Name; }
                        catch (FormatException) { ownName = null; } // the region build logs what's wrong
                    }
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning(P + "reading the galaxy file: " + e.Message);
                    ownHash = "";
                    ownText = null;
                }
                return ownHash;
            }
        }

        /// <summary>The host's file hash as the crew sees it: the room's GH (the host publishes its own there), ours alone.</summary>
        internal static string HostHash => Room() == null ? OwnHash : RoomString(HashKey) ?? "";

        /// <summary>One read of this PC's galaxy file: its hash ("" for none) and text (null for none). The decision and
        /// the build both use this one snapshot.</summary>
        internal static string Snapshot(out string json)
        {
            string hash = OwnHash; // re-reads the file when it changed
            json = hash == "" ? null : ownText;
            return hash;
        }

        /// <summary>The hash the crew sees for this PC (our player GH; on the host, the room's GH), or null alone.</summary>
        internal static string PublishedHash()
        {
            if (Room() == null) return null;
            if (IsHost) return RoomString(HashKey) ?? "";
            var me = Patches.PhotonNetworkType?.GetMethod("get_player", Static)?.Invoke(null, null);
            return Value(Props(me), HashKey) ?? "";
        }

        /// <summary>Leave the crew with a message (from the next Update, not inside the game's map load).</summary>
        internal static void LeaveCrew(string why, string message)
        {
            Plugin.Log.LogWarning(P + why + " Leaving the crew.");
            pendingLeave = message;
        }

        private static string Named(string region) => string.IsNullOrEmpty(region) ? "" : region + " ";

        /// <summary>The host has the region off for this mission, but it's in our map.</summary>
        internal static string RegionOffMessage(string region) =>
            $"The host's galaxy region {Named(region)}is off for this mission and yours was on; rejoin to fly together.";

        /// <summary>The host has the region on, and it didn't load here (our build failed, or we decided before the
        /// crew's information arrived).</summary>
        internal static string DidntLoadMessage(string region) =>
            $"The host has galaxy region {Named(region)}on and it didn't load here; rejoin to fly together.";

        internal static string CantBuildMessage(string region) =>
            $"This crew is on a mission in {(string.IsNullOrEmpty(region) ? "a galaxy region" : region)}, a galaxy region you don't have "
            + "(or yours is a different version). Get the host's galaxy file into BepInEx/galaxies and set [Galaxy] Region to it, "
            + "or join when the crew is back in the briefing room.";

        /// <summary>The host's region name, from the room; null alone or hosting.</summary>
        internal static string HostRegionName => IsHost || Room() == null ? null : RoomString(NameKey);

        internal static bool IsHost =>
            Room() == null || Patches.PhotonNetworkType?.GetMethod("get_isMasterClient", Static)?.Invoke(null, null) is bool b && b;

        /// <summary>The host's decision for the mission under way ("off" or a hash), or null between missions.</summary>
        internal static string RunningDecision()
        {
            var v = Room() != null ? RoomString(ActiveKey) : null;
            return string.IsNullOrEmpty(v) ? null : v;
        }

        /// <summary>Host: the decision for this mission, for players who join it later.</summary>
        internal static void PublishDecision(string value, string name)
        {
            var room = Room();
            if (room == null) return;
            PhotonProps.Set(room, ActiveKey, value ?? Off);
            Plugin.Log.LogInfo(P + $"this mission's region: {(value == Off || value == null ? "off" : name)}");
        }

        /// <summary>Every player but the host, as name and file hash ("" for none), in the room's player order.</summary>
        internal static List<KeyValuePair<string, string>> Players()
        {
            var result = new List<KeyValuePair<string, string>>();
            if (Room() == null) return result;
            if (!(Patches.PhotonNetworkType?.GetMethod("get_playerList", Static)?.Invoke(null, null) is Array players)) return result;
            foreach (var p in players)
            {
                if (p == null) continue;
                if (p.GetType().GetMethod("get_isMasterClient", All)?.Invoke(p, null) is bool master && master) continue;
                var name = p.GetType().GetMethod("get_name", All)?.Invoke(p, null) as string;
                var props = Props(p);
                var hash = props != null && props.Contains(HashKey) ? props[HashKey] as string : null;
                result.Add(new KeyValuePair<string, string>(string.IsNullOrEmpty(name) ? "a crewmate" : name, hash ?? ""));
            }
            return result;
        }

        internal static void Update()
        {
            if (Time.unscaledTime < nextUpdate) return;
            nextUpdate = Time.unscaledTime + 1f;
            var photon = Patches.PhotonNetworkType;
            if (photon == null) return;

            // Off the bridge no mission is flown: the region counts as out until the next mission's hook decides.
            if (!OnBridge()) GalaxyRegion.MissionOver();

            if (pendingLeave != null)
            {
                var message = pendingLeave;
                pendingLeave = null;
                GalaxyRegion.MissionOver(); // nothing for the checks below until a new mission decides
                if (Room() != null)
                {
                    Leave(photon);
                    RoomBrowser.Show(message);
                }
                return; // PUN is leaving: no second leave from the checks below
            }

            // Our hash in our player properties: kept locally out of a room, sent with the next join.
            string own = OwnHash;
            var me = photon.GetMethod("get_player", Static)?.Invoke(null, null);
            if (me != null && (Value(Props(me), HashKey) ?? "") != own) PhotonProps.Set(me, HashKey, own);

            var room = Room();
            if (room == null)
            {
                checkedRoom = null;
                return;
            }
            var roomName = room.GetType().GetMethod("get_name", All)?.Invoke(room, null) as string;
            if (IsHost)
            {
                checkedRoom = roomName;
                var props = Props(room);
                if ((Value(props, HashKey) ?? "") != own) PhotonProps.Set(room, HashKey, own);
                string name = own == "" ? "" : GalaxyRegion.RegionName;
                if ((Value(props, NameKey) ?? "") != name) PhotonProps.Set(room, NameKey, name);
                // Off the bridge there's no mission and no decision to follow. (The phase is already the bridge's when
                // the map hook publishes it: GamePhaseManager.SwitchToPhaseRPC sets the index before entering.)
                if (!OnBridge() && !string.IsNullOrEmpty(Value(props, ActiveKey))) PhotonProps.Set(room, ActiveKey, "");
                return;
            }

            // On the bridge with the region in our list while the host has it off (its build failed, or its file changed
            // after the crew was told): we're out of sync with the crew, so leave as a joiner without the file does.
            if (pendingLeave == null && OnBridge() && GalaxyRegion.DecidedThisMission)
            {
                var hostDecision = RunningDecision();
                if (GalaxyRegion.Active && hostDecision == Off)
                {
                    LeaveCrew("the host has the region off for this mission but it's in our map.", RegionOffMessage(RoomString(NameKey)));
                    return;
                }
                // The reverse: the host has it on, we decided off (e.g. before a crewmate's GH arrived).
                if (!GalaxyRegion.Active && hostDecision != null && hostDecision != Off)
                {
                    LeaveCrew("the host has the region on for this mission but it isn't in our map.", DidntLoadMessage(RoomString(NameKey)));
                    return;
                }
            }

            // Joined someone else's crew: give the room's properties a moment, then check once.
            if (checkedRoom != roomName)
            {
                checkedRoom = roomName;
                joinedAt = Time.unscaledTime;
                return;
            }
            if (joinedAt < 0 || Time.unscaledTime - joinedAt < 2f) return;
            joinedAt = -1;
            var running = RunningDecision();
            if (running == null || running == Off || running == own) return;

            string region = RoomString(NameKey);
            LeaveCrew($"crew {roomName} is on a mission in {region} ({running.Substring(0, Math.Min(8, running.Length))}); "
                + $"our file: {(own == "" ? "none" : own.Substring(0, 8))}.", CantBuildMessage(region));
        }

        private static bool OnBridge()
        {
            try { return GamePhaseManager.IEFBKOMBFGO != null && GamePhaseManager.IEFBKOMBFGO.GetCurrentGamePhase() == typeof(GamePhase_Bridge); }
            catch (Exception) { return true; } // unsure: keep the decision
        }

        private static object Room() => Patches.PhotonNetworkType?.GetMethod("get_room", Static)?.Invoke(null, null);

        private static IDictionary Props(object owner) =>
            owner?.GetType().GetMethod("get_customProperties", All)?.Invoke(owner, null) as IDictionary;

        private static string Value(IDictionary props, string key) => props != null && props.Contains(key) ? props[key] as string : null;

        private static string RoomString(string key) => Value(Props(Room()), key);

        private static void Leave(Type photon)
        {
            var leave = photon.GetMethods(Static).Where(m => m.Name == "LeaveRoom").OrderBy(m => m.GetParameters().Length).FirstOrDefault();
            if (leave == null) return;
            var args = leave.GetParameters()
                .Select(p => p.IsOptional ? p.DefaultValue : p.ParameterType == typeof(bool) ? (object)true : null).ToArray();
            leave.Invoke(null, args);
        }
    }
}
