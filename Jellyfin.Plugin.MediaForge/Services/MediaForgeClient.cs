using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Jellyfin.Plugin.MediaForge.Models;

namespace Jellyfin.Plugin.MediaForge.Services;

/// <summary>Server-side client for the MediaForge companion module.</summary>
public sealed class MediaForgeClient
{
    private const int MaxResponseBytes = 16 * 1024 * 1024;
    private const int MaxImageBytes = 8 * 1024 * 1024;
    private static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(15);
    private static readonly HashSet<string> AllowedImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg",
        "image/png",
        "image/webp",
        "image/gif",
        "image/avif",
    };
    private readonly HttpClient _httpClient;
    private readonly SecretStore _secrets;

    public MediaForgeClient(HttpClient httpClient, SecretStore secrets)
    {
        _httpClient = httpClient;
        _secrets = secrets;
    }

    public Task<JsonElement> GetHealthAsync(CancellationToken cancellationToken)
        => SendAsync(HttpMethod.Get, "api/v1/connector/health", null, cancellationToken);

    public Task<JsonElement> GetSourcesAsync(CancellationToken cancellationToken)
        => SendAsync(HttpMethod.Get, "api/v1/connector/sources", null, cancellationToken);

    public Task<JsonElement> SearchAsync(string keyword, string site, CancellationToken cancellationToken)
        => SendAsync(
            HttpMethod.Post,
            "api/v1/connector/search",
            new { keyword, site },
            cancellationToken,
            SearchTimeout);

    public Task<JsonElement> GetSeriesAsync(string url, CancellationToken cancellationToken)
        => GetWithUrlAsync("api/v1/connector/series", url, cancellationToken);

    public Task<JsonElement> GetSeasonsAsync(string url, CancellationToken cancellationToken)
        => GetWithUrlAsync("api/v1/connector/seasons", url, cancellationToken);

    public Task<JsonElement> GetEpisodesAsync(string url, CancellationToken cancellationToken)
        => GetWithUrlAsync("api/v1/connector/episodes", url, cancellationToken);

    public Task<JsonElement> GetProvidersAsync(string url, CancellationToken cancellationToken)
        => GetWithUrlAsync("api/v1/connector/providers", url, cancellationToken);

    public Task<JsonElement> GetProgressAsync(IReadOnlyCollection<long> queueIds, CancellationToken cancellationToken)
        => SendAsync(HttpMethod.Post, "api/v1/connector/progress", new { queue_ids = queueIds }, cancellationToken);

    public Task<JsonElement> GetDiscoverAsync(CancellationToken cancellationToken)
        => SendAsync(HttpMethod.Get, "api/v1/connector/discover", null, cancellationToken);

    public async Task<MediaForgeImage> GetImageAsync(string url, CancellationToken cancellationToken)
    {
        var encodedUrl = Uri.EscapeDataString(url);
        try
        {
            return await GetImageFromPathAsync(
                "api/img?url=" + encodedUrl,
                cancellationToken).ConfigureAwait(false);
        }
        catch (MediaForgeException exception) when (exception.StatusCode == HttpStatusCode.BadGateway)
        {
            // MediaForge 1.5 session-protects /api/img. The module fallback
            // exposes the same hardened core proxy behind scoped API-key auth.
            return await GetImageFromPathAsync(
                "api/v1/connector/image?url=" + encodedUrl,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<MediaForgeImage> GetImageFromPathAsync(
        string path,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(TimeSpan.FromSeconds(30));
        var requestToken = timeoutSource.Token;
        var config = Plugin.Instance?.Configuration
            ?? throw new MediaForgeException(HttpStatusCode.ServiceUnavailable, "Plugin-Konfiguration ist nicht verfügbar.");
        var apiKey = _secrets.GetApiKey();
        if (apiKey is null)
        {
            throw new MediaForgeException(HttpStatusCode.ServiceUnavailable, "In Jellyfin ist kein gültiger MediaForge-API-Schlüssel konfiguriert.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri(config.MediaForgeUrl, path));
        request.Headers.Add("X-Api-Key", apiKey);

        try
        {
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                requestToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw SafeUpstreamError(response.StatusCode);
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (!AllowedImageTypes.Contains(mediaType)
                || response.Content.Headers.ContentLength > MaxImageBytes)
            {
                throw new MediaForgeException(HttpStatusCode.BadGateway, "MediaForge hat keine gültige Bildantwort geliefert.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(requestToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            while (true)
            {
                var read = await stream.ReadAsync(chunk, requestToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (buffer.Length + read > MaxImageBytes)
                {
                    throw new MediaForgeException(HttpStatusCode.BadGateway, "MediaForge hat eine unerwartet große Bildantwort geliefert.");
                }

                buffer.Write(chunk, 0, read);
            }

            return new MediaForgeImage(buffer.ToArray(), mediaType);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MediaForgeException(HttpStatusCode.GatewayTimeout, "MediaForge hat beim Laden des Bildes nicht rechtzeitig geantwortet.");
        }
        catch (HttpRequestException)
        {
            throw new MediaForgeException(HttpStatusCode.BadGateway, "Das Bild konnte nicht sicher von MediaForge geladen werden.");
        }
    }

    public async Task<long> QueueAsync(MediaRequest request, CancellationToken cancellationToken)
    {
        var response = await SendAsync(
            HttpMethod.Post,
            "api/v1/connector/download",
            new
            {
                episodes = request.Episodes,
                language = request.Language,
                provider = request.Provider,
                title = request.Title,
                series_url = request.SeriesUrl,
                upscale = request.Upscale,
            },
            cancellationToken).ConfigureAwait(false);

        if (response.TryGetProperty("queue_id", out var queueId))
        {
            if (queueId.ValueKind == JsonValueKind.Number
                && queueId.TryGetInt64(out var numeric)
                && numeric > 0)
            {
                return numeric;
            }

            if (queueId.ValueKind == JsonValueKind.String
                && long.TryParse(queueId.GetString(), out numeric)
                && numeric > 0)
            {
                return numeric;
            }
        }

        throw new MediaForgeException(
            HttpStatusCode.BadGateway,
            "MediaForge hat keine gültige Warteschlangen-ID zurückgegeben.");
    }

    private Task<JsonElement> GetWithUrlAsync(string path, string url, CancellationToken cancellationToken)
        => SendAsync(HttpMethod.Get, $"{path}?url={Uri.EscapeDataString(url)}", null, cancellationToken);

    private async Task<JsonElement> SendAsync(
        HttpMethod method,
        string relativePath,
        object? body,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout ?? TimeSpan.FromSeconds(90));
        var requestToken = timeoutSource.Token;
        var config = Plugin.Instance?.Configuration
            ?? throw new MediaForgeException(HttpStatusCode.ServiceUnavailable, "Plugin-Konfiguration ist nicht verfügbar.");
        var apiKey = _secrets.GetApiKey();
        if (apiKey is null)
        {
            throw new MediaForgeException(HttpStatusCode.ServiceUnavailable, "In Jellyfin ist kein gültiger MediaForge-API-Schlüssel konfiguriert.");
        }

        var requestUri = BuildUri(config.MediaForgeUrl, relativePath);
        using var request = new HttpRequestMessage(method, requestUri);
        request.Headers.Add("X-Api-Key", apiKey);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MediaForgeException(HttpStatusCode.GatewayTimeout, "MediaForge hat nicht rechtzeitig geantwortet.");
        }
        catch (HttpRequestException)
        {
            throw new MediaForgeException(HttpStatusCode.BadGateway, "MediaForge ist vom Jellyfin-Server aus nicht erreichbar.");
        }

        try
        {
            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    throw SafeUpstreamError(response.StatusCode);
                }

                if (response.Content.Headers.ContentLength > MaxResponseBytes)
                {
                    throw new MediaForgeException(HttpStatusCode.BadGateway, "MediaForge hat eine unerwartet große Antwort geliefert.");
                }

                await using var stream = await response.Content.ReadAsStreamAsync(requestToken).ConfigureAwait(false);
                using var buffer = new MemoryStream();
                var chunk = new byte[81920];
                while (true)
                {
                    var read = await stream.ReadAsync(chunk, requestToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    if (buffer.Length + read > MaxResponseBytes)
                    {
                        throw new MediaForgeException(HttpStatusCode.BadGateway, "MediaForge hat eine unerwartet große Antwort geliefert.");
                    }

                    buffer.Write(chunk, 0, read);
                }

                try
                {
                    using var document = buffer.Length == 0
                        ? JsonDocument.Parse("{}")
                        : JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 64 });
                    return document.RootElement.Clone();
                }
                catch (JsonException)
                {
                    throw new MediaForgeException(HttpStatusCode.BadGateway, "MediaForge hat keine gültige JSON-Antwort geliefert.");
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MediaForgeException(HttpStatusCode.GatewayTimeout, "MediaForge hat nicht rechtzeitig geantwortet.");
        }
        catch (HttpRequestException)
        {
            throw new MediaForgeException(HttpStatusCode.BadGateway, "Die Antwort von MediaForge wurde unterbrochen.");
        }
    }

    private static Uri BuildUri(string baseUrl, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)
            || baseUrl.Length > 2048
            || !Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var root)
            || (root.Scheme != Uri.UriSchemeHttp && root.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(root.Host)
            || !string.IsNullOrEmpty(root.UserInfo)
            || !string.IsNullOrEmpty(root.Query)
            || !string.IsNullOrEmpty(root.Fragment))
        {
            throw new MediaForgeException(HttpStatusCode.ServiceUnavailable, "Die konfigurierte MediaForge-URL ist ungültig.");
        }

        return new Uri(root.AbsoluteUri.TrimEnd('/') + "/" + relativePath.TrimStart('/'), UriKind.Absolute);
    }

    private static MediaForgeException SafeUpstreamError(HttpStatusCode statusCode)
    {
        return statusCode switch
        {
            HttpStatusCode.BadRequest => new(statusCode, "MediaForge hat die Anfrage abgelehnt."),
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new(
                HttpStatusCode.BadGateway,
                "MediaForge-Authentifizierung oder API-Berechtigungen sind ungültig."),
            HttpStatusCode.NotFound => new(statusCode, "Der angeforderte Inhalt wurde in MediaForge nicht gefunden."),
            HttpStatusCode.TooManyRequests => new(statusCode, "MediaForge begrenzt derzeit weitere Anfragen. Bitte später erneut versuchen."),
            HttpStatusCode.ServiceUnavailable => new(statusCode, "MediaForge ist derzeit nicht verfügbar."),
            _ => new(HttpStatusCode.BadGateway, "MediaForge konnte die Anfrage nicht verarbeiten."),
        };
    }
}

public sealed record MediaForgeImage(byte[] Data, string MediaType);

/// <summary>Error returned while talking to MediaForge.</summary>
public sealed class MediaForgeException : Exception
{
    public MediaForgeException(HttpStatusCode statusCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
    }

    public HttpStatusCode StatusCode { get; }
}
