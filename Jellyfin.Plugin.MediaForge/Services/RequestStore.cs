using System.Text.Json;
using Jellyfin.Plugin.MediaForge.Models;

namespace Jellyfin.Plugin.MediaForge.Services;

/// <summary>
/// Small, atomic JSON request store. Request volume is bounded and low, so a
/// single locked document avoids shipping another native SQLite runtime into
/// Jellyfin while still providing durable state across server restarts.
/// </summary>
public sealed class RequestStore
{
    private const int MaxStoredRequests = 20_000;
    private const long MaxStoreBytes = 64L * 1024 * 1024;
    private readonly string _path;
    private readonly int _maxStoredRequests;
    private readonly long _maxStoreBytes;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private StoreDocument _document;

    public RequestStore()
        : this(Plugin.Instance?.DataFolderPath
            ?? throw new InvalidOperationException("MediaForge plugin data path is not available."))
    {
    }

    internal RequestStore(
        string dataPath,
        int maxStoredRequests = MaxStoredRequests,
        long maxStoreBytes = MaxStoreBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxStoredRequests, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxStoreBytes, 1);
        Directory.CreateDirectory(dataPath);
        _path = Path.Combine(dataPath, "requests.json");
        _maxStoredRequests = maxStoredRequests;
        _maxStoreBytes = maxStoreBytes;
        _document = Load();
    }

    public async Task<AddRequestResult> TryAddAsync(
        string userId,
        string username,
        CreateMediaRequest request,
        string initialStatus,
        int maxOpen,
        CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var episodesJson = JsonSerializer.Serialize(request.Episodes);
            var duplicate = _document.Requests.LastOrDefault(item =>
                item.UserId == userId
                && item.SeriesUrl == request.SeriesUrl
                && item.EpisodesJson == episodesJson
                && IsOpen(item));
            if (duplicate is not null)
            {
                return new AddRequestResult(null, Clone(duplicate), false, false);
            }

            if (_document.Requests.Count(item => item.UserId == userId && IsOpen(item)) >= maxOpen)
            {
                return new AddRequestResult(null, null, true, false);
            }

            var document = CloneDocument();
            PruneCompleted(document, _maxStoredRequests - 1);
            if (document.Requests.Count >= _maxStoredRequests)
            {
                return new AddRequestResult(null, null, false, true);
            }

            var item = new MediaRequest
            {
                Id = document.NextId++,
                UserId = userId,
                Username = username,
                Title = request.Title,
                SeriesUrl = request.SeriesUrl,
                Source = request.Source,
                MediaType = request.MediaType,
                SelectionLabel = request.SelectionLabel,
                EpisodesJson = episodesJson,
                Language = request.Language,
                Provider = request.Provider,
                Upscale = request.Upscale,
                Status = initialStatus,
                CreatedUtc = DateTime.UtcNow,
            };
            document.Requests.Add(item);
            await SaveLockedAsync(document, cancellationToken).ConfigureAwait(false);
            _document = document;
            return new AddRequestResult(Clone(item), null, false, false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<MediaRequest>> ListForUserAsync(
        string userId,
        int limit,
        CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _document.Requests
                .Where(item => item.UserId == userId)
                .OrderByDescending(item => item.Id)
                .Take(Math.Clamp(limit, 1, 500))
                .Select(item => Clone(item)!)
                .ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<MediaRequest>> ListAllAsync(int limit, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _document.Requests
                .OrderByDescending(item => item.Id)
                .Take(Math.Clamp(limit, 1, 500))
                .Select(item => Clone(item)!)
                .ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<MediaRequest?> GetAsync(long id, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return Clone(_document.Requests.FirstOrDefault(item => item.Id == id));
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> TryClaimAsync(long id, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = _document.Requests.FirstOrDefault(candidate => candidate.Id == id);
            if (current is null || current.Status is not (RequestStatuses.Pending or RequestStatuses.Failed))
            {
                return false;
            }

            var document = CloneDocument();
            var item = document.Requests.First(candidate => candidate.Id == id);
            item.Status = RequestStatuses.Processing;
            item.Error = null;
            await SaveLockedAsync(document, cancellationToken).ConfigureAwait(false);
            _document = document;
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public Task MarkQueuedAsync(long id, long? queueId, string decidedBy, CancellationToken cancellationToken)
        => UpdateDecisionAsync(id, RequestStatuses.Queued, decidedBy, queueId, null, cancellationToken);

    public Task MarkFailedAsync(long id, string error, string decidedBy, CancellationToken cancellationToken)
        => UpdateDecisionAsync(id, RequestStatuses.Failed, decidedBy, null, error, cancellationToken);

    public Task MarkAvailableAsync(long id, string decidedBy, CancellationToken cancellationToken)
        => UpdateDecisionAsync(id, RequestStatuses.Available, decidedBy, null, null, cancellationToken);

    public async Task<bool> TryUpdateProcessingPlanAsync(
        long id,
        string title,
        string mediaType,
        string selectionLabel,
        IReadOnlyList<string> episodes,
        CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = _document.Requests.FirstOrDefault(candidate => candidate.Id == id);
            if (current?.Status != RequestStatuses.Processing)
            {
                return false;
            }

            var document = CloneDocument();
            var item = document.Requests.First(candidate => candidate.Id == id);
            item.Title = title;
            item.MediaType = mediaType;
            item.SelectionLabel = selectionLabel;
            item.EpisodesJson = JsonSerializer.Serialize(episodes);
            await SaveLockedAsync(document, cancellationToken).ConfigureAwait(false);
            _document = document;
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> TryRejectAsync(long id, string reason, string decidedBy, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = _document.Requests.FirstOrDefault(candidate => candidate.Id == id);
            if (current is null || current.Status is not (RequestStatuses.Pending or RequestStatuses.Failed))
            {
                return false;
            }

            var document = CloneDocument();
            var item = document.Requests.First(candidate => candidate.Id == id);
            ApplyDecision(item, RequestStatuses.Rejected, decidedBy, null, reason);
            await SaveLockedAsync(document, cancellationToken).ConfigureAwait(false);
            _document = document;
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<WithdrawRequestResult> TryWithdrawAsync(
        long id,
        string userId,
        string username,
        CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = _document.Requests.FirstOrDefault(candidate => candidate.Id == id && candidate.UserId == userId);
            if (current is null)
            {
                return WithdrawRequestResult.NotFound;
            }

            if (current.Status != RequestStatuses.Pending)
            {
                return WithdrawRequestResult.NotPending;
            }

            var document = CloneDocument();
            var item = document.Requests.First(candidate => candidate.Id == id);
            ApplyDecision(item, RequestStatuses.Withdrawn, username, null, null);
            await SaveLockedAsync(document, cancellationToken).ConfigureAwait(false);
            _document = document;
            return WithdrawRequestResult.Withdrawn;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SyncQueueStatesAsync(
        string userId,
        IReadOnlyDictionary<long, string> queueStates,
        CancellationToken cancellationToken)
    {
        if (queueStates.Count == 0)
        {
            return;
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = CloneDocument();
            var changed = false;
            foreach (var item in document.Requests.Where(item =>
                         item.UserId == userId
                         && item.Status == RequestStatuses.Queued
                         && item.MediaForgeQueueId.HasValue))
            {
                if (!queueStates.TryGetValue(item.MediaForgeQueueId!.Value, out var queueStatus))
                {
                    continue;
                }

                var (status, error) = queueStatus switch
                {
                    RequestStatuses.Completed => (RequestStatuses.Completed, (string?)null),
                    RequestStatuses.Partial => (RequestStatuses.Partial, "MediaForge hat den Download nur teilweise abgeschlossen."),
                    RequestStatuses.Failed => (RequestStatuses.Failed, "MediaForge konnte den Download nicht abschließen."),
                    RequestStatuses.Cancelled => (RequestStatuses.Cancelled, "Der Download wurde außerhalb von Jellyfin abgebrochen."),
                    _ => (string.Empty, null),
                };
                if (status.Length == 0)
                {
                    continue;
                }

                item.Status = status;
                item.Error = error;
                changed = true;
            }

            if (changed)
            {
                await SaveLockedAsync(document, cancellationToken).ConfigureAwait(false);
                _document = document;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task UpdateDecisionAsync(
        long id,
        string status,
        string decidedBy,
        long? queueId,
        string? error,
        CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_document.Requests.All(candidate => candidate.Id != id))
            {
                return;
            }

            var document = CloneDocument();
            var item = document.Requests.First(candidate => candidate.Id == id);
            ApplyDecision(item, status, decidedBy, queueId, error);
            await SaveLockedAsync(document, cancellationToken).ConfigureAwait(false);
            _document = document;
        }
        finally
        {
            _lock.Release();
        }
    }

    private StoreDocument Load()
    {
        if (!File.Exists(_path))
        {
            return new StoreDocument();
        }

        try
        {
            if (new FileInfo(_path).Length > _maxStoreBytes)
            {
                throw new JsonException("Request store exceeds the safe size limit.");
            }

            var loaded = JsonSerializer.Deserialize<StoreDocument>(File.ReadAllText(_path), _jsonOptions)
                ?? new StoreDocument();
            loaded.Requests ??= [];
            foreach (var interrupted in loaded.Requests.Where(item => item.Status == RequestStatuses.Processing))
            {
                interrupted.Status = RequestStatuses.Failed;
                interrupted.Error = "Die Übergabe wurde durch einen Jellyfin-Neustart unterbrochen und kann erneut versucht werden.";
                interrupted.DecidedUtc = DateTime.UtcNow;
                interrupted.DecidedBy = "recovery";
            }

            loaded.NextId = Math.Max(loaded.NextId, loaded.Requests.Select(item => item.Id).DefaultIfEmpty().Max() + 1);
            return loaded;
        }
        catch (JsonException)
        {
            // Preserve the unreadable document for manual recovery and start
            // a fresh store instead of preventing Jellyfin from starting.
            var backup = _path + ".invalid-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            try
            {
                File.Copy(_path, backup, overwrite: false);
            }
            catch (IOException)
            {
                // Recovery backup is best effort. Never overwrite another backup.
            }

            return new StoreDocument();
        }
    }

    private async Task SaveLockedAsync(StoreDocument document, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("Request store directory is unavailable.");
        var temporary = Path.Combine(directory, Path.GetRandomFileName());
        try
        {
            await using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, document, _jsonOptions, cancellationToken).ConfigureAwait(false);
                if (stream.Length > _maxStoreBytes)
                {
                    throw new IOException("Request store exceeds the safe size limit.");
                }

                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private MediaRequest? Clone(MediaRequest? item)
        => item is null
            ? null
            : JsonSerializer.Deserialize<MediaRequest>(JsonSerializer.Serialize(item, _jsonOptions), _jsonOptions);

    private StoreDocument CloneDocument()
        => JsonSerializer.Deserialize<StoreDocument>(JsonSerializer.Serialize(_document, _jsonOptions), _jsonOptions)
            ?? throw new InvalidOperationException("Request store could not be cloned.");

    private static void PruneCompleted(StoreDocument document, int targetCount)
    {
        var excess = document.Requests.Count - targetCount;
        if (excess <= 0)
        {
            return;
        }

        var removable = document.Requests
            .Where(item => !IsOpen(item))
            .OrderBy(item => item.Id)
            .Take(excess)
            .Select(item => item.Id)
            .ToHashSet();
        document.Requests.RemoveAll(item => removable.Contains(item.Id));
    }

    private static bool IsOpen(MediaRequest item)
        => item.Status is RequestStatuses.Pending or RequestStatuses.Processing or RequestStatuses.Queued;

    private static void ApplyDecision(MediaRequest item, string status, string decidedBy, long? queueId, string? error)
    {
        item.Status = status;
        item.DecidedUtc = DateTime.UtcNow;
        item.DecidedBy = decidedBy;
        item.MediaForgeQueueId = queueId;
        item.Error = error;
    }

    private sealed class StoreDocument
    {
        public long NextId { get; set; } = 1;

        public List<MediaRequest> Requests { get; set; } = [];
    }
}

/// <summary>Atomic result of checking duplicate/limit constraints and adding a request.</summary>
public sealed record AddRequestResult(
    MediaRequest? Request,
    MediaRequest? Duplicate,
    bool LimitReached,
    bool StoreCapacityReached);

/// <summary>Result of a user attempting to withdraw an unapproved request.</summary>
public enum WithdrawRequestResult
{
    NotFound,
    NotPending,
    Withdrawn,
}
