using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.MediaForge.Configuration;
using Jellyfin.Plugin.MediaForge.Models;

namespace Jellyfin.Plugin.MediaForge.Services;

/// <summary>
/// Shared request application layer used by both the Jellyfin HTTP API and the
/// optional in-process Jellix bridge.
/// </summary>
public sealed class MediaForgeRequestApplicationService
{
    public const int MaxEpisodesPerRequest = 500;
    public const int MaxKnownSources = 32;
    private static readonly TimeSpan RateWindow = TimeSpan.FromMinutes(1);
    private readonly MediaForgeClient _mediaForge;
    private readonly RequestStore _store;
    private readonly MediaAccessGrantStore _grants;
    private readonly UserRateLimiter _rateLimiter;
    private readonly JellyfinLibraryAvailabilityService _libraryAvailability;
    private readonly JellixSelectionTokenStore _selectionTokens;
    private readonly Func<PluginConfiguration> _configuration;

    public MediaForgeRequestApplicationService(
        MediaForgeClient mediaForge,
        RequestStore store,
        MediaAccessGrantStore grants,
        UserRateLimiter rateLimiter,
        JellyfinLibraryAvailabilityService libraryAvailability,
        JellixSelectionTokenStore selectionTokens)
        : this(
            mediaForge,
            store,
            grants,
            rateLimiter,
            libraryAvailability,
            selectionTokens,
            () => Plugin.Instance?.Configuration ?? new PluginConfiguration())
    {
    }

    internal MediaForgeRequestApplicationService(
        MediaForgeClient mediaForge,
        RequestStore store,
        MediaAccessGrantStore grants,
        UserRateLimiter rateLimiter,
        JellyfinLibraryAvailabilityService libraryAvailability,
        JellixSelectionTokenStore selectionTokens,
        Func<PluginConfiguration> configuration)
    {
        _mediaForge = mediaForge;
        _store = store;
        _grants = grants;
        _rateLimiter = rateLimiter;
        _libraryAvailability = libraryAvailability;
        _selectionTokens = selectionTokens;
        _configuration = configuration;
    }

    public async Task<IReadOnlyList<MediaForgeSourceInfo>> GetAllowedSourcesAsync(
        string userId,
        CancellationToken cancellationToken,
        bool applyRateLimit = true)
    {
        if (applyRateLimit && !Allow(userId, "catalog", 120))
        {
            throw TooManyRequests();
        }

        return ReadAllowedSources(
            await _mediaForge.GetSourcesAsync(cancellationToken).ConfigureAwait(false),
            CurrentConfiguration);
    }

    public async Task<MediaForgeSearchResponse> SearchAsync(
        string userId,
        string query,
        string source,
        string? mediaType,
        bool issueSelectionTokens,
        CancellationToken cancellationToken)
    {
        query = query?.Trim() ?? string.Empty;
        source = source?.Trim().ToLowerInvariant() ?? string.Empty;
        mediaType = NormalizeMediaType(mediaType);
        if (query.Length is < 2 or > 120 || query.Any(char.IsControl))
        {
            throw new MediaForgeApplicationException(HttpStatusCode.BadRequest, "Der Suchbegriff ist ungültig.");
        }

        if (issueSelectionTokens && mediaType is null)
        {
            throw new MediaForgeApplicationException(HttpStatusCode.BadRequest, "Der Medientyp muss Film oder Serie sein.");
        }

        var allSources = string.Equals(source, "all", StringComparison.OrdinalIgnoreCase);
        if (!Allow(userId, allSources ? "search" : "search-source", allSources ? 12 : 120))
        {
            throw TooManyRequests();
        }

        var sources = ReadAllowedSources(
            await _mediaForge.GetSourcesAsync(cancellationToken).ConfigureAwait(false),
            CurrentConfiguration);
        if (mediaType is not null)
        {
            sources = sources.Where(item => item.MediaTypes.Contains(mediaType, StringComparer.Ordinal)).ToList();
        }

        if (allSources)
        {
            var maximum = Math.Clamp(CurrentConfiguration.MaxSearchSources, 1, MaxKnownSources);
            sources = sources.Take(maximum).ToList();
        }
        else
        {
            sources = sources
                .Where(item => string.Equals(item.Id, source, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        if (sources.Count == 0)
        {
            throw new MediaForgeApplicationException(
                HttpStatusCode.BadRequest,
                "Die ausgewählte MediaForge-Quelle ist für diesen Medientyp nicht freigegeben oder deaktiviert.");
        }

        var groups = await Task.WhenAll(sources.Select(async item =>
        {
            try
            {
                var data = await _mediaForge.SearchAsync(query, item.Id, cancellationToken).ConfigureAwait(false);
                _grants.GrantFromJson(userId, item.Id, data);
                return new MediaForgeSearchGroup(item.Id, item.Label, data, null);
            }
            catch (MediaForgeException exception)
            {
                return new MediaForgeSearchGroup(item.Id, item.Label, null, exception.Message);
            }
        })).ConfigureAwait(false);

        if (!issueSelectionTokens)
        {
            return new MediaForgeSearchResponse(groups, []);
        }

        var candidates = new List<JellixSearchItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            if (!group.Data.HasValue)
            {
                continue;
            }

            foreach (var candidate in ReadSearchCandidates(group.Data.Value, group.Source, mediaType!))
            {
                if (!seen.Add(candidate.Url))
                {
                    continue;
                }

                var token = _selectionTokens.Issue(
                    userId,
                    candidate.MediaType,
                    candidate.Title,
                    candidate.Year,
                    candidate.Source,
                    candidate.Url);
                candidates.Add(new JellixSearchItem(token, candidate.Title, candidate.Year));
                if (candidates.Count >= 25)
                {
                    return new MediaForgeSearchResponse(groups, candidates);
                }
            }
        }

        return new MediaForgeSearchResponse(groups, candidates);
    }

    public async Task<MissingMediaPlan> PlanAsync(
        string userId,
        AutomaticMediaRequest request,
        bool requireGrant,
        bool applyRateLimit,
        CancellationToken cancellationToken)
    {
        Normalize(request);
        var validationError = ValidateAutomaticRequest(request, requireOptions: false);
        if (validationError is not null)
        {
            throw new MediaForgeApplicationException(HttpStatusCode.BadRequest, validationError);
        }

        if (applyRateLimit && !Allow(userId, "plan", 12))
        {
            throw TooManyRequests();
        }

        if (!await SourceIsAllowedAsync(request.Source, request.MediaType, cancellationToken).ConfigureAwait(false))
        {
            throw SourceNotAllowed();
        }

        return await BuildMissingPlanAsync(userId, request, cancellationToken, requireGrant).ConfigureAwait(false);
    }

    public async Task<SubmitMediaRequestResult> SubmitSelectionAsync(
        string userId,
        string username,
        string selectionToken,
        CancellationToken cancellationToken)
    {
        if (!Allow(userId, "request", 10))
        {
            throw TooManyRequests();
        }

        if (!_selectionTokens.TryConsumeAny(selectionToken, userId, out var selection))
        {
            throw new MediaForgeApplicationException(
                HttpStatusCode.BadRequest,
                "Die Auswahl ist abgelaufen, wurde bereits verwendet oder gehört zu einem anderen Benutzer.");
        }

        var config = CurrentConfiguration;
        var request = new AutomaticMediaRequest
        {
            Title = selection.Title,
            SeriesUrl = selection.Url,
            Source = selection.Source,
            MediaType = selection.MediaType,
            Language = config.DefaultLanguage,
            Provider = config.DefaultProvider,
        };
        return await SubmitCoreAsync(
            userId,
            username,
            request,
            requireGrant: false,
            expectedMediaType: selection.MediaType,
            rateLimitAlreadyApplied: true,
            cancellationToken).ConfigureAwait(false);
    }

    public Task<SubmitMediaRequestResult> SubmitAutomaticAsync(
        string userId,
        string username,
        AutomaticMediaRequest request,
        bool requireGrant,
        CancellationToken cancellationToken)
        => SubmitCoreAsync(
            userId,
            username,
            request,
            requireGrant,
            expectedMediaType: null,
            rateLimitAlreadyApplied: false,
            cancellationToken);

    public async Task<IReadOnlyList<MediaRequest>> ListForUserAsync(
        string userId,
        int limit,
        bool synchronizeProgress,
        CancellationToken cancellationToken)
    {
        var requests = await _store.ListForUserAsync(userId, limit, cancellationToken).ConfigureAwait(false);
        if (!synchronizeProgress)
        {
            return requests;
        }

        if (!Allow(userId, "progress", 30))
        {
            throw TooManyRequests();
        }

        var queueIds = requests
            .Where(item => item.Status == RequestStatuses.Queued && item.MediaForgeQueueId.HasValue)
            .Select(item => item.MediaForgeQueueId!.Value)
            .Distinct()
            .Take(500)
            .ToArray();
        if (queueIds.Length == 0)
        {
            return requests;
        }

        var upstream = await _mediaForge.GetProgressAsync(queueIds, cancellationToken).ConfigureAwait(false);
        var progress = ReadProgress(upstream, queueIds);
        await _store.SyncQueueStatesAsync(
            userId,
            progress.ToDictionary(item => item.QueueId, item => item.Status),
            cancellationToken).ConfigureAwait(false);
        var refreshed = await _store.ListForUserAsync(userId, limit, cancellationToken).ConfigureAwait(false);
        var byQueueId = progress.ToDictionary(item => item.QueueId);
        foreach (var request in refreshed)
        {
            if (request.MediaForgeQueueId.HasValue
                && byQueueId.TryGetValue(request.MediaForgeQueueId.Value, out var state))
            {
                request.Progress = (int)Math.Round(Math.Clamp(state.Percent, 0, 100), MidpointRounding.AwayFromZero);
                request.QueueRunning = state.Status == "running";
            }
        }

        return refreshed;
    }

    public async Task<IReadOnlyList<MediaForgeProgressInfo>> GetProgressAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        if (!Allow(userId, "progress", 30))
        {
            throw TooManyRequests();
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
            return [];
        }

        var upstream = await _mediaForge.GetProgressAsync(queueIds, cancellationToken).ConfigureAwait(false);
        var progress = ReadProgress(upstream, queueIds);
        await _store.SyncQueueStatesAsync(
            userId,
            progress.ToDictionary(item => item.QueueId, item => item.Status),
            cancellationToken).ConfigureAwait(false);
        return progress;
    }

    public Task<WithdrawRequestResult> WithdrawAsync(
        long id,
        string userId,
        string username,
        CancellationToken cancellationToken)
        => _store.TryWithdrawAsync(id, userId, username, cancellationToken);

    public Task<MediaRequest?> GetAsync(long id, CancellationToken cancellationToken)
        => _store.GetAsync(id, cancellationToken);

    public Task<IReadOnlyList<MediaRequest>> ListAllAsync(int limit, CancellationToken cancellationToken)
        => _store.ListAllAsync(limit, cancellationToken);

    public Task<bool> RejectAsync(
        long id,
        string reason,
        string decidedBy,
        CancellationToken cancellationToken)
        => _store.TryRejectAsync(id, reason, decidedBy, cancellationToken);

    public Task<MediaRequest> ApproveAsync(long id, string decidedBy, CancellationToken cancellationToken)
        => QueueRequestAsync(id, decidedBy, cancellationToken, refreshAvailability: true);

    private async Task<SubmitMediaRequestResult> SubmitCoreAsync(
        string userId,
        string username,
        AutomaticMediaRequest request,
        bool requireGrant,
        string? expectedMediaType,
        bool rateLimitAlreadyApplied,
        CancellationToken cancellationToken)
    {
        Normalize(request);
        var validationError = ValidateAutomaticRequest(request, requireOptions: true);
        if (validationError is not null)
        {
            throw new MediaForgeApplicationException(HttpStatusCode.BadRequest, validationError);
        }

        var config = CurrentConfiguration;
        if (config.MaintenanceMode)
        {
            throw new MediaForgeApplicationException(
                HttpStatusCode.ServiceUnavailable,
                string.IsNullOrWhiteSpace(config.MaintenanceMessage)
                    ? "Anfragen sind derzeit deaktiviert."
                    : SafeText(config.MaintenanceMessage, 500, "Anfragen sind derzeit deaktiviert."));
        }

        if (!rateLimitAlreadyApplied && !Allow(userId, "request", 10))
        {
            throw TooManyRequests();
        }

        if (!await SourceIsAllowedAsync(request.Source, request.MediaType, cancellationToken).ConfigureAwait(false))
        {
            throw SourceNotAllowed();
        }

        var plan = await BuildMissingPlanAsync(userId, request, cancellationToken, requireGrant).ConfigureAwait(false);
        if (expectedMediaType is not null
            && !string.Equals(plan.IsMovie ? "movie" : "series", expectedMediaType, StringComparison.Ordinal))
        {
            throw new MediaForgeApplicationException(HttpStatusCode.BadRequest, "Der gefundene Medientyp stimmt nicht mit der Auswahl überein.");
        }

        if (plan.MissingUrls.Count == 0)
        {
            return new SubmitMediaRequestResult(SubmitDisposition.AlreadyAvailable, null, null, 0);
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
        var maxPending = Math.Clamp(config.MaxPendingRequestsPerUser, 1, 100);
        var addResult = await _store.TryAddAsync(
            userId,
            username,
            calculated,
            RequestStatuses.Pending,
            maxPending,
            cancellationToken).ConfigureAwait(false);
        if (addResult.Duplicate is not null)
        {
            return new SubmitMediaRequestResult(SubmitDisposition.Duplicate, null, addResult.Duplicate, maxPending);
        }

        if (addResult.LimitReached)
        {
            return new SubmitMediaRequestResult(SubmitDisposition.LimitReached, null, null, maxPending);
        }

        if (addResult.StoreCapacityReached)
        {
            return new SubmitMediaRequestResult(SubmitDisposition.StoreCapacityReached, null, null, maxPending);
        }

        var stored = addResult.Request
            ?? throw new InvalidOperationException("Request store returned no result.");
        if (!config.AutoApproveRequests)
        {
            return new SubmitMediaRequestResult(SubmitDisposition.Stored, stored, null, maxPending);
        }

        var queued = await QueueRequestAsync(stored.Id, "automatic", cancellationToken).ConfigureAwait(false);
        return new SubmitMediaRequestResult(
            queued.Status == RequestStatuses.Queued ? SubmitDisposition.Queued : SubmitDisposition.QueueFailed,
            queued,
            null,
            maxPending);
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
            if (!await SourceIsAllowedAsync(request.Source, request.MediaType, cancellationToken).ConfigureAwait(false))
            {
                throw SourceNotAllowed();
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

            var queueResult = await _mediaForge.QueueAsync(request, cancellationToken).ConfigureAwait(false);
            var warning = queueResult.AcceptedEpisodeCount.HasValue
                && queueResult.AcceptedEpisodeCount.Value != request.Episodes.Count
                ? $"MediaForge hat {queueResult.AcceptedEpisodeCount.Value} von {request.Episodes.Count} geplanten Episoden bestätigt. Die Warteschlange wurde nicht erneut gesendet."
                : null;
            await _store.MarkQueuedAsync(id, queueResult.QueueId, decidedBy, warning, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is MediaForgeException or MediaForgeApplicationException or HttpRequestException or OperationCanceledException)
        {
            var error = exception switch
            {
                MediaForgeException mediaForgeException => mediaForgeException.Message,
                MediaForgeApplicationException applicationException => applicationException.Message,
                _ => "Die Übergabe an MediaForge wurde unterbrochen.",
            };
            await _store.MarkFailedAsync(id, error, decidedBy, CancellationToken.None).ConfigureAwait(false);
        }

        return await _store.GetAsync(id, CancellationToken.None).ConfigureAwait(false) ?? request;
    }

    private async Task<bool> SourceIsAllowedAsync(
        string source,
        string? mediaType,
        CancellationToken cancellationToken)
    {
        var normalizedType = NormalizeMediaType(mediaType);
        var sources = ReadAllowedSources(
            await _mediaForge.GetSourcesAsync(cancellationToken).ConfigureAwait(false),
            CurrentConfiguration);
        return sources.Any(item =>
            string.Equals(item.Id, source, StringComparison.OrdinalIgnoreCase)
            && (normalizedType is null || item.MediaTypes.Contains(normalizedType, StringComparer.Ordinal)));
    }

    private async Task<MissingMediaPlan> BuildMissingPlanAsync(
        string userId,
        AutomaticMediaRequest request,
        CancellationToken cancellationToken,
        bool requireGrant)
    {
        if (requireGrant
            && (!_grants.IsGranted(userId, request.SeriesUrl, out var grantedSource)
                || !string.Equals(grantedSource, request.Source, StringComparison.OrdinalIgnoreCase)))
        {
            throw new MediaForgeApplicationException(
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
            throw new MediaForgeApplicationException(HttpStatusCode.BadGateway, "MediaForge hat keine gültige Staffelliste geliefert.");
        }

        if (seasons.GetArrayLength() > 100)
        {
            throw new MediaForgeApplicationException(HttpStatusCode.BadRequest, "Der Titel enthält mehr als 100 Staffeln und kann nicht sicher automatisch geplant werden.");
        }

        var seasonItems = seasons.EnumerateArray().ToArray();
        if (seasonItems.Length == 0)
        {
            throw new MediaForgeApplicationException(HttpStatusCode.NotFound, "Für diesen Titel wurden keine verfügbaren Inhalte gefunden.");
        }

        var missing = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        HashSet<string>? languages = null;
        var total = 0;
        foreach (var season in seasonItems)
        {
            if (season.ValueKind != JsonValueKind.Object)
            {
                throw InvalidSeasonList();
            }

            var seasonUrl = ReadJsonString(season, "url", 2048);
            var seasonNumber = ReadOptionalInt(season, "season_number");
            var expectedEpisodeCount = ReadExpectedEpisodeCount(season);
            if (!MediaAccessGrantStore.TryNormalizeUrl(seasonUrl, out var normalizedSeasonUrl))
            {
                throw InvalidSeasonList();
            }

            _grants.GrantUrl(userId, request.Source, normalizedSeasonUrl);
            var seasonEpisodes = await MediaForgeEpisodeParser.FetchCompleteAsync(
                token => _mediaForge.GetEpisodesAsync(normalizedSeasonUrl, token),
                response => _grants.GrantFromJson(userId, request.Source, response),
                seasonNumber,
                expectedEpisodeCount,
                cancellationToken).ConfigureAwait(false);
            foreach (var episode in seasonEpisodes)
            {
                if (!seen.Add(episode.Url))
                {
                    throw new MediaForgeApplicationException(HttpStatusCode.BadGateway, "MediaForge hat doppelte Episoden-URLs geliefert. Es wurde nichts eingereiht.");
                }

                total++;
                if (!isMovie && (episode.SeasonNumber is null or < 0 || episode.EpisodeNumber is null or <= 0))
                {
                    throw new MediaForgeApplicationException(HttpStatusCode.BadGateway, "MediaForge hat eine Episode ohne gültige Staffel- oder Folgennummer geliefert. Es wurde nichts eingereiht.");
                }

                var alreadyAvailable = isMovie
                    ? libraryState.ItemExists
                    : episode.SeasonNumber.HasValue
                        && episode.EpisodeNumber.HasValue
                        && libraryState.Episodes.Contains(new LibraryEpisodeKey(episode.SeasonNumber.Value, episode.EpisodeNumber.Value));
                if (alreadyAvailable)
                {
                    continue;
                }

                if (episode.Languages.Count > 0)
                {
                    if (languages is null)
                    {
                        languages = new HashSet<string>(episode.Languages, StringComparer.Ordinal);
                    }
                    else
                    {
                        languages.IntersectWith(episode.Languages);
                    }
                }

                missing.Add(episode.Url);
                if (missing.Count > MaxEpisodesPerRequest)
                {
                    throw new MediaForgeApplicationException(
                        HttpStatusCode.BadRequest,
                        $"Es fehlen mehr als {MaxEpisodesPerRequest} Episoden. Bitte den Titel in MediaForge in mehreren Schritten einreihen.");
                }
            }
        }

        if (total == 0)
        {
            throw new MediaForgeApplicationException(HttpStatusCode.NotFound, "Für diesen Titel wurden keine verfügbaren Episoden gefunden.");
        }

        var selectionLabel = isMovie ? "Film" : missing.Count == 1 ? "1 fehlende Episode" : $"{missing.Count} fehlende Episoden";
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
            languages is null ? [] : languages.Order(StringComparer.Ordinal).ToArray(),
            providers);
    }

    internal static int ReadExpectedEpisodeCount(JsonElement season)
    {
        var count = ReadOptionalInt(season, "episode_count");
        if (!count.HasValue || count.Value < 0)
        {
            throw InvalidSeasonList();
        }

        return count.Value;
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
            if (response.ValueKind != JsonValueKind.Object)
            {
                return new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            }

            var output = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            foreach (var property in response.EnumerateObject())
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

    internal static List<MediaForgeSourceInfo> ReadAllowedSources(
        JsonElement response,
        PluginConfiguration? configuration = null)
    {
        configuration ??= Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var allowlist = (configuration.AllowedSources ?? string.Empty)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var output = new List<MediaForgeSourceInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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

            var id = ReadJsonString(item, "id", 80);
            var label = ReadJsonString(item, "label", 200);
            var hasAdult = item.TryGetProperty("adult", out var adultValue)
                && adultValue.ValueKind is JsonValueKind.True or JsonValueKind.False;
            var hasEnabled = item.TryGetProperty("enabled", out var enabledValue);
            var validEnabled = !hasEnabled || enabledValue.ValueKind is JsonValueKind.True or JsonValueKind.False;
            var enabled = validEnabled && (!hasEnabled || enabledValue.ValueKind == JsonValueKind.True);
            var adult = hasAdult && adultValue.ValueKind == JsonValueKind.True;
            var mediaTypes = ReadMediaTypes(item);
            if (!hasAdult
                || !validEnabled
                || string.IsNullOrWhiteSpace(id)
                || !enabled
                || adult
                || mediaTypes.Count == 0
                || (allowlist.Count > 0 && !allowlist.Contains(id))
                || !seen.Add(id))
            {
                continue;
            }

            output.Add(new MediaForgeSourceInfo(id, string.IsNullOrWhiteSpace(label) ? id : label, false, mediaTypes));
            if (output.Count >= MaxKnownSources)
            {
                break;
            }
        }

        return output;
    }

    internal static IReadOnlyList<MediaForgeProgressInfo> ReadProgress(
        JsonElement response,
        IReadOnlyCollection<long> requestedIds)
    {
        var output = new List<MediaForgeProgressInfo>();
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
            if (status is not ("queued" or "running" or RequestStatuses.Completed or RequestStatuses.Partial
                or RequestStatuses.Failed or RequestStatuses.Cancelled))
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
            output.Add(new MediaForgeProgressInfo(queueId, status!, current, total, percent, phase));
        }

        return output;
    }

    internal static string? ValidateAutomaticRequest(AutomaticMediaRequest request, bool requireOptions)
    {
        if (string.IsNullOrWhiteSpace(request.Title)
            || string.IsNullOrWhiteSpace(request.SeriesUrl)
            || string.IsNullOrWhiteSpace(request.Source)
            || (requireOptions && (string.IsNullOrWhiteSpace(request.Language) || string.IsNullOrWhiteSpace(request.Provider))))
        {
            return requireOptions ? "Titel, URL, Quelle, Sprache und Provider werden benötigt." : "Titel, URL und Quelle werden benötigt.";
        }

        if (request.Title.Length > 300
            || request.SeriesUrl.Length > 2048
            || request.Source.Length > 80
            || request.MediaType.Length > 20
            || request.Language.Length > 100
            || request.Provider.Length > 100
            || !MediaAccessGrantStore.TryNormalizeUrl(request.SeriesUrl, out _))
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

    private static void Normalize(AutomaticMediaRequest request)
    {
        request.Title = request.Title?.Trim() ?? string.Empty;
        request.SeriesUrl = request.SeriesUrl?.Trim() ?? string.Empty;
        request.Source = request.Source?.Trim().ToLowerInvariant() ?? string.Empty;
        request.MediaType = NormalizeMediaType(request.MediaType) ?? "series";
        request.Language = request.Language?.Trim() ?? string.Empty;
        request.Provider = request.Provider?.Trim() ?? string.Empty;
    }

    private static IReadOnlyList<SearchCandidate> ReadSearchCandidates(JsonElement data, string source, string expectedMediaType)
    {
        var output = new List<SearchCandidate>();
        if (data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("results", out var results)
            || results.ValueKind != JsonValueKind.Array)
        {
            return output;
        }

        foreach (var item in results.EnumerateArray().Take(200))
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var title = ReadJsonString(item, "title", 300);
            if (string.IsNullOrWhiteSpace(title))
            {
                title = ReadJsonString(item, "name", 300);
            }

            var rawUrl = ReadJsonString(item, "url", 2048);
            if (string.IsNullOrWhiteSpace(rawUrl))
            {
                rawUrl = ReadJsonString(item, "link", 2048);
            }

            var rawMediaType = ReadJsonString(item, "media_type", 20);
            var resultMediaType = string.IsNullOrWhiteSpace(rawMediaType)
                ? expectedMediaType
                : NormalizeMediaType(rawMediaType);
            if (string.IsNullOrWhiteSpace(title)
                || resultMediaType is null
                || !string.Equals(resultMediaType, expectedMediaType, StringComparison.Ordinal)
                || !MediaAccessGrantStore.TryNormalizeUrl(rawUrl, out var normalizedUrl))
            {
                continue;
            }

            var year = ReadJsonString(item, "year", 16);
            if (string.IsNullOrWhiteSpace(year))
            {
                year = ReadJsonString(item, "release_year", 16);
            }

            output.Add(new SearchCandidate(title, year, source, normalizedUrl, resultMediaType));
        }

        return output;
    }

    private static IReadOnlyList<string> ReadMediaTypes(JsonElement item)
    {
        var output = new HashSet<string>(StringComparer.Ordinal);
        if (item.TryGetProperty("media_types", out var values) && values.ValueKind == JsonValueKind.Array)
        {
            foreach (var value in values.EnumerateArray())
            {
                if (value.ValueKind == JsonValueKind.String && NormalizeMediaType(value.GetString()) is { } normalized)
                {
                    output.Add(normalized);
                }
            }
        }

        return output.Order(StringComparer.Ordinal).ToArray();
    }

    private static string? NormalizeMediaType(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "movie" or "movies" or "film" or "filme" => "movie",
            "series" or "serie" or "serien" => "series",
            _ => null,
        };

    private bool Allow(string userId, string operation, int limit)
        => _rateLimiter.TryConsume(userId, operation, limit, RateWindow);

    private static MediaForgeApplicationException TooManyRequests()
        => new(HttpStatusCode.TooManyRequests, "Zu viele Anfragen. Bitte kurz warten.");

    private static MediaForgeApplicationException SourceNotAllowed()
        => new(HttpStatusCode.BadRequest, "Die angegebene MediaForge-Quelle ist nicht freigegeben oder deaktiviert.");

    private static MediaForgeApplicationException InvalidSeasonList()
        => new(HttpStatusCode.BadGateway, "MediaForge hat eine unvollständige oder ungültige Staffelliste geliefert. Es wurde nichts eingereiht.");

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

        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out numeric))
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

    private static void ReadNestedProviderIds(JsonElement detail, string field, IDictionary<string, string> output)
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
        if (!string.IsNullOrWhiteSpace(value) && value.Length <= 100 && !value.Any(char.IsControl))
        {
            output.TryAdd(provider, value.Trim());
        }
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

    private static string SafeText(string? value, int maximum, string fallback)
    {
        var clean = new string((value ?? string.Empty).Where(character => !char.IsControl(character)).Take(maximum).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(clean) ? fallback : clean;
    }

    private PluginConfiguration CurrentConfiguration => _configuration() ?? new PluginConfiguration();

    private sealed record SearchCandidate(string Title, string Year, string Source, string Url, string MediaType);
}

public sealed class MediaForgeApplicationException : Exception
{
    public MediaForgeApplicationException(HttpStatusCode statusCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
    }

    public HttpStatusCode StatusCode { get; }
}

public sealed record MediaForgeSourceInfo(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("adult")] bool Adult,
    [property: JsonPropertyName("media_types")] IReadOnlyList<string> MediaTypes);

public sealed record MediaForgeSearchGroup(
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("data")] JsonElement? Data,
    [property: JsonPropertyName("error")] string? Error);

public sealed record MediaForgeSearchResponse(
    IReadOnlyList<MediaForgeSearchGroup> Groups,
    IReadOnlyList<JellixSearchItem> Items);

public sealed record JellixSearchItem(
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("year")] string Year);

public sealed record MediaForgeProgressInfo(
    [property: JsonPropertyName("queue_id")] long QueueId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("current_episode")] int CurrentEpisode,
    [property: JsonPropertyName("total_episodes")] int TotalEpisodes,
    [property: JsonPropertyName("percent")] double Percent,
    [property: JsonPropertyName("phase")] string Phase);

public sealed record MissingMediaPlan(
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

public sealed record MissingPlanResponse(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("is_movie")] bool IsMovie,
    [property: JsonPropertyName("total_count")] int TotalCount,
    [property: JsonPropertyName("existing_count")] int ExistingCount,
    [property: JsonPropertyName("missing_count")] int MissingCount,
    [property: JsonPropertyName("selection_label")] string SelectionLabel,
    [property: JsonPropertyName("languages")] IReadOnlyList<string> Languages,
    [property: JsonPropertyName("providers")] IReadOnlyDictionary<string, IReadOnlyList<string>> Providers);

public enum SubmitDisposition
{
    Stored,
    Queued,
    QueueFailed,
    Duplicate,
    LimitReached,
    StoreCapacityReached,
    AlreadyAvailable,
}

public sealed record SubmitMediaRequestResult(
    SubmitDisposition Disposition,
    MediaRequest? Request,
    MediaRequest? Duplicate,
    int MaxPending);
