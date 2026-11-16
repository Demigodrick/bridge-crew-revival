using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace StbcRevival.Galaxy
{
    /// <summary>
    /// The game's procedural space stations ("Ship - SpaceStationProcedural …") have no model of their own: their
    /// ProceduralSpaceStationBody builds one from pieces in its Awake (GenerateStation, decompiled), which the picture
    /// taker never lets run. This builds the same station on the picture instance, without the game's script:
    /// - the prefab's own settings (_partsCount, default 4; _randomSeed, default 27; _assetSet, a ProceduralStationSet
    ///   with the base/body/domes/rings/arms piece prefabs) are read;
    /// - the pieces are chosen by StationPlan with UnityEngine.Random seeded as the game seeds it, so the picture is
    ///   the station a crew sees (the game reseeds every time, so every copy of a prefab looks the same). The global
    ///   Random state is saved before and put back after, so the game's own randomness isn't moved;
    /// - each piece is instantiated under the inactive picture holder (as the game does at the scene root, but
    ///   nothing wakes) and attached by its attach points exactly as the game's attach step does, with the holder in
    ///   place of the scene root for the moment an attach point is unparented. Rings and arms as the game places them;
    ///   the attach points are then removed (the game's ClearAttachPoints). The game's final uniform rescale is left
    ///   out: it doesn't change a picture framed to the model's bounds.
    /// Everything is then stripped with the rest of the instance before it is activated (ProceduralStationPart and any
    /// other piece script included), so no piece script ever runs.
    /// </summary>
    internal static class GalaxyStations
    {
        /// <summary>
        /// Builds the station's pieces into the (inactive) instance. Null when it isn't a procedural station, else a
        /// description of what was built; throws when it can't be built.
        /// </summary>
        internal static string Build(GameObject instance, Transform holder)
        {
            var body = instance.GetComponentInChildren<ProceduralSpaceStationBody>(true);
            if (body == null) return null;
            var t = Traverse.Create(body);
            int count = (int)t.Field("_partsCount").GetValue();
            int seed = (int)t.Field("_randomSeed").GetValue();
            var set = t.Field("_assetSet").GetValue() as ProceduralStationSet;
            if (count <= 0) return $"seed {seed}, no parts (the game builds nothing either)";
            if (set == null) throw new InvalidOperationException("the station has no piece set (_assetSet)");
            var s = Traverse.Create(set);
            var lists = new Dictionary<string, GameObject[]>
            {
                { "base", s.Field("_base").GetValue() as GameObject[] ?? new GameObject[0] },
                { "body", s.Field("_body").GetValue() as GameObject[] ?? new GameObject[0] },
                { "domes", s.Field("_domes").GetValue() as GameObject[] ?? new GameObject[0] },
                { "rings", s.Field("_rings").GetValue() as GameObject[] ?? new GameObject[0] },
                { "arms", s.Field("_arms").GetValue() as GameObject[] ?? new GameObject[0] },
            };

            StationPlan plan;
            var saved = UnityEngine.Random.state;
            try
            {
                UnityEngine.Random.InitState(seed);
                plan = StationPlan.Make(count, lists["base"].Length, lists["body"].Length, lists["domes"].Length,
                    lists["rings"].Length, lists["arms"].Length, UnityEngine.Random.Range, () => UnityEngine.Random.value);
            }
            finally { UnityEngine.Random.state = saved; }

            GameObject Prefab(StationPlan.Piece p)
            {
                var prefab = lists[p.List][p.Index];
                if (prefab == null) throw new InvalidOperationException($"piece {p} of {set.name} is missing");
                return prefab;
            }

            var parts = new List<ProceduralStationPart>();
            ProceduralStationPart Part(GameObject go)
            {
                var part = go.GetComponent<ProceduralStationPart>();
                if (part == null) throw new InvalidOperationException($"piece {go.name} isn't a station part");
                parts.Add(part);
                return part;
            }

            // The first piece goes under the station body, keeping its own local placement.
            var first = (GameObject)UnityEngine.Object.Instantiate(Prefab(plan.First), body.transform, false);
            if (plan.Parts.Count > 0)
            {
                var previous = Part(first);
                foreach (var step in plan.Parts)
                {
                    // At the prefab's own placement, as the game's Instantiate at the scene root, but under the holder.
                    var part = Part((GameObject)UnityEngine.Object.Instantiate(Prefab(step.Piece), holder, true));
                    Attach(part, previous, holder);
                    if (step.Ring.HasValue)
                    {
                        var ring = Part((GameObject)UnityEngine.Object.Instantiate(Prefab(step.Ring.Value), part.transform, false));
                        ring.transform.localRotation = Quaternion.identity;
                    }
                    for (int j = 0; j < step.ArmCount; j++)
                    {
                        var arm = Part((GameObject)UnityEngine.Object.Instantiate(Prefab(step.Arm), part.transform, false));
                        arm.transform.localRotation = Quaternion.identity;
                        arm.transform.Rotate(new Vector3(0f, 0f, 360f / step.ArmCount * j), Space.Self);
                    }
                    previous = part;
                }
                foreach (var part in parts) ClearAttachPoints(part);
            }
            return $"seed {seed}, {count} parts from {set.name}: {plan}";
        }

        // The game's attach step: the part's attach point 1 moved onto the previous part's attach point 0, carrying
        // the part with it; then the part under the previous one and its attach point back under it. The holder
        // (inactive) stands in for the scene root.
        private static void Attach(ProceduralStationPart part, ProceduralStationPart previous, Transform holder)
        {
            var points = AttachPoints(part);
            var previousPoints = AttachPoints(previous);
            if (points.Length < 2 || previousPoints.Length < 1 || points[1] == null || previousPoints[0] == null)
                throw new InvalidOperationException($"station piece {part.name} or {previous.name} lacks attach points");
            var mine = points[1];
            var theirs = previousPoints[0];
            mine.SetParent(holder, true);
            part.transform.SetParent(mine, true);
            mine.position = theirs.position;
            part.transform.SetParent(previous.transform, true);
            mine.SetParent(part.transform, true);
        }

        private static Transform[] AttachPoints(ProceduralStationPart part) =>
            Traverse.Create(part).Field("_attachPoints").GetValue() as Transform[] ?? new Transform[0];

        // ProceduralStationPart.ClearAttachPoints, at once rather than at the frame's end.
        private static void ClearAttachPoints(ProceduralStationPart part)
        {
            foreach (var point in AttachPoints(part))
                if (point != null) UnityEngine.Object.DestroyImmediate(point.gameObject);
            Traverse.Create(part).Field("_attachPoints").SetValue(null);
        }
    }
}
