using RightMenuCheck.Core.Inventory;
using RightMenuCheck.Core.Metadata;
using RightMenuCheck.Windows.Management;

namespace RightMenuCheck.IntegrationTests.Management;

public sealed class SystemUninstallLauncherTests
{
    [Fact]
    public async Task VendorPlanLaunchesDirectExecutableWithStructuredArguments()
    {
        var executable = GetFixtureExecutable();
        var markerPath = Path.Combine(
            Path.GetTempPath(),
            $"RightMenuCheck-Uninstall-{Guid.NewGuid():N}.txt");
        var owner = new ApplicationOwnerMetadata(
            ApplicationOwnerKind.InstalledApplication,
            OwnershipConfidence.High,
            "Synthetic Uninstaller",
            "RightMenuCheck Tests",
            "1.0.0.0",
            Path.GetDirectoryName(executable),
            ProductCode: null,
            PackageFullName: null,
            new RegistrySource(
                RegistryHiveKind.CurrentUser,
                RegistryViewKind.Registry64,
                "Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\RightMenuCheck.Uninstall.Fixture"),
            "RightMenuCheck.Uninstall.Fixture",
            $"\"{executable}\" --marker \"{markerPath}\"",
            QuietUninstallString: null,
            IsWindowsInstaller: false,
            IsSystemProtected: false,
            "Synthetic fixture");
        var plan = new ApplicationUninstallPlanner().CreatePlan(
            owner,
            allowSystemProtected: false);
        var service = new ApplicationUninstallService(
            new UnexpectedPackageUninstaller(),
            new SystemProcessUninstallLauncher());

        try
        {
            var result = await service.ExecuteAsync(plan, CancellationToken.None);

            Assert.True(plan.IsSupported, plan.BlockReason);
            Assert.Equal(ApplicationUninstallMethod.VendorExecutable, plan.Method);
            Assert.True(result.Started);
            Assert.True(result.Completed);
            Assert.Equal(0, result.ExitCode);
            Assert.Equal("uninstall-completed", File.ReadAllText(markerPath));
        }
        finally
        {
            File.Delete(markerPath);
        }
    }

    private static string GetFixtureExecutable()
    {
        var output = new DirectoryInfo(AppContext.BaseDirectory);
        var binRoot = output.Parent?.Parent ??
                      throw new DirectoryNotFoundException("The shared artifacts/bin directory was not found.");
        return Path.Combine(
            binRoot.FullName,
            "RightMenuCheck.Uninstall.Fixture",
            "debug",
            "RightMenuCheck.Uninstall.Fixture.exe");
    }

    private sealed class UnexpectedPackageUninstaller : IPackageUninstaller
    {
        public Task<PackageUninstallResult> RemoveCurrentUserPackageAsync(
            string packageFullName,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Package uninstaller must not be used for vendor plans.");
    }
}
