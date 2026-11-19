using System;
using System.Collections.Generic;

namespace StbcRevival.Science
{
    /// <summary>
    /// A bridge's science console placement file (BepInEx/bridges/science/&lt;layout name&gt;.json, made with the bridge
    /// designer), format 1:
    ///   { "format": 1, "added": [ ...added parts, as a layout's... ],
    ///     "screen": { "pivot": [x,y,z], "turn": deg, "tilt": deg, "size": [w,h] }, "stand": [x,y,z] }
    /// Positions in the bridge scene's space (as a layout's), metres. The screen's pivot is its middle; turn is about
    /// up, as a layout's turns; tilt leans its top away from the person reading it. This reads the screen and the stand
    /// point; the added parts are read as a layout's (BridgeLayout.ParseLayout), here only checked to be a list of objects.
    /// No Unity in here, so it's tested outside the game.
    /// </summary>
    internal sealed class SciencePlacementFile
    {
        public float[] ScreenPivot;   // x, y, z
        public float ScreenTurn, ScreenTilt;
        public float ScreenWidth, ScreenHeight;
        public float[] Stand;         // x, y, z
        public int AddedCount;

        // A screen bigger than this is a mistake (a wall, not a console).
        public const float MaxScreenSize = 5f;

        /// <summary>The file's screen and stand point; throws FormatException, saying what's wrong, if it isn't one.</summary>
        public static SciencePlacementFile Parse(string text)
        {
            var root = MiniJson.Parse(text) as Dictionary<string, object>;
            if (root == null) throw new FormatException("not a JSON object");
            if (!root.TryGetValue("format", out var format) || !(format is double f) || f != 1) throw new FormatException($"format {format ?? "missing"}, not 1");
            var file = new SciencePlacementFile();
            if (root.TryGetValue("added", out var added) && added != null)
            {
                if (!(added is List<object> list)) throw new FormatException("added isn't a list");
                foreach (var item in list) if (!(item is Dictionary<string, object>)) throw new FormatException("an added part isn't an object");
                file.AddedCount = list.Count;
            }
            if (!root.TryGetValue("screen", out var s) || !(s is Dictionary<string, object> screen)) throw new FormatException("no screen");
            file.ScreenPivot = Numbers(screen, "pivot", 3, "screen");
            file.ScreenTurn = Number(screen, "turn", "screen", 0f);
            file.ScreenTilt = Number(screen, "tilt", "screen", 0f);
            var size = Numbers(screen, "size", 2, "screen");
            if (size[0] <= 0f || size[1] <= 0f || size[0] > MaxScreenSize || size[1] > MaxScreenSize)
                throw new FormatException($"screen: size isn't between 0 and {MaxScreenSize} m");
            file.ScreenWidth = size[0];
            file.ScreenHeight = size[1];
            file.Stand = Numbers(root, "stand", 3, "the file");
            return file;
        }

        private static float[] Numbers(Dictionary<string, object> o, string name, int count, string what)
        {
            if (!o.TryGetValue(name, out var v) || !(v is List<object> a) || a.Count != count)
                throw new FormatException($"{what}: {name} isn't {count} numbers");
            var r = new float[count];
            for (int i = 0; i < count; i++)
            {
                if (!(a[i] is double d) || double.IsNaN(d) || double.IsInfinity(d) || Math.Abs(d) > 1e4)
                    throw new FormatException($"{what}: {name} isn't {count} numbers");
                r[i] = (float)d;
            }
            return r;
        }

        private static float Number(Dictionary<string, object> o, string name, string what, float otherwise)
        {
            if (!o.TryGetValue(name, out var v) || v == null) return otherwise;
            if (!(v is double d) || double.IsNaN(d) || double.IsInfinity(d)) throw new FormatException($"{what}: {name} isn't a number");
            return (float)d;
        }
    }
}
