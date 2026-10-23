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
        var decompiler = new CSharpDecompiler(dll, new UniversalAssemblyResolver(dll, false, null), new DecompilerSettings { ThrowOnAssemblyResolveErrors = false });
        foreach (var m in t.Methods.Where(m => (args.Length > 2 ? m.Name == args[2] : live.Contains(m)) && m.HasBody))
        {
            Console.WriteLine("// " + L(m));
            try { Console.WriteLine(decompiler.DecompileAsString(MetadataTokenHelpers.EntityHandleOrNil(m.MetadataToken.ToInt32()))); }
            catch (Exception e) { Console.WriteLine("// FAIL " + e.Message); }
        }
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
