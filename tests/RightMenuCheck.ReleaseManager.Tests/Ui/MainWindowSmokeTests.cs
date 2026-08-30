using System.Threading;
using System.Windows.Threading;
using RightMenuCheck.Distribution;
using RightMenuCheck.ReleaseManager.Announcements;
using RightMenuCheck.ReleaseManager.Configuration;
using RightMenuCheck.ReleaseManager.Publishing;
using RightMenuCheck.ReleaseManager.Services;
using RightMenuCheck.ReleaseManager.Tests.TestSupport;
using RightMenuCheck.ReleaseManager.ViewModels;

namespace RightMenuCheck.ReleaseManager.Tests.Ui;

public sealed class MainWindowSmokeTests
{
    [Fact]
    public void MainWindowLoadsRealWpfResourcesWithoutNetworkAccess()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var application = new App();
                application.InitializeComponent();
                var github = new FakeGitHubRepositoryClient();
                var configuration = new ReleaseManagerConfiguration(
                    RepositoryCoordinates.Parse("owner/repo"),
                    "unused-test-token",
                    "main",
                    ["https://mirror.example/"],
                    "unused-signing-key.pem");
                using var viewModel = new ReleaseManagerViewModel(
                    configuration,
                    github,
                    new ReleaseAdministrationService(github),
                    new ReleasePublishingService(
                        Path.GetTempPath(),
                        configuration,
                        github,
                        new NeverRunPublishScriptRunner(),
                        new ReleaseArtifactBuilder(),
                        new InMemorySigningKeyProvider("unused-test-key")),
                    new AnnouncementManagementService(
                        configuration,
                        github,
                        new InMemorySigningKeyProvider("unused-test-key")));
                var window = new MainWindow(viewModel);

                Assert.Same(viewModel, window.DataContext);
                Assert.Equal("RightMenuCheck 发布管理器", window.Title);

                window.Close();
                application.Shutdown();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        })
        {
            IsBackground = true,
            Name = "RightMenuCheck.ReleaseManager.WpfSmoke",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "WPF smoke test did not finish.");
        Assert.Null(failure);
    }

    private sealed class NeverRunPublishScriptRunner : IPublishScriptRunner
    {
        public bool SupportsVersionArgument => false;

        public Task<PublishScriptResult> RunAsync(
            PublishScriptRequest request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The WPF construction smoke test must not publish.");
    }
}
