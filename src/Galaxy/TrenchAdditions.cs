using System;
using System.Collections.Generic;
using UnityEngine;

namespace StbcRevival.Galaxy
{
    /// <summary>
    /// The galaxy file's additions to the Trench's own systems (its "trench" section): new families in an existing
    /// system, for Revival Patrol and Free Roam only, on and off together with the region (the same crew decision; the
    /// file hash covers them).
    ///
    /// Swap, don't edit: each Trench system with additions gets a copy (Object.Instantiate of its SystemRoot: same name,
    /// SystemName, SystemIDTag, tags, skybox, lights). The copy's SystemTransforms are the original's own transform
    /// objects, by reference, then a new transform for each new node. Before every mission on the Trench the Trench link
    /// points at the copy when on and at the original otherwise (LinkSwap), so the system keeps its id and position.
    /// Its new transforms come after its own in the list, so the SectorIds of the Trench systems after it move up by
    /// that many for the mission; every PC of the crew builds the same list (the same decision), so they agree.
    ///
    /// Why the original's transforms and nodes can be shared, unchanged (decompiled SectorMap.InitAllSystems,
    /// SystemRoot.MakeRunTimeClone, SystemTransform.Clone/LinkParentAndChildren):
    /// - InitAllSystems only calls AssignSector on the asset transforms of the systems in the list (the game does this
    ///   to the originals on every mission anyway); with the copy linked instead, each is assigned once.
    /// - MakeRunTimeClone instantiates the copy and Clone()s every transform into it: everything the mission changes
    ///   lives on those per-mission clones, never on the shared objects.
    /// - LinkParentAndChildren gives a runtime transform its parent from its node's _parent.Transform.id. A new family's
    ///   _parent is the original ROOT node, whose Transform (the original's ROOT transform) is in the copy's list, so
    ///   the new family's runtime parent is the system's ROOT, as for the families already there.
    /// - The original ROOT node's _children is the game's list and stays as it is, so the runtime ROOT doesn't list the
    ///   new families as children. Nothing live walks down from ROOT: the impulse map (FamilyRegions/FamilyMap) and
    ///   ContainerTransform walk up from POIs and bodies, and FindChildrenWithTags is used on regions, POIs and bodies.
    /// - A copy of the ROOT node (with the new families in its _children) was not chosen: the existing families'
    ///   _parent is the original ROOT node, so they'd look up the original ROOT transform's id, which isn't assigned
    ///   when that transform isn't in the list (stale or default): they'd lose their parent or get a wrong one.
    /// </summary>
    internal static partial class GalaxyRegion
    {
        // The built file's copies: original Trench system -> its copy with the additions, and back.
        private static Dictionary<Map.SystemRoot, Map.SystemRoot> trenchCopies;
        private static Dictionary<Map.SystemRoot, Map.SystemRoot> copyOriginals;
        private static string additionsSummary; // "<n> families in <systems>"

        /// <summary>The Trench system as the game has it for one of our copies, else the system itself.</summary>
        internal static Map.SystemRoot Original(Map.SystemRoot root) =>
            root != null && copyOriginals != null && copyOriginals.TryGetValue(root, out var o) ? o : root;

        private static void SetTrenchCopies(Dictionary<Map.SystemRoot, Map.SystemRoot> copies, string summary)
        {
            trenchCopies = copies != null && copies.Count > 0 ? copies : null;
            copyOriginals = null;
            if (trenchCopies != null)
            {
                copyOriginals = new Dictionary<Map.SystemRoot, Map.SystemRoot>();
                foreach (var kv in trenchCopies) copyOriginals[kv.Value] = kv.Key;
            }
            additionsSummary = summary;
        }

        /// <summary>A new Trench asset: the originals back in whatever its list holds, the copies forgotten.</summary>
        private static void ForgetTrenchCopies(Map.SectorRoot map)
        {
            SwapTrench(map, false, null);
            SetTrenchCopies(null, null);
        }

        /// <summary>The Trench links of systems with additions: at the copies when on, else at the originals.
        /// Logged when <paramref name="hook"/> is given and there are additions.</summary>
        private static void SwapTrench(Map.SectorRoot map, bool on, string hook)
        {
            if (trenchCopies == null || map == null || map.Systems == null) return;
            LinkSwap.Apply(map.Systems, l => l.System, (l, s) => l.System = s, trenchCopies, on);
            if (hook == null) return;
            if (on) Plugin.Log.LogInfo(P + $"Trench additions on: {additionsSummary} ({hook})");
            else Plugin.Log.LogInfo(P + $"Trench additions off: {SystemNames(trenchCopies.Keys)} as the game has them ({hook})");
        }

        private static string SystemNames(IEnumerable<Map.SystemRoot> systems)
        {
            var names = new List<string>();
            foreach (var s in systems) names.Add(s == null ? "?" : CatalogueNames.Display(!string.IsNullOrEmpty(s.SystemName) ? s.SystemName : s.name));
            return string.Join(", ", names.ToArray());
        }

        /// <summary>Each Trench system the file adds to (by the check's matching rule), with its additions in file order;
        /// anything not found goes in <paramref name="missing"/>, so nothing is built.</summary>
        private static List<KeyValuePair<Map.SystemRoot, List<TrenchAddition>>> ResolveAdditions(GalaxyFile file, Catalogue catalogue,
            GameParts game, List<string> missing)
        {
            var result = new List<KeyValuePair<Map.SystemRoot, List<TrenchAddition>>>();
            if (file.Trench == null) return result;
            var bySystem = new Dictionary<Map.SystemRoot, List<TrenchAddition>>();
            foreach (var add in file.Trench)
            {
                string at = "trench " + add.System;
                var found = GalaxyCheck.FindTrench(catalogue, add.System);
                var original = found != null ? game.SystemOrNull(found.Name) : null;
                if (original == null)
                {
                    missing.Add($"{at}: Trench system '{add.System}'");
                    continue;
                }
                var root = game.Root(original);
                // The ROOT node's transform must be one of the system's own: the new families' runtime parent comes from it.
                if (root == null || root.Transform == null || !original.SystemTransforms.Contains(root.Transform))
                    missing.Add($"{at}: '{found.Name}' has no ROOT node with a transform to hang families from");
                game.ResolveNodes(at, add.Nodes, missing);
                // Two additions naming the same system ("Venov" and "2824|Venov") make one copy, in file order.
                if (!bySystem.TryGetValue(original, out var list))
                {
                    bySystem[original] = list = new List<TrenchAddition>();
                    result.Add(new KeyValuePair<Map.SystemRoot, List<TrenchAddition>>(original, list));
                }
                list.Add(add);
            }
            return result;
        }

        /// <summary>A copy of each Trench system with its additions; original -> copy. Everything was resolved first.</summary>
        private static Dictionary<Map.SystemRoot, Map.SystemRoot> BuildAdditions(List<KeyValuePair<Map.SystemRoot, List<TrenchAddition>>> additions,
            GameParts game, out string summary)
        {
            var copies = new Dictionary<Map.SystemRoot, Map.SystemRoot>();
            int families = 0;
            foreach (var kv in additions)
            {
                var original = kv.Key;
                var copy = UnityEngine.Object.Instantiate(original); // name text, SystemIDTag, tags, skybox and lights as they are
                copy.name = original.name;                              // not "(Clone)"
                copy.hideFlags |= HideFlags.DontUnloadUnusedAsset;     // the bridge load and every warp run Resources.UnloadUnusedAssets
                // The original's own transforms, by reference (cloned per mission by MakeRunTimeClone), then the new ones.
                copy.SystemTransforms = new List<SystemTransform>(original.SystemTransforms);
                int own = copy.SystemTransforms.Count;
                var root = game.Root(original); // the game's ROOT node: the new families' parent, left as it is
                int added = 0;
                foreach (var add in kv.Value)
                {
                    BuildNodes("trench " + add.System, add.Nodes, root, copy, game, original);
                    foreach (var n in add.Nodes) if (n.Parent == null) added++;
                }
                families += added;
                copies[original] = copy;
                Plugin.Log.LogInfo(P + $"  Trench {SystemNames(new[] { original })}: its {own} nodes kept as they are (shared), "
                    + $"{copy.SystemTransforms.Count - own} new under its ROOT, {added} new families; the original ROOT node unchanged, "
                    + "the new families' parent by reference");
            }
            summary = $"{families} families in {SystemNames(copies.Keys)}";
            return copies;
        }
    }
}
