using System.Diagnostics;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;

namespace DataverseFileManager;

/// <summary>
/// Owns the MCP client connection to the Dataverse MCP server, including the interactive
/// OAuth 2.1 authorization-code + PKCE public-client flow (system browser + loopback listener)
/// provided by the official SDK's <see cref="HttpClientTransport"/>.
/// </summary>
public sealed class DataverseMcpConnection : IAsyncDisposable
{
    private readonly DataverseFileManagerOptions _options;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly HttpClient _httpClient = new();
    private McpClient? _client;

    public DataverseMcpConnection(DataverseFileManagerOptions options, ILoggerFactory? loggerFactory = null)
    {
        _options = options;
        _loggerFactory = loggerFactory;
    }

    public McpClient Client =>
        _client ?? throw new InvalidOperationException("Not connected. Call ConnectAsync first.");

    public async Task<McpClient> ConnectAsync(CancellationToken ct = default)
    {
        if (_client is not null) return _client;

        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = _options.McpEndpoint,
            Name = _options.ClientName,
            OAuth = new ClientOAuthOptions
            {
                ClientId = _options.ClientId,
                RedirectUri = _options.RedirectUri,
                Scopes = _options.Scopes,
                AuthorizationRedirectDelegate = HandleAuthorizationUrlAsync,
            },
        }, _httpClient, _loggerFactory);

        _client = await McpClient
            .CreateAsync(transport, loggerFactory: _loggerFactory, cancellationToken: ct)
            .ConfigureAwait(false);
        return _client;
    }

    /// <summary>
    /// Catches the OAuth redirect: starts a loopback listener on the redirect authority,
    /// opens the system browser to the authorization URL, and returns the auth code.
    /// </summary>
    private async Task<string?> HandleAuthorizationUrlAsync(
        Uri authorizationUrl, Uri redirectUri, CancellationToken ct)
    {
        using var listener = new HttpListener();
        // Listen on the authority root so any callback path (e.g. /callback) is captured.
        listener.Prefixes.Add($"{redirectUri.Scheme}://{redirectUri.Authority}/");
        listener.Start();

        OpenBrowser(authorizationUrl);

        var context = await listener.GetContextAsync().WaitAsync(ct).ConfigureAwait(false);
        var code = context.Request.QueryString["code"];
        var error = context.Request.QueryString["error"];

        var body = string.IsNullOrEmpty(error)
            ? "<html><body><h2>Authentication complete.</h2><p>You can close this window.</p></body></html>"
            : $"<html><body><h2>Authentication failed</h2><p>{WebUtility.HtmlEncode(error)}</p></body></html>";
        var buffer = Encoding.UTF8.GetBytes(body);
        context.Response.ContentType = "text/html";
        context.Response.ContentLength64 = buffer.Length;
        await context.Response.OutputStream.WriteAsync(buffer, ct).ConfigureAwait(false);
        context.Response.Close();
        listener.Stop();

        return string.IsNullOrEmpty(error) ? code : null;
    }

    private static void OpenBrowser(Uri url) =>
        Process.Start(new ProcessStartInfo { FileName = url.ToString(), UseShellExecute = true });

    public async ValueTask DisposeAsync()
    {
        if (_client is not null) await _client.DisposeAsync().ConfigureAwait(false);
        _httpClient.Dispose();
    }
}
