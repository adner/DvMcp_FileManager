namespace DataverseFileManager;

/// <summary>
/// Helpers for the virtual-path convention: a node's <c>Path</c> has no trailing slash
/// (except root "/"), and a node's <c>ParentPath</c> equals its parent's <c>Path</c>.
/// </summary>
public static class VirtualPath
{
    public const string Root = "/";

    /// <summary>Normalizes to a leading-slash, forward-slash, no-trailing-slash path.</summary>
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return Root;
        var p = path.Replace('\\', '/').Trim();
        if (!p.StartsWith('/')) p = "/" + p;
        while (p.Contains("//")) p = p.Replace("//", "/");
        if (p.Length > 1) p = p.TrimEnd('/');
        return p.Length == 0 ? Root : p;
    }

    /// <summary>Parent folder path. Root's parent is the empty string.</summary>
    public static string GetParent(string path)
    {
        var p = Normalize(path);
        if (p == Root) return string.Empty;
        var idx = p.LastIndexOf('/');
        return idx <= 0 ? Root : p[..idx];
    }

    /// <summary>Leaf segment (file or folder name). Root returns "/".</summary>
    public static string GetLeaf(string path)
    {
        var p = Normalize(path);
        if (p == Root) return Root;
        var idx = p.LastIndexOf('/');
        return p[(idx + 1)..];
    }

    /// <summary>
    /// Ancestor folder paths from the topmost (just under root) down to the immediate parent,
    /// excluding root itself. Used to auto-create missing folders before an insert.
    /// </summary>
    public static IReadOnlyList<string> AncestorFolders(string path)
    {
        var chain = new List<string>();
        var cur = GetParent(path);
        while (!string.IsNullOrEmpty(cur) && cur != Root)
        {
            chain.Add(cur);
            cur = GetParent(cur);
        }
        chain.Reverse();
        return chain;
    }

    /// <summary>Escapes a value for inclusion in a single-quoted SQL string literal.</summary>
    public static string EscapeSqlLiteral(string value) => value.Replace("'", "''");
}
