// Unity 5.4's serialized layout of the game's script types, worked out from Assembly-CSharp with Unity's own rules,
// because the game's asset files carry no type trees. Used by `xref layout` (print a type's layout) and
// `xref read` (decode objects out of an asset file with it).
//
// The rules (Unity 5.4, as the game was built):
//   - A field is saved when it isn't static, const or readonly, isn't [NonSerialized], and is public or
//     [SerializeField]. A script's fields come base class first, in declaration order.
//   - Saved types: bool, the integer types, float, double, string; enums (as Int32); links to UnityEngine.Object
//     subclasses (a PPtr: Int32 file ID, Int64 path ID); Unity's own value types (Vector3, Color, ...);
//     [Serializable] non-generic classes and structs of the game, inline (classes never null: an empty one is
//     written); and T[] or List<T> of any of those (Int32 count, then the items). Nothing else (dictionaries,
//     hash sets, queues, interfaces, abstract or generic classes, arrays of arrays).
//   - Inline classes nest at most 7 deep; deeper ones aren't written.
//   - After a string, an array or list, or a field smaller than 4 bytes (bool, byte, short, char), the data is
//     padded to 4 bytes.
//   - A MonoBehaviour or ScriptableObject (class 114) starts with Unity's own header: PPtr<GameObject>
//     m_GameObject, UInt8 m_Enabled (padded), PPtr<MonoScript> m_Script, string m_Name.

#nullable enable
using System.Text;
using Mono.Cecil;

sealed class LayoutNode
{
    public string Name = "";
    public string Kind = "";        // "prim", "string", "pptr", "array", "struct", "skip"
    public string Type = "";        // prim: Boolean, Int32...; pptr: target type; struct: type name
    public int Size;                // prim only
    public bool AlignAfter;
    public LayoutNode? Item;        // array
    public List<LayoutNode> Fields = new(); // struct
    public string? Note;
}

sealed class Layouts
{
    const int MaxDepth = 7;
    readonly ModuleDefinition module;

    public Layouts(ModuleDefinition module) => this.module = module;

    static readonly Dictionary<string, (string type, int size)> Prims = new()
    {
        ["System.Boolean"] = ("Boolean", 1), ["System.Byte"] = ("UInt8", 1), ["System.SByte"] = ("SInt8", 1),
        ["System.Char"] = ("Char", 2), ["System.Int16"] = ("Int16", 2), ["System.UInt16"] = ("UInt16", 2),
        ["System.Int32"] = ("Int32", 4), ["System.UInt32"] = ("UInt32", 4), ["System.Int64"] = ("Int64", 8),
        ["System.UInt64"] = ("UInt64", 8), ["System.Single"] = ("Single", 4), ["System.Double"] = ("Double", 8),
    };

    // Unity's own value types, as their fields (all floats unless said).
    static readonly Dictionary<string, string[]> UnityStructs = new()
    {
        ["UnityEngine.Vector2"] = new[] { "x", "y" },
        ["UnityEngine.Vector3"] = new[] { "x", "y", "z" },
        ["UnityEngine.Vector4"] = new[] { "x", "y", "z", "w" },
        ["UnityEngine.Quaternion"] = new[] { "x", "y", "z", "w" },
        ["UnityEngine.Color"] = new[] { "r", "g", "b", "a" },
        ["UnityEngine.Rect"] = new[] { "x", "y", "width", "height" },
        ["UnityEngine.Bounds"] = new[] { "center.x", "center.y", "center.z", "extent.x", "extent.y", "extent.z" },
        ["UnityEngine.Matrix4x4"] = Enumerable.Range(0, 16).Select(i => $"e{i % 4}{i / 4}").ToArray(),
    };

    /// <summary>The header Unity writes before a script's own fields.</summary>
    public static List<LayoutNode> ScriptHeader() => new()
    {
        new LayoutNode { Name = "m_GameObject", Kind = "pptr", Type = "GameObject" },
        new LayoutNode { Name = "m_Enabled", Kind = "prim", Type = "UInt8", Size = 1, AlignAfter = true },
        new LayoutNode { Name = "m_Script", Kind = "pptr", Type = "MonoScript" },
        new LayoutNode { Name = "m_Name", Kind = "string", Type = "string", AlignAfter = true },
    };

    /// <summary>A script type's whole layout: Unity's header, then its saved fields.</summary>
    public LayoutNode Script(TypeDefinition t)
    {
        var root = new LayoutNode { Name = t.FullName, Kind = "struct", Type = t.FullName };
        root.Fields.AddRange(ScriptHeader());
        root.Fields.AddRange(FieldsOf(t, 0));
        return root;
    }

    public List<LayoutNode> FieldsOf(TypeDefinition t, int depth)
    {
        var chain = new List<TypeDefinition>();
        for (var c = t; c != null && !IsUnityBase(c.FullName) && c.FullName != "System.Object" && c.FullName != "System.ValueType"; c = Resolve(c.BaseType))
            chain.Insert(0, c);
        var result = new List<LayoutNode>();
        foreach (var c in chain)
            foreach (var f in c.Fields)
            {
                if (f.IsStatic || f.IsLiteral || f.IsInitOnly || f.IsNotSerialized) continue;
                bool marked = f.CustomAttributes.Any(a => a.AttributeType.FullName == "UnityEngine.SerializeField");
                if (!f.IsPublic && !marked) continue;
                var node = Node(f.Name, f.FieldType, depth);
                if (node != null) result.Add(node);
            }
        return result;
    }

    static bool IsUnityBase(string name) => name is "UnityEngine.MonoBehaviour" or "UnityEngine.ScriptableObject"
        or "UnityEngine.Behaviour" or "UnityEngine.Component" or "UnityEngine.Object";

    TypeDefinition? Resolve(TypeReference? r)
    {
        if (r == null) return null;
        try { return r.Resolve(); } catch { return null; }
    }

    bool IsUnityObject(TypeReference r)
    {
        for (var t = Resolve(r); t != null; t = Resolve(t.BaseType))
            if (t.FullName == "UnityEngine.Object") return true;
        return false;
    }

    /// <summary>The node for a field of this type, or null when Unity doesn't save it.</summary>
    LayoutNode? Node(string name, TypeReference type, int depth, bool inArray = false)
    {
        if (Prims.TryGetValue(type.FullName, out var p))
            return new LayoutNode { Name = name, Kind = "prim", Type = p.type, Size = p.size, AlignAfter = p.size < 4 && !inArray };
        if (type.FullName == "System.String") return new LayoutNode { Name = name, Kind = "string", Type = "string", AlignAfter = true };
        if (UnityStructs.TryGetValue(type.FullName, out var parts))
        {
            var s = new LayoutNode { Name = name, Kind = "struct", Type = type.Name };
            foreach (var part in parts) s.Fields.Add(new LayoutNode { Name = part, Kind = "prim", Type = "Single", Size = 4 });
            return s;
        }
        if (type.FullName == "UnityEngine.Color32")
        {
            var s = new LayoutNode { Name = name, Kind = "struct", Type = "Color32" };
            foreach (var part in new[] { "r", "g", "b", "a" }) s.Fields.Add(new LayoutNode { Name = part, Kind = "prim", Type = "UInt8", Size = 1 });
            return s;
        }
        if (type.FullName == "UnityEngine.LayerMask")
            return new LayoutNode { Name = name, Kind = "prim", Type = "UInt32", Size = 4, Note = "LayerMask" };
        if (type.FullName is "UnityEngine.AnimationCurve" or "UnityEngine.Gradient" or "UnityEngine.RectOffset" or "UnityEngine.GUIStyle")
            return new LayoutNode { Name = name, Kind = "skip", Type = type.Name, Note = "a Unity type this reader doesn't know: data after it can't be read" };

        if (type is ArrayType at)
        {
            if (at.ElementType is ArrayType || at.Rank != 1) return null;
            var item = Node("item", at.ElementType, depth, inArray: true);
            return item == null ? null : new LayoutNode { Name = name, Kind = "array", Type = at.ElementType.Name + "[]", Item = item, AlignAfter = true };
        }
        if (type is GenericInstanceType g)
        {
            if (g.ElementType.FullName != "System.Collections.Generic.List`1") return null;
            var arg = g.GenericArguments[0];
            if (arg is ArrayType || arg is GenericInstanceType gi && gi.ElementType.FullName == "System.Collections.Generic.List`1") return null;
            var item = Node("item", arg, depth, inArray: true);
            return item == null ? null : new LayoutNode { Name = name, Kind = "array", Type = $"List<{arg.Name}>", Item = item, AlignAfter = true };
        }
        if (type.IsGenericParameter) return null;

        if (IsUnityObject(type)) return new LayoutNode { Name = name, Kind = "pptr", Type = type.FullName };

        var def = Resolve(type);
        if (def == null) return null;
        if (def.IsEnum)
            return new LayoutNode { Name = name, Kind = "prim", Type = "Int32", Size = 4, Note = "enum " + def.FullName };
        if (def.Module != module) return null;   // System and Unity types not handled above aren't saved
        if (!def.IsSerializable || def.IsAbstract || def.IsInterface || def.HasGenericParameters) return null;
        if (depth >= MaxDepth) return null;
        var node = new LayoutNode { Name = name, Kind = "struct", Type = def.FullName };
        node.Fields.AddRange(FieldsOf(def, depth + 1));
        return node;
    }

    public static void Print(LayoutNode n, StringBuilder text, string indent = "")
    {
        string line = n.Kind switch
        {
            "prim" => $"{n.Type} {n.Name}",
            "string" => $"string {n.Name}",
            "pptr" => $"PPtr<{n.Type}> {n.Name}",
            "array" => $"{n.Type} {n.Name}  (Int32 count, then each)",
            "struct" => $"{n.Type} {n.Name}",
            _ => $"?? {n.Type} {n.Name}",
        };
        if (n.AlignAfter && n.Kind != "string" && n.Kind != "array") line += "  (padded to 4)";
        if (n.Note != null) line += $"  [{n.Note}]";
        text.Append(indent).Append(line).Append('\n');
        if (n.Kind == "array" && n.Item != null && (n.Item.Kind == "struct" || n.Item.Kind == "array")) foreach (var f in n.Item.Fields) Print(f, text, indent + "    ");
        if (n.Kind == "struct") foreach (var f in n.Fields) Print(f, text, indent + "  ");
    }

    // ---- Reading ----------------------------------------------------------------------------------------------

    /// <summary>Decodes data with a layout: dictionaries, lists, numbers, strings and "file:path" links.</summary>
    public static object? Read(BinaryReader r, LayoutNode n, long objectStart)
    {
        object? value;
        switch (n.Kind)
        {
            case "prim":
                value = n.Type switch
                {
                    "Boolean" => r.ReadByte() != 0, "UInt8" => r.ReadByte(), "SInt8" => (object)r.ReadSByte(),
                    "Char" => r.ReadUInt16(), "Int16" => r.ReadInt16(), "UInt16" => r.ReadUInt16(),
                    "Int32" => r.ReadInt32(), "UInt32" => r.ReadUInt32(), "Int64" => r.ReadInt64(),
                    "UInt64" => r.ReadUInt64(), "Single" => r.ReadSingle(), "Double" => r.ReadDouble(),
                    _ => throw new InvalidDataException("unknown " + n.Type),
                };
                break;
            case "string":
            {
                int length = r.ReadInt32();
                if (length < 0 || length > 1 << 24) throw new InvalidDataException($"{n.Name}: string length {length}");
                value = Encoding.UTF8.GetString(r.ReadBytes(length));
                break;
            }
            case "pptr":
            {
                int file = r.ReadInt32();
                long path = r.ReadInt64();
                value = file == 0 && path == 0 ? null : $"{file}:{path}";
                break;
            }
            case "array":
            {
                int count = r.ReadInt32();
                if (count < 0 || count > 1 << 22) throw new InvalidDataException($"{n.Name}: count {count}");
                var list = new List<object?>();
                for (int i = 0; i < count; i++) list.Add(Read(r, n.Item!, objectStart));
                value = list;
                break;
            }
            case "struct":
            {
                var d = new Dictionary<string, object?>();
                foreach (var f in n.Fields) d[f.Name] = Read(r, f, objectStart);
                value = d;
                break;
            }
            default:
                throw new InvalidDataException($"{n.Name}: {n.Type} can't be read ({n.Note})");
        }
        if (n.AlignAfter)
        {
            long at = r.BaseStream.Position - objectStart;
            if (at % 4 != 0) r.BaseStream.Position += 4 - at % 4;
        }
        return value;
    }
}
