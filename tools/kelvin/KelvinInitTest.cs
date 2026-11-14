using System;
using System.IO;
using System.Reflection;
using System.Collections;
class KelvinInitTest {
 static string root;
 static void Main(string[] args) {
  root=args[0];
  AppDomain.CurrentDomain.AssemblyResolve += (s,e) => {
   string name=new AssemblyName(e.Name).Name+".dll";
   foreach(string dir in new[]{"stbc_Data/Managed","BepInEx/core","BepInEx/plugins"}) {
    string p=Path.Combine(root,Path.Combine(dir,name)); if(File.Exists(p)) return Assembly.LoadFrom(p);
   } return null;
  };
  var a=Assembly.LoadFrom(args[1]); var t=a.GetType("StbcRevival.HullName",true);
  var flags=BindingFlags.Static|BindingFlags.NonPublic;
  var field=t.GetField("kelvinPaints",flags);
  var paints=(IEnumerable)(field!=null?field.GetValue(null):t.GetProperty("kelvinPaints",flags).GetValue(null,null));
  int count=0;
  foreach(var p in paints) foreach(var line in (IEnumerable)p.GetType().GetField("Lines",BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public).GetValue(p)) {
   if(line.GetType().GetField("Style").GetValue(line)==null) throw new Exception("Kelvin lettering style is null (source order regression)");
   count++;
  }
  if(count!=4) throw new Exception("Expected four lettering lines");
  Console.WriteLine("PASS: all four Kelvin lettering styles initialized despite HullKelvin.cs preceding HullName.cs");
 }
}
