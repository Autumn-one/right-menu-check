using RightMenuCheck.Core.Inventory;
using RightMenuCheck.Core.Metadata;
using RightMenuCheck.Windows.Management;
using RightMenuCheck.Windows.Tests.Registry;

namespace RightMenuCheck.Windows.Tests.Management;

public sealed class ContextMenuStateActionPlannerTests
{
    private const string HandlerClsid = "{11111111-2222-3333-4444-555555555555}";

    [Fact]
    public void DisableClassicUsesCurrentUserBlockedValueAndReportsGlobalImpact()
    {
        var planner = new ContextMenuStateActionPlanner(new InMemoryRegistryReader(
            RegistryViewKind.Registry64));

        var plan = planner.CreatePlan(
            CreateRegistryMetadata(ContextMenuRegistrationKind.ClassicContextMenuHandler),
            ContextMenuStateAction.Disable,
            allowSystemProtected: false);

        Assert.True(plan.IsSupported);
        Assert.False(plan.IsNoChange);
        Assert.False(plan.RequiresElevation);
        Assert.True(plan.HasGlobalClsidImpact);
        var mutation = Assert.Single(plan.Mutations);
        Assert.Equal(RegistryMutationKind.SetValue, mutation.Kind);
        Assert.Equal(RegistryHiveKind.CurrentUser, mutation.Source.Hive);
        Assert.EndsWith("Shell Extensions\\Blocked", mutation.Source.KeyPath);
        Assert.Equal(HandlerClsid, mutation.Value?.Name);
    }

    [Fact]
    public void DisableClassicIsNoChangeWhenClsidIsAlreadyBlocked()
    {
        var registry = new InMemoryRegistryReader(RegistryViewKind.Registry64);
        registry.SetValue(
            RegistryHiveKind.CurrentUser,
            RegistryViewKind.Registry64,
            "Software\\Microsoft\\Windows\\CurrentVersion\\Shell Extensions\\Blocked",
            HandlerClsid,
            "Existing");
        var planner = new ContextMenuStateActionPlanner(registry);

        var plan = planner.CreatePlan(
            CreateRegistryMetadata(ContextMenuRegistrationKind.ClassicContextMenuHandler),
            ContextMenuStateAction.Disable,
            allowSystemProtected: false);

        Assert.True(plan.IsNoChange);
        Assert.Empty(plan.Mutations);
    }

    [Fact]
    public void EnableClassicDeletesCurrentUserBlockedValue()
    {
        var registry = new InMemoryRegistryReader(RegistryViewKind.Registry64);
        registry.SetValue(
            RegistryHiveKind.CurrentUser,
            RegistryViewKind.Registry64,
            "Software\\Microsoft\\Windows\\CurrentVersion\\Shell Extensions\\Blocked",
            HandlerClsid,
            "Existing");
        var planner = new ContextMenuStateActionPlanner(registry);

        var plan = planner.CreatePlan(
            CreateRegistryMetadata(ContextMenuRegistrationKind.ClassicContextMenuHandler),
            ContextMenuStateAction.Enable,
            allowSystemProtected: false);

        Assert.False(plan.IsNoChange);
        Assert.Equal(RegistryMutationKind.DeleteValue, Assert.Single(plan.Mutations).Kind);
    }

    [Fact]
    public void DisableMachineStaticVerbRequiresElevation()
    {
        var planner = new ContextMenuStateActionPlanner(new InMemoryRegistryReader(
            RegistryViewKind.Registry64));

        var plan = planner.CreatePlan(
            CreateRegistryMetadata(ContextMenuRegistrationKind.StaticVerb),
            ContextMenuStateAction.Disable,
            allowSystemProtected: false);

        Assert.True(plan.IsSupported);
        Assert.True(plan.RequiresElevation);
        Assert.False(plan.HasGlobalClsidImpact);
        var mutation = Assert.Single(plan.Mutations);
        Assert.Equal("LegacyDisable", mutation.Value?.Name);
    }

    [Fact]
    public void PackagedAndProtectedRegistrationsAreBlockedByDefault()
    {
        var planner = new ContextMenuStateActionPlanner(new InMemoryRegistryReader(
            RegistryViewKind.Registry64));

        var packaged = planner.CreatePlan(
            CreatePackageMetadata(),
            ContextMenuStateAction.Disable,
            allowSystemProtected: false);
        var protectedPlan = planner.CreatePlan(
            CreateRegistryMetadata(
                ContextMenuRegistrationKind.ClassicContextMenuHandler,
                systemProtected: true),
            ContextMenuStateAction.Disable,
            allowSystemProtected: false);

        Assert.False(packaged.IsSupported);
        Assert.Contains("Packaged", packaged.BlockReason, StringComparison.Ordinal);
        Assert.False(protectedPlan.IsSupported);
        Assert.Contains("protected", protectedPlan.BlockReason, StringComparison.OrdinalIgnoreCase);
    }

    private static ContextMenuRegistrationMetadata CreateRegistryMetadata(
        ContextMenuRegistrationKind kind,
        bool systemProtected = false)
    {
        var registration = new ContextMenuRegistration
        {
            Id = $"test-{kind}",
            Source = new RegistryContextMenuSource(new RegistrySource(
                RegistryHiveKind.LocalMachine,
                RegistryViewKind.Registry64,
                "Software\\Classes\\*\\shell\\test")),
            ClassPath = "*",
            RegistrationPath = "Software\\Classes\\*\\shell\\test",
            CanonicalName = "test",
            DisplayName = "Test",
            TargetKind = ContextMenuTargetKind.File,
            Kind = kind,
            HandlerClsid = HandlerClsid,
        };
        return new ContextMenuRegistrationMetadata(
            registration,
            Components: [],
            Owner: systemProtected
                ? CreateProtectedOwner()
                : null,
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
            HandlerClsid = HandlerClsid,
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
        "Microsoft Windows",
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
}
