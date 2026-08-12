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
    private static readonly TimeSpan RateWindow = TimeSpan.FromMinutes(1);
    private readonly MediaForgeClient _mediaForge;
    private readonly RequestStore _store;
    private readonly MediaAccessGrantStore _grants;
    private readonly UserRateLimiter _rateLimiter;
    private readonly SecretStore _secrets;

    public MediaForgeRequestsController(
        MediaForgeClient mediaForge,
        RequestStore store,
        MediaAccessGrantStore grants,
        UserRateLimiter rateLimiter,
        SecretStore secrets)
    {
        _mediaForge = mediaForge;
        _store = store;
        _grants = grants;
        _rateLimiter = rateLimiter;
        _secrets = secrets;
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
            var response = await _mediaForge.GetSourcesAsync(cancellationToken).ConfigureAwait(false);
            return Ok(FilterSources(response));
        }
        catch (MediaForgeException exception)
        {
            return MediaForgeError(exception);
        }
    }

    [HttpGet("Search")]
    public async Task<IActionResult> Search(
        [Required, MinLength(2), MaxLength(120)] string query,
        string source = "all",
        CancellationToken cancellationToken = default)
    {
        var (userId, _) = CurrentUser();
        if (!Allow(userId, "search", 12))
        {
            return RateLimitExceeded();
        }

        try
        {
            var sourcesResponse = await _mediaForge.GetSourcesAsync(cancellationToken).ConfigureAwait(false);
            var sources = ReadAllowedSources(sourcesResponse);
            if (!string.Equals(source, "all", StringComparison.OrdinalIgnoreCase))
            {
                sources = sources.Where(item => string.Equals(item.Id, source, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            if (sources.Count == 0)
            {
                return BadRequest(new { error = "Die ausgewählte MediaForge-Quelle ist nicht freigegeben oder deaktiviert." });
            }

            var groups = await Task.WhenAll(sources.Select(async item =>
            {
                try
                {
                    var result = await _mediaForge.SearchAsync(query.Trim(), item.Id, cancellationToken).ConfigureAwait(false);
                    _grants.GrantFromJson(userId, item.Id, result);
                    return new SearchGroup(item.Id, item.Label, result, null);
                }
                catch (MediaForgeException exception)
                {
                    return new SearchGroup(item.Id, item.Label, null, exception.Message);
                }
            })).ConfigureAwait(false);
            return Ok(new { groups });
        }
        catch (MediaForgeException exception)
        {
            return MediaForgeError(exception);
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

    [HttpGet("Requests/Mine")]
    public async Task<IActionResult> GetMyRequests(CancellationToken cancellationToken)
    {
        var (userId, _) = CurrentUser();
        return Ok(await _store.ListForUserAsync(userId, 200, cancellationToken).ConfigureAwait(false));
    }

    [HttpGet("Requests/Progress")]
    public async Task<IActionResult> GetMyProgress(CancellationToken cancellationToken)
    {
        var (userId, _) = CurrentUser();
        if (!Allow(userId, "progress", 30))
        {
            return RateLimitExceeded();
        }

        var requests = await _store.ListForUserAsync(userId, 200, cancellationToken).ConfigureAwait(false);
        var queueIds = requests
            .Where(item => item.Status == RequestStatuses.Queued && item.MediaForgeQueueId.HasValue)
            .Select(item => item.MediaForgeQueueId!.Value)
            .Distinct()
            .Take(200)
            .ToArray();
        if (queueIds.Length == 0)
        {
            return Ok(new { items = Array.Empty<object>() });
        }

        try
        {
            var upstream = await _mediaForge.GetProgressAsync(queueIds, cancellationToken).ConfigureAwait(false);
            var progress = ReadProgress(upstream, queueIds);
            await _store.SyncQueueStatesAsync(
                userId,
                progress.ToDictionary(item => item.QueueId, item => item.Status),
                cancellationToken).ConfigureAwait(false);
            return Ok(new { items = progress });
        }
        catch (MediaForgeException exception)
        {
            return MediaForgeError(exception);
        }
    }

    [HttpDelete("Requests/{id:long}")]
    public async Task<IActionResult> WithdrawRequest(long id, CancellationToken cancellationToken)
    {
        var (userId, username) = CurrentUser();
        var result = await _store.TryWithdrawAsync(id, userId, username, cancellationToken).ConfigureAwait(false);
        return result switch
        {
            WithdrawRequestResult.NotFound => NotFound(new { error = "Anfrage nicht gefunden." }),
            WithdrawRequestResult.NotPending => Conflict(new
            {
                error = "Nur noch nicht freigegebene Anfragen können zurückgezogen werden.",
            }),
            _ => Ok(await _store.GetAsync(id, cancellationToken).ConfigureAwait(false)),
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

        var config = Plugin.Instance?.Configuration;
        if (config?.MaintenanceMode == true)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                error = string.IsNullOrWhiteSpace(config.MaintenanceMessage)
                    ? "Anfragen sind derzeit deaktiviert."
                    : config.MaintenanceMessage,
            });
        }

        var (userId, username) = CurrentUser();
        if (!Allow(userId, "request", 10))
        {
            return RateLimitExceeded();
        }

        try
        {
            var sourcesResponse = await _mediaForge.GetSourcesAsync(cancellationToken).ConfigureAwait(false);
            var sourceAllowed = ReadAllowedSources(sourcesResponse)
                .Any(item => string.Equals(item.Id, request.Source, StringComparison.OrdinalIgnoreCase));
            if (!sourceAllowed)
            {
                return BadRequest(new { error = "Die angegebene MediaForge-Quelle ist nicht freigegeben oder deaktiviert." });
            }
        }
        catch (MediaForgeException exception)
        {
            return MediaForgeError(exception);
        }

        if (!_grants.AreGranted(userId, request.Source, request.Episodes.Prepend(request.SeriesUrl)))
        {
            return BadRequest(new
            {
                error = "Die Medienauswahl ist nicht mehr gültig. Bitte die Suche erneut öffnen und die Auswahl neu laden.",
            });
        }

        var maxPending = Math.Clamp(config?.MaxPendingRequestsPerUser ?? 10, 1, 100);
        var addResult = await _store.TryAddAsync(
            userId,
            username,
            request,
            RequestStatuses.Pending,
            maxPending,
            cancellationToken).ConfigureAwait(false);
        if (addResult.Duplicate is not null)
        {
            return Conflict(new { error = "Diese Auswahl wurde bereits angefragt.", request = addResult.Duplicate });
        }

        if (addResult.LimitReached)
        {
            return StatusCode(StatusCodes.Status429TooManyRequests, new
            {
                error = $"Du kannst höchstens {maxPending} offene Anfragen gleichzeitig haben.",
            });
        }

        var stored = addResult.Request
            ?? throw new InvalidOperationException("Request store returned no result.");
        if (config?.AutoApproveRequests != true)
        {
            return Accepted(stored);
        }

        var queued = await QueueRequestAsync(stored.Id, "automatic", cancellationToken).ConfigureAwait(false);
        return queued.Status == RequestStatuses.Queued
            ? Ok(queued)
            : StatusCode(StatusCodes.Status502BadGateway, queued);
    }

    [HttpGet("Admin/Requests")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<IActionResult> GetAllRequests(CancellationToken cancellationToken)
        => Ok(await _store.ListAllAsync(500, cancellationToken).ConfigureAwait(false));

    [HttpPost("Admin/Requests/{id:long}/Approve")]
    [Consumes(MediaTypeNames.Application.Json)]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<IActionResult> Approve(long id, CancellationToken cancellationToken)
    {
        var existing = await _store.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return NotFound(new { error = "Anfrage nicht gefunden." });
        }

        var (_, admin) = CurrentUser();
        var result = await QueueRequestAsync(id, admin, cancellationToken).ConfigureAwait(false);
        if (result.Status == RequestStatuses.Queued)
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
        var item = await _store.GetAsync(id, cancellationToken).ConfigureAwait(false);
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
        var rejected = await _store.TryRejectAsync(id, reason, admin, cancellationToken).ConfigureAwait(false);
        if (!rejected)
        {
            return Conflict(new { error = "Diese Anfrage kann in ihrem aktuellen Status nicht abgelehnt werden." });
        }

        return Ok(await _store.GetAsync(id, cancellationToken).ConfigureAwait(false));
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

    private async Task<MediaRequest> QueueRequestAsync(long id, string decidedBy, CancellationToken cancellationToken)
    {
        if (!await _store.TryClaimAsync(id, cancellationToken).ConfigureAwait(false))
        {
            return await _store.GetAsync(id, cancellationToken).ConfigureAwait(false)
                ?? new MediaRequest { Id = id, Status = RequestStatuses.Failed, Error = "Anfrage nicht gefunden." };
        }

        var request = await _store.GetAsync(id, CancellationToken.None).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Claimed request disappeared.");
        try
        {
            var sourcesResponse = await _mediaForge.GetSourcesAsync(cancellationToken).ConfigureAwait(false);
            if (!ReadAllowedSources(sourcesResponse)
                .Any(item => string.Equals(item.Id, request.Source, StringComparison.OrdinalIgnoreCase)))
            {
                throw new MediaForgeException(HttpStatusCode.BadRequest, "Die Quelle dieser Anfrage ist nicht mehr freigegeben.");
            }

            var queueId = await _mediaForge.QueueAsync(request, cancellationToken).ConfigureAwait(false);
            await _store.MarkQueuedAsync(id, queueId, decidedBy, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is MediaForgeException or HttpRequestException or TaskCanceledException)
        {
            var error = exception is MediaForgeException mediaForgeException
                ? mediaForgeException.Message
                : "Die Übergabe an MediaForge wurde unterbrochen.";
            await _store.MarkFailedAsync(id, error, decidedBy, CancellationToken.None).ConfigureAwait(false);
        }

        return await _store.GetAsync(id, CancellationToken.None).ConfigureAwait(false) ?? request;
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

    private static object FilterSources(JsonElement response) => new { sources = ReadAllowedSources(response) };

    private static List<SourceInfo> ReadAllowedSources(JsonElement response)
    {
        var config = Plugin.Instance?.Configuration;
        var allowlist = (config?.AllowedSources ?? string.Empty)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var max = Math.Clamp(config?.MaxSearchSources ?? 8, 1, 32);
        var output = new List<SourceInfo>();
        if (response.ValueKind != JsonValueKind.Object
            || !response.TryGetProperty("sources", out var sources)
            || sources.ValueKind != JsonValueKind.Array)
        {
            return output;
        }

        foreach (var item in sources.EnumerateArray())
        {
            var id = item.TryGetProperty("id", out var idValue) ? idValue.GetString() ?? string.Empty : string.Empty;
            var label = item.TryGetProperty("label", out var labelValue) ? labelValue.GetString() ?? id : id;
            var enabled = !item.TryGetProperty("enabled", out var enabledValue) || enabledValue.ValueKind == JsonValueKind.True;
            var adult = item.TryGetProperty("adult", out var adultValue) && adultValue.ValueKind == JsonValueKind.True;
            if (string.IsNullOrWhiteSpace(id)
                || id.Length > 80
                || id.Any(char.IsControl)
                || !enabled
                || (adult && config?.AllowAdultSources != true)
                || (allowlist.Count > 0 && !allowlist.Contains(id)))
            {
                continue;
            }

            output.Add(new SourceInfo(id, SafeIdentity(label), adult));
            if (output.Count >= max)
            {
                break;
            }
        }

        return output;
    }

    private static IReadOnlyList<ProgressInfo> ReadProgress(JsonElement response, IReadOnlyCollection<long> requestedIds)
    {
        var output = new List<ProgressInfo>();
        var allowed = requestedIds.ToHashSet();
        var seen = new HashSet<long>();
        if (response.ValueKind != JsonValueKind.Object
            || !response.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Array)
        {
            return output;
        }

        foreach (var item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("queue_id", out var queueIdValue)
                || queueIdValue.ValueKind != JsonValueKind.Number
                || !queueIdValue.TryGetInt64(out var queueId)
                || !allowed.Contains(queueId)
                || !seen.Add(queueId)
                || !item.TryGetProperty("status", out var statusValue)
                || statusValue.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var status = statusValue.GetString();
            if (status is not ("queued" or "running" or RequestStatuses.Completed
                or RequestStatuses.Partial or RequestStatuses.Failed or RequestStatuses.Cancelled))
            {
                continue;
            }

            var total = ReadBoundedInt(item, "total_episodes", 0, MaxEpisodesPerRequest);
            var current = ReadBoundedInt(item, "current_episode", 0, total > 0 ? total : MaxEpisodesPerRequest);
            var percent = ReadBoundedDouble(item, "percent", 0, 100);
            var phase = item.TryGetProperty("phase", out var phaseValue)
                && phaseValue.ValueKind == JsonValueKind.String
                && phaseValue.GetString() is "download" or "ffmpeg" or "upscaling" or "move"
                    ? phaseValue.GetString()!
                    : "download";
            output.Add(new ProgressInfo(queueId, status, current, total, percent, phase));
        }

        return output;
    }

    private static int ReadBoundedInt(JsonElement item, string name, int minimum, int maximum)
        => item.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var parsed)
                ? Math.Clamp(parsed, minimum, maximum)
                : minimum;

    private static double ReadBoundedDouble(JsonElement item, string name, double minimum, double maximum)
        => item.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out var parsed)
                ? Math.Clamp(parsed, minimum, maximum)
                : minimum;

    private IActionResult MediaForgeError(MediaForgeException exception)
    {
        var status = (int)exception.StatusCode;
        if (status is < 400 or > 599)
        {
            status = StatusCodes.Status502BadGateway;
        }

        return StatusCode(status, new { error = exception.Message });
    }

    private sealed record SourceInfo(string Id, string Label, bool Adult);

    private sealed record SearchGroup(string Source, string Label, JsonElement? Data, string? Error);

    private sealed record ProgressInfo(
        [property: JsonPropertyName("queue_id")] long QueueId,
        string Status,
        [property: JsonPropertyName("current_episode")] int CurrentEpisode,
        [property: JsonPropertyName("total_episodes")] int TotalEpisodes,
        double Percent,
        string Phase);
}
