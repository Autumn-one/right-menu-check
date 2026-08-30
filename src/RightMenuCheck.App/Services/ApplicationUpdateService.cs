using System.IO;
using System.Text;
using RightMenuCheck.Distribution;
using RightMenuCheck.Windows.Diagnostics;

namespace RightMenuCheck.App.Services;

public enum ApplicationUpdateState
{
    Current,
    Required,
    Unavailable,
}

public sealed record ApplicationUpdateCheck(
    ApplicationUpdateState State,
    SignedUpdateManifest? Manifest,
    UpdateDecision? Decision,
    string Message);

public interface IApplicationUpdateService
{
    Task<ApplicationUpdateCheck> CheckAsync(CancellationToken cancellationToken);

    Task PrepareAndLaunchAsync(
        SignedUpdateManifest manifest,
        IProgress<double>? progress,
        CancellationToken cancellationToken);
}

public sealed class ApplicationUpdateService : IApplicationUpdateService
{
    private readonly EmbeddedDistributionConfiguration _configuration;
    private readonly IDistributionDocumentClient _documentClient;
    private readonly IApplicationInstallContext _installContext;
    private readonly IAppLogger _logger;
    private readonly IUpdaterLauncher _updaterLauncher;

    public ApplicationUpdateService(
        EmbeddedDistributionConfiguration configuration,
        IDistributionDocumentClient documentClient,
        IApplicationInstallContext installContext,
        IUpdaterLauncher updaterLauncher,
        IAppLogger logger)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _documentClient = documentClient ?? throw new ArgumentNullException(nameof(documentClient));
        _installContext = installContext ?? throw new ArgumentNullException(nameof(installContext));
        _updaterLauncher = updaterLauncher ?? throw new ArgumentNullException(nameof(updaterLauncher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ApplicationUpdateCheck> CheckAsync(CancellationToken cancellationToken)
    {
        var manifest = await _documentClient.FetchVerifiedAsync<SignedUpdateManifest>(
                _configuration.Settings.GetUpdateManifestCandidates(),
                GetCachePath("update.json"),
                candidate => candidate.HasValidSignature(_configuration.PublicKeyPem),
                candidate => candidate.Payload.Sequence,
                cancellationToken)
            .ConfigureAwait(false);
        if (manifest is null)
        {
            _logger.Log(
                AppLogLevel.Warning,
                "update.check_unavailable",
                "No verified update manifest was available; version state cannot be confirmed.");
            return new ApplicationUpdateCheck(
                ApplicationUpdateState.Unavailable,
                Manifest: null,
                Decision: null,
                "无法取得经过验证的版本信息。");
        }

        var decision = UpdatePolicyEvaluator.Evaluate(
            ApplicationVersionProvider.GetCurrent(),
            manifest,
            _configuration.PublicKeyPem,
            DateTimeOffset.UtcNow);
        _logger.Log(
            decision.Kind == UpdateDecisionKind.InvalidManifest
                ? AppLogLevel.Error
                : AppLogLevel.Information,
            "update.check_completed",
            "Verified update check completed.",
            new Dictionary<string, object?>
            {
                ["decision"] = decision.Kind.ToString(),
                ["targetVersion"] = decision.TargetVersion?.ToString(),
            });
        var state = decision.Kind switch
        {
            UpdateDecisionKind.Current => ApplicationUpdateState.Current,
            UpdateDecisionKind.Required => ApplicationUpdateState.Required,
            _ => ApplicationUpdateState.Unavailable,
        };
        return new ApplicationUpdateCheck(state, manifest, decision, decision.Reason);
    }

    public async Task PrepareAndLaunchAsync(
        SignedUpdateManifest manifest,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var decision = UpdatePolicyEvaluator.Evaluate(
            ApplicationVersionProvider.GetCurrent(),
            manifest,
            _configuration.PublicKeyPem,
            DateTimeOffset.UtcNow);
        if (decision.Kind != UpdateDecisionKind.Required || decision.TargetVersion is null)
        {
            throw new InvalidOperationException("The signed manifest does not require an update.");
        }

        var versionRoot = Path.Combine(
            _installContext.UpdateRoot,
            decision.TargetVersion.Value.ToString());
        var packagePath = await _documentClient.DownloadPackageAsync(
                manifest.Payload.Package,
                Path.Combine(versionRoot, "download"),
                progress,
                cancellationToken)
            .ConfigureAwait(false);
        var updaterSource = Path.GetFullPath(_installContext.UpdaterSourcePath);
        if (!File.Exists(updaterSource))
        {
            throw new FileNotFoundException("Update helper is missing from this installation.", updaterSource);
        }

        var updaterDirectory = Path.Combine(versionRoot, "helper");
        Directory.CreateDirectory(updaterDirectory);
        var updaterPath = Path.Combine(updaterDirectory, "RightMenuCheck.Updater.exe");
        File.Copy(updaterSource, updaterPath, overwrite: true);
        var healthToken = Guid.NewGuid().ToString("N");
        using var readyEndpoint = UpdateReadyHandshake.Create();
        var applicationPath = Path.GetFullPath(_installContext.ApplicationPath);
        var installDirectory = Path.GetFullPath(_installContext.TargetInstallDirectory);
        var request = new UpdateInstallRequest(
            UpdateInstallRequest.CurrentSchemaVersion,
            _installContext.ProcessId,
            packagePath,
            applicationPath,
            installDirectory,
            manifest,
            healthToken,
            readyEndpoint.PipeName,
            readyEndpoint.Nonce);
        var requestPath = Path.Combine(updaterDirectory, $"request-{healthToken}.json");
        File.WriteAllText(
            requestPath,
            DistributionJson.Serialize(request, writeIndented: true),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        _updaterLauncher.Launch(updaterPath, requestPath);
        await UpdateReadyHandshake.WaitAsync(
                readyEndpoint,
                TimeSpan.FromSeconds(30),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static string GetCachePath(string fileName) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RightMenuCheck",
        "Distribution",
        fileName);

}
