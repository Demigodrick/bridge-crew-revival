// The game's script objects (MonoBehaviours and ScriptableObjects, class 114) read out of its asset files with the
// layouts from Layout.cs: each object's script is found through its m_Script link (a MonoScript, class 115, maybe in
// another file), and its data decoded as dictionaries, lists, numbers, strings and "file:path" links. An object
// decoded must use exactly its stored size: the check that the layout is right. Shared by `xref read`/`check` and
// the galaxy designer (tools/galaxy-designer, which links this file); no console output here.

#nullable enable
using System.Text;
using Mono.Cecil;
using StbcRevival;

sealed class ScriptObjects : IDisposable
{
    public sealed class Script
    {
        public string ClassName = "", Namespace = "";
        public string FullName => Namespace == "" ? ClassName : Namespace + "." + ClassName;
    }

    readonly string dataDir;
    readonly Layouts layouts;
    readonly Dictionary<string, TypeDefinition> types;
    readonly Dictionary<string, AssetFile?> opened = new();
    readonly Dictionary<string, Script?> scripts = new();
    readonly Dictionary<TypeDefinition, LayoutNode> layoutOf = new();
    static readonly LayoutNode HeaderLayout = new() { Kind = "struct", Fields = Layouts.ScriptHeader() };

    /// <summary>Problems met finding scripts (a MonoScript that can't be read), for the caller to show.</summary>
    public readonly List<string> Warnings = new();

    public ScriptObjects(string dataDir, ModuleDefinition module)
    {
        this.dataDir = dataDir;
        Module = module;
        layouts = new Layouts(module);
        types = new Dictionary<string, TypeDefinition>();
        foreach (var t in module.GetTypes()) types[t.FullName.Replace('/', '.')] = t;
    }

    public ModuleDefinition Module { get; }
    public string DataDir => dataDir;

    /// <summary>One of the game's asset files (stbc_Data/name), opened once.</summary>
    public AssetFile Open(string name) => TryOpen(name) ?? throw new FileNotFoundException("can't open " + name, Path.Combine(dataDir, name));

    /// <summary>One of the game's asset files, or null when it isn't there or can't be read.</summary>
    public AssetFile? TryOpen(string name)
    {
        if (opened.TryGetValue(name, out var f)) return f;
        try { f = new AssetFile(Path.Combine(dataDir, name)); }
        catch (Exception) { f = null; }
        return opened[name] = f;
    }

    /// <summary>The object a link points at, from the file the link is in: the file and its entry, or null.</summary>
    public (AssetFile file, AssetFile.Entry entry)? Resolve(AssetFile from, PPtr link)
    {
        if (link.IsNull) return null;
        var file = link.FileId == 0 ? from : from.FileOf(link) is { } name && name != "?" ? TryOpen(name) : null;
        var e = file?.Find(link.PathId);
        return e == null ? null : (file!, e);
    }

    /// <summary>A "file:path" link as the decoded data writes it.</summary>
    public static PPtr ParseLink(string? s)
    {
        if (s == null) return default;
        var parts = s.Split(':');
        return new PPtr { FileId = int.Parse(parts[0]), PathId = long.Parse(parts[1]) };
    }

    /// <summary>Unity's header of a script object: m_GameObject, m_Enabled, m_Script, m_Name.</summary>
    public static Dictionary<string, object?> Header(AssetFile f, AssetFile.Entry e)
        => (Dictionary<string, object?>)Layouts.Read(f.At(e), HeaderLayout, e.Offset)!;

    /// <summary>The script of a class-114 object, or null when it can't be found.</summary>
    public Script? ScriptOf(AssetFile f, AssetFile.Entry e) => ScriptAt(f, ParseLink((string?)Header(f, e)["m_Script"]));

    public Script? ScriptAt(AssetFile from, PPtr link)
    {
        var fileName = from.FileOf(link) ?? Path.GetFileName(from.Path);
        var key = fileName + ":" + link.PathId;
        if (scripts.TryGetValue(key, out var known)) return known;
        Script? s = null;
        try
        {
            var f = TryOpen(fileName);
            var e = f?.Find(link.PathId);
            if (f != null && e != null && e.ClassId == 115) s = ReadMonoScript(f.At(e), e);
        }
        catch (Exception ex) { Warnings.Add($"script {key}: {ex.Message}"); }
        return scripts[key] = s;
    }

    /// <summary>The game's type for a script, or null when Assembly-CSharp has none (Unity's own UI scripts).</summary>
    public TypeDefinition? TypeOf(Script s) => types.TryGetValue(s.FullName, out var t) ? t : null;

    public TypeDefinition? TypeNamed(string fullName) => types.TryGetValue(fullName, out var t) ? t : null;

    /// <summary>Whether a type is the named one or derives from it.</summary>
    public static bool Is(TypeDefinition? t, string baseFullName)
    {
        for (; t != null; t = SafeBase(t))
            if (t.FullName == baseFullName) return true;
        return false;
    }

    static TypeDefinition? SafeBase(TypeDefinition t)
    {
        try { return t.BaseType?.Resolve(); } catch { return null; }
    }

    public LayoutNode LayoutOf(TypeDefinition t)
    {
        if (!layoutOf.TryGetValue(t, out var l)) layoutOf[t] = l = layouts.Script(t);
        return l;
    }

    /// <summary>
    /// Decodes a script object with its type's layout. problem: null when it read exactly its stored size, else
    /// what went wrong. A field that can't be read (a Unity type not written out, such as AnimationCurve) stops the
    /// reading there: the fields before it are given, with the problem.
    /// </summary>
    public Dictionary<string, object?>? Read(AssetFile f, AssetFile.Entry e, TypeDefinition t, out string? problem)
    {
        problem = null;
        var value = new Dictionary<string, object?>();
        try
        {
            var r = f.At(e);
            foreach (var field in LayoutOf(t).Fields) value[field.Name] = Layouts.Read(r, field, e.Offset);
            long used = r.BaseStream.Position - e.Offset;
            if (used != e.Size) problem = $"used {used} of {e.Size} bytes";
        }
        catch (Exception ex) { problem = ex.Message; }
        return value;
    }

    /// <summary>Decodes a script object with its own script's type; null with a problem when it can't be.</summary>
    public Dictionary<string, object?>? Read(AssetFile f, AssetFile.Entry e, out string? problem)
    {
        var s = ScriptOf(f, e);
        var t = s == null ? null : TypeOf(s);
        if (t == null) { problem = "no script type"; return null; }
        return Read(f, e, t, out problem);
    }

    // MonoScript (class 115) in Unity 5.4: string m_Name, Int32 m_ExecutionOrder, Hash128 m_PropertiesHash (16 bytes),
    // string m_ClassName, string m_Namespace, string m_AssemblyName, bool m_IsEditorScript.
    static Script ReadMonoScript(BinaryReader r, AssetFile.Entry e)
    {
        Str(r, e);
        r.ReadInt32();
        r.ReadBytes(16);
        return new Script { ClassName = Str(r, e), Namespace = Str(r, e) };
    }

    static string Str(BinaryReader r, AssetFile.Entry e)
    {
        int length = r.ReadInt32();
        if (length < 0 || length > 4096) throw new InvalidDataException("bad MonoScript string");
        var text = Encoding.UTF8.GetString(r.ReadBytes(length));
        long at = r.BaseStream.Position - e.Offset;
        if (at % 4 != 0) r.BaseStream.Position += 4 - at % 4;
        return text;
    }

    public void Dispose()
    {
        foreach (var f in opened.Values) f?.Dispose();
        opened.Clear();
    }
}
