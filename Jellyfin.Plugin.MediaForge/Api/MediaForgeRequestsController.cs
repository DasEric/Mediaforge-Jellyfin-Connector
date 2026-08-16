using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Mime;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.MediaForge.Models;
using Jellyfin.Plugin.MediaForge.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.MediaForge.Api;

/// <summary>Jellyfin API for search, user requests and admin decisions.</summary>
[ApiController]
[Route("MediaForgeRequests")]
[Authorize]
[Produces(MediaTypeNames.Application.Json)]
public sealed class MediaForgeRequestsController : ControllerBase
{
    private const int MaxEpisodesPerRequest = 500;
    private const int MaxKnownSources = 32;
    private static readonly TimeSpan RateWindow = TimeSpan.FromMinutes(1);
    private readonly MediaForgeClient _mediaForge;
    private readonly MediaAccessGrantStore _grants;
    private readonly UserRateLimiter _rateLimiter;
    private readonly SecretStore _secrets;
    private readonly MediaForgeRequestApplicationService _application;

    public MediaForgeRequestsController(
        MediaForgeClient mediaForge,
        MediaAccessGrantStore grants,
        UserRateLimiter rateLimiter,
        SecretStore secrets,
        MediaForgeRequestApplicationService application)
    {
        _mediaForge = mediaForge;
        _grants = grants;
        _rateLimiter = rateLimiter;
        _secrets = secrets;
        _application = application;
    }

    [HttpGet("InjectionScript")]
    [AllowAnonymous]
    public IActionResult GetInjectionScript() => Embedded("Web.injection.js", "application/javascript");

    [HttpGet("Page")]
    [AllowAnonymous]
    public IActionResult GetPage() => Embedded("Web.requests.html", MediaTypeNames.Text.Html);

    [HttpGet("PageScript")]
    [AllowAnonymous]
    public IActionResult GetPageScript() => Embedded("Web.requests.js", "application/javascript");

    [HttpGet("Status")]
    public IActionResult GetStatus()
    {
        var config = Plugin.Instance?.Configuration;
        return Ok(new
        {
            configured = !string.IsNullOrWhiteSpace(config?.MediaForgeUrl) && _secrets.HasApiKey,
            mode = config?.AutoApproveRequests == true ? "automatic" : "approval",
            maintenance = config?.MaintenanceMode == true,
            maintenanceMessage = config?.MaintenanceMessage ?? string.Empty,
            defaultLanguage = config?.DefaultLanguage ?? "German Dub",
            defaultProvider = config?.DefaultProvider ?? "VOE",
            maxSearchSources = Math.Clamp(config?.MaxSearchSources ?? 8, 1, MaxKnownSources),
        });
    }

    [HttpGet("Sources")]
    public async Task<IActionResult> GetSources(CancellationToken cancellationToken)
    {
        var (userId, _) = CurrentUser();
        if (!Allow(userId, "catalog", 120))
        {
            return RateLimitExceeded();
        }

        try
        {
            var sources = await _application.GetAllowedSourcesAsync(
                userId,
                cancellationToken,
                applyRateLimit: false).ConfigureAwait(false);
            return Ok(new { sources });
        }
        catch (MediaForgeException exception)
        {
            return MediaForgeError(exception);
        }
    }

    [HttpGet("Discover")]
    public async Task<IActionResult> Discover(CancellationToken cancellationToken)
    {
        var (userId, _) = CurrentUser();
        if (!Allow(userId, "discover", 12))
        {
            return RateLimitExceeded();
        }

        try
        {
            var sourcesTask = _application.GetAllowedSourcesAsync(
                userId,
                cancellationToken,
                applyRateLimit: false);
            var discoverTask = _mediaForge.GetDiscoverAsync(cancellationToken);
            await Task.WhenAll(sourcesTask, discoverTask).ConfigureAwait(false);

            var allowed = (await sourcesTask.ConfigureAwait(false))
                .ToDictionary(source => source.Id, source => source.Label, StringComparer.OrdinalIgnoreCase);
            var rows = new Dictionary<string, IReadOnlyList<DiscoverItem>>(StringComparer.Ordinal)
            {
                ["new"] = ReadDiscoverRow(await discoverTask.ConfigureAwait(false), "new", allowed),
                ["popular"] = ReadDiscoverRow(await discoverTask.ConfigureAwait(false), "popular", allowed),
                ["movies"] = ReadDiscoverRow(await discoverTask.ConfigureAwait(false), "movies", allowed),
            };

            foreach (var item in rows.Values.SelectMany(items => items))
            {
                _grants.GrantUrl(userId, item.Source, item.Url);
            }

            return Ok(new { rows });
        }
        catch (MediaForgeException exception)
        {
            return MediaForgeError(exception);
        }
    }

    [HttpGet("Image")]
    [Produces("image/jpeg", "image/png", "image/webp", "image/gif", "image/avif")]
    public async Task<IActionResult> Image(
        [Required, MaxLength(4096)] string url,
        CancellationToken cancellationToken)
    {
        var (userId, _) = CurrentUser();
        if (!Allow(userId, "image", 240))
        {
            return RateLimitExceeded();
        }

        if (!TryReadMediaForgeImageUrl(url, out var normalized))
        {
            return BadRequest(new { error = "Ungültige Bild-URL." });
        }

        try
        {
            var image = await _mediaForge.GetImageAsync(normalized, cancellationToken).ConfigureAwait(false);
            Response.Headers.CacheControl = "private, max-age=86400";
            return File(image.Data, image.MediaType);
        }
        catch (MediaForgeException)
        {
            return NotFound();
        }
    }

    [HttpGet("Search")]
    public async Task<IActionResult> Search(
        [Required, MinLength(2), MaxLength(120)] string query,
        string source = "all",
        CancellationToken cancellationToken = default)
    {
        var (userId, _) = CurrentUser();
        try
        {
            var response = await _application.SearchAsync(
                userId,
                query,
                source,
                mediaType: null,
                issueSelectionTokens: false,
                cancellationToken).ConfigureAwait(false);
            return Ok(new { groups = response.Groups });
        }
        catch (Exception exception) when (exception is MediaForgeException or MediaForgeApplicationException)
        {
            return ApplicationError(exception);
        }
    }

    [HttpGet("Series")]
    public Task<IActionResult> GetSeries([Required] string url, CancellationToken cancellationToken)
        => ProxyGranted(url, token => _mediaForge.GetSeriesAsync(url, token), cancellationToken);

    [HttpGet("Seasons")]
    public Task<IActionResult> GetSeasons([Required] string url, CancellationToken cancellationToken)
        => ProxyGranted(url, token => _mediaForge.GetSeasonsAsync(url, token), cancellationToken);

    [HttpGet("Episodes")]
    public Task<IActionResult> GetEpisodes([Required] string url, CancellationToken cancellationToken)
        => ProxyGranted(url, token => _mediaForge.GetEpisodesAsync(url, token), cancellationToken);

    [HttpGet("Providers")]
    public Task<IActionResult> GetProviders([Required] string url, CancellationToken cancellationToken)
        => ProxyGranted(url, token => _mediaForge.GetProvidersAsync(url, token), cancellationToken);

    [HttpPost("Requests/Plan")]
    [Consumes(MediaTypeNames.Application.Json)]
    public async Task<IActionResult> PlanRequest(
        [FromBody] AutomaticMediaRequest request,
        CancellationToken cancellationToken)
    {
        var (userId, _) = CurrentUser();
        try
        {
            var plan = await _application.PlanAsync(
                userId,
                request,
                requireGrant: true,
                applyRateLimit: true,
                cancellationToken).ConfigureAwait(false);
            return Ok(plan.ToResponse());
        }
        catch (Exception exception) when (exception is MediaForgeException or MediaForgeApplicationException)
        {
            return ApplicationError(exception);
        }
    }

    [HttpPost("Requests/Automatic")]
    [Consumes(MediaTypeNames.Application.Json)]
    public async Task<IActionResult> CreateAutomaticRequest(
        [FromBody] AutomaticMediaRequest request,
        CancellationToken cancellationToken)
    {
        var (userId, username) = CurrentUser();
        try
        {
            var result = await _application.SubmitAutomaticAsync(
                userId,
                username,
                request,
                requireGrant: true,
                cancellationToken).ConfigureAwait(false);
            return MapSubmitResult(result, request.MediaType);
        }
        catch (Exception exception) when (exception is MediaForgeException or MediaForgeApplicationException)
        {
            return ApplicationError(exception);
        }
    }

    [HttpGet("Requests/Mine")]
    public async Task<IActionResult> GetMyRequests(CancellationToken cancellationToken)
    {
        var (userId, _) = CurrentUser();
        return Ok(await _application.ListForUserAsync(
            userId,
            200,
            synchronizeProgress: false,
            cancellationToken).ConfigureAwait(false));
    }

    [HttpGet("Requests/Progress")]
    public async Task<IActionResult> GetMyProgress(CancellationToken cancellationToken)
    {
        var (userId, _) = CurrentUser();
        try
        {
            var progress = await _application.GetProgressAsync(userId, cancellationToken).ConfigureAwait(false);
            return Ok(new { items = progress });
        }
        catch (Exception exception) when (exception is MediaForgeException or MediaForgeApplicationException)
        {
            return ApplicationError(exception);
        }
    }

    [HttpDelete("Requests/{id:long}")]
    public async Task<IActionResult> WithdrawRequest(long id, CancellationToken cancellationToken)
    {
        var (userId, username) = CurrentUser();
        var result = await _application.WithdrawAsync(id, userId, username, cancellationToken).ConfigureAwait(false);
        return result switch
        {
            WithdrawRequestResult.NotFound => NotFound(new { error = "Anfrage nicht gefunden." }),
            WithdrawRequestResult.NotPending => Conflict(new
            {
                error = "Nur noch nicht freigegebene Anfragen können zurückgezogen werden.",
            }),
            _ => Ok(await _application.GetAsync(id, cancellationToken).ConfigureAwait(false)),
        };
    }

    [HttpPost("Requests")]
    [Consumes(MediaTypeNames.Application.Json)]
    public async Task<IActionResult> CreateRequest(
        [FromBody] CreateMediaRequest request,
        CancellationToken cancellationToken)
    {
        Normalize(request);
        var validationError = ValidateRequest(request);
        if (validationError is not null)
        {
            return BadRequest(new { error = validationError });
        }

        var (userId, username) = CurrentUser();
        try
        {
            var result = await _application.SubmitAutomaticAsync(
                userId,
                username,
                new AutomaticMediaRequest
                {
                    Title = request.Title,
                    SeriesUrl = request.SeriesUrl,
                    Source = request.Source,
                    MediaType = request.MediaType,
                    Language = request.Language,
                    Provider = request.Provider,
                    Upscale = request.Upscale,
                },
                requireGrant: true,
                cancellationToken).ConfigureAwait(false);
            return MapSubmitResult(result, request.MediaType);
        }
        catch (Exception exception) when (exception is MediaForgeException or MediaForgeApplicationException)
        {
            return ApplicationError(exception);
        }
    }

    [HttpGet("Admin/Requests")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<IActionResult> GetAllRequests(CancellationToken cancellationToken)
        => Ok(await _application.ListAllAsync(500, cancellationToken).ConfigureAwait(false));

    [HttpPost("Admin/Requests/{id:long}/Approve")]
    [Consumes(MediaTypeNames.Application.Json)]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<IActionResult> Approve(long id, CancellationToken cancellationToken)
    {
        var existing = await _application.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return NotFound(new { error = "Anfrage nicht gefunden." });
        }

        var (_, admin) = CurrentUser();
        var result = await _application.ApproveAsync(id, admin, cancellationToken).ConfigureAwait(false);
        if (result.Status is RequestStatuses.Queued or RequestStatuses.Available)
        {
            return Ok(result);
        }

        if (result.Status != RequestStatuses.Failed)
        {
            return Conflict(new { error = "Diese Anfrage kann in ihrem aktuellen Status nicht freigegeben werden." });
        }

        return StatusCode(StatusCodes.Status502BadGateway, result);
    }

    [HttpPost("Admin/Requests/{id:long}/Reject")]
    [Consumes(MediaTypeNames.Application.Json)]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<IActionResult> Reject(
        long id,
        [FromBody] RejectMediaRequest? payload,
        CancellationToken cancellationToken)
    {
        var item = await _application.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return NotFound(new { error = "Anfrage nicht gefunden." });
        }

        var reason = payload?.Reason?.Trim() ?? string.Empty;
        if (reason.Any(char.IsControl))
        {
            return BadRequest(new { error = "Der Ablehnungsgrund enthält ungültige Steuerzeichen." });
        }

        var (_, admin) = CurrentUser();
        var rejected = await _application.RejectAsync(id, reason, admin, cancellationToken).ConfigureAwait(false);
        if (!rejected)
        {
            return Conflict(new { error = "Diese Anfrage kann in ihrem aktuellen Status nicht abgelehnt werden." });
        }

        return Ok(await _application.GetAsync(id, cancellationToken).ConfigureAwait(false));
    }

    [HttpGet("Admin/ApiKey")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public IActionResult GetApiKeyStatus()
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(new { hasApiKey = _secrets.HasApiKey });
    }

    [HttpPost("Admin/ApiKey")]
    [Consumes(MediaTypeNames.Application.Json)]
    [Authorize(Policy = Policies.RequiresElevation)]
    public IActionResult UpdateApiKey([FromBody] UpdateApiKeyRequest payload)
    {
        try
        {
            _secrets.SetApiKey(payload.ApiKey);
            Response.Headers.CacheControl = "no-store";
            return Ok(new { hasApiKey = true });
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { error = exception.Message });
        }
    }

    [HttpDelete("Admin/ApiKey")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public IActionResult DeleteApiKey()
    {
        _secrets.ClearApiKey();
        Response.Headers.CacheControl = "no-store";
        return Ok(new { hasApiKey = false });
    }

    [HttpPost("Admin/Test")]
    [Consumes(MediaTypeNames.Application.Json)]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<IActionResult> TestConnection(CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _mediaForge.GetHealthAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (MediaForgeException exception)
        {
            return MediaForgeError(exception);
        }
    }

    private async Task<IActionResult> ProxyGranted(
        string url,
        Func<CancellationToken, Task<JsonElement>> action,
        CancellationToken cancellationToken)
    {
        var (userId, _) = CurrentUser();
        if (!Allow(userId, "catalog", 120))
        {
            return RateLimitExceeded();
        }

        if (!_grants.IsGranted(userId, url, out var source))
        {
            return BadRequest(new { error = "Diese MediaForge-URL wurde nicht durch deine aktuelle Suche freigegeben." });
        }

        try
        {
            var response = await action(cancellationToken).ConfigureAwait(false);
            _grants.GrantFromJson(userId, source, response);
            return Ok(response);
        }
        catch (MediaForgeException exception)
        {
            return MediaForgeError(exception);
        }
    }

    private IActionResult Embedded(string suffix, string contentType)
    {
        Response.Headers.XContentTypeOptions = "nosniff";
        var resourceName = $"{typeof(Plugin).Namespace}.{suffix}";
        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
        return stream is null ? NotFound() : File(stream, contentType);
    }

    private (string Id, string Name) CurrentUser()
    {
        var name = User.FindFirst(ClaimTypes.Name)?.Value
            ?? User.Identity?.Name
            ?? "unknown";
        var id = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? User.FindFirst("Jellyfin-UserId")?.Value
            ?? User.FindFirst("UserId")?.Value
            ?? name;
        return (SafeIdentity(id), SafeIdentity(name));
    }

    private bool Allow(string userId, string operation, int limit)
        => _rateLimiter.TryConsume(userId, operation, limit, RateWindow);

    private static IActionResult RateLimitExceeded()
        => new ObjectResult(new { error = "Zu viele Anfragen. Bitte kurz warten." })
        {
            StatusCode = StatusCodes.Status429TooManyRequests,
        };

    private static string? ValidateRequest(CreateMediaRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Title)
            || string.IsNullOrWhiteSpace(request.SeriesUrl)
            || string.IsNullOrWhiteSpace(request.Source)
            || string.IsNullOrWhiteSpace(request.Language)
            || string.IsNullOrWhiteSpace(request.Provider))
        {
            return "Titel, URL, Quelle, Sprache und Provider werden benötigt.";
        }

        if (request.Episodes is null || request.Episodes.Count is < 1 or > MaxEpisodesPerRequest)
        {
            return $"Eine Anfrage muss zwischen 1 und {MaxEpisodesPerRequest} Episoden enthalten.";
        }

        if (!SafeHttpUrl(request.SeriesUrl)
            || request.Episodes.Any(url => !SafeHttpUrl(url)))
        {
            return "MediaForge-URLs müssen gültige HTTP- oder HTTPS-URLs sein.";
        }

        if (request.Episodes.Distinct(StringComparer.Ordinal).Count() != request.Episodes.Count)
        {
            return "Die Episodenliste enthält Duplikate.";
        }

        if (new[] { request.Title, request.Source, request.MediaType, request.SelectionLabel, request.Language, request.Provider }
            .Any(value => value.Any(char.IsControl)))
        {
            return "Die Anfrage enthält ungültige Steuerzeichen.";
        }

        return null;
    }

    private static bool SafeHttpUrl(string value)
        => MediaAccessGrantStore.TryNormalizeUrl(value, out _);

    private static void Normalize(CreateMediaRequest request)
    {
        request.Title = request.Title?.Trim() ?? string.Empty;
        request.SeriesUrl = request.SeriesUrl?.Trim() ?? string.Empty;
        request.Source = request.Source?.Trim().ToLowerInvariant() ?? string.Empty;
        request.MediaType = request.MediaType?.Trim().ToLowerInvariant() == "movie" ? "movie" : "series";
        request.SelectionLabel = request.SelectionLabel?.Trim() ?? string.Empty;
        request.Language = request.Language?.Trim() ?? string.Empty;
        request.Provider = request.Provider?.Trim() ?? string.Empty;
        request.Episodes = request.Episodes?.Select(url => url?.Trim() ?? string.Empty).ToList() ?? [];
    }

    private static string SafeIdentity(string value)
    {
        var clean = new string(value.Where(character => !char.IsControl(character)).Take(200).ToArray());
        return string.IsNullOrWhiteSpace(clean) ? "unknown" : clean;
    }

    private static IReadOnlyList<DiscoverItem> ReadDiscoverRow(
        JsonElement response,
        string rowName,
        IReadOnlyDictionary<string, string> allowedSources)
    {
        const int maxItemsPerRow = 18;
        var output = new List<DiscoverItem>();
        if (response.ValueKind != JsonValueKind.Object
            || !response.TryGetProperty("rows", out var rows)
            || rows.ValueKind != JsonValueKind.Object
            || !rows.TryGetProperty(rowName, out var row)
            || row.ValueKind != JsonValueKind.Array)
        {
            return output;
        }

        foreach (var item in row.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var source = ReadJsonString(item, "source", 80);
            var title = ReadJsonString(item, "title", 300);
            var rawUrl = ReadJsonString(item, "url", 2048);
            if (!allowedSources.TryGetValue(source, out var sourceLabel)
                || string.IsNullOrWhiteSpace(title)
                || !MediaAccessGrantStore.TryNormalizeUrl(rawUrl, out var normalizedUrl))
            {
                continue;
            }

            var posterUrl = ReadJsonString(item, "poster_url", 4096);
            if (!TryReadMediaForgeImageUrl(posterUrl, out _))
            {
                posterUrl = string.Empty;
            }

            var mediaType = ReadJsonString(item, "media_type", 20);
            output.Add(new DiscoverItem(
                SafeIdentity(title),
                normalizedUrl,
                source,
                sourceLabel,
                mediaType == "movies" ? "movie" : "series",
                posterUrl,
                ReadJsonString(item, "year", 16)));
            if (output.Count >= maxItemsPerRow)
            {
                break;
            }
        }

        return output;
    }

    private static string ReadJsonString(JsonElement item, string name, int maximum)
    {
        if (!item.TryGetProperty(name, out var value))
        {
            return string.Empty;
        }

        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            _ => string.Empty,
        };
        return text.Length <= maximum && !text.Any(char.IsControl) ? text.Trim() : string.Empty;
    }

    private static bool TryReadMediaForgeImageUrl(string value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 4096
            || !value.StartsWith("/api/img?", StringComparison.Ordinal))
        {
            return false;
        }

        var query = value[(value.IndexOf('?', StringComparison.Ordinal) + 1)..];
        string? rawUrl = null;
        var pairs = query.Split('&');
        if (pairs.Length != 1)
        {
            return false;
        }

        try
        {
            foreach (var pair in pairs)
            {
                var separator = pair.IndexOf('=', StringComparison.Ordinal);
                var name = Uri.UnescapeDataString(separator >= 0 ? pair[..separator] : pair);
                if (!string.Equals(name, "url", StringComparison.Ordinal) || rawUrl is not null)
                {
                    return false;
                }

                rawUrl = Uri.UnescapeDataString(separator >= 0 ? pair[(separator + 1)..] : string.Empty);
            }
        }
        catch (UriFormatException)
        {
            return false;
        }

        return rawUrl is not null && MediaAccessGrantStore.TryNormalizeUrl(rawUrl, out normalized);
    }

    private IActionResult MediaForgeError(MediaForgeException exception)
    {
        var status = (int)exception.StatusCode;
        if (status is < 400 or > 599)
        {
            status = StatusCodes.Status502BadGateway;
        }

        return StatusCode(status, new { error = exception.Message });
    }

    private IActionResult ApplicationError(Exception exception)
        => exception switch
        {
            MediaForgeException mediaForgeException => MediaForgeError(mediaForgeException),
            MediaForgeApplicationException applicationException => StatusCode(
                NormalizeStatus(applicationException.StatusCode),
                new { error = applicationException.Message }),
            _ => StatusCode(StatusCodes.Status502BadGateway, new { error = "MediaForge konnte die Anfrage nicht verarbeiten." }),
        };

    private IActionResult MapSubmitResult(SubmitMediaRequestResult result, string mediaType)
        => result.Disposition switch
        {
            SubmitDisposition.Stored => Accepted(result.Request),
            SubmitDisposition.Queued => Ok(result.Request),
            SubmitDisposition.QueueFailed => StatusCode(StatusCodes.Status502BadGateway, result.Request),
            SubmitDisposition.Duplicate => Conflict(new
            {
                error = "Diese fehlenden Inhalte wurden bereits angefragt.",
                request = result.Duplicate,
            }),
            SubmitDisposition.LimitReached => StatusCode(StatusCodes.Status429TooManyRequests, new
            {
                error = $"Du kannst höchstens {result.MaxPending} offene Anfragen gleichzeitig haben.",
            }),
            SubmitDisposition.StoreCapacityReached => StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                error = "Der Anfragespeicher ist voll. Bitte abgeschlossene Anfragen bereinigen oder den Administrator informieren.",
            }),
            SubmitDisposition.AlreadyAvailable => Conflict(new
            {
                error = string.Equals(mediaType, "movie", StringComparison.OrdinalIgnoreCase)
                    ? "Der Film ist bereits vollständig vorhanden."
                    : "Alle verfügbaren Staffeln und Episoden sind bereits vorhanden.",
                alreadyAvailable = true,
            }),
            _ => StatusCode(StatusCodes.Status502BadGateway),
        };

    private static int NormalizeStatus(HttpStatusCode statusCode)
    {
        var status = (int)statusCode;
        return status is >= 400 and <= 599 ? status : StatusCodes.Status502BadGateway;
    }

    internal static int ReadExpectedEpisodeCount(JsonElement season)
        => MediaForgeRequestApplicationService.ReadExpectedEpisodeCount(season);

    private sealed record DiscoverItem(
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("url")] string Url,
        [property: JsonPropertyName("source")] string Source,
        [property: JsonPropertyName("source_label")] string SourceLabel,
        [property: JsonPropertyName("media_type")] string MediaType,
        [property: JsonPropertyName("poster_url")] string PosterUrl,
        [property: JsonPropertyName("year")] string Year);

}
