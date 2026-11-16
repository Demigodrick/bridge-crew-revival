// `xref read <file> [Class] [name]`: decodes the script objects (MonoBehaviours and ScriptableObjects, class 114)
// in one of the game's asset files with the layouts from Layout.cs (through ScriptObjects.cs), and prints them as
// JSON. Every object decoded must use exactly its stored size: a check that the layout is right, said per object.

using System.Text.Json;
using Mono.Cecil;

static class ReadAssets
{
    public static int Run(string dataDir, ModuleDefinition module, string file, string? classFilter, string? nameFilter, bool layoutCheckOnly)
    {
        using var objects = new ScriptObjects(dataDir, module);
        var asset = objects.Open(file);
        var output = new List<object>();
        int good = 0, bad = 0, unknown = 0;
        var badByClass = new Dictionary<string, int>();
        var goodByClass = new Dictionary<string, int>();
        foreach (var e in asset.Objects.Where(o => o.ClassId == 114).ToList())
        {
            var header = ScriptObjects.Header(asset, e);
            var script = objects.ScriptAt(asset, ScriptObjects.ParseLink((string?)header["m_Script"]));
            if (script == null) { unknown++; continue; }
            if (classFilter != null && script.ClassName != classFilter && script.FullName != classFilter) continue;
            var name = (string)header["m_Name"]!;
            if (nameFilter != null && name != nameFilter) continue;
            var t = objects.TypeOf(script);
            if (t == null) { unknown++; continue; }

            var value = objects.Read(asset, e, t, out var problem);
            var counts = problem == null ? goodByClass : badByClass;
            counts[script.FullName] = counts.GetValueOrDefault(script.FullName) + 1;
            if (problem == null) good++; else bad++;
            if (!layoutCheckOnly) output.Add(new Dictionary<string, object?> { ["pathId"] = e.PathId, ["class"] = script.FullName, ["ok"] = problem ?? "exact", ["data"] = value });
        }
        foreach (var w in objects.Warnings) Console.Error.WriteLine(w);
        if (layoutCheckOnly)
        {
            Console.WriteLine($"{file}: {good} script objects read exactly, {bad} not, {unknown} with no script found");
            foreach (var kv in goodByClass.OrderBy(k => k.Key)) Console.WriteLine($"  ok   {kv.Value,5}  {kv.Key}");
            foreach (var kv in badByClass.OrderBy(k => k.Key)) Console.WriteLine($"  BAD  {kv.Value,5}  {kv.Key}");
        }
        else Console.WriteLine(JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }));
        return bad == 0 ? 0 : 2;
    }
}
