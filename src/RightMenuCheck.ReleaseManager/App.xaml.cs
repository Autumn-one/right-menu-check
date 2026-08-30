using System.Windows;
using System.Windows.Markup;
using RightMenuCheck.ReleaseManager.Announcements;
using RightMenuCheck.ReleaseManager.Configuration;
using RightMenuCheck.ReleaseManager.GitHub;
using RightMenuCheck.ReleaseManager.Publishing;
using RightMenuCheck.ReleaseManager.Services;
using RightMenuCheck.ReleaseManager.ViewModels;

namespace RightMenuCheck.ReleaseManager;

public partial class App : Application, IDisposable
{
    private HttpClient? _httpClient;
    private ReleaseManagerViewModel? _viewModel;

    private void OnStartup(object sender, StartupEventArgs e)
    {
        try
        {
            var repositoryRoot = RepositoryRootLocator.Find();
            var configuration = ReleaseManagerConfiguration.Load(repositoryRoot);
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(10),
            };
            var github = new GitHubRepositoryClient(
                _httpClient,
                configuration.Repository,
                configuration.AccessToken);
            var keyProvider = new FileDistributionSigningKeyProvider(
                configuration.SigningPrivateKeyPath,
                Path.Combine(repositoryRoot, "distribution", "update-public-key.pem"));
            _ = keyProvider.ReadPrivateKey();
            _viewModel = new ReleaseManagerViewModel(
                configuration,
                github,
                new ReleaseAdministrationService(github),
                new ReleasePublishingService(
                    repositoryRoot,
                    configuration,
                    github,
                    new PowerShellPublishScriptRunner(),
                    new ReleaseArtifactBuilder(),
                    keyProvider),
                new AnnouncementManagementService(configuration, github, keyProvider));
            MainWindow = new MainWindow(_viewModel);
            MainWindow.Show();
        }
        catch (Exception exception) when (exception is
               FileNotFoundException or
               DirectoryNotFoundException or
               InvalidDataException or
               InvalidOperationException or
               XamlParseException or
               ArgumentException)
        {
            MessageBox.Show(
                exception.Message,
                "RightMenuCheck 发布管理器",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void OnExit(object sender, ExitEventArgs e)
    {
        Dispose();
    }

    public void Dispose()
    {
        _viewModel?.Dispose();
        _viewModel = null;
        _httpClient?.Dispose();
        _httpClient = null;
        GC.SuppressFinalize(this);
    }
}
