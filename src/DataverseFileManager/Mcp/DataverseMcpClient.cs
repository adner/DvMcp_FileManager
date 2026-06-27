using System.Text;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace DataverseFileManager;

/// <summary>
/// Deterministic, typed wrappers over the Dataverse MCP tools (consumed as a pure API).
/// Each method maps to exactly one MCP tool call and parses its textual result.
/// </summary>
internal sealed class DataverseMcpClient
{
    private readonly McpClient _client;
    private readonly string _table;

    public DataverseMcpClient(McpClient client, string table)
    {
        _client = client;
        _table = table;
    }

    public async Task<JsonElement> ReadQueryAsync(string sql, CancellationToken ct = default)
    {
        var text = await CallTextAsync("read_query", new() { ["querytext"] = sql }, ct).ConfigureAwait(false);
        return ParseJson(text);
    }

    public async Task<Guid> CreateRecordAsync(IReadOnlyDictionary<string, object?> item, CancellationToken ct = default)
    {
        var text = await CallTextAsync("create_record",
            new() { ["tablename"] = _table, ["item"] = item }, ct).ConfigureAwait(false);
        return ExtractGuid(text)
            ?? throw new DataverseMcpException($"Could not parse a record id from: {text}");
    }

    public async Task UpdateRecordAsync(Guid recordId, IReadOnlyDictionary<string, object?> item, CancellationToken ct = default)
    {
        await CallTextAsync("update_record",
            new() { ["tablename"] = _table, ["recordId"] = recordId.ToString(), ["item"] = item }, ct)
            .ConfigureAwait(false);
    }

    public async Task DeleteRecordAsync(Guid recordId, CancellationToken ct = default)
    {
        await CallTextAsync("delete_record",
            new() { ["tablename"] = _table, ["recordId"] = recordId.ToString(), ["hasUserApproved"] = true }, ct)
            .ConfigureAwait(false);
    }

    public async Task<(Uri SasUrl, string ContinuationToken)> InitFileUploadAsync(
        Guid recordId, string fileAttribute, string fileName, CancellationToken ct = default)
    {
        var text = await CallTextAsync("init_file_upload", new()
        {
            ["tablename"] = _table,
            ["recordId"] = recordId.ToString(),
            ["fileAttributeName"] = fileAttribute,
            ["fileName"] = fileName,
        }, ct).ConfigureAwait(false);

        var json = ParseJson(StripLeadingComment(text));
        var sas = json.GetProperty("SasUrl").GetString()!;
        var token = json.GetProperty("FileContinuationToken").GetString()!;
        return (new Uri(sas), token);
    }

    public async Task CommitFileUploadAsync(string continuationToken, string fileName, CancellationToken ct = default)
    {
        await CallTextAsync("commit_file_upload",
            new() { ["continuationToken"] = continuationToken, ["fileName"] = fileName }, ct)
            .ConfigureAwait(false);
    }

    public async Task<FileDownloadInfo> FileDownloadAsync(Guid recordId, string fileAttribute, CancellationToken ct = default)
    {
        var text = await CallTextAsync("file_download",
            new() { ["tablename"] = _table, ["recordId"] = recordId.ToString(), ["fileAttributeName"] = fileAttribute }, ct)
            .ConfigureAwait(false);

        var json = ParseJson(text);
        return new FileDownloadInfo(
            new Uri(json.GetProperty("SasUrl").GetString()!),
            json.TryGetProperty("FileName", out var fn) ? fn.GetString() : null,
            json.TryGetProperty("FileSizeInBytes", out var fs) && fs.ValueKind == JsonValueKind.Number ? fs.GetInt64() : null,
            json.TryGetProperty("MimeType", out var mt) ? mt.GetString() : null);
    }

    private async Task<string> CallTextAsync(string tool, Dictionary<string, object?> args, CancellationToken ct)
    {
        var result = await _client.CallToolAsync(tool, args, cancellationToken: ct).ConfigureAwait(false);
        var text = GetText(result);
        if (result.IsError == true)
            throw new DataverseMcpException($"MCP tool '{tool}' returned an error: {text}");
        return text;
    }

    private static string GetText(CallToolResult result)
    {
        var sb = new StringBuilder();
        foreach (var block in result.Content)
            if (block is TextContentBlock t)
                sb.Append(t.Text);
        return sb.ToString();
    }

    private static JsonElement ParseJson(string text)
    {
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    /// <summary>init_file_upload prefixes the JSON with a "// ..." comment line.</summary>
    private static string StripLeadingComment(string text)
    {
        var idx = text.IndexOf('{');
        return idx >= 0 ? text[idx..] : text;
    }

    private static Guid? ExtractGuid(string text)
    {
        foreach (var token in text.Split(
                     new[] { ' ', '\n', '\r', '\t', '.', ',' }, StringSplitOptions.RemoveEmptyEntries))
            if (Guid.TryParse(token, out var g))
                return g;
        return null;
    }
}

internal sealed record FileDownloadInfo(Uri SasUrl, string? FileName, long? FileSizeInBytes, string? MimeType);

/// <summary>Raised when a Dataverse MCP tool call fails or returns an unparseable result.</summary>
public sealed class DataverseMcpException : Exception
{
    public DataverseMcpException(string message) : base(message) { }
}
