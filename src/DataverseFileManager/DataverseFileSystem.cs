using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace DataverseFileManager;

/// <summary>
/// Default <see cref="IDataverseFileSystem"/> implementation: maps filesystem operations onto
/// the verified Dataverse MCP calls plus direct SAS blob transfer.
/// </summary>
public sealed class DataverseFileSystem : IDataverseFileSystem, IAsyncDisposable
{
    private static readonly string SelectColumns = string.Join(", ",
        FileItemColumns.Id, FileItemColumns.Name, FileItemColumns.Path, FileItemColumns.ParentPath,
        FileItemColumns.IsFolder, FileItemColumns.SizeBytes, FileItemColumns.Extension,
        FileItemColumns.MimeType, FileItemColumns.CreatedOn, FileItemColumns.ModifiedOn);

    private readonly DataverseMcpClient _mcp;
    private readonly SasBlobClient _blob;
    private readonly HttpClient _blobHttp;
    private readonly DataverseMcpConnection? _ownedConnection;

    private DataverseFileSystem(McpClient client, string table, DataverseMcpConnection? ownedConnection)
    {
        _mcp = new DataverseMcpClient(client, table);
        _blobHttp = new HttpClient();
        _blob = new SasBlobClient(_blobHttp);
        _ownedConnection = ownedConnection;
    }

    /// <summary>Connects (interactive OAuth on first run) and returns a ready file system.</summary>
    public static async Task<DataverseFileSystem> CreateAsync(
        DataverseFileManagerOptions options, ILoggerFactory? loggerFactory = null, CancellationToken ct = default)
    {
        var connection = new DataverseMcpConnection(options, loggerFactory);
        var client = await connection.ConnectAsync(ct).ConfigureAwait(false);
        return new DataverseFileSystem(client, options.TableName, connection);
    }

    /// <summary>Wraps an already-connected MCP client (caller owns its lifetime).</summary>
    public static DataverseFileSystem FromConnectedClient(McpClient client, string tableName = FileItemColumns.Table)
        => new(client, tableName, ownedConnection: null);

    public async Task<IReadOnlyList<FileItem>> ListFolderAsync(string path, CancellationToken ct = default)
    {
        var parent = VirtualPath.Normalize(path);
        var sql = $"SELECT {SelectColumns} FROM {FileItemColumns.Table} " +
                  $"WHERE {FileItemColumns.ParentPath} = '{VirtualPath.EscapeSqlLiteral(parent)}' " +
                  $"ORDER BY {FileItemColumns.IsFolder} DESC, {FileItemColumns.Name}";
        var rows = await _mcp.ReadQueryAsync(sql, ct).ConfigureAwait(false);
        return MapRows(rows);
    }

    public async Task<FileItem?> GetItemAsync(string path, CancellationToken ct = default)
    {
        var p = VirtualPath.Normalize(path);
        var sql = $"SELECT TOP 1 {SelectColumns} FROM {FileItemColumns.Table} " +
                  $"WHERE {FileItemColumns.Path} = '{VirtualPath.EscapeSqlLiteral(p)}'";
        var rows = await _mcp.ReadQueryAsync(sql, ct).ConfigureAwait(false);
        var mapped = MapRows(rows);
        return mapped.Count > 0 ? mapped[0] : null;
    }

    public async Task<FileItem> CreateFolderAsync(string path, CancellationToken ct = default)
    {
        var p = VirtualPath.Normalize(path);
        await EnsureAncestorsAsync(p, ct).ConfigureAwait(false);

        var existing = await GetItemAsync(p, ct).ConfigureAwait(false);
        if (existing is not null) return existing;

        return await InsertFolderAsync(p, ct).ConfigureAwait(false);
    }

    public async Task<FileItem> UploadAsync(string localPath, string remotePath, CancellationToken ct = default)
    {
        var p = VirtualPath.Normalize(remotePath);
        await EnsureAncestorsAsync(p, ct).ConfigureAwait(false);

        var fileName = VirtualPath.GetLeaf(p);
        var extension = Path.GetExtension(fileName);
        var size = new FileInfo(localPath).Length;

        // Upsert: an in-place edit re-uploads to an existing path, so replace that record's content
        // rather than creating a duplicate row. Only a brand-new path inserts a record.
        var existing = await GetItemAsync(p, ct).ConfigureAwait(false);
        if (existing is { IsFolder: true })
            throw new IOException($"Cannot upload a file to '{p}': a folder already exists there.");

        var id = existing?.RecordId ?? await _mcp.CreateRecordAsync(new Dictionary<string, object?>
        {
            [FileItemColumns.Name] = fileName,
            [FileItemColumns.Path] = p,
            [FileItemColumns.ParentPath] = VirtualPath.GetParent(p),
            [FileItemColumns.IsFolder] = false,
            [FileItemColumns.Extension] = extension,
            [FileItemColumns.MimeType] = GuessMimeType(extension),
        }, ct).ConfigureAwait(false);

        var (sasUrl, token) = await _mcp.InitFileUploadAsync(id, FileItemColumns.FileContent, fileName, ct)
            .ConfigureAwait(false);

        await using (var source = File.OpenRead(localPath))
            await _blob.UploadAsync(sasUrl, source, ct).ConfigureAwait(false);

        await _mcp.CommitFileUploadAsync(token, fileName, ct).ConfigureAwait(false);
        await _mcp.UpdateRecordAsync(id, new Dictionary<string, object?>
        {
            [FileItemColumns.SizeBytes] = size,
        }, ct).ConfigureAwait(false);

        return new FileItem
        {
            Path = p,
            Name = fileName,
            ParentPath = VirtualPath.GetParent(p),
            IsFolder = false,
            SizeBytes = size,
            Extension = extension,
            MimeType = GuessMimeType(extension),
            RecordId = id,
        };
    }

    public async Task DownloadAsync(string remotePath, string localPath, CancellationToken ct = default)
    {
        var item = await RequireFileAsync(remotePath, ct).ConfigureAwait(false);
        var info = await _mcp.FileDownloadAsync(item.RecordId, FileItemColumns.FileContent, ct).ConfigureAwait(false);

        await using var source = await _blob.DownloadAsync(info.SasUrl, ct).ConfigureAwait(false);
        await using var destination = File.Create(localPath);
        await source.CopyToAsync(destination, ct).ConfigureAwait(false);
    }

    public async Task<Stream> OpenReadAsync(string remotePath, CancellationToken ct = default)
    {
        var item = await RequireFileAsync(remotePath, ct).ConfigureAwait(false);
        var info = await _mcp.FileDownloadAsync(item.RecordId, FileItemColumns.FileContent, ct).ConfigureAwait(false);
        return await _blob.DownloadAsync(info.SasUrl, ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string path, CancellationToken ct = default)
    {
        var p = VirtualPath.Normalize(path);
        var item = await GetItemAsync(p, ct).ConfigureAwait(false)
                   ?? throw new FileNotFoundException($"No item at '{p}'.");

        if (item.IsFolder)
            foreach (var child in await ListFolderAsync(p, ct).ConfigureAwait(false))
                await DeleteAsync(child.Path, ct).ConfigureAwait(false);

        await _mcp.DeleteRecordAsync(item.RecordId, ct).ConfigureAwait(false);
    }

    public async Task MoveAsync(string fromPath, string toPath, CancellationToken ct = default)
    {
        var from = VirtualPath.Normalize(fromPath);
        var to = VirtualPath.Normalize(toPath);
        if (from == to) return;

        var item = await GetItemAsync(from, ct).ConfigureAwait(false)
                   ?? throw new FileNotFoundException($"No item at '{from}'.");

        // A folder cannot be moved into itself or one of its own descendants.
        if (item.IsFolder && to.StartsWith(from + "/", StringComparison.Ordinal))
            throw new IOException($"Cannot move '{from}' into its own subtree ('{to}').");

        if (await GetItemAsync(to, ct).ConfigureAwait(false) is not null)
            throw new IOException($"An item already exists at '{to}'.");

        await EnsureAncestorsAsync(to, ct).ConfigureAwait(false);
        await RelocateAsync(item, to, ct).ConfigureAwait(false);
    }

    public async Task RenameAsync(string path, string newName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(newName) || newName.Contains('/') || newName.Contains('\\'))
            throw new ArgumentException($"Invalid file name '{newName}'.", nameof(newName));

        var p = VirtualPath.Normalize(path);
        var item = await GetItemAsync(p, ct).ConfigureAwait(false)
                   ?? throw new FileNotFoundException($"No item at '{p}'.");

        var parent = VirtualPath.GetParent(p);
        var newPath = VirtualPath.Normalize(
            parent is "" or VirtualPath.Root ? "/" + newName : parent + "/" + newName);

        if (newPath == p) return; // no-op
        if (await GetItemAsync(newPath, ct).ConfigureAwait(false) is not null)
            throw new IOException($"An item already exists at '{newPath}'.");

        await RelocateAsync(item, newPath, ct).ConfigureAwait(false);
    }

    // --- internals ---

    /// <summary>
    /// Moves <paramref name="node"/> (and, for a folder, its entire subtree) to <paramref name="newPath"/>,
    /// rewriting each record's <c>name</c>/<c>path</c>/<c>parent_path</c>. Because <c>read_query</c> has no
    /// prefix match, the subtree is gathered by recursive enumeration (the <c>parent_path</c> primitive).
    /// </summary>
    private async Task RelocateAsync(FileItem node, string newPath, CancellationToken ct)
    {
        var subtree = new List<FileItem>();
        await CollectSubtreeAsync(node, subtree, ct).ConfigureAwait(false);

        foreach (var n in subtree)
        {
            // n.Path starts with node.Path; the suffix after it is preserved under newPath.
            var relocated = VirtualPath.Normalize(newPath + n.Path[node.Path.Length..]);
            var leaf = VirtualPath.GetLeaf(relocated);

            var fields = new Dictionary<string, object?>
            {
                [FileItemColumns.Name] = leaf,
                [FileItemColumns.Path] = relocated,
                [FileItemColumns.ParentPath] = VirtualPath.GetParent(relocated),
            };
            if (!n.IsFolder)
                fields[FileItemColumns.Extension] = Path.GetExtension(leaf);

            await _mcp.UpdateRecordAsync(n.RecordId, fields, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Depth-first collection of a node and all its descendants (parent before children).</summary>
    private async Task CollectSubtreeAsync(FileItem node, List<FileItem> acc, CancellationToken ct)
    {
        acc.Add(node);
        if (node.IsFolder)
            foreach (var child in await ListFolderAsync(node.Path, ct).ConfigureAwait(false))
                await CollectSubtreeAsync(child, acc, ct).ConfigureAwait(false);
    }

    private async Task<FileItem> RequireFileAsync(string path, CancellationToken ct)
    {
        var item = await GetItemAsync(path, ct).ConfigureAwait(false)
                   ?? throw new FileNotFoundException($"No file at '{VirtualPath.Normalize(path)}'.");
        if (item.IsFolder)
            throw new InvalidOperationException($"'{item.Path}' is a folder, not a file.");
        return item;
    }

    private async Task EnsureAncestorsAsync(string path, CancellationToken ct)
    {
        foreach (var folder in VirtualPath.AncestorFolders(path))
            if (await GetItemAsync(folder, ct).ConfigureAwait(false) is null)
                await InsertFolderAsync(folder, ct).ConfigureAwait(false);
    }

    private async Task<FileItem> InsertFolderAsync(string path, CancellationToken ct)
    {
        var name = VirtualPath.GetLeaf(path);
        var parent = VirtualPath.GetParent(path);
        var id = await _mcp.CreateRecordAsync(new Dictionary<string, object?>
        {
            [FileItemColumns.Name] = name,
            [FileItemColumns.Path] = path,
            [FileItemColumns.ParentPath] = parent,
            [FileItemColumns.IsFolder] = true,
        }, ct).ConfigureAwait(false);

        return new FileItem { Path = path, Name = name, ParentPath = parent, IsFolder = true, RecordId = id };
    }

    private static IReadOnlyList<FileItem> MapRows(JsonElement rows)
    {
        if (rows.ValueKind != JsonValueKind.Array) return Array.Empty<FileItem>();

        var list = new List<FileItem>(rows.GetArrayLength());
        foreach (var row in rows.EnumerateArray())
            list.Add(MapRow(row));
        return list;
    }

    private static FileItem MapRow(JsonElement row)
    {
        var path = ReadString(row, FileItemColumns.Path) ?? VirtualPath.Root;
        return new FileItem
        {
            RecordId = ReadGuid(row, FileItemColumns.Id),
            Path = path,
            Name = ReadString(row, FileItemColumns.Name) ?? VirtualPath.GetLeaf(path),
            ParentPath = ReadString(row, FileItemColumns.ParentPath),
            IsFolder = ReadBool(row, FileItemColumns.IsFolder),
            SizeBytes = ReadLong(row, FileItemColumns.SizeBytes),
            Extension = ReadString(row, FileItemColumns.Extension),
            MimeType = ReadString(row, FileItemColumns.MimeType),
            CreatedOn = ReadDate(row, FileItemColumns.CreatedOn),
            ModifiedOn = ReadDate(row, FileItemColumns.ModifiedOn),
        };
    }

    private static string? ReadString(JsonElement row, string name) =>
        row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool ReadBool(JsonElement row, string name)
    {
        if (!row.TryGetProperty(name, out var v)) return false;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(v.GetString(), out var b) && b,
            JsonValueKind.Number => v.GetInt32() != 0,
            _ => false,
        };
    }

    private static long? ReadLong(JsonElement row, string name) =>
        row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;

    private static DateTimeOffset? ReadDate(JsonElement row, string name) =>
        row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(v.GetString(), out var d) ? d : null;

    private static Guid ReadGuid(JsonElement row, string name) =>
        row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            && Guid.TryParse(v.GetString(), out var g) ? g : Guid.Empty;

    private static string GuessMimeType(string extension) => extension.ToLowerInvariant() switch
    {
        ".txt" => "text/plain",
        ".pdf" => "application/pdf",
        ".json" => "application/json",
        ".xml" => "application/xml",
        ".csv" => "text/csv",
        ".html" or ".htm" => "text/html",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".zip" => "application/zip",
        _ => "application/octet-stream",
    };

    public async ValueTask DisposeAsync()
    {
        _blobHttp.Dispose();
        if (_ownedConnection is not null) await _ownedConnection.DisposeAsync().ConfigureAwait(false);
    }
}
