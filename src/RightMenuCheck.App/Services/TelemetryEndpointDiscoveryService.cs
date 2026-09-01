using System.IO;
using RightMenuCheck.Distribution;
using RightMenuCheck.Windows.Diagnostics;

namespace RightMenuCheck.App.Services;

public sealed record ResolvedTelemetryEndpoint(
    Uri BaseAddress,
    bool AllowsInsecureHttp,
    string ResolutionKind);

public interface ITelemetryEndpointDiscoveryService
{
    Task<ResolvedTelemetryEndpoint?> ResolveAsync(CancellationToken cancellationToken);
}

public sealed class TelemetryEndpointDiscoveryService : ITelemetryEndpointDiscoveryService
{
    private const string OverrideEnvironmentVariable = "RIGHTMENUCHECK_TELEMETRY_URL";
    private readonly EmbeddedDistributionConfiguration _configuration;
    private readonly IDistributionDocumentClient _documentClient;
    private readonly IAppLogger _logger;
    private readonly Func<string?> _overrideProvider;

    public TelemetryEndpointDiscoveryService(
        EmbeddedDistributionConfiguration configuration,
        IDistributionDocumentClient documentClient,
        IAppLogger logger,
        Func<string?>? overrideProvider = null)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _documentClient = documentClient ?? throw new ArgumentNullException(nameof(documentClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _overrideProvider = overrideProvider ??
                            (() => Environment.GetEnvironmentVariable(OverrideEnvironmentVariable));
    }

    public async Task<ResolvedTelemetryEndpoint?> ResolveAsync(
        CancellationToken cancellationToken)
    {
        var overrideUrl = _overrideProvider();
        if (!string.IsNullOrWhiteSpace(overrideUrl))
        {
            if (Uri.TryCreate(overrideUrl, UriKind.Absolute, out var overrideUri) &&
                overrideUri.IsLoopback &&
                overrideUri.AbsoluteUri.EndsWith('/'))
            {
                _logger.Log(
                    AppLogLevel.Information,
                    "telemetry.loopback_override_enabled",
                    "A loopback telemetry endpoint override is enabled for integration testing.");
                return new ResolvedTelemetryEndpoint(
                    overrideUri,
                    AllowsInsecureHttp: false,
                    ResolutionKind: "LoopbackOverride");
            }

            _logger.Log(
                AppLogLevel.Warning,
                "telemetry.override_rejected",
                "A non-loopback telemetry endpoint override was rejected.");
        }

        if (!string.IsNullOrWhiteSpace(_configuration.Settings.TelemetryBaseUrl))
        {
            return new ResolvedTelemetryEndpoint(
                new Uri(_configuration.Settings.TelemetryBaseUrl, UriKind.Absolute),
                AllowsInsecureHttp: false,
                ResolutionKind: "Embedded");
        }

        var discovery = _configuration.Settings.TelemetryDiscovery;
        if (discovery is null)
        {
            return null;
        }

        var document = await _documentClient.FetchVerifiedAsync<SignedTelemetryEndpoint>(
                _configuration.Settings.GetTelemetryEndpointCandidates(),
                GetCachePath(),
                candidate => candidate.HasValidSignature(_configuration.PublicKeyPem),
                candidate => candidate.Payload.Sequence,
                cancellationToken)
            .ConfigureAwait(false);
        if (document is null)
        {
            _logger.Log(
                AppLogLevel.Warning,
                "telemetry.discovery_unavailable",
                "No verified telemetry endpoint document was available.");
            return null;
        }

        var decision = TelemetryEndpointPolicy.Evaluate(
            document,
            _configuration.PublicKeyPem,
            discovery.ProductId,
            DateTimeOffset.UtcNow);
        if (decision is not
            {
                Kind: TelemetryEndpointDecisionKind.Available,
                BaseAddress: { } baseAddress,
            })
        {
            _logger.Log(
                AppLogLevel.Warning,
                "telemetry.discovery_rejected",
                "The signed telemetry endpoint document was not usable.",
                new Dictionary<string, object?>
                {
                    ["decision"] = decision.Kind.ToString(),
                    ["sequence"] = document.Payload.Sequence,
                });
            return null;
        }

        _logger.Log(
            decision.AllowsInsecureHttp ? AppLogLevel.Warning : AppLogLevel.Information,
            "telemetry.discovery_completed",
            "A signed telemetry endpoint was resolved.",
            new Dictionary<string, object?>
            {
                ["sequence"] = document.Payload.Sequence,
                ["insecureHttp"] = decision.AllowsInsecureHttp,
            });
        return new ResolvedTelemetryEndpoint(
            baseAddress,
            decision.AllowsInsecureHttp,
            ResolutionKind: "SignedDiscovery");
    }

    private static string GetCachePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RightMenuCheck",
        "Distribution",
        "telemetry-endpoint.json");
}
