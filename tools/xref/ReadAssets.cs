// `xref read <file> [Class] [name]`: decodes the script objects (MonoBehaviours and ScriptableObjects, class 114)
// in one of the game's asset files with the layouts from Layout.cs, and prints them as JSON. Each object's
// script is found through its m_Script link (a MonoScript, class 115, maybe in another file). Every object
// decoded must use exactly its stored size: a check that the layout is right, said per object.

using System.Text;
using System.Text.Json;
using Mono.Cecil;
using StbcRevival;

static class ReadAssets
{
    sealed class Script { public string ClassName = "", Namespace = ""; public string FullName => Namespace == "" ? ClassName : Namespace + "." + ClassName; }

    public static int Run(string dataDir, ModuleDefinition module, string file, string? classFilter, string? nameFilter, bool layoutCheckOnly)
    {
        var layouts = new Layouts(module);
        var types = module.GetTypes().ToDictionary(t => t.FullName.Replace('/', '.'), t => t);
        var opened = new Dictionary<string, AssetFile>();
        AssetFile Open(string name)
        {
            if (!opened.TryGetValue(name, out var f)) opened[name] = f = new AssetFile(Path.Combine(dataDir, name));
            return f;
        }
        var scripts = new Dictionary<string, Script?>();
        Script? ScriptAt(AssetFile from, PPtr link)
        {
            var fileName = from.FileOf(link) ?? Path.GetFileName(from.Path);
            var key = fileName + ":" + link.PathId;
            if (scripts.TryGetValue(key, out var known)) return known;
            Script? s = null;
            try
            {
                var f = Open(fileName);
                var e = f.Find(link.PathId);
                if (e != null && e.ClassId == 115) s = ReadMonoScript(f.At(e), e);
            }
            catch (Exception ex) { Console.Error.WriteLine($"script {key}: {ex.Message}"); }
            return scripts[key] = s;
        }

        var asset = Open(file);
        var output = new List<object>();
        int good = 0, bad = 0, unknown = 0;
        var badByClass = new Dictionary<string, int>();
        var goodByClass = new Dictionary<string, int>();
        foreach (var e in asset.Objects.Where(o => o.ClassId == 114).ToList())
        {
            var r = asset.At(e);
            var header = (Dictionary<string, object?>)Layouts.Read(r, new LayoutNode { Kind = "struct", Fields = Layouts.ScriptHeader() }, e.Offset)!;
            var scriptLink = ParseLink((string?)header["m_Script"]);
            var script = ScriptAt(asset, scriptLink);
            if (script == null) { unknown++; continue; }
            if (classFilter != null && script.ClassName != classFilter && script.FullName != classFilter) continue;
            var name = (string)header["m_Name"]!;
            if (nameFilter != null && name != nameFilter) continue;
            if (!types.TryGetValue(script.FullName, out var t)) { unknown++; continue; }

            var layout = layouts.Script(t);
            string? problem = null;
            object? value = null;
            try
            {
                r = asset.At(e);
                value = Layouts.Read(r, layout, e.Offset);
                long used = r.BaseStream.Position - e.Offset;
                if (used != e.Size) problem = $"used {used} of {e.Size} bytes";
            }
            catch (Exception ex) { problem = ex.Message; }
            var counts = problem == null ? goodByClass : badByClass;
            counts[script.FullName] = counts.GetValueOrDefault(script.FullName) + 1;
            if (problem == null) good++; else bad++;
            if (!layoutCheckOnly) output.Add(new Dictionary<string, object?> { ["pathId"] = e.PathId, ["class"] = script.FullName, ["ok"] = problem ?? "exact", ["data"] = value });
        }
        if (layoutCheckOnly)
        {
            Console.WriteLine($"{file}: {good} script objects read exactly, {bad} not, {unknown} with no script found");
            foreach (var kv in goodByClass.OrderBy(k => k.Key)) Console.WriteLine($"  ok   {kv.Value,5}  {kv.Key}");
            foreach (var kv in badByClass.OrderBy(k => k.Key)) Console.WriteLine($"  BAD  {kv.Value,5}  {kv.Key}");
        }
        else Console.WriteLine(JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }));
        foreach (var f in opened.Values) f.Dispose();
        return bad == 0 ? 0 : 2;
    }

    static PPtr ParseLink(string? s)
    {
        if (s == null) return default;
        var parts = s.Split(':');
        return new PPtr { FileId = int.Parse(parts[0]), PathId = long.Parse(parts[1]) };
    }

    // MonoScript (class 115) in Unity 5.4: string m_Name, Int32 m_ExecutionOrder, Hash128 m_PropertiesHash (16 bytes),
    // string m_ClassName, string m_Namespace, string m_AssemblyName, bool m_IsEditorScript.
    static Script? ReadMonoScript(BinaryReader r, AssetFile.Entry e)
    {
        Str(r, e);
        r.ReadInt32();
        r.ReadBytes(16);
        var s = new Script { ClassName = Str(r, e), Namespace = Str(r, e) };
        return s;
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
}
