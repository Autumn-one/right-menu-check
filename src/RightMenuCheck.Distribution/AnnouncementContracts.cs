using System.Text.Json.Serialization;

namespace RightMenuCheck.Distribution;

public enum AnnouncementKind
{
    Information,
    Warning,
    Maintenance,
}

public sealed record AnnouncementMessage(
    [property: JsonPropertyOrder(0)] string Id,
    [property: JsonPropertyOrder(1)] int Revision,
    [property: JsonPropertyOrder(2)] string Title,
    [property: JsonPropertyOrder(3)] string Body,
    [property: JsonPropertyOrder(4)] AnnouncementKind Kind,
    [property: JsonPropertyOrder(5)] DateTimeOffset StartsAtUtc,
    [property: JsonPropertyOrder(6)] DateTimeOffset? EndsAtUtc,
    [property: JsonPropertyOrder(7)] string? MinimumVersion,
    [property: JsonPropertyOrder(8)] string? MaximumVersion);

public sealed record AnnouncementFeedPayload(
    [property: JsonPropertyOrder(0)] long Sequence,
    [property: JsonPropertyOrder(1)] DateTimeOffset IssuedAtUtc,
    [property: JsonPropertyOrder(2)] DateTimeOffset ExpiresAtUtc,
    [property: JsonPropertyOrder(3)] IReadOnlyList<AnnouncementMessage> Messages);

public sealed record SignedAnnouncementFeed(
    [property: JsonPropertyOrder(0)] int SchemaVersion,
    [property: JsonPropertyOrder(1)] AnnouncementFeedPayload Payload,
    [property: JsonPropertyOrder(2)] string SignatureAlgorithm,
    [property: JsonPropertyOrder(3)] string Signature)
{
    public const int CurrentSchemaVersion = 2;

    public static SignedAnnouncementFeed Create(
        AnnouncementFeedPayload payload,
        string privateKeyPem) =>
        new(
            CurrentSchemaVersion,
            payload,
            DistributionSignature.Algorithm,
            DistributionSignature.Sign(payload, privateKeyPem));

    public bool HasValidSignature(string publicKeyPem) =>
        SchemaVersion == CurrentSchemaVersion &&
        SignatureAlgorithm.Equals(DistributionSignature.Algorithm, StringComparison.Ordinal) &&
        DistributionSignature.Verify(Payload, Signature, publicKeyPem);
}

public static class AnnouncementSelector
{
    public static IReadOnlyList<AnnouncementMessage> SelectPending(
        SignedAnnouncementFeed feed,
        string publicKeyPem,
        SemanticVersion currentVersion,
        DateTimeOffset now,
        IReadOnlyDictionary<string, int> shownRevisions)
    {
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(shownRevisions);
        if (!feed.HasValidSignature(publicKeyPem))
        {
            return [];
        }

        if (feed.Payload.Sequence <= 0 ||
            feed.Payload.ExpiresAtUtc <= feed.Payload.IssuedAtUtc ||
            feed.Payload.IssuedAtUtc > now.AddMinutes(5) ||
            feed.Payload.ExpiresAtUtc <= now)
        {
            return [];
        }

        return feed.Payload.Messages
            .Where(message => IsActive(message, currentVersion, now) &&
                              (!shownRevisions.TryGetValue(message.Id, out var revision) ||
                               revision < message.Revision))
            .OrderBy(static message => message.StartsAtUtc)
            .ThenBy(static message => message.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsActive(
        AnnouncementMessage message,
        SemanticVersion currentVersion,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(message.Id) ||
            string.IsNullOrWhiteSpace(message.Title) ||
            string.IsNullOrWhiteSpace(message.Body) ||
            message.Revision < 1 ||
            message.StartsAtUtc > now ||
            message.EndsAtUtc is { } end && end <= now)
        {
            return false;
        }

        if (message.MinimumVersion is { } minimum &&
            (!SemanticVersion.TryParse(minimum, out var parsedMinimum) ||
             currentVersion < parsedMinimum))
        {
            return false;
        }

        return message.MaximumVersion is not { } maximum ||
               SemanticVersion.TryParse(maximum, out var parsedMaximum) &&
               currentVersion <= parsedMaximum;
    }
}
