using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.MediaForge.Models;
using Jellyfin.Plugin.MediaForge.Services;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.MediaForge.Integration;

/// <summary>
/// Optional, versioned in-process JSON bridge for Jellix. This concrete type is
/// discovered by exact name so neither plugin needs a shared contract assembly.
/// </summary>
public sealed class JellixBridge
{
    private const string ProtocolVersion = "1";
    private const int MaxPayloadBytes = 2 * 1024 * 1024;
    private const int MaxJsonDepth = 8;
    private readonly MediaForgeRequestApplicationService _application;
    private readonly MediaForgeClient _mediaForge;
    private readonly IUserManager _userManager;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 16,
    };

    public JellixBridge(
        MediaForgeRequestApplicationService application,
        MediaForgeClient mediaForge,
        IUserManager userManager)
    {
        _application = application;
        _mediaForge = mediaForge;
        _userManager = userManager;
    }

    /// <summary>Invokes a protocol-v1 bridge operation and returns sanitized JSON.</summary>
    public async Task<string> InvokeAsync(
        string protocolVersion,
        string operation,
        string jellyfinUserId,
        string username,
        string payloadJson,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(protocolVersion, ProtocolVersion, StringComparison.Ordinal))
        {
            throw Rejected("Unsupported bridge protocol.");
        }

        if (operation is not ("status" or "search" or "submit" or "list"))
        {
            throw Rejected("Unsupported bridge operation.");
        }

        using var payload = ParsePayload(payloadJson);
        _ = username; // Display-only input. The server-loaded Jellyfin user is authoritative.

        string result;
        if (operation == "status")
        {
            ValidateNoProperties(payload.RootElement);
            ValidateStatusUserId(jellyfinUserId);
            var status = await _mediaForge.CheckHealthAsync(cancellationToken).ConfigureAwait(false);
            result = Serialize(new
            {
                healthy = status.Healthy,
                configured = status.Configured,
                apiKeyValid = status.ApiKeyValid,
                version = ReadVersion(),
            });
        }
        else
        {
            var userId = ParseUserId(jellyfinUserId);
            var user = _userManager.GetUserById(userId);
            if (user is null || user.HasPermission(PermissionKind.IsDisabled))
            {
                throw Rejected("The Jellyfin user does not exist or is disabled.");
            }
            var authoritativeId = user.Id.ToString("N");
            var authoritativeName = SafeText(user.Username, 200, "unknown");
            result = operation switch
            {
                "search" => await SearchAsync(authoritativeId, payload.RootElement, cancellationToken).ConfigureAwait(false),
                "submit" => await SubmitAsync(authoritativeId, authoritativeName, payload.RootElement, cancellationToken).ConfigureAwait(false),
                "list" => await ListAsync(authoritativeId, payload.RootElement, cancellationToken).ConfigureAwait(false),
                _ => throw Rejected("Unsupported bridge operation."),
            };
        }

        if (Encoding.UTF8.GetByteCount(result) > MaxPayloadBytes)
        {
            throw Rejected("Bridge response is too large.");
        }

        return result;
    }

    private async Task<string> SearchAsync(
        string userId,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        ValidateProperties(payload, "query", "mediaType");
        var query = ReadRequiredString(payload, "query", 2, 120);
        var mediaType = ReadRequiredString(payload, "mediaType", 5, 6).ToLowerInvariant();
        if (mediaType is not ("movie" or "series"))
        {
            throw Rejected("Media type must be movie or series.");
        }

        var response = await _application.SearchAsync(
            userId,
            query,
            "all",
            mediaType,
            issueSelectionTokens: true,
            cancellationToken).ConfigureAwait(false);
        return Serialize(new { items = response.Items.Take(25).ToArray() });
    }

    private async Task<string> SubmitAsync(
        string userId,
        string username,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        ValidateProperties(payload, "selectionToken");
        var token = ReadRequiredString(payload, "selectionToken", 1, 100);
        var result = await _application.SubmitSelectionAsync(
            userId,
            username,
            token,
            cancellationToken).ConfigureAwait(false);
        if (result.Disposition is SubmitDisposition.Duplicate)
        {
            throw Rejected("This content already has an open request.");
        }

        if (result.Disposition is SubmitDisposition.LimitReached)
        {
            throw Rejected("The user request limit has been reached.");
        }

        if (result.Disposition is SubmitDisposition.StoreCapacityReached)
        {
            throw Rejected("The request store is currently full.");
        }

        if (result.Disposition is SubmitDisposition.AlreadyAvailable)
        {
            throw Rejected("The selected content is already available.");
        }

        var request = result.Request ?? throw Rejected("The request could not be stored.");
        return Serialize(new
        {
            id = request.Id,
            title = SafeText(request.Title, 300, "MediaForge"),
            status = MapStatus(request),
        });
    }

    private async Task<string> ListAsync(
        string userId,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        ValidateNoProperties(payload);
        var requests = await _application.ListForUserAsync(
            userId,
            500,
            synchronizeProgress: true,
            cancellationToken).ConfigureAwait(false);
        var items = requests.Take(500).Select(request => new JellixRequestResponse(
            request.Id,
            SafeText(request.Title, 300, "MediaForge"),
            MapStatus(request),
            request.Progress.HasValue ? Math.Clamp(request.Progress.Value, 0, 100) : null)).ToArray();
        return Serialize(new { items });
    }

    private string Serialize<T>(T value) => JsonSerializer.Serialize(value, _jsonOptions);

    private static JsonDocument ParsePayload(string payloadJson)
    {
        payloadJson ??= string.Empty;
        if (Encoding.UTF8.GetByteCount(payloadJson) > MaxPayloadBytes)
        {
            throw Rejected("Bridge payload is too large.");
        }

        try
        {
            var document = JsonDocument.Parse(payloadJson, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = MaxJsonDepth,
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                document.Dispose();
                throw Rejected("Bridge payload must be a JSON object.");
            }

            return document;
        }
        catch (JsonException)
        {
            throw Rejected("Bridge payload is invalid.");
        }
    }

    private static Guid ParseUserId(string value)
    {
        if (!Guid.TryParseExact(value, "N", out var userId) || userId == Guid.Empty)
        {
            throw Rejected("The Jellyfin user ID is invalid.");
        }

        return userId;
    }

    private static void ValidateStatusUserId(string value)
    {
        if (!Guid.TryParseExact(value, "N", out var userId) || userId != Guid.Empty)
        {
            throw Rejected("Status requires an empty Jellyfin user ID.");
        }
    }

    private static void ValidateNoProperties(JsonElement payload)
        => ValidateProperties(payload, []);

    private static void ValidateProperties(JsonElement payload, params string[] allowed)
    {
        var allowedSet = allowed.ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in payload.EnumerateObject())
        {
            if (!allowedSet.Contains(property.Name) || !seen.Add(property.Name))
            {
                throw Rejected("Bridge payload contains unexpected fields.");
            }
        }

        if (seen.Count != allowedSet.Count)
        {
            throw Rejected("Bridge payload is missing required fields.");
        }
    }

    private static string ReadRequiredString(JsonElement payload, string name, int minimum, int maximum)
    {
        if (!payload.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            throw Rejected("Bridge payload contains an invalid field.");
        }

        var text = value.GetString()?.Trim() ?? string.Empty;
        if (text.Length < minimum || text.Length > maximum || text.Any(char.IsControl))
        {
            throw Rejected("Bridge payload contains an invalid field.");
        }

        return text;
    }

    internal static string MapStatus(MediaRequest request)
        => request.Status switch
        {
            RequestStatuses.Pending or RequestStatuses.Processing => "pending",
            RequestStatuses.Queued when request.QueueRunning => "downloading",
            RequestStatuses.Queued => "queued",
            RequestStatuses.Completed or RequestStatuses.Available => "available",
            RequestStatuses.Partial or RequestStatuses.Failed or RequestStatuses.Cancelled => "failed",
            RequestStatuses.Rejected => "rejected",
            RequestStatuses.Withdrawn => "withdrawn",
            _ => "failed",
        };

    private static string ReadVersion()
        => typeof(JellixBridge).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
            .Split('+', 2)[0]
            ?? typeof(JellixBridge).Assembly.GetName().Version?.ToString(3)
            ?? "unknown";

    private static string SafeText(string? value, int maximum, string fallback)
    {
        var clean = new string((value ?? string.Empty)
            .Where(character => !char.IsControl(character))
            .Take(maximum)
            .ToArray()).Trim();
        return string.IsNullOrWhiteSpace(clean) ? fallback : clean;
    }

    private static MediaForgeApplicationException Rejected(string message)
        => new(HttpStatusCode.BadRequest, message);

    private sealed record JellixRequestResponse(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("progress")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Progress);
}
