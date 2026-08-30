using System.IO;
using System.Windows;
using RightMenuCheck.Distribution;
using RightMenuCheck.Windows.Diagnostics;

namespace RightMenuCheck.App.Services;

public interface IApplicationAnnouncementService
{
    Task ShowPendingAsync(Window owner, CancellationToken cancellationToken);
}

public sealed class ApplicationAnnouncementService : IApplicationAnnouncementService
{
    private readonly EmbeddedDistributionConfiguration _configuration;
    private readonly IDistributionDocumentClient _documentClient;
    private readonly IAppLogger _logger;
    private readonly IAnnouncementStateStore _stateStore;

    public ApplicationAnnouncementService(
        EmbeddedDistributionConfiguration configuration,
        IDistributionDocumentClient documentClient,
        IAnnouncementStateStore stateStore,
        IAppLogger logger)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _documentClient = documentClient ?? throw new ArgumentNullException(nameof(documentClient));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task ShowPendingAsync(Window owner, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var feed = await _documentClient.FetchVerifiedAsync<SignedAnnouncementFeed>(
            _configuration.Settings.GetAnnouncementCandidates(),
            GetCachePath(),
            candidate => candidate.HasValidSignature(_configuration.PublicKeyPem),
            candidate => candidate.Payload.Sequence,
            cancellationToken);
        if (feed is null)
        {
            _logger.Log(
                AppLogLevel.Information,
                "announcement.feed_unavailable",
                "No verified announcement feed was available.");
            return;
        }

        var shown = await _stateStore.ReadAsync(cancellationToken);
        var pending = AnnouncementSelector.SelectPending(
            feed,
            _configuration.PublicKeyPem,
            ApplicationVersionProvider.GetCurrent(),
            DateTimeOffset.UtcNow,
            shown);
        _logger.Log(
            AppLogLevel.Information,
            "announcement.selection_completed",
            "Signed announcement selection completed.",
            new Dictionary<string, object?>
            {
                ["feedSequence"] = feed.Payload.Sequence,
                ["knownRevisionCount"] = shown.Count,
                ["pendingCount"] = pending.Count,
            });
        foreach (var message in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _logger.Log(
                AppLogLevel.Information,
                "announcement.window_opening",
                "A signed announcement window is opening.",
                new Dictionary<string, object?>
                {
                    ["messageId"] = message.Id,
                    ["revision"] = message.Revision,
                });
            var window = new AnnouncementWindow(message)
            {
                Owner = owner,
            };
            if (window.ShowDialog() == true)
            {
                await _stateStore.MarkShownAsync(
                    message.Id,
                    message.Revision,
                    cancellationToken);
                _logger.Log(
                    AppLogLevel.Information,
                    "announcement.shown",
                    "Signed announcement was acknowledged.",
                    new Dictionary<string, object?>
                    {
                        ["messageId"] = message.Id,
                        ["revision"] = message.Revision,
                    });
            }
            else
            {
                _logger.Log(
                    AppLogLevel.Information,
                    "announcement.dismissed_without_acknowledgement",
                    "A signed announcement window closed without acknowledgement.",
                    new Dictionary<string, object?>
                    {
                        ["messageId"] = message.Id,
                        ["revision"] = message.Revision,
                    });
            }
        }
    }

    private static string GetCachePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RightMenuCheck",
        "Distribution",
        "messages.json");
}
