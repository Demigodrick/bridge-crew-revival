using System;
using System.Collections.Generic;

namespace StbcRevival.Galaxy
{
    /// <summary>
    /// BepInEx/galaxies/thumbs.json: the index of the pictures the mod takes of the game's SectorBody prefabs (in
    /// galaxies/thumbs/), for the galaxy designer:
    /// <c>{ "thumbsFormat": 1, "pictures": { "&lt;prefab name&gt;": { "file": "&lt;png&gt;", "kind": "&lt;kind&gt;", "label": "&lt;readable name&gt;" } } }</c>.
    /// thumbsFormat 0: a run cut short (the player left the shuttle); the pictures listed are there, the rest are
    /// taken on the next shuttle visit.
    /// </summary>
    public sealed class ThumbIndex
    {
        /// <summary>The format the mod writes for a finished run. An index with a lower one is taken again.</summary>
        public const int CurrentFormat = 1;

        public sealed class Picture
        {
            public string File;
            /// <summary>star, planet, moon, station, asteroids, nebula or other, as the mod reads it from the prefab's tags.</summary>
            public string Kind;
            /// <summary>The body's in-game name in English, else a tidy version of the prefab name.</summary>
            public string Label;
        }

        public int Format = CurrentFormat;
        public Dictionary<string, Picture> Pictures = new Dictionary<string, Picture>();

        public static ThumbIndex Parse(string json)
        {
            try
            {
                var root = Json.Obj(MiniJson.Parse(json), "the picture index");
                var index = new ThumbIndex { Format = (int)Json.Num(root, "thumbsFormat", "the picture index", true, 0) };
                if (root.ContainsKey("pictures") && root["pictures"] != null)
                    foreach (var kv in Json.Obj(root["pictures"], "pictures"))
                    {
                        string where = "picture '" + kv.Key + "'";
                        var d = Json.Obj(kv.Value, where);
                        index.Pictures[kv.Key] = new Picture
                        {
                            File = Json.Str(d, "file", where, true, null),
                            Kind = Json.Str(d, "kind", where, false, "other"),
                            Label = Json.Str(d, "label", where, false, null) ?? CatalogueNames.Tidy(kv.Key),
                        };
                    }
                return index;
            }
            catch (FormatException) { throw; }
            catch (Exception e)
            {
                throw new FormatException("Picture index: " + e.Message, e);
            }
        }

        public string ToJson()
        {
            var pictures = new Dictionary<string, object>();
            foreach (var kv in Pictures)
                pictures[kv.Key] = new Dictionary<string, object>
                {
                    { "file", kv.Value.File }, { "kind", kv.Value.Kind ?? "other" }, { "label", kv.Value.Label ?? CatalogueNames.Tidy(kv.Key) },
                };
            return MiniJson.Write(new Dictionary<string, object> { { "thumbsFormat", Format }, { "pictures", pictures } });
        }

        /// <summary>Whether an index file's text (null: no file) is from a finished run in the current format; unreadable is not.</summary>
        public static bool IsCurrent(string json)
        {
            if (json == null) return false;
            try { return Parse(json).Format >= CurrentFormat; }
            catch (FormatException) { return false; }
        }
    }
}
