// xref: find your way around the game's obfuscated Assembly-CSharp.dll (reads it from your install;
// nothing from the game is stored here).
//
// A method is "live" when it's reachable (call/callvirt/newobj/ldftn) from any method whose name isn't
// an 11-uppercase-letter obfuscated name; the obfuscator adds many unreachable decoys.
//
// Usage: dotnet run -- <command> [args]     (game folder: $GAME_DIR, default ~/.local/share/Steam/...)
//   find <method>                  methods with that name
//   type <Type> [all]              fields and live methods of a type (all: dead ones too)
//   src <Type> [method]            decompile a type's live methods, or one method
//   callers <Type> <method>        live methods calling it
//   callertypes <Type> <method>    live callers grouped by type
//   fieldrefs <Type> <field>       live methods using a field
//   str <text>                     live methods with a string literal containing <text>
//   returns <Type>                 live parameterless methods returning it, and fields of that type
//   names <regex>                  identifiers (types, members) matching a regex
//   dump [dir]                     every type decompiled to <dir>/<namespace>/<type>.cs (default decompiled/, gitignored)
//
// names.map (next to this file) gives obfuscated names readable ones, per type; src and dump decompile with it
// applied, so a name reads the same where it's declared and everywhere it's used. See the comment at its top.
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;
using Mono.Cecil;
using Mono.Cecil.Cil;

var gameDir = Environment.GetEnvironmentVariable("GAME_DIR")
              ?? Path.Combine(Environment.GetEnvironmentVariable("HOME")!, ".local/share/Steam/steamapps/common/Star Trek Bridge Crew");
var dll = Path.Combine(gameDir, "stbc_Data", "Managed", "Assembly-CSharp.dll");
if (args.Length == 0) { Console.Error.WriteLine("usage: see the comment at the top of Program.cs"); return 1; }

var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(Path.GetDirectoryName(dll)!);
var asm = AssemblyDefinition.ReadAssembly(dll, new ReaderParameters { AssemblyResolver = resolver });
var types = asm.MainModule.GetTypes().ToList();
var all = types.SelectMany(t => t.Methods).ToList();
var obf = new Regex("^[A-Z]{11}$");

var callees = new Dictionary<MethodDefinition, List<MethodDefinition>>();
var callers = new Dictionary<MethodDefinition, List<MethodDefinition>>();
foreach (var m in all)
{
    var list = new List<MethodDefinition>();
    if (m.HasBody)
        foreach (var ins in m.Body.Instructions)
            if (ins.Operand is MethodReference mr && SafeResolve(mr) is { } target && target.Module == asm.MainModule)
                list.Add(target);
    callees[m] = list;
    foreach (var t in list) { if (!callers.TryGetValue(t, out var c)) callers[t] = c = new(); c.Add(m); }
}
var live = new HashSet<MethodDefinition>();
var queue = new Queue<MethodDefinition>(all.Where(m => !obf.IsMatch(m.Name)));
while (queue.Count > 0) { var m = queue.Dequeue(); if (live.Add(m)) foreach (var c in callees[m]) queue.Enqueue(c); }

string Sig(MethodDefinition m) => $"{(m.IsStatic ? "static " : "")}{m.ReturnType.Name} {m.Name}({string.Join(", ", m.Parameters.Select(p => p.ParameterType.Name + " " + p.Name))})";
string L(MethodDefinition m) => $"{(live.Contains(m) ? "LIVE" : "dead")} {m.DeclaringType.FullName}::{Sig(m)}";
TypeDefinition? T(string name) => types.FirstOrDefault(t => t.FullName == name) ?? types.FirstOrDefault(t => t.Name == name);
IEnumerable<MethodDefinition> M(string type, string name) => T(type)?.Methods.Where(m => m.Name == name) ?? Enumerable.Empty<MethodDefinition>();


// ---- names.map --------------------------------------------------------------------------------------------
var renamed = new Dictionary<TypeDefinition, List<string>>(); // per type, "new (was OLD)" for the dump's headers

// The map applied to the loaded assembly, which is written to a temp folder and decompiled from there.
CSharpDecompiler Decompiler()
{
    var path = dll;
    var mapFile = Path.Combine(Here(), "names.map");
    if (File.Exists(mapFile))
    {
        ApplyMap(File.ReadAllLines(mapFile));
        var dir = Path.Combine(Path.GetTempPath(), "stbc-xref");
        Directory.CreateDirectory(dir);
        path = Path.Combine(dir, "Assembly-CSharp.dll");
        asm.Write(path); // gives every definition its token in the copy
    }
    var resolver = new UniversalAssemblyResolver(path, false, null);
    resolver.AddSearchDirectory(Path.GetDirectoryName(dll)!);
    return new CSharpDecompiler(path, resolver, new DecompilerSettings { ThrowOnAssemblyResolveErrors = false });
}

void ApplyMap(string[] lines)
{
    var todo = new List<(Action rename, TypeDefinition owner, string note)>();
    for (int n = 0; n < lines.Length; n++)
    {
        var line = lines[n].Split('#')[0].Trim();
        if (line == "") continue;
        var parts = line.Split('=');
        if (parts.Length != 2) { Console.Error.WriteLine($"names.map:{n + 1}: expected 'Type = Name' or 'Type::member = Name'"); continue; }
        string lhs = parts[0].Trim(), name = parts[1].Trim();
        if (name.EndsWith("?")) name = name.TrimEnd('?') + "_maybe"; // a guess, and it reads like one
        var at = lhs.IndexOf("::", StringComparison.Ordinal);
        var type = T(at < 0 ? lhs : lhs.Substring(0, at));
        if (type == null) { Console.Error.WriteLine($"names.map:{n + 1}: no type {lhs}"); continue; }
        if (at < 0)
        {
            var t = type;
            todo.Add((() => { if (name.Contains('.') && !t.IsNested) { t.Namespace = name.Substring(0, name.LastIndexOf('.')); t.Name = name.Substring(name.LastIndexOf('.') + 1); } else t.Name = name; },
                t, $"type {name} (was {t.Name})"));
            continue;
        }
        var old = lhs.Substring(at + 2);
        var members = type.Fields.Where(f => f.Name == old).Cast<IMemberDefinition>()
            .Concat(type.Methods.Where(m => m.Name == old)).Concat(type.Properties.Where(p => p.Name == old)).Concat(type.Events.Where(e => e.Name == old)).ToList();
        if (members.Count == 0) { Console.Error.WriteLine($"names.map:{n + 1}: {type.FullName} has no {old}"); continue; }
        foreach (var member in members) { var m = member; todo.Add((() => m.Name = name, type, $"{name} (was {old})")); }
    }
    foreach (var (rename, owner, note) in todo)
    {
        if (!renamed.TryGetValue(owner, out var list)) renamed[owner] = list = new();
        list.Add(note);
    }
    foreach (var (rename, _, _) in todo) rename(); // after every lookup: a renamed type changes full names
}

switch (args[0])
{
    case "find":
        foreach (var m in all.Where(m => m.Name == args[1])) Console.WriteLine(L(m));
        break;
    case "type":
    {
        var t = T(args[1]);
        if (t == null) { Console.WriteLine("no such type"); break; }
        Console.WriteLine($"type {t.FullName} : {t.BaseType?.FullName}");
        foreach (var f in t.Fields) Console.WriteLine($"  field {(f.IsStatic ? "static " : "")}{f.FieldType.FullName} {f.Name}");
        foreach (var p in t.Properties) Console.WriteLine($"  property {p.PropertyType.FullName} {p.Name}");
        foreach (var m in t.Methods.Where(m => live.Contains(m) || args.Length > 2)) Console.WriteLine("  " + L(m));
        break;
    }
    case "src":
    {
        var t = T(args[1]);
        if (t == null) { Console.WriteLine("no such type"); break; }
        var picked = t.Methods.Where(m => (args.Length > 2 ? m.Name == args[2] : live.Contains(m)) && m.HasBody).ToList(); // before renaming
        var decompiler = Decompiler();
        if (args.Length > 2 && picked.Count == 0) picked = t.Methods.Where(m => m.Name == args[2] && m.HasBody).ToList(); // a mapped name
        foreach (var m in picked)
        {
            Console.WriteLine("// " + L(m));
            try { Console.WriteLine(decompiler.DecompileAsString(MetadataTokenHelpers.EntityHandleOrNil(m.MetadataToken.ToInt32()))); }
            catch (Exception e) { Console.WriteLine("// FAIL " + e.Message); }
        }
        break;
    }
    case "dump":
    {
        // Every type, one file each under <dir>/<namespace>/, each method headed by its LIVE/dead line. "dead" only
        // means no direct call was found: coroutines, event handlers and delegates can still run it.
        var dir = args.Length > 1 ? args[1] : "decompiled";
        var decompiler = Decompiler();
        int files = 0;
        foreach (var t in types.Where(t => !t.IsNested))
        {
            var text = new System.Text.StringBuilder($"// {t.FullName} : {t.BaseType?.FullName}\n");
            foreach (var nested in new[] { t }.Concat(Nested(t)))
            {
                if (nested != t) text.Append($"\n// ===== nested {nested.FullName} : {nested.BaseType?.FullName}\n");
                if (renamed.TryGetValue(nested, out var was)) text.Append($"// names.map: {string.Join(", ", was)}\n");
                foreach (var f in nested.Fields) text.Append($"// field {(f.IsStatic ? "static " : "")}{f.FieldType.FullName} {f.Name}\n");
                foreach (var m in nested.Methods.Where(m => m.HasBody))
                {
                    text.Append("\n// " + L(m) + "\n");
                    try { text.Append(decompiler.DecompileAsString(MetadataTokenHelpers.EntityHandleOrNil(m.MetadataToken.ToInt32()))); }
                    catch (Exception e) { text.Append("// FAIL " + e.Message + "\n"); }
                }
            }
            var path = Path.Combine(dir, t.Namespace == "" ? "_" : t.Namespace, Regex.Replace(t.Name, "[^A-Za-z0-9_.-]", "_") + ".cs");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text.ToString());
            files++;
        }
        Console.WriteLine($"{files} files in {dir}");
        break;
    }
    case "callers":
        foreach (var m in M(args[1], args[2]))
        {
            Console.WriteLine("== " + L(m));
            foreach (var c in (callers.GetValueOrDefault(m) ?? new()).Where(live.Contains).Distinct()) Console.WriteLine("   <- " + L(c));
        }
        break;
    case "callertypes":
        foreach (var m in M(args[1], args[2]))
            foreach (var g in (callers.GetValueOrDefault(m) ?? new()).Where(live.Contains).GroupBy(c => c.DeclaringType.FullName).OrderBy(g => g.Key))
                Console.WriteLine($"{g.Key}  ({g.Count()})");
        break;
    case "fieldrefs":
        foreach (var m in all.Where(m => live.Contains(m) && m.HasBody && m.Body.Instructions.Any(i =>
                     i.Operand is FieldReference fr && (fr.DeclaringType.FullName == args[1] || fr.DeclaringType.Name == args[1]) && fr.Name == args[2])))
            Console.WriteLine(L(m));
        break;
    case "str":
        foreach (var m in all.Where(m => live.Contains(m) && m.HasBody && m.Body.Instructions.Any(i =>
                     i.OpCode == OpCodes.Ldstr && ((string)i.Operand).Contains(args[1]))))
            Console.WriteLine(L(m));
        break;
    case "returns":
        foreach (var m in all.Where(m => live.Contains(m) && m.ReturnType.Name == args[1] && m.Parameters.Count == 0))
        {
            var lc = (callers.GetValueOrDefault(m) ?? new()).Where(live.Contains).ToList();
            Console.WriteLine($"{L(m)}  callers {lc.Count} in {lc.Select(c => c.DeclaringType.FullName).Distinct().Count()} types");
        }
        foreach (var t in types)
            foreach (var f in t.Fields.Where(f => f.FieldType.Name == args[1]))
                Console.WriteLine($"field {(f.IsStatic ? "static " : "")}{t.FullName}::{f.Name}");
        break;
    case "names":
    {
        var re = new Regex(args[1]);
        var names = types.Select(t => t.FullName)
            .Concat(types.SelectMany(t => t.Fields.Select(f => t.FullName + "::" + f.Name)))
            .Concat(all.Select(m => m.DeclaringType.FullName + "::" + m.Name))
            .Where(n => re.IsMatch(n)).Distinct().OrderBy(n => n);
        foreach (var n in names) Console.WriteLine(n);
        break;
    }
    default:
        Console.Error.WriteLine("unknown command " + args[0]);
        return 1;
}
return 0;

static MethodDefinition? SafeResolve(MethodReference mr)
{
    try { return mr.Resolve(); } catch { return null; }
}

static IEnumerable<TypeDefinition> Nested(TypeDefinition t) => t.NestedTypes.SelectMany(n => new[] { n }.Concat(Nested(n)));

static string Here([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
