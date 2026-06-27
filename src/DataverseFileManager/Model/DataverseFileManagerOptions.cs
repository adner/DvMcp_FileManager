namespace DataverseFileManager;

/// <summary>
/// Configuration for connecting the file-manager library to a Dataverse environment
/// through its MCP server (consumed as a pure API — no agent).
/// </summary>
public sealed class DataverseFileManagerOptions
{
    /// <summary>The MCP endpoint, e.g. https://org.crm4.dynamics.com/api/mcp.</summary>
    public required Uri McpEndpoint { get; init; }

    /// <summary>Entra ID application (client) id for the public-client PKCE flow.</summary>
    public required string ClientId { get; init; }

    /// <summary>
    /// Loopback redirect URI. A concrete port is required so the local listener can bind;
    /// Entra ignores the port for loopback, so a registered "http://localhost/callback" matches.
    /// </summary>
    public Uri RedirectUri { get; init; } = new("http://localhost:1179/callback");

    /// <summary>Optional OAuth scopes. If null the SDK discovers them from server metadata.</summary>
    public IList<string>? Scopes { get; init; }

    /// <summary>Logical name of the file-system table.</summary>
    public string TableName { get; init; } = FileItemColumns.Table;

    /// <summary>Display name advertised to the MCP server.</summary>
    public string ClientName { get; init; } = "DataverseFileManager";

    /// <summary>
    /// Builds options for a Dataverse org URL, deriving the MCP endpoint and a default
    /// <c>{org}/.default</c> scope. Pass <paramref name="tableName"/> to target a table whose
    /// logical name differs from the built-in default.
    /// </summary>
    public static DataverseFileManagerOptions ForOrg(
        Uri orgUrl, string clientId, Uri? redirectUri = null, string? tableName = null)
    {
        var baseUrl = orgUrl.GetLeftPart(UriPartial.Authority);
        return new DataverseFileManagerOptions
        {
            McpEndpoint = new Uri($"{baseUrl}/api/mcp"),
            ClientId = clientId,
            RedirectUri = redirectUri ?? new Uri("http://localhost:1179/callback"),
            Scopes = new List<string> { $"{baseUrl}/.default" },
            TableName = string.IsNullOrWhiteSpace(tableName) ? FileItemColumns.Table : tableName,
        };
    }
}
