using System.Collections;
using System.Reflection;

namespace StbcRevival.Missions
{
    /// <summary>
    /// Torpedo restocks reach the whole crew. The torpedo count is part of the tactical console's state, which
    /// the game takes from whoever sits at Tactical (LauncherWeaponClient.ConsoleDeserialize reads the ammo):
    /// when that's a guest, their game kept sending its old count and undid the host's restock. So the host
    /// counts its restocks in the room property "RST", and every game restocks its own copy of the ship when
    /// the count goes up (LastStandWaves.Restock); the Tactical player's game then sends the full count.
    /// </summary>
    internal static class RestockSync
    {
        private const string Key = "RST";
        private const BindingFlags Static = BindingFlags.Public | BindingFlags.Static;
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        private static int seen = -1;   // the last count acted on (or found on joining)
        private static string seenRoom;

        /// <summary>The host restocked: tell the crew.</summary>
        internal static void Announce()
        {
            var room = Room;
            if (room == null) return;
            int next = Count(room) + 1;
            if (PhotonProps.Set(room, Key, next)) seen = next;
        }

        /// <summary>Each frame: restock when the host's count goes up.</summary>
        internal static void Update()
        {
            var room = Room;
            if (room == null)
            {
                seenRoom = null;
                return;
            }
            var name = room.GetType().GetMethod("get_name", All)?.Invoke(room, null) as string;
            int count = Count(room);
            if (name != seenRoom)
            {
                seenRoom = name; // just joined: restocks before now aren't ours to repeat
                seen = count;
                return;
            }
            if (count <= seen) return;
            seen = count;
            Plugin.Log.LogInfo("Restock from the host: torpedoes " + LastStandWaves.Restock());
        }

        private static object Room => Patches.PhotonNetworkType?.GetMethod("get_room", Static)?.Invoke(null, null);

        private static int Count(object room) =>
            room.GetType().GetMethod("get_customProperties", All)?.Invoke(room, null) is IDictionary props && props[Key] is int n ? n : 0;
    }
}
