using System.Net.Http.Headers;

namespace DataverseFileManager;

/// <summary>
/// Moves the actual file bytes over the direct HTTPS PUT/GET to the Azure blob SAS URLs
/// brokered by the MCP file tools. The MCP channel never carries the bytes.
/// </summary>
internal sealed class SasBlobClient
{
    private readonly HttpClient _http;

    public SasBlobClient(HttpClient http) => _http = http;

    /// <summary>Uploads bytes to a write SAS URL. Requires the Azure block-blob header.</summary>
    public async Task UploadAsync(Uri sasUrl, Stream content, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, sasUrl);
        request.Headers.TryAddWithoutValidation("x-ms-blob-type", "BlockBlob");
        request.Content = new StreamContent(content);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Opens a read stream over a read SAS URL.</summary>
    public async Task<Stream> DownloadAsync(Uri sasUrl, CancellationToken ct = default)
    {
        var response = await _http
            .GetAsync(sasUrl, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
    }
}
