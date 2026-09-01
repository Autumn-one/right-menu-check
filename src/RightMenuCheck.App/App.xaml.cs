using System.IO;
using System.IO.Pipes;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using RightMenuCheck.App.Services;
using RightMenuCheck.App.ViewModels;
using RightMenuCheck.Distribution;
using RightMenuCheck.Windows.Diagnostics;
using RightMenuCheck.Windows.Security;

namespace RightMenuCheck.App;

public partial class App : Application, IDisposable
{
    private AnnouncementStateStore? _announcementStateStore;
    private HttpClient? _httpClient;
    private CancellationTokenSource? _lifetime;
    private IAppLogger? _logger;
    private AppTelemetryClient? _telemetryClient;
    private ApplicationUpdateCoordinator? _updateCoordinator;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _lifetime = new CancellationTokenSource();
        _logger = StructuredFileLogger.CreateDefault("app");
        var assembly = Assembly.GetExecutingAssembly();
        _logger.Log(
            AppLogLevel.Information,
            "app.started",
            "RightMenuCheck started.",
            new Dictionary<string, object?>
            {
                ["version"] = assembly.GetName().Version?.ToString(),
                ["informationalVersion"] = assembly
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                    .InformationalVersion,
                ["architecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
                ["osVersion"] = Environment.OSVersion.VersionString,
                ["executablePath"] = Environment.ProcessPath,
            });
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        try
        {
            await StartApplicationAsync(e.Args, _lifetime.Token);
        }
        catch (Exception exception) when (exception is
                                           ArgumentException or
                                           InvalidDataException or
                                           IOException or
                                           UnauthorizedAccessException or
                                           InvalidOperationException or
                                           HttpRequestException)
        {
            _logger.Log(
                AppLogLevel.Error,
                "app.startup_failed",
                "Application startup failed.",
                exception: exception);
            MessageBox.Show(
                $"RightMenuCheck 无法启动：{exception.Message}",
                "启动失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(exitCode: 1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _lifetime?.Cancel();
        _updateCoordinator?.Dispose();
        _updateCoordinator = null;
        if (_telemetryClient is not null)
        {
            try
            {
                _telemetryClient.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException)
            {
                _logger?.Log(
                    AppLogLevel.Warning,
                    "telemetry.stop_failed",
                    "Telemetry session did not report normal shutdown.",
                    exception: exception);
            }

            _telemetryClient.Dispose();
            _telemetryClient = null;
        }

        if (_logger is not null)
        {
            _logger.Log(
                AppLogLevel.Information,
                "app.stopped",
                "RightMenuCheck stopped.",
                new Dictionary<string, object?> { ["exitCode"] = e.ApplicationExitCode });
            try
            {
                _logger.FlushAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (IOException)
            {
            }

            _logger.Dispose();
        }

        Dispose();

        base.OnExit(e);
    }

    public void Dispose()
    {
        _updateCoordinator?.Dispose();
        _updateCoordinator = null;
        _telemetryClient?.Dispose();
        _telemetryClient = null;
        _announcementStateStore?.Dispose();
        _announcementStateStore = null;
        _httpClient?.Dispose();
        _httpClient = null;
        _lifetime?.Dispose();
        _lifetime = null;
        GC.SuppressFinalize(this);
    }

    private async Task StartApplicationAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ProcessElevationPolicy.ThrowIfElevated("RightMenuCheck startup");
        var startupArguments = ApplicationStartupArguments.Parse(arguments);
        var configuration = EmbeddedDistributionConfigurationLoader.Load();
        _httpClient = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"RightMenuCheck/{ApplicationVersionProvider.GetCurrent()}");
        var documentClient = new DistributionDocumentClient(_httpClient, _logger!);
        var updateService = new ApplicationUpdateService(
            configuration,
            documentClient,
            new SystemApplicationInstallContext(),
            new SystemUpdaterLauncher(),
            _logger!);
        _updateCoordinator = new ApplicationUpdateCoordinator(updateService, _logger!);
        while (true)
        {
            ApplicationUpdatePreparation preparation;
            try
            {
                preparation = await _updateCoordinator.CheckAndPrepareAsync(
                    downloadProgress: null,
                    cancellationToken);
            }
            catch (Exception exception) when (IsRecoverableUpdateFailure(exception))
            {
                var failedWindow = new VersionStatusWindow(
                    $"暂时无法准备更新：{exception.Message}");
                MainWindow = failedWindow;
                _ = failedWindow.ShowDialog();
                continue;
            }

            var updateCheck = preparation.Check;
            if (updateCheck.State == ApplicationUpdateState.Current)
            {
                break;
            }

            if (preparation.PreparedUpdate is { } preparedUpdate)
            {
                var updateWindow = new UpdateRequiredWindow(updateService, preparedUpdate);
                MainWindow = updateWindow;
                if (updateWindow.ShowDialog() == true)
                {
                    Shutdown();
                }

                return;
            }

            var statusWindow = new VersionStatusWindow(updateCheck.Message);
            MainWindow = statusWindow;
            _ = statusWindow.ShowDialog();
        }

        var viewModel = new MainWindowViewModel(
            new ContextMenuDataService(_logger!),
            new ContextMenuManagementService(_logger!));
        var mainWindow = new MainWindow(viewModel);
        if (startupArguments is
            {
                UpdateHealthPipeName: { } healthPipeName,
                UpdateHealthToken: { } healthToken,
            })
        {
            mainWindow.InitialScanCompleted += async (_, _) =>
            {
                try
                {
                    await ReportUpdateHealthAsync(
                        healthPipeName,
                        healthToken,
                        CancellationToken.None);
                }
                catch (Exception exception) when (exception is
                                                   IOException or
                                                   TimeoutException or
                                                   InvalidOperationException)
                {
                    _logger!.Log(
                        AppLogLevel.Error,
                        "update.health_report_failed",
                        "Updated application could not report healthy startup.",
                        exception: exception);
                }
            };
        }

        MainWindow = mainWindow;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        mainWindow.Show();
        _updateCoordinator.Start(
            (prepared, token) => PromptForUpdateAsync(
                updateService,
                mainWindow,
                prepared,
                token),
            cancellationToken);
        if (startupArguments.UpdateRolledBack)
        {
            _logger!.Log(
                AppLogLevel.Warning,
                "update.rollback_started",
                "Application restarted after an update rollback.");
        }

        var telemetryDiscovery = new TelemetryEndpointDiscoveryService(
            configuration,
            documentClient,
            _logger!);
        StartTelemetry(await telemetryDiscovery.ResolveAsync(cancellationToken));
        _announcementStateStore = new AnnouncementStateStore(_logger!);
        var announcementService = new ApplicationAnnouncementService(
            configuration,
            documentClient,
            _announcementStateStore,
            _logger!);
        await announcementService.ShowPendingAsync(mainWindow, cancellationToken);
    }

    private async Task<bool> PromptForUpdateAsync(
        IApplicationUpdateService updateService,
        Window owner,
        PreparedApplicationUpdate preparedUpdate,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var operation = Dispatcher.InvokeAsync(
            () =>
            {
                var updateWindow = new UpdateRequiredWindow(updateService, preparedUpdate)
                {
                    Owner = owner,
                };
                var accepted = updateWindow.ShowDialog() == true;
                if (accepted)
                {
                    _ = Dispatcher.BeginInvoke(Shutdown);
                }

                return accepted;
            },
            DispatcherPriority.Normal,
            cancellationToken);
        return await operation.Task.ConfigureAwait(false);
    }

    private void StartTelemetry(ResolvedTelemetryEndpoint? endpoint)
    {
        if (endpoint is null)
        {
            return;
        }

        var options = new TelemetryClientOptions(
            endpoint.BaseAddress,
            allowInsecureRemoteHttp: endpoint.AllowsInsecureHttp);
        _telemetryClient = new AppTelemetryClient(
            options,
            new MachineIdentityProvider(_logger!),
            _logger!);
        _ = _telemetryClient.StartAsync(CancellationToken.None);
    }

    private static async Task ReportUpdateHealthAsync(
        string pipeName,
        string healthToken,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(TimeSpan.FromSeconds(10));
        await using var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.Out,
            PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(timeoutSource.Token);
            var report = new UpdateHealthReport(
                healthToken,
                Environment.ProcessId,
                ApplicationVersionProvider.GetCurrent().ToString());
            await using var writer = new StreamWriter(pipe)
            {
                AutoFlush = true,
            };
            await writer.WriteLineAsync(
                DistributionJson.Serialize(report).AsMemory(),
                timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Update health pipe connection timed out.");
        }
    }

    private void App_DispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.Log(
            AppLogLevel.Error,
            "app.unhandled_exception",
            "Unhandled UI exception.",
            exception: e.Exception);
        e.Handled = false;
    }

    private static bool IsRecoverableUpdateFailure(Exception exception) => exception is
        HttpRequestException or
        IOException or
        InvalidDataException or
        InvalidOperationException or
        TimeoutException or
        UnauthorizedAccessException;
}
