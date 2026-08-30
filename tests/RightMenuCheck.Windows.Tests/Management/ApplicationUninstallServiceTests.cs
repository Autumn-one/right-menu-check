using RightMenuCheck.Core.Inventory;
using RightMenuCheck.Core.Metadata;
using RightMenuCheck.Windows.Management;

namespace RightMenuCheck.Windows.Tests.Management;

public sealed class ApplicationUninstallServiceTests
{
    [Fact]
    public void PlannerCreatesExactPackageAndMsiPlans()
    {
        var planner = new ApplicationUninstallPlanner();
        var package = planner.CreatePlan(CreatePackageOwner(), allowSystemProtected: false);
        var msi = planner.CreatePlan(CreateMsiOwner(), allowSystemProtected: false);

        Assert.True(package.IsSupported);
        Assert.Equal(ApplicationUninstallMethod.PackageCurrentUser, package.Method);
        Assert.Equal(CreatePackageOwner().PackageFullName, package.PackageFullName);
        Assert.Empty(package.Arguments);
        Assert.True(msi.IsSupported);
        Assert.Equal(ApplicationUninstallMethod.MsiProductCode, msi.Method);
        Assert.EndsWith("msiexec.exe", msi.ExecutablePath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["/x", "{11111111-2222-3333-4444-555555555555}"], msi.Arguments);
        Assert.True(msi.RequiresElevation);
    }

    [Fact]
    public void PlannerParsesDirectVendorExecutableIntoStructuredArguments()
    {
        var executable = Environment.ProcessPath ??
                         throw new InvalidOperationException("Test process path is unavailable.");
        var owner = CreateInstalledOwner(
            OwnershipConfidence.High,
            Path.GetDirectoryName(executable),
            $"\"{executable}\" --uninstall --scope user");
        var planner = new ApplicationUninstallPlanner();

        var plan = planner.CreatePlan(owner, allowSystemProtected: false);

        Assert.True(plan.IsSupported, plan.BlockReason);
        Assert.Equal(ApplicationUninstallMethod.VendorExecutable, plan.Method);
        Assert.Equal(Path.GetFullPath(executable), plan.ExecutablePath);
        Assert.Equal(["--uninstall", "--scope", "user"], plan.Arguments);
        Assert.False(plan.RequiresElevation);
    }

    [Fact]
    public void PlannerRejectsForbiddenIntermediaryLowConfidenceAndProtectedOwner()
    {
        var commandPath = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var forbidden = CreateInstalledOwner(
            OwnershipConfidence.High,
            Environment.SystemDirectory,
            $"\"{commandPath}\" /c uninstall.exe");
        var lowConfidence = CreateInstalledOwner(
            OwnershipConfidence.Low,
            Path.GetDirectoryName(Environment.ProcessPath),
            $"\"{Environment.ProcessPath}\" --uninstall");
        var protectedOwner = CreateMsiOwner() with { IsSystemProtected = true };
        var planner = new ApplicationUninstallPlanner();

        var forbiddenPlan = planner.CreatePlan(forbidden, allowSystemProtected: false);
        var lowPlan = planner.CreatePlan(lowConfidence, allowSystemProtected: false);
        var protectedPlan = planner.CreatePlan(protectedOwner, allowSystemProtected: false);

        Assert.False(forbiddenPlan.IsSupported);
        Assert.Contains("forbidden", forbiddenPlan.BlockReason, StringComparison.OrdinalIgnoreCase);
        Assert.False(lowPlan.IsSupported);
        Assert.Contains("high-confidence", lowPlan.BlockReason, StringComparison.OrdinalIgnoreCase);
        Assert.False(protectedPlan.IsSupported);
        Assert.Contains("protected", protectedPlan.BlockReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ServiceDispatchesPackageAndProcessPlansWithoutShellCommandStrings()
    {
        var packageUninstaller = new FakePackageUninstaller();
        var processLauncher = new FakeProcessLauncher();
        var service = new ApplicationUninstallService(packageUninstaller, processLauncher);
        var planner = new ApplicationUninstallPlanner();

        var packageResult = await service.ExecuteAsync(
            planner.CreatePlan(CreatePackageOwner(), allowSystemProtected: false),
            CancellationToken.None);
        var msiResult = await service.ExecuteAsync(
            planner.CreatePlan(CreateMsiOwner(), allowSystemProtected: false),
            CancellationToken.None);

        Assert.True(packageResult.Completed);
        Assert.Equal(CreatePackageOwner().PackageFullName, packageUninstaller.LastPackageFullName);
        Assert.True(msiResult.Completed);
        Assert.EndsWith("msiexec.exe", processLauncher.LastExecutable, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["/x", "{11111111-2222-3333-4444-555555555555}"], processLauncher.LastArguments);
        Assert.True(processLauncher.LastRequestedElevation);
    }

    [Fact]
    public void ResidualDetectorUsesExactPackageOrProductIdentity()
    {
        var packageOwner = CreatePackageOwner();
        var productOwner = CreateMsiOwner();
        var registrations = new[]
        {
            CreateRegistration(packageOwner),
            CreateRegistration(productOwner),
            CreateRegistration(CreateInstalledOwner(
                OwnershipConfidence.High,
                "C:\\Other",
                "C:\\Other\\uninstall.exe")),
        };

        var packageResiduals = UninstallResidualDetector.FindResiduals(
            packageOwner,
            registrations);
        var productResiduals = UninstallResidualDetector.FindResiduals(
            productOwner,
            registrations);

        Assert.Single(packageResiduals);
        Assert.Equal(packageOwner.PackageFullName, packageResiduals[0].Owner?.PackageFullName);
        Assert.Single(productResiduals);
        Assert.Equal(productOwner.ProductCode, productResiduals[0].Owner?.ProductCode);
    }

    private static ApplicationOwnerMetadata CreatePackageOwner() => new(
        ApplicationOwnerKind.Package,
        OwnershipConfidence.Exact,
        "Sample Package",
        "Sample Publisher",
        "1.0.0.0",
        "C:\\Packages\\Sample",
        ProductCode: null,
        "Sample.Package_1.0.0.0_x64__publisher",
        UninstallRegistrySource: null,
        UninstallKeyName: null,
        UninstallString: null,
        QuietUninstallString: null,
        IsWindowsInstaller: false,
        IsSystemProtected: false,
        "test");

    private static ApplicationOwnerMetadata CreateMsiOwner() => new(
        ApplicationOwnerKind.InstalledApplication,
        OwnershipConfidence.High,
        "Sample MSI",
        "Sample Publisher",
        "1.0.0.0",
        "C:\\Program Files\\Sample",
        "{11111111-2222-3333-4444-555555555555}",
        PackageFullName: null,
        new RegistrySource(
            RegistryHiveKind.LocalMachine,
            RegistryViewKind.Registry64,
            "Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\{11111111-2222-3333-4444-555555555555}"),
        "{11111111-2222-3333-4444-555555555555}",
        "msiexec.exe /x {11111111-2222-3333-4444-555555555555}",
        QuietUninstallString: null,
        IsWindowsInstaller: true,
        IsSystemProtected: false,
        "test");

    private static ApplicationOwnerMetadata CreateInstalledOwner(
        OwnershipConfidence confidence,
        string? installLocation,
        string? uninstallString) =>
        new(
            ApplicationOwnerKind.InstalledApplication,
            confidence,
            "Vendor App",
            "Vendor",
            "1.0",
            installLocation,
            ProductCode: null,
            PackageFullName: null,
            new RegistrySource(
                RegistryHiveKind.CurrentUser,
                RegistryViewKind.Registry64,
                "Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\Vendor"),
            "Vendor",
            uninstallString,
            QuietUninstallString: null,
            IsWindowsInstaller: false,
            IsSystemProtected: false,
            "test");

    private static ContextMenuRegistrationMetadata CreateRegistration(
        ApplicationOwnerMetadata owner)
    {
        var registration = new ContextMenuRegistration
        {
            Id = Guid.NewGuid().ToString("N"),
            Source = new RegistryContextMenuSource(new RegistrySource(
                RegistryHiveKind.CurrentUser,
                RegistryViewKind.Registry64,
                "Software\\Classes\\*\\shell\\test")),
            ClassPath = "*",
            RegistrationPath = "Software\\Classes\\*\\shell\\test",
            CanonicalName = "test",
            DisplayName = "Test",
            TargetKind = ContextMenuTargetKind.File,
            Kind = ContextMenuRegistrationKind.StaticVerb,
        };
        return new ContextMenuRegistrationMetadata(
            registration,
            Components: [],
            owner,
            Issues: []);
    }

    private sealed class FakePackageUninstaller : IPackageUninstaller
    {
        public string? LastPackageFullName { get; private set; }

        public Task<PackageUninstallResult> RemoveCurrentUserPackageAsync(
            string packageFullName,
            CancellationToken cancellationToken)
        {
            LastPackageFullName = packageFullName;
            return Task.FromResult(new PackageUninstallResult(
                Succeeded: true,
                ErrorType: null,
                ErrorMessage: null));
        }
    }

    private sealed class FakeProcessLauncher : IProcessUninstallLauncher
    {
        public string? LastExecutable { get; private set; }

        public IReadOnlyList<string>? LastArguments { get; private set; }

        public bool LastRequestedElevation { get; private set; }

        public Task<ProcessUninstallResult> LaunchAsync(
            string executablePath,
            IReadOnlyList<string> arguments,
            bool requestElevation,
            CancellationToken cancellationToken)
        {
            LastExecutable = executablePath;
            LastArguments = arguments;
            LastRequestedElevation = requestElevation;
            return Task.FromResult(new ProcessUninstallResult(Started: true, ExitCode: 0));
        }
    }
}
