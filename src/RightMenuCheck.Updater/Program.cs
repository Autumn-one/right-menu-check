using RightMenuCheck.Distribution;
using RightMenuCheck.Windows.Diagnostics;
using System.ComponentModel;
using System.Text.Json;

namespace RightMenuCheck.Updater;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var logger = StructuredFileLogger.CreateDefault("updater");
        try
        {
            var requestPath = ParseRequestPath(args);
            var request = DistributionJson.Deserialize<UpdateInstallRequest>(
                await File.ReadAllTextAsync(requestPath).ConfigureAwait(false));
            var installer = new UpdateInstaller(
                new SafeZipExtractor(),
                new SystemUpdateProcessController(),
                new FileUpdateHealthMonitor(),
                EmbeddedDistributionPublicKey.Load(),
                logger);
            var result = await installer
                .InstallAsync(request, CancellationToken.None)
                .ConfigureAwait(false);
            logger.Log(
                result.Succeeded ? AppLogLevel.Information : AppLogLevel.Error,
                result.Succeeded ? "update.completed" : "update.failed",
                result.Message,
                new Dictionary<string, object?>
                {
                    ["expectedVersion"] = request.Manifest.Payload.Version,
                    ["rolledBack"] = result.RolledBack,
                    ["errorType"] = result.ErrorType,
                });
            await logger.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            TryDeleteRequest(requestPath);
            return result.Succeeded ? 0 : result.RolledBack ? 2 : 1;
        }
        catch (Exception exception) when (exception is
                                           ArgumentException or
                                           InvalidDataException or
                                           IOException or
                                           UnauthorizedAccessException or
                                           InvalidOperationException or
                                           Win32Exception or
                                           JsonException)
        {
            logger.Log(
                AppLogLevel.Error,
                "update.unhandled_failure",
                "Updater failed before installation completed.",
                exception: exception);
            await logger.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            return 1;
        }
    }

    private static string ParseRequestPath(string[] args)
    {
        if (args.Length != 2 ||
            !args[0].Equals("--request", StringComparison.Ordinal) ||
            !Path.IsPathFullyQualified(args[1]) ||
            !File.Exists(args[1]))
        {
            throw new ArgumentException("Updater requires an existing absolute --request JSON path.");
        }

        return Path.GetFullPath(args[1]);
    }

    private static void TryDeleteRequest(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
