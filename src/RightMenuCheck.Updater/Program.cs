using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using RightMenuCheck.Distribution;
using RightMenuCheck.Windows.Diagnostics;
using RightMenuCheck.Windows.Security;

namespace RightMenuCheck.Updater;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        using var logger = StructuredFileLogger.CreateDefault("updater");
        var application = new Application
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown,
        };
        var window = new UpdateProgressWindow();
        var exitCode = 1;
        application.Startup += async (_, _) =>
        {
            window.Show();
            var outcome = await ExecuteAsync(
                args,
                logger,
                new UpdateProgressObserver(window));
            exitCode = outcome.ExitCode;
            if (outcome.Succeeded)
            {
                window.ShowCompleted();
                await Task.Delay(TimeSpan.FromMilliseconds(600));
            }
            else
            {
                window.ShowFailure(outcome.DisplayMessage);
                await Task.Delay(TimeSpan.FromSeconds(2));
            }

            try
            {
                await logger.FlushAsync(CancellationToken.None);
            }
            catch (IOException)
            {
            }

            window.AllowClose();
            window.Close();
            application.Shutdown(exitCode);
        };
        _ = application.Run();
        return exitCode;
    }

    private static async Task<UpdaterExecutionOutcome> ExecuteAsync(
        string[] args,
        IAppLogger logger,
        IUpdateTransactionObserver observer)
    {
        try
        {
            ProcessElevationPolicy.ThrowIfElevated("Updater execution");
            var requestPath = ParseRequestPath(args);
            var request = DistributionJson.Deserialize<UpdateInstallRequest>(
                await File.ReadAllTextAsync(requestPath).ConfigureAwait(false));
            var installer = new UpdateInstaller(
                new SafeZipExtractor(),
                new SystemUpdateProcessController(),
                new NamedPipeUpdateHealthMonitor(),
                new SystemUpdateTargetPolicy(),
                new NamedPipeUpdateReadySignal(),
                EmbeddedDistributionPublicKey.Load(),
                logger,
                observer);
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
            TryDeleteRequest(requestPath);
            return new UpdaterExecutionOutcome(
                result.Succeeded ? 0 : result.RolledBack ? 2 : 1,
                result.Succeeded,
                result.RolledBack
                    ? "新版本未通过启动验证，原版本已经恢复。"
                    : "更新没有完成，当前版本不会被替换。");
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
            return new UpdaterExecutionOutcome(
                ExitCode: 1,
                Succeeded: false,
                "更新程序未能完成准备，当前版本不会被替换。");
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

    private sealed record UpdaterExecutionOutcome(
        int ExitCode,
        bool Succeeded,
        string DisplayMessage);
}
