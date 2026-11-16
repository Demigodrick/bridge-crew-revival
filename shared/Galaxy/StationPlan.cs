using System;
using System.Collections.Generic;

namespace StbcRevival.Galaxy
{
    /// <summary>
    /// Which pieces the game's procedural space station is made of, worked out the way the game's
    /// ProceduralSpaceStationBody.GenerateStation picks them (decompiled), so the picture taker can build the same
    /// station without running the game's script. The game seeds UnityEngine.Random with the prefab's _randomSeed
    /// (27 unless set) and draws, in this order:
    /// - one part: a body-or-dome piece (body + domes);
    /// - more: a first body-or-base piece (body + base); then for each further part a body piece, the last a dome,
    ///   attached to the one before; after each, with value &lt; 0.3 a ring around it, else with a second value
    ///   &lt; 0.6 Range(3, 6) copies of one arm piece around it.
    /// The draws are given as delegates (Unity's Random in the game, a fake in tests); the plan is pure.
    /// </summary>
    public sealed class StationPlan
    {
        /// <summary>A piece: which of the set's lists ("base", "body", "domes", "rings", "arms") and its index there.</summary>
        public struct Piece
        {
            public string List;
            public int Index;
            public override string ToString() => List + "[" + Index + "]";
        }

        public sealed class Part
        {
            public Piece Piece;
            /// <summary>A ring around this part, or null.</summary>
            public Piece? Ring;
            /// <summary>Copies of one arm piece around this part (ArmCount of them), or none when ArmCount is 0.</summary>
            public Piece Arm;
            public int ArmCount;
        }

        public Piece First;
        /// <summary>The parts after the first, each attached to the one before.</summary>
        public List<Part> Parts = new List<Part>();

        /// <param name="range">UnityEngine.Random.Range(int min, int max): min inclusive, max exclusive.</param>
        /// <param name="value">UnityEngine.Random.value.</param>
        /// <returns>null for partsCount ≤ 0 (the game builds nothing).</returns>
        public static StationPlan Make(int partsCount, int bases, int bodies, int domes, int rings, int arms,
            Func<int, int, int> range, Func<float> value)
        {
            if (partsCount <= 0) return null;
            var plan = new StationPlan();
            if (partsCount == 1)
            {
                plan.First = BodyPiece(bodies, "domes", domes, range);
                return plan;
            }
            plan.First = BodyPiece(bodies, "base", bases, range);
            int more = partsCount - 1;
            for (int i = 0; i < more; i++)
            {
                var part = new Part
                {
                    Piece = i != more - 1 ? BodyPiece(bodies, null, 0, range) : new Piece { List = "domes", Index = Pick(range, domes, "domes") },
                };
                if (value() < 0.3f) part.Ring = new Piece { List = "rings", Index = Pick(range, rings, "rings") };
                else if (value() < 0.6f)
                {
                    part.ArmCount = range(3, 6);
                    part.Arm = new Piece { List = "arms", Index = Pick(range, arms, "arms") };
                }
                plan.Parts.Add(part);
            }
            return plan;
        }

        // ProceduralStationSet.GetRandomBodyPiece: one draw over the body list followed by the extra list.
        private static Piece BodyPiece(int bodies, string extra, int extras, Func<int, int, int> range)
        {
            int i = Pick(range, bodies + extras, "body" + (extra != null ? " and " + extra : ""));
            return i < bodies ? new Piece { List = "body", Index = i } : new Piece { List = extra, Index = i - bodies };
        }

        private static int Pick(Func<int, int, int> range, int count, string what)
        {
            if (count <= 0) throw new InvalidOperationException("the station set has no " + what + " pieces");
            int i = range(0, count);
            if (i < 0 || i >= count) throw new InvalidOperationException("a draw outside the " + what + " pieces");
            return i;
        }

        public override string ToString()
        {
            var parts = new List<string> { First.ToString() };
            foreach (var p in Parts)
                parts.Add(p.Piece + (p.Ring.HasValue ? " +ring " + p.Ring.Value : "") + (p.ArmCount > 0 ? " +" + p.ArmCount + "×" + p.Arm : ""));
            return string.Join(", ", parts.ToArray());
        }
    }
}
