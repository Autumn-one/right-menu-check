using System.Security.Cryptography;
using System.Text;
using RightMenuCheck.Distribution;
using RightMenuCheck.ReleaseManager.Announcements;
using RightMenuCheck.ReleaseManager.Configuration;
using RightMenuCheck.ReleaseManager.GitHub;
using RightMenuCheck.ReleaseManager.Services;
using RightMenuCheck.ReleaseManager.Tests.TestSupport;

namespace RightMenuCheck.ReleaseManager.Tests.Announcements;

public sealed class AnnouncementManagementServiceTests
{
    [Fact]
    public async Task AddsRevisesAndWithdrawsSignedAnnouncementsWithShaConcurrency()
    {
        var keys = CreateKeys();
        var github = new FakeGitHubRepositoryClient();
        var now = DateTimeOffset.Parse("2026-08-31T03:00:00Z", CultureInfo.InvariantCulture);
        using var service = CreateService(github, keys.PrivateKey, now);
        var original = new AnnouncementEditorInput(
            "maintenance-2026-08",
            "维护通知",
            "服务将在今晚维护。",
            AnnouncementKind.Maintenance,
            now.AddMinutes(-5),
            now.AddHours(1),
            "0.1.0",
            null);

        var added = await service.AddAsync(original, CancellationToken.None);
        var addedMessage = Assert.Single(added.Feed.Payload.Messages);
        var revised = await service.ReviseAsync(
            addedMessage.Id,
            addedMessage.Revision,
            original with { Body = "维护时间已调整。" },
            CancellationToken.None);
        var revisedMessage = Assert.Single(revised.Feed.Payload.Messages);
        var withdrawn = await service.WithdrawAsync(
            revisedMessage.Id,
            revisedMessage.Revision,
            CancellationToken.None);

        Assert.Equal(1, addedMessage.Revision);
        Assert.Equal(2, revisedMessage.Revision);
        Assert.Equal("维护时间已调整。", revisedMessage.Body);
        Assert.Empty(withdrawn.Feed.Payload.Messages);
        Assert.Equal(1, added.Feed.Payload.Sequence);
        Assert.Equal(2, revised.Feed.Payload.Sequence);
        Assert.Equal(3, withdrawn.Feed.Payload.Sequence);
        Assert.Equal(now, added.Feed.Payload.IssuedAtUtc);
        Assert.Equal(now.AddDays(365), added.Feed.Payload.ExpiresAtUtc);
        Assert.True(added.Feed.HasValidSignature(keys.PublicKey));
        Assert.True(revised.Feed.HasValidSignature(keys.PublicKey));
        Assert.True(withdrawn.Feed.HasValidSignature(keys.PublicKey));
        Assert.Collection(
            github.PutRequests,
            request => Assert.Null(request.ExistingSha),
            request => Assert.Equal("sha-1", request.ExistingSha),
            request => Assert.Equal("sha-2", request.ExistingSha));
        Assert.All(github.PutRequests, request =>
        {
            Assert.Equal("distribution/messages.json", request.Path);
            Assert.Equal("main", request.Branch);
        });
    }

    [Fact]
    public async Task InvalidRemoteSignatureStopsBeforeOverwrite()
    {
        var keys = CreateKeys();
        var otherKeys = CreateKeys();
        var now = DateTimeOffset.Parse("2026-08-31T03:00:00Z", CultureInfo.InvariantCulture);
        var invalidFeed = SignedAnnouncementFeed.Create(
            new AnnouncementFeedPayload(
                Sequence: 4,
                IssuedAtUtc: now.AddMinutes(-1),
                ExpiresAtUtc: now.AddDays(365),
                Messages: []),
            otherKeys.PrivateKey);
        var github = new FakeGitHubRepositoryClient
        {
            RepositoryFile = new GitHubRepositoryFile(
                AnnouncementManagementService.AnnouncementPath,
                "untrusted-sha",
                Encoding.UTF8.GetBytes(DistributionJson.Serialize(invalidFeed))),
        };
        using var service = CreateService(github, keys.PrivateKey, now);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.LoadAsync(CancellationToken.None));

        Assert.Contains("签名无效", exception.Message, StringComparison.Ordinal);
        Assert.Empty(github.PutRequests);
    }

    [Fact]
    public async Task StaleRevisionCannotOverwriteCurrentAnnouncement()
    {
        var keys = CreateKeys();
        var now = DateTimeOffset.Parse("2026-08-31T03:00:00Z", CultureInfo.InvariantCulture);
        var message = new AnnouncementMessage(
            "notice",
            3,
            "Title",
            "Body",
            AnnouncementKind.Information,
            now,
            null,
            null,
            null);
        var feed = SignedAnnouncementFeed.Create(
            new AnnouncementFeedPayload(
                Sequence: 9,
                IssuedAtUtc: now.AddMinutes(-1),
                ExpiresAtUtc: now.AddDays(365),
                Messages: [message]),
            keys.PrivateKey);
        var github = new FakeGitHubRepositoryClient
        {
            RepositoryFile = new GitHubRepositoryFile(
                AnnouncementManagementService.AnnouncementPath,
                "sha-current",
                Encoding.UTF8.GetBytes(DistributionJson.Serialize(feed))),
        };
        using var service = CreateService(github, keys.PrivateKey, now);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ReviseAsync(
                "notice",
                expectedRevision: 2,
                new AnnouncementEditorInput(
                    "notice",
                    "Changed",
                    "Changed body",
                    AnnouncementKind.Information,
                    now,
                    null,
                    null,
                    null),
                CancellationToken.None));

        Assert.Empty(github.PutRequests);
    }

    private static AnnouncementManagementService CreateService(
        FakeGitHubRepositoryClient github,
        string privateKey,
        DateTimeOffset now) => new(
        new ReleaseManagerConfiguration(
            RepositoryCoordinates.Parse("owner/repo"),
            "secret",
            "main",
            ["https://mirror.example/"],
            "unused.pem"),
        github,
        new InMemorySigningKeyProvider(privateKey),
        new FixedTimeProvider(now));

    private static SigningKeys CreateKeys()
    {
        using var algorithm = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new SigningKeys(
            algorithm.ExportPkcs8PrivateKeyPem(),
            algorithm.ExportSubjectPublicKeyInfoPem());
    }

    private sealed record SigningKeys(string PrivateKey, string PublicKey);
}
