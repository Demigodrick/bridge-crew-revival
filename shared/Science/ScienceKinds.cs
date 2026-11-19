using System;

namespace StbcRevival.Science
{
    // What the things the console scans are: the area a Patrol reading gives and its kind's name, a debris field's name, and where
    // the mission spawner has to put debris for it to land where a ship died.
    internal static class ScienceKinds
    {
        // Patrol's scannable props by index, A-D: Protomatter, String fragment, Graviton, Verteron.
        public static UpgradeArea? ReadingArea(int kind)
        {
            switch (kind)
            {
                case 0: return UpgradeArea.Weapons;
                case 1: return UpgradeArea.Repair;
                case 2: return UpgradeArea.Shields;
                case 3: return UpgradeArea.Impulse;
                default: return null;
            }
        }

        // A reading's kind as the crew is told it ("Graviton stream analysed: ..."), as Patrol's Discoveries name them.
        public static string ReadingName(int kind)
        {
            switch (kind)
            {
                case 0: return "Protomatter";
                case 1: return "String fragment";
                case 2: return "Graviton stream";
                case 3: return "Verteron pocket";
                default: return null;
            }
        }

        // The kind a reading of this area is (ReadingArea the other way round; each area is one kind).
        public static int ReadingKind(UpgradeArea area)
        {
            for (int k = 0; k < 4; k++) if (ReadingArea(k) == area) return k;
            return -1;
        }

        // The name for a reading of this area, or null.
        public static string ReadingNameFor(UpgradeArea area) { return ReadingName(ReadingKind(area)); }

        // The game's teams as ints (Ledger.IsHostileTeam); pirates are 7 and 8.
        public static string Faction(int team)
        {
            switch (team)
            {
                case 2: return "Klingon";
                case 4: return "Raider";
                case 5: return "Romulan";
                case 6: return "Borg";
                case 7: case 8: return "Pirate";
                default: return "Hostile";
            }
        }

        public static string DebrisName(int team) { return Faction(team) + " debris"; }

        // The spawner's angle (AtAngleAndDistanceFromBody): degrees to turn a body's level forward (fx, fz) about
        // the up axis so it points along a level offset (dx, dz). Unity is left-handed: +90 turns +z to +x.
        public static double Bearing(double fx, double fz, double dx, double dz)
        {
            return Math.Atan2(fz * dx - fx * dz, fx * dx + fz * dz) * 180.0 / Math.PI;
        }
    }
}
