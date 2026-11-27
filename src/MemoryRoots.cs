using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace StbcRevival
{
    /// <summary>
    /// What holds the memory that a full collection can't free. MemoryLog shows the floor climbing mission after
    /// mission (a host's two hours: 206 MB in the first briefing room, 374 after a Patrol, 405, then 473 MB), but not
    /// what's kept. After each settle in the briefing room or on the shuttle, this walks everything reachable from the
    /// static fields of the game's and the mod's types, a few milliseconds a frame, and logs:
    ///   - the static fields holding the most (an estimate: array lengths, strings and object sizes, each object
    ///     counted for the first field that reaches it, the mod's fields walked first);
    ///   - the static fields that still reach destroyed Unity objects (a bridge, ship or console left behind by a
    ///     mission, kept alive with everything its fields hold: the usual leak), and the commonest types among them.
    /// Estimates only (native memory such as textures and meshes isn't in the managed heap or here), and the game
    /// runs on while it walks, so a number can be a little off. "[roots]" lines. Off unless [Debug] MemoryRoots is on:
    /// the walk keeps a list of every object it has seen (tens of MB on a big heap) until it finishes.
    /// </summary>
    internal static class MemoryRoots
    {
        private const double MsPerFrame = 4.0;
        private const int MaxObjects = 3000000; // the walk's own list of what it has seen costs about 30 bytes each

        private sealed class Root { public string Name; public long Bytes; public int Objects, Destroyed; }
        private sealed class ByReference : IEqualityComparer<object>
        {
            bool IEqualityComparer<object>.Equals(object a, object b) => ReferenceEquals(a, b);
            int IEqualityComparer<object>.GetHashCode(object o) => RuntimeHelpers.GetHashCode(o);
        }

        private static readonly Dictionary<Type, FieldInfo[]> fieldsOf = new Dictionary<Type, FieldInfo[]>();
        private static HashSet<object> seen;
        private static Stack<object> pending;
        private static List<KeyValuePair<string, FieldInfo>> roots;
        private static int nextRoot;
        private static Root current;
        private static List<Root> done;
        private static Dictionary<string, int> destroyedTypes;
        private static string where;
        private static long floor;
        private static float started;
        private static int frames;
        private static bool capped;

        internal static bool Running => seen != null;

        /// <summary>After a settle (a full collection): walk from the statics, unless a walk is already under way.</summary>
        internal static void Start(string phase, long heapFloor)
        {
            if (Running || !Plugin.MemoryRootsLog.Value) return;
            try
            {
                where = phase;
                floor = heapFloor;
                started = Time.realtimeSinceStartup;
                frames = 0;
                capped = false;
                seen = new HashSet<object>(new ByReference());
                pending = new Stack<object>();
                done = new List<Root>();
                destroyedTypes = new Dictionary<string, int>();
                roots = new List<KeyValuePair<string, FieldInfo>>();
                var mod = typeof(MemoryRoots).Assembly;
                var assemblies = new List<Assembly> { mod };
                assemblies.AddRange(AppDomain.CurrentDomain.GetAssemblies().Where(a => a != mod
                    && (a.GetName().Name == "Assembly-CSharp" || a.GetName().Name == "Assembly-CSharp-firstpass" || a.GetName().Name == "StbcCore")));
                foreach (var assembly in assemblies)
                {
                    Type[] types;
                    try { types = assembly.GetTypes(); }
                    catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
                    foreach (var type in types)
                    {
                        if (type == typeof(MemoryRoots) || type.IsGenericTypeDefinition || type.ContainsGenericParameters) continue;
                        foreach (var field in type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                            if (!field.IsLiteral && Followed(field.FieldType))
                                roots.Add(new KeyValuePair<string, FieldInfo>((assembly == mod ? "mod " : "") + type.Name + "." + field.Name, field));
                    }
                }
                nextRoot = 0;
                current = null;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Memory: [roots] couldn't start: " + e.Message);
                Clear();
            }
        }

        /// <summary>Each frame: a few milliseconds of the walk.</summary>
        internal static void Step()
        {
            if (!Running) return;
            frames++;
            var clock = Stopwatch.StartNew();
            try
            {
                while (clock.Elapsed.TotalMilliseconds < MsPerFrame)
                {
                    if (pending.Count == 0)
                    {
                        if (current != null && current.Objects > 0) done.Add(current);
                        current = null;
                        if (nextRoot >= roots.Count || capped) { Finish(); return; }
                        var root = roots[nextRoot++];
                        object value;
                        try { value = root.Value.GetValue(null); } catch { continue; }
                        if (value == null) continue;
                        current = new Root { Name = root.Key };
                        Visit(value);
                        continue;
                    }
                    Walk(pending.Pop());
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Memory: [roots] stopped: " + e.Message);
                Clear();
            }
        }

        private static void Clear()
        {
            seen = null; pending = null; roots = null; done = null; destroyedTypes = null; current = null;
        }

        private static void Finish()
        {
            long total = done.Sum(r => r.Bytes);
            int objects = done.Sum(r => r.Objects), destroyed = done.Sum(r => r.Destroyed);
            Plugin.Log.LogInfo($"Memory: [roots] in {where} (floor {floor / 1048576.0:0.0} MB): about {total / 1048576.0:0.0} MB in {objects} objects reached from {roots.Count} static fields"
                               + $"{(capped ? " (stopped at the object limit: partial)" : "")}, {destroyed} of them destroyed Unity objects; walked over {frames} frames, {Time.realtimeSinceStartup - started:0.0} s");
            Plugin.Log.LogInfo("Memory: [roots] holding most: " + string.Join("; ", done.OrderByDescending(r => r.Bytes).Take(20)
                .Select(r => $"{r.Name} {r.Bytes / 1048576.0:0.0} MB ({r.Objects} objects{(r.Destroyed > 0 ? $", {r.Destroyed} destroyed" : "")})").ToArray()));
            if (destroyed > 0)
            {
                Plugin.Log.LogInfo("Memory: [roots] reaching destroyed Unity objects: " + string.Join("; ", done.Where(r => r.Destroyed > 0).OrderByDescending(r => r.Destroyed).Take(15)
                    .Select(r => $"{r.Name} {r.Destroyed} ({r.Bytes / 1048576.0:0.0} MB)").ToArray()));
                Plugin.Log.LogInfo("Memory: [roots] destroyed objects still held, by type: " + string.Join(", ", destroyedTypes.OrderByDescending(p => p.Value).Take(25)
                    .Select(p => $"{p.Key} {p.Value}").ToArray()));
            }
            Clear();
        }

        // A value worth following: not a number, a text, an enum, a pointer or reflection's own objects.
        private static bool Followed(Type type) =>
            !type.IsPrimitive && !type.IsEnum && !type.IsPointer && type != typeof(string) && type != typeof(IntPtr) && type != typeof(UIntPtr)
            && type != typeof(decimal) && type != typeof(DateTime) && type != typeof(TimeSpan) && type != typeof(Guid);

        private static void Visit(object value)
        {
            if (value == null || value is string || value is Type || value is MemberInfo || value is Assembly || value is Module
                || value is System.Threading.Thread || value is Pointer) return;
            if (value.GetType().IsValueType) { Walk(value); return; } // a boxed struct: its fields, not counted itself
            if (!seen.Add(value)) return;
            if (seen.Count >= MaxObjects) { capped = true; return; }
            pending.Push(value);
        }

        private static void Walk(object value)
        {
            var type = value.GetType();
            bool counted = !type.IsValueType;
            if (counted) current.Objects++;
            if (value is Array array)
            {
                var element = type.GetElementType();
                current.Bytes += 32 + (long)array.Length * ElementSize(element);
                if (element.IsPrimitive || element.IsEnum) return;
                if (element == typeof(string)) { foreach (string s in array) if (s != null) current.Bytes += 24 + 2L * s.Length; return; }
                if (!Followed(element) || (element.IsValueType && FieldsOf(element).Length == 0)) return;
                foreach (var item in array) Visit(item);
                return;
            }
            if (counted)
            {
                current.Bytes += 16;
                if (value is UnityEngine.Object unity && unity == null) // destroyed, yet still referenced
                {
                    current.Destroyed++;
                    destroyedTypes.TryGetValue(type.Name, out int n);
                    destroyedTypes[type.Name] = n + 1;
                }
            }
            foreach (var field in FieldsOf(type))
            {
                object inner;
                try { inner = field.GetValue(value); } catch { continue; }
                if (counted) current.Bytes += field.FieldType.IsValueType ? 8 : IntPtr.Size;
                if (inner == null) continue;
                if (inner is string text) { if (seen.Add(text)) current.Bytes += 24 + 2L * text.Length; continue; }
                Visit(inner);
            }
        }

        private static FieldInfo[] FieldsOf(Type type)
        {
            if (fieldsOf.TryGetValue(type, out var known)) return known;
            var list = new List<FieldInfo>();
            for (var t = type; t != null && t != typeof(object); t = t.BaseType)
            {
                // Unity's own objects are native underneath: nothing managed to follow below the game's scripts.
                if (t == typeof(UnityEngine.Object) || t == typeof(Component) || t == typeof(Behaviour) || t == typeof(MonoBehaviour) || t == typeof(ScriptableObject)) break;
                foreach (var field in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    var ft = field.FieldType;
                    if (ft == typeof(string)) { list.Add(field); continue; }
                    if (!Followed(ft)) continue;
                    // A struct with nothing to follow inside (a Vector3, a Color): not worth boxing to look.
                    if (ft.IsValueType && (ft == type || FieldsOf(ft).Length == 0)) continue;
                    list.Add(field);
                }
            }
            return fieldsOf[type] = list.ToArray();
        }

        private static int ElementSize(Type element)
        {
            if (element == typeof(byte) || element == typeof(sbyte) || element == typeof(bool)) return 1;
            if (element == typeof(short) || element == typeof(ushort) || element == typeof(char)) return 2;
            if (element == typeof(int) || element == typeof(uint) || element == typeof(float) || element == typeof(Color32)) return 4;
            if (element == typeof(long) || element == typeof(ulong) || element == typeof(double) || element == typeof(Vector2)) return 8;
            if (element == typeof(Vector3)) return 12;
            if (element == typeof(Vector4) || element == typeof(Quaternion) || element == typeof(Color)) return 16;
            if (element == typeof(Matrix4x4)) return 64;
            if (element.IsEnum) return 4;
            return element.IsValueType ? 16 : IntPtr.Size;
        }
    }
}
