using System.Text.Json;

namespace DataverseFileManager;

/// <summary>
/// Loads <see cref="DataverseFileManagerOptions"/> from environment variables, falling back to an
/// optional <c>appsettings.json</c> (its <c>Dataverse</c> section). This keeps per-deployment
/// connection settings — the org URL and Entra application (client) id — out of source control.
/// Environment variables take precedence, so CI / containers can override without a config file.
/// </summary>
/// <remarks>
/// Recognised keys (env var → JSON property):
/// <list type="bullet">
///   <item><c>DATAVERSE_ORG_URL</c> → <c>OrgUrl</c> (required), e.g. <c>https://contoso.crm.dynamics.com</c></item>
///   <item><c>DATAVERSE_CLIENT_ID</c> → <c>ClientId</c> (required), the public-client app id</item>
///   <item><c>DATAVERSE_REDIRECT_URI</c> → <c>RedirectUri</c> (optional)</item>
///   <item><c>DATAVERSE_TABLE_NAME</c> → <c>TableName</c> (optional)</item>
/// </list>
/// </remarks>
public static class DataverseConfig
{
    public const string OrgUrlVar = "DATAVERSE_ORG_URL";
    public const string ClientIdVar = "DATAVERSE_CLIENT_ID";
    public const string RedirectUriVar = "DATAVERSE_REDIRECT_URI";
    public const string TableNameVar = "DATAVERSE_TABLE_NAME";

    /// <summary>
    /// Resolves connection settings. <paramref name="appSettingsPath"/> defaults to
    /// <c>appsettings.json</c> next to the running assembly; a missing file is fine as long as the
    /// required values come from environment variables.
    /// </summary>
    /// <exception cref="InvalidOperationException">Org URL or client id could not be resolved.</exception>
    public static DataverseFileManagerOptions Load(string? appSettingsPath = null)
    {
        appSettingsPath ??= Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        JsonElement? section = TryReadDataverseSection(appSettingsPath);

        string? orgUrl = FromEnv(OrgUrlVar) ?? FromJson(section, "OrgUrl");
        string? clientId = FromEnv(ClientIdVar) ?? FromJson(section, "ClientId");
        string? redirectUri = FromEnv(RedirectUriVar) ?? FromJson(section, "RedirectUri");
        string? tableName = FromEnv(TableNameVar) ?? FromJson(section, "TableName");

        if (string.IsNullOrWhiteSpace(orgUrl) || string.IsNullOrWhiteSpace(clientId))
            throw new InvalidOperationException(
                "Missing Dataverse connection settings. Provide an org URL and client id via the " +
                $"{OrgUrlVar} / {ClientIdVar} environment variables, or an appsettings.json with a " +
                "\"Dataverse\" section (copy appsettings.example.json to appsettings.json and fill it in).");

        return DataverseFileManagerOptions.ForOrg(
            orgUrl: new Uri(orgUrl),
            clientId: clientId,
            redirectUri: string.IsNullOrWhiteSpace(redirectUri) ? null : new Uri(redirectUri),
            tableName: tableName);
    }

    private static string? FromEnv(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string? FromJson(JsonElement? section, string property)
    {
        if (section is { } el && el.TryGetProperty(property, out var value) &&
            value.ValueKind == JsonValueKind.String)
        {
            var s = value.GetString();
            return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        }
        return null;
    }

    private static JsonElement? TryReadDataverseSection(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            // Clone so the value survives disposal of the JsonDocument.
            return doc.RootElement.TryGetProperty("Dataverse", out var section)
                ? section.Clone()
                : null;
        }
        catch (JsonException)
        {
            return null; // a malformed file shouldn't crash startup; env vars may still satisfy the load
        }
    }
}
