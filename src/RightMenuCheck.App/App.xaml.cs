using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using RightMenuCheck.App.Services;
using RightMenuCheck.App.ViewModels;
using RightMenuCheck.Windows.Diagnostics;

namespace RightMenuCheck.App;

public partial class App : Application
{
    private IAppLogger? _logger;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
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
        var viewModel = new MainWindowViewModel(
            new ContextMenuDataService(_logger),
            new ContextMenuManagementService(_logger));
        MainWindow = new MainWindow(viewModel);
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
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

        base.OnExit(e);
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
}
