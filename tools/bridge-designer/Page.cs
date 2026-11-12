using System.Reflection;

/// <summary>
/// The page's files (web/): built into the program (so it's one file to hand out), or from a folder given with --web.
/// </summary>
sealed class Page
{
    readonly string folder;
    readonly Dictionary<string, string> embedded = new Dictionary<string, string>(StringComparer.Ordinal);
    static readonly Assembly Me = typeof(Page).Assembly;

    public Page(string folder)
    {
        this.folder = folder;
        // Resource names are "web/<path>" (the csproj names them so), with \ if built on Windows.
        foreach (var name in Me.GetManifestResourceNames())
            if (name.StartsWith("web/") || name.StartsWith("web\\")) embedded[name.Replace('\\', '/')["web/".Length..]] = name;
    }

    public (string Type, byte[] Bytes) Get(string rel)
    {
        string type = Path.GetExtension(rel) switch
        {
            ".html" => "text/html; charset=utf-8", ".js" => "text/javascript", ".css" => "text/css",
            ".png" => "image/png", ".svg" => "image/svg+xml", _ => "application/octet-stream",
        };
        if (folder != null)
        {
            string file = Path.GetFullPath(Path.Combine(folder, rel));
            if (!file.StartsWith(Path.GetFullPath(folder) + Path.DirectorySeparatorChar) || !File.Exists(file)) return (type, null);
            return (type, File.ReadAllBytes(file));
        }
        if (!embedded.TryGetValue(rel, out var resource)) return (type, null);
        using var s = Me.GetManifestResourceStream(resource);
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return (type, ms.ToArray());
    }
}
