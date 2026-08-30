using RightMenuCheck.Core.Inventory;
using RightMenuCheck.Core.Metadata;
using RightMenuCheck.Windows.Backup;
using RightMenuCheck.Windows.Management;
using RightMenuCheck.Windows.Tests.Backup;
using RightMenuCheck.Windows.Tests.Registry;

namespace RightMenuCheck.Windows.Tests.Management;

public sealed class ContextMenuRemovalServiceTests
{
    private const string KeyPath = "Software\\Classes\\RightMenuCheck.Test\\shell\\sample";

    [Fact]
    public void CreatePlanDeletesOnlySelectedRootAndMarksMachineElevation()
    {
        var registry = new InMemoryRegistryReader(RegistryViewKind.Registry64);
        registry.AddKey(RegistryHiveKind.LocalMachine, RegistryViewKind.Registry64, KeyPath);
        var service = CreateService(registry);

        var plan = service.CreatePlan(
            CreateRegistryMetadata(RegistryHiveKind.LocalMachine),
            allowSystemProtected: false);

        Assert.True(plan.IsSupported);
        Assert.False(plan.IsNoChange);
        Assert.True(plan.RequiresElevation);
        var mutation = Assert.IsType<RegistryMutation>(plan.Mutation);
        Assert.Equal(RegistryMutationKind.DeleteKeyTree, mutation.Kind);
        Assert.Equal(KeyPath, mutation.Source.KeyPath);
        Assert.Contains("Shared CLSID", plan.ImpactDescription, StringComparison.Ordinal);
    }

    [Fact]
    public void CreatePlanReportsNoChangeForMissingRegistration()
    {
        var registry = new InMemoryRegistryReader(RegistryViewKind.Registry64);
        var service = CreateService(registry);

        var plan = service.CreatePlan(
            CreateRegistryMetadata(RegistryHiveKind.CurrentUser),
            allowSystemProtected: false);

        Assert.True(plan.IsSupported);
        Assert.True(plan.IsNoChange);
        Assert.Null(plan.Mutation);
    }

    [Fact]
    public void CreatePlanRejectsPackagedAndSystemProtectedRegistrations()
    {
        var registry = new InMemoryRegistryReader(RegistryViewKind.Registry64);
        registry.AddKey(RegistryHiveKind.LocalMachine, RegistryViewKind.Registry64, KeyPath);
        var service = CreateService(registry);

        var packagePlan = service.CreatePlan(CreatePackageMetadata(), allowSystemProtected: false);
        var protectedPlan = service.CreatePlan(
            CreateRegistryMetadata(
                RegistryHiveKind.LocalMachine,
                owner: CreateProtectedOwner()),
            allowSystemProtected: false);

        Assert.False(packagePlan.IsSupported);
        Assert.Contains("package", packagePlan.BlockReason, StringComparison.OrdinalIgnoreCase);
        Assert.False(protectedPlan.IsSupported);
        Assert.Contains("protected", protectedPlan.BlockReason, StringComparison.OrdinalIgnoreCase);
    }

    private static ContextMenuRemovalService CreateService(InMemoryRegistryReader registry)
    {
        var snapshot = new RegistrySnapshotReader(registry, new FakeSecurityDescriptorReader());
        return new ContextMenuRemovalService(
            registry,
            new RightMenuBackupService(snapshot),
            new RegistryTransactionExecutor(
                registry,
                snapshot,
                new NoOpRegistryWriter(),
                new NoOpJournalStore()));
    }

    private static ContextMenuRegistrationMetadata CreateRegistryMetadata(
        RegistryHiveKind hive,
        ApplicationOwnerMetadata? owner = null)
    {
        var registration = new ContextMenuRegistration
        {
            Id = "registry",
            Source = new RegistryContextMenuSource(new RegistrySource(
                hive,
                RegistryViewKind.Registry64,
                KeyPath)),
            ClassPath = "RightMenuCheck.Test",
            RegistrationPath = KeyPath,
            CanonicalName = "sample",
            DisplayName = "Sample",
            TargetKind = ContextMenuTargetKind.FileType,
            Kind = ContextMenuRegistrationKind.StaticVerb,
        };
        return new ContextMenuRegistrationMetadata(
            registration,
            Components: [],
            owner,
            Issues: []);
    }

    private static ContextMenuRegistrationMetadata CreatePackageMetadata()
    {
        var registration = new ContextMenuRegistration
        {
            Id = "package",
            Source = new PackageContextMenuSource(
                "Package",
                "Package_1.0.0.0_x64__publisher",
                "Package_publisher",
                "App",
                PackageArchitectureKind.X64,
                "C:\\Packages\\Package\\AppxManifest.xml"),
            ClassPath = "*",
            RegistrationPath = "Applications\\App\\Extensions\\context\\verb",
            CanonicalName = "verb",
            DisplayName = "Verb",
            TargetKind = ContextMenuTargetKind.File,
            Kind = ContextMenuRegistrationKind.PackagedExplorerCommand,
        };
        return new ContextMenuRegistrationMetadata(
            registration,
            Components: [],
            Owner: null,
            Issues: []);
    }

    private static ApplicationOwnerMetadata CreateProtectedOwner() => new(
        ApplicationOwnerKind.WindowsSystem,
        OwnershipConfidence.High,
        "Windows",
        "Microsoft Corporation",
        "1.0",
        "C:\\Windows",
        ProductCode: null,
        PackageFullName: null,
        UninstallRegistrySource: null,
        UninstallKeyName: null,
        UninstallString: null,
        QuietUninstallString: null,
        IsWindowsInstaller: false,
        IsSystemProtected: true,
        "test");

    private sealed class NoOpRegistryWriter : IRegistryWriter
    {
        public void SetValue(RegistrySource source, RightMenuCheck.Core.Backup.RegistryValueSnapshot value)
        {
        }

        public void DeleteValue(RegistrySource source, string valueName)
        {
        }

        public void DeleteKeyTree(RegistrySource source)
        {
        }

        public void RestoreKeyTree(
            RegistrySource root,
            IReadOnlyList<RightMenuCheck.Core.Backup.RegistryKeySnapshot> keys)
        {
        }
    }

    private sealed class NoOpJournalStore : IRegistryActionJournalStore
    {
        public Task<string> WriteAsync(
            RegistryActionJournal journal,
            CancellationToken cancellationToken = default) =>
            Task.FromResult("unused.json");
    }
}
