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
    private readonly RequestStore _store;
    private readonly MediaAccessGrantStore _grants;
    private readonly UserRateLimiter _rateLimiter;
    private readonly SecretStore _secrets;
    private readonly JellyfinLibraryAvailabilityService _libraryAvailability;

    public MediaForgeRequestsController(
        MediaForgeClient mediaForge,
        RequestStore store,
        MediaAccessGrantStore grants,
        UserRateLimiter rateLimiter,
        SecretStore secrets,
        JellyfinLibraryAvailabilityService libraryAvailability)
    {
        _mediaForge = mediaForge;
        _store = store;
        _grants = grants;
        _rateLimiter = rateLimiter;
        _secrets = secrets;
        _libraryAvailability = libraryAvailability;
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
            var sourcesTask = _mediaForge.GetSourcesAsync(cancellationToken);
            var discoverTask = _mediaForge.GetDiscoverAsync(cancellationToken);
            await Task.WhenAll(sourcesTask, discoverTask).ConfigureAwait(false);

            var allowed = ReadAllowedSources(await sourcesTask.ConfigureAwait(false))
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
        if (!Allow(userId, "search", 12))
        {
            return RateLimitExceeded();
        }

        try
        {
            var sourcesResponse = await _mediaForge.GetSourcesAsync(cancellationToken).ConfigureAwait(false);
            var sources = ReadAllowedSources(sourcesResponse);
            if (string.Equals(source, "all", StringComparison.OrdinalIgnoreCase))
            {
                var maximum = Math.Clamp(Plugin.Instance?.Configuration.MaxSearchSources ?? 8, 1, MaxKnownSources);
                sources = sources.Take(maximum).ToList();
            }
            else
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

    [HttpPost("Requests/Plan")]
    [Consumes(MediaTypeNames.Application.Json)]
    public async Task<IActionResult> PlanRequest(
        [FromBody] AutomaticMediaRequest request,
        CancellationToken cancellationToken)
    {
        Normalize(request);
        var validationError = ValidateAutomaticRequest(request, requireOptions: false);
        if (validationError is not null)
        {
            return BadRequest(new { error = validationError });
        }

        var (userId, _) = CurrentUser();
        if (!Allow(userId, "plan", 12))
        {
            return RateLimitExceeded();
        }

        try
        {
            if (!await SourceIsAllowedAsync(request.Source, cancellationToken).ConfigureAwait(false))
            {
                return BadRequest(new { error = "Die angegebene MediaForge-Quelle ist nicht freigegeben oder deaktiviert." });
            }

            var plan = await BuildMissingPlanAsync(userId, request, cancellationToken).ConfigureAwait(false);
            return Ok(plan.ToResponse());
        }
        catch (MediaForgeException exception)
        {
            return MediaForgeError(exception);
        }
    }

    [HttpPost("Requests/Automatic")]
    [Consumes(MediaTypeNames.Application.Json)]
    public async Task<IActionResult> CreateAutomaticRequest(
        [FromBody] AutomaticMediaRequest request,
        CancellationToken cancellationToken)
    {
        Normalize(request);
        var validationError = ValidateAutomaticRequest(request, requireOptions: true);
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

        MissingMediaPlan plan;
        try
        {
            if (!await SourceIsAllowedAsync(request.Source, cancellationToken).ConfigureAwait(false))
            {
                return BadRequest(new { error = "Die angegebene MediaForge-Quelle ist nicht freigegeben oder deaktiviert." });
            }

            plan = await BuildMissingPlanAsync(userId, request, cancellationToken).ConfigureAwait(false);
        }
        catch (MediaForgeException exception)
        {
            return MediaForgeError(exception);
        }

        if (plan.MissingUrls.Count == 0)
        {
            return Conflict(new
            {
                error = plan.IsMovie
                    ? "Der Film ist bereits vollständig vorhanden."
                    : "Alle verfügbaren Staffeln und Episoden sind bereits vorhanden.",
                alreadyAvailable = true,
            });
        }

        var calculated = new CreateMediaRequest
        {
            Title = plan.Title,
            SeriesUrl = request.SeriesUrl,
            Source = request.Source,
            MediaType = plan.IsMovie ? "movie" : "series",
            SelectionLabel = plan.SelectionLabel,
            Episodes = plan.MissingUrls.ToList(),
            Language = request.Language,
            Provider = request.Provider,
            Upscale = request.Upscale,
        };
        var maxPending = Math.Clamp(config?.MaxPendingRequestsPerUser ?? 10, 1, 100);
        var addResult = await _store.TryAddAsync(
            userId,
            username,
            calculated,
            RequestStatuses.Pending,
            maxPending,
            cancellationToken).ConfigureAwait(false);
        if (addResult.Duplicate is not null)
        {
            return Conflict(new { error = "Diese fehlenden Inhalte wurden bereits angefragt.", request = addResult.Duplicate });
        }

        if (addResult.LimitReached)
        {
            return StatusCode(StatusCodes.Status429TooManyRequests, new
            {
                error = $"Du kannst höchstens {maxPending} offene Anfragen gleichzeitig haben.",
            });
        }

        if (addResult.StoreCapacityReached)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                error = "Der Anfragespeicher ist voll. Bitte abgeschlossene Anfragen bereinigen oder den Administrator informieren.",
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

        MissingMediaPlan plan;
        try
        {
            plan = await BuildMissingPlanAsync(
                userId,
                new AutomaticMediaRequest
                {
                    Title = request.Title,
                    SeriesUrl = request.SeriesUrl,
                    Source = request.Source,
                    MediaType = request.MediaType,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (MediaForgeException exception)
        {
            return MediaForgeError(exception);
        }

        if (plan.MissingUrls.Count == 0)
        {
            return Conflict(new
            {
                error = plan.IsMovie
                    ? "Der Film ist bereits vollständig vorhanden."
                    : "Alle verfügbaren Staffeln und Episoden sind bereits vorhanden.",
                alreadyAvailable = true,
            });
        }

        request.Title = plan.Title;
        request.MediaType = plan.IsMovie ? "movie" : "series";
        request.SelectionLabel = plan.SelectionLabel;
        request.Episodes = plan.MissingUrls.ToList();

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

        if (addResult.StoreCapacityReached)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                error = "Der Anfragespeicher ist voll. Bitte abgeschlossene Anfragen bereinigen oder den Administrator informieren.",
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
        var result = await QueueRequestAsync(
            id,
            admin,
            cancellationToken,
            refreshAvailability: true).ConfigureAwait(false);
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

    private async Task<MediaRequest> QueueRequestAsync(
        long id,
        string decidedBy,
        CancellationToken cancellationToken,
        bool refreshAvailability = false)
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

            if (refreshAvailability)
            {
                var refreshedPlan = await BuildMissingPlanAsync(
                    request.UserId,
                    new AutomaticMediaRequest
                    {
                        Title = request.Title,
                        SeriesUrl = request.SeriesUrl,
                        Source = request.Source,
                        MediaType = request.MediaType,
                    },
                    cancellationToken,
                    requireGrant: false).ConfigureAwait(false);
                if (refreshedPlan.MissingUrls.Count == 0)
                {
                    await _store.MarkAvailableAsync(id, decidedBy, CancellationToken.None).ConfigureAwait(false);
                    return await _store.GetAsync(id, CancellationToken.None).ConfigureAwait(false) ?? request;
                }

                if (!await _store.TryUpdateProcessingPlanAsync(
                        id,
                        refreshedPlan.Title,
                        refreshedPlan.IsMovie ? "movie" : "series",
                        refreshedPlan.SelectionLabel,
                        refreshedPlan.MissingUrls,
                        CancellationToken.None).ConfigureAwait(false))
                {
                    return await _store.GetAsync(id, CancellationToken.None).ConfigureAwait(false) ?? request;
                }

                request = await _store.GetAsync(id, CancellationToken.None).ConfigureAwait(false) ?? request;
            }

            var queueId = await _mediaForge.QueueAsync(request, cancellationToken).ConfigureAwait(false);
            await _store.MarkQueuedAsync(id, queueId, decidedBy, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is MediaForgeException or HttpRequestException or OperationCanceledException)
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

    private static void Normalize(AutomaticMediaRequest request)
    {
        request.Title = request.Title?.Trim() ?? string.Empty;
        request.SeriesUrl = request.SeriesUrl?.Trim() ?? string.Empty;
        request.Source = request.Source?.Trim().ToLowerInvariant() ?? string.Empty;
        request.MediaType = request.MediaType?.Trim().ToLowerInvariant() == "movie" ? "movie" : "series";
        request.Language = request.Language?.Trim() ?? string.Empty;
        request.Provider = request.Provider?.Trim() ?? string.Empty;
    }

    private static string? ValidateAutomaticRequest(AutomaticMediaRequest request, bool requireOptions)
    {
        if (string.IsNullOrWhiteSpace(request.Title)
            || string.IsNullOrWhiteSpace(request.SeriesUrl)
            || string.IsNullOrWhiteSpace(request.Source)
            || (requireOptions && (string.IsNullOrWhiteSpace(request.Language) || string.IsNullOrWhiteSpace(request.Provider))))
        {
            return requireOptions
                ? "Titel, URL, Quelle, Sprache und Provider werden benötigt."
                : "Titel, URL und Quelle werden benötigt.";
        }

        if (request.Title.Length > 300
            || request.SeriesUrl.Length > 2048
            || request.Source.Length > 80
            || request.MediaType.Length > 20
            || request.Language.Length > 100
            || request.Provider.Length > 100
            || !SafeHttpUrl(request.SeriesUrl))
        {
            return "Die Anfrage enthält ungültige oder zu lange Werte.";
        }

        if (new[] { request.Title, request.Source, request.MediaType, request.Language, request.Provider }
            .Any(value => value.Any(char.IsControl)))
        {
            return "Die Anfrage enthält ungültige Steuerzeichen.";
        }

        return null;
    }

    private async Task<bool> SourceIsAllowedAsync(string source, CancellationToken cancellationToken)
    {
        var response = await _mediaForge.GetSourcesAsync(cancellationToken).ConfigureAwait(false);
        return ReadAllowedSources(response)
            .Any(item => string.Equals(item.Id, source, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<MissingMediaPlan> BuildMissingPlanAsync(
        string userId,
        AutomaticMediaRequest request,
        CancellationToken cancellationToken,
        bool requireGrant = true)
    {
        if (requireGrant
            && (!_grants.IsGranted(userId, request.SeriesUrl, out var grantedSource)
                || !string.Equals(grantedSource, request.Source, StringComparison.OrdinalIgnoreCase)))
        {
            throw new MediaForgeException(
                HttpStatusCode.BadRequest,
                "Der Titel ist nicht mehr durch deine aktuelle Suche freigegeben. Bitte die Suche neu öffnen.");
        }

        var detailTask = _mediaForge.GetSeriesAsync(request.SeriesUrl, cancellationToken);
        var seasonsTask = _mediaForge.GetSeasonsAsync(request.SeriesUrl, cancellationToken);
        await Task.WhenAll(detailTask, seasonsTask).ConfigureAwait(false);
        var detail = await detailTask.ConfigureAwait(false);
        var seasonsResponse = await seasonsTask.ConfigureAwait(false);
        _grants.GrantFromJson(userId, request.Source, seasonsResponse);

        var title = ReadJsonString(detail, "title", 300);
        if (string.IsNullOrWhiteSpace(title))
        {
            title = request.Title;
        }

        var description = ReadJsonString(detail, "description", 4000);
        var isMovie = detail.TryGetProperty("is_movie", out var movieValue)
            ? movieValue.ValueKind == JsonValueKind.True
            : request.MediaType == "movie";
        var libraryState = _libraryAvailability.GetAvailability(new LibraryMediaIdentity(
            title,
            ReadReleaseYear(detail),
            isMovie,
            ReadProviderIds(detail)));
        if (seasonsResponse.ValueKind != JsonValueKind.Object
            || !seasonsResponse.TryGetProperty("seasons", out var seasons)
            || seasons.ValueKind != JsonValueKind.Array)
        {
            throw new MediaForgeException(HttpStatusCode.BadGateway, "MediaForge hat keine gültige Staffelliste geliefert.");
        }

        if (seasons.GetArrayLength() > 100)
        {
            throw new MediaForgeException(HttpStatusCode.BadRequest, "Der Titel enthält mehr als 100 Staffeln und kann nicht sicher automatisch geplant werden.");
        }

        var seasonItems = seasons.EnumerateArray().ToArray();
        if (seasonItems.Length == 0)
        {
            throw new MediaForgeException(HttpStatusCode.NotFound, "Für diesen Titel wurden keine verfügbaren Inhalte gefunden.");
        }

        var missing = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        HashSet<string>? languages = null;
        var total = 0;
        foreach (var season in seasonItems)
        {
            var seasonUrl = ReadJsonString(season, "url", 2048);
            var seasonNumber = ReadOptionalInt(season, "season_number");
            if (!MediaAccessGrantStore.TryNormalizeUrl(seasonUrl, out var normalizedSeasonUrl))
            {
                continue;
            }

            _grants.GrantUrl(userId, request.Source, normalizedSeasonUrl);
            var episodesResponse = await _mediaForge.GetEpisodesAsync(normalizedSeasonUrl, cancellationToken).ConfigureAwait(false);
            _grants.GrantFromJson(userId, request.Source, episodesResponse);
            if (episodesResponse.ValueKind != JsonValueKind.Object
                || !episodesResponse.TryGetProperty("episodes", out var episodes)
                || episodes.ValueKind != JsonValueKind.Array)
            {
                throw new MediaForgeException(HttpStatusCode.BadGateway, "MediaForge hat keine gültige Episodenliste geliefert.");
            }

            foreach (var episode in episodes.EnumerateArray())
            {
                var episodeUrl = ReadJsonString(episode, "url", 2048);
                if (!MediaAccessGrantStore.TryNormalizeUrl(episodeUrl, out var normalizedEpisodeUrl)
                    || !seen.Add(normalizedEpisodeUrl))
                {
                    continue;
                }

                total++;
                var episodeNumber = ReadOptionalInt(episode, "episode_number");
                var episodeSeason = ReadOptionalInt(episode, "season_number") ?? seasonNumber;
                var alreadyAvailable = isMovie
                    ? libraryState.ItemExists
                    : episodeSeason.HasValue
                        && episodeNumber.HasValue
                        && libraryState.Episodes.Contains(new LibraryEpisodeKey(
                            episodeSeason.Value,
                            episodeNumber.Value));
                if (!alreadyAvailable)
                {
                    var episodeLanguages = new HashSet<string>(StringComparer.Ordinal);
                    if (episode.TryGetProperty("languages", out var languageValues)
                        && languageValues.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var languageValue in languageValues.EnumerateArray().Take(32))
                        {
                            if (languageValue.ValueKind == JsonValueKind.String)
                            {
                                var language = languageValue.GetString()?.Trim() ?? string.Empty;
                                if (language.Length is > 0 and <= 100 && !language.Any(char.IsControl))
                                {
                                    episodeLanguages.Add(language);
                                }
                            }
                        }
                    }

                    if (episodeLanguages.Count > 0)
                    {
                        if (languages is null)
                        {
                            languages = episodeLanguages;
                        }
                        else
                        {
                            languages.IntersectWith(episodeLanguages);
                        }
                    }

                    missing.Add(normalizedEpisodeUrl);
                    if (missing.Count > MaxEpisodesPerRequest)
                    {
                        throw new MediaForgeException(
                            HttpStatusCode.BadRequest,
                            $"Es fehlen mehr als {MaxEpisodesPerRequest} Episoden. Bitte den Titel in MediaForge in mehreren Schritten einreihen.");
                    }
                }
            }
        }

        if (total == 0)
        {
            throw new MediaForgeException(HttpStatusCode.NotFound, "Für diesen Titel wurden keine verfügbaren Episoden gefunden.");
        }

        var selectionLabel = isMovie
            ? "Film"
            : missing.Count == 1 ? "1 fehlende Episode" : $"{missing.Count} fehlende Episoden";
        var providers = missing.Count == 0
            ? new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            : await ReadProviderOptionsAsync(userId, request.Source, missing[0], cancellationToken).ConfigureAwait(false);
        return new MissingMediaPlan(
            title,
            description,
            isMovie,
            total,
            missing,
            selectionLabel,
            languages is null ? Array.Empty<string>() : languages.Order(StringComparer.Ordinal).ToArray(),
            providers);
    }

    private async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> ReadProviderOptionsAsync(
        string userId,
        string source,
        string episodeUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await _mediaForge.GetProvidersAsync(episodeUrl, cancellationToken).ConfigureAwait(false);
            _grants.GrantFromJson(userId, source, response);
            var output = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            if (response.ValueKind != JsonValueKind.Object
                || !response.TryGetProperty("providers", out var matrix)
                || matrix.ValueKind != JsonValueKind.Object)
            {
                return output;
            }

            foreach (var property in matrix.EnumerateObject().Take(32))
            {
                if (property.Name.Length is < 1 or > 100
                    || property.Name.Any(char.IsControl)
                    || property.Value.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var values = property.Value.EnumerateArray()
                    .Where(value => value.ValueKind == JsonValueKind.String)
                    .Select(value => value.GetString()?.Trim() ?? string.Empty)
                    .Where(value => value.Length is > 0 and <= 100 && !value.Any(char.IsControl))
                    .Distinct(StringComparer.Ordinal)
                    .Take(32)
                    .ToArray();
                if (values.Length > 0)
                {
                    output[property.Name] = values;
                }
            }

            return output;
        }
        catch (MediaForgeException)
        {
            return new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        }
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
        var output = new List<SourceInfo>();
        var seenSourceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (response.ValueKind != JsonValueKind.Object
            || !response.TryGetProperty("sources", out var sources)
            || sources.ValueKind != JsonValueKind.Array)
        {
            return output;
        }

        foreach (var item in sources.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var id = item.TryGetProperty("id", out var idValue) && idValue.ValueKind == JsonValueKind.String
                ? idValue.GetString() ?? string.Empty
                : string.Empty;
            var label = item.TryGetProperty("label", out var labelValue) && labelValue.ValueKind == JsonValueKind.String
                ? labelValue.GetString() ?? id
                : id;
            var hasAdultFlag = item.TryGetProperty("adult", out var adultValue)
                && adultValue.ValueKind is JsonValueKind.True or JsonValueKind.False;
            var hasEnabledFlag = item.TryGetProperty("enabled", out var enabledValue);
            var hasValidEnabledFlag = !hasEnabledFlag
                || enabledValue.ValueKind is JsonValueKind.True or JsonValueKind.False;
            var enabled = hasValidEnabledFlag && (!hasEnabledFlag || enabledValue.ValueKind == JsonValueKind.True);
            var adult = hasAdultFlag && adultValue.ValueKind == JsonValueKind.True;
            if (!hasAdultFlag
                || !hasValidEnabledFlag
                || string.IsNullOrWhiteSpace(id)
                || id.Length > 80
                || id.Any(char.IsControl)
                || !enabled
                || adult
                || (allowlist.Count > 0 && !allowlist.Contains(id))
                || !seenSourceIds.Add(id))
            {
                continue;
            }

            output.Add(new SourceInfo(id, SafeIdentity(label), adult));
            if (output.Count >= MaxKnownSources)
            {
                break;
            }
        }

        return output;
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

    private static int? ReadOptionalInt(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var numeric))
        {
            return numeric is >= 0 and <= 100000 ? numeric : null;
        }

        if (value.ValueKind == JsonValueKind.String
            && int.TryParse(value.GetString(), out numeric))
        {
            return numeric is >= 0 and <= 100000 ? numeric : null;
        }

        return null;
    }

    private static int? ReadReleaseYear(JsonElement detail)
    {
        foreach (var field in new[] { "release_year", "year" })
        {
            var value = ReadJsonString(detail, field, 32);
            if (value.Length >= 4
                && int.TryParse(value.AsSpan(0, 4), out var year)
                && year is >= 1800 and <= 3000)
            {
                return year;
            }
        }

        return null;
    }

    private static IReadOnlyDictionary<string, string> ReadProviderIds(JsonElement detail)
    {
        var output = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddProviderId(output, "Imdb", ReadJsonString(detail, "imdb_id", 100));
        AddProviderId(output, "Tmdb", ReadJsonString(detail, "tmdb_id", 100));
        AddProviderId(output, "Tvdb", ReadJsonString(detail, "tvdb_id", 100));
        ReadNestedProviderIds(detail, "provider_ids", output);
        ReadNestedProviderIds(detail, "external_ids", output);
        return output;
    }

    private static void ReadNestedProviderIds(
        JsonElement detail,
        string field,
        IDictionary<string, string> output)
    {
        if (!detail.TryGetProperty(field, out var ids) || ids.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var pair in new[]
        {
            (Json: "imdb_id", Jellyfin: "Imdb"),
            (Json: "tmdb_id", Jellyfin: "Tmdb"),
            (Json: "tvdb_id", Jellyfin: "Tvdb"),
            (Json: "Imdb", Jellyfin: "Imdb"),
            (Json: "Tmdb", Jellyfin: "Tmdb"),
            (Json: "Tvdb", Jellyfin: "Tvdb"),
        })
        {
            AddProviderId(output, pair.Jellyfin, ReadJsonString(ids, pair.Json, 100));
        }
    }

    private static void AddProviderId(IDictionary<string, string> output, string provider, string value)
    {
        if (!string.IsNullOrWhiteSpace(value)
            && value.Length <= 100
            && !value.Any(char.IsControl))
        {
            output.TryAdd(provider, value.Trim());
        }
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
                && phaseValue.GetString() is "download" or "ffmpeg"
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

    private sealed record SourceInfo(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("label")] string Label,
        [property: JsonPropertyName("adult")] bool Adult);

    private sealed record SearchGroup(
        [property: JsonPropertyName("source")] string Source,
        [property: JsonPropertyName("label")] string Label,
        [property: JsonPropertyName("data")] JsonElement? Data,
        [property: JsonPropertyName("error")] string? Error);

    private sealed record DiscoverItem(
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("url")] string Url,
        [property: JsonPropertyName("source")] string Source,
        [property: JsonPropertyName("source_label")] string SourceLabel,
        [property: JsonPropertyName("media_type")] string MediaType,
        [property: JsonPropertyName("poster_url")] string PosterUrl,
        [property: JsonPropertyName("year")] string Year);

    private sealed record ProgressInfo(
        [property: JsonPropertyName("queue_id")] long QueueId,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("current_episode")] int CurrentEpisode,
        [property: JsonPropertyName("total_episodes")] int TotalEpisodes,
        [property: JsonPropertyName("percent")] double Percent,
        [property: JsonPropertyName("phase")] string Phase);

    private sealed record MissingMediaPlan(
        string Title,
        string Description,
        bool IsMovie,
        int TotalCount,
        IReadOnlyList<string> MissingUrls,
        string SelectionLabel,
        IReadOnlyList<string> Languages,
        IReadOnlyDictionary<string, IReadOnlyList<string>> Providers)
    {
        public MissingPlanResponse ToResponse()
            => new(
                Title,
                Description,
                IsMovie,
                TotalCount,
                TotalCount - MissingUrls.Count,
                MissingUrls.Count,
                SelectionLabel,
                Languages,
                Providers);
    }

    private sealed record MissingPlanResponse(
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("description")] string Description,
        [property: JsonPropertyName("is_movie")] bool IsMovie,
        [property: JsonPropertyName("total_count")] int TotalCount,
        [property: JsonPropertyName("existing_count")] int ExistingCount,
        [property: JsonPropertyName("missing_count")] int MissingCount,
        [property: JsonPropertyName("selection_label")] string SelectionLabel,
        [property: JsonPropertyName("languages")] IReadOnlyList<string> Languages,
        [property: JsonPropertyName("providers")] IReadOnlyDictionary<string, IReadOnlyList<string>> Providers);
}
