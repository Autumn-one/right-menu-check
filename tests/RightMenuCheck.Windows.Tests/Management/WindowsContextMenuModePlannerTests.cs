using RightMenuCheck.Core.Inventory;
using RightMenuCheck.Windows.Management;
using RightMenuCheck.Windows.Registry;
using RightMenuCheck.Windows.Tests.Registry;

namespace RightMenuCheck.Windows.Tests.Management;

public sealed class WindowsContextMenuModePlannerTests
{
    private const RegistryViewKind View = RegistryViewKind.Registry64;

    [Fact]
    public void ReportsUnsupportedBeforeWindows11()
    {
        var planner = CreatePlanner(new InMemoryRegistryReader(View), build: 19045);

        var status = planner.GetStatus();
        var plan = planner.CreatePlan(WindowsContextMenuMode.Classic);

        Assert.Equal(WindowsContextMenuMode.Unsupported, status.Mode);
        Assert.False(status.CanChange);
        Assert.False(plan.IsSupported);
        Assert.Null(plan.MutationPlan);
    }

    [Fact]
    public void CreatesCurrentUserClassicOverrideWhenNoneExists()
    {
        var planner = CreatePlanner(new InMemoryRegistryReader(View));

        var status = planner.GetStatus();
        var plan = planner.CreatePlan(WindowsContextMenuMode.Classic);

        Assert.Equal(WindowsContextMenuMode.Windows11, status.Mode);
        Assert.True(status.CanChange);
        Assert.True(plan.IsSupported);
        Assert.False(plan.IsNoChange);
        var mutation = Assert.Single(plan.MutationPlan!.Mutations);
        Assert.Equal(RegistryMutationKind.SetValue, mutation.Kind);
        Assert.Equal(RegistryHiveKind.CurrentUser, mutation.Source.Hive);
        Assert.Equal(View, mutation.Source.View);
        Assert.Equal(WindowsContextMenuModePlanner.OverrideKeyPath, mutation.Source.KeyPath);
        Assert.Equal(string.Empty, mutation.Value?.Name);
        Assert.Equal(string.Empty, mutation.Value?.Text);
    }

    [Fact]
    public void RecognizesExactClassicOverrideAndPlansOnlyKnownKeyRemoval()
    {
        var reader = new InMemoryRegistryReader(View);
        reader.SetValue(
            RegistryHiveKind.CurrentUser,
            View,
            WindowsContextMenuModePlanner.OverrideKeyPath,
            valueName: null,
            string.Empty,
            RegistryValueDataKind.Text);
        var planner = CreatePlanner(reader);

        var status = planner.GetStatus();
        var plan = planner.CreatePlan(WindowsContextMenuMode.Windows11);

        Assert.Equal(WindowsContextMenuMode.Classic, status.Mode);
        Assert.True(plan.IsSupported);
        var mutation = Assert.Single(plan.MutationPlan!.Mutations);
        Assert.Equal(RegistryMutationKind.DeleteKeyTree, mutation.Kind);
        Assert.Equal(WindowsContextMenuModePlanner.OverrideClsidKeyPath, mutation.Source.KeyPath);
    }

    [Fact]
    public void BlocksUnknownCustomOverrideWithoutCreatingMutation()
    {
        var reader = new InMemoryRegistryReader(View);
        reader.SetValue(
            RegistryHiveKind.CurrentUser,
            View,
            WindowsContextMenuModePlanner.OverrideKeyPath,
            valueName: null,
            "custom.dll",
            RegistryValueDataKind.Text);
        reader.SetValue(
            RegistryHiveKind.CurrentUser,
            View,
            WindowsContextMenuModePlanner.OverrideKeyPath,
            "ThreadingModel",
            "Apartment",
            RegistryValueDataKind.Text);
        var planner = CreatePlanner(reader);

        var status = planner.GetStatus();
        var plan = planner.CreatePlan(WindowsContextMenuMode.Classic);

        Assert.Equal(WindowsContextMenuMode.Custom, status.Mode);
        Assert.False(status.CanChange);
        Assert.False(plan.IsSupported);
        Assert.Contains("不会修改", plan.BlockReason, StringComparison.Ordinal);
        Assert.Null(plan.MutationPlan);
    }

    [Fact]
    public void BlocksExactInprocOverrideWhenParentContainsUnrelatedData()
    {
        var reader = new InMemoryRegistryReader(View);
        reader.SetValue(
            RegistryHiveKind.CurrentUser,
            View,
            WindowsContextMenuModePlanner.OverrideKeyPath,
            valueName: null,
            string.Empty,
            RegistryValueDataKind.Text);
        reader.SetValue(
            RegistryHiveKind.CurrentUser,
            View,
            WindowsContextMenuModePlanner.OverrideClsidKeyPath,
            "Owner",
            "another-tool",
            RegistryValueDataKind.Text);
        var planner = CreatePlanner(reader);

        var status = planner.GetStatus();
        var plan = planner.CreatePlan(WindowsContextMenuMode.Windows11);

        Assert.Equal(WindowsContextMenuMode.Custom, status.Mode);
        Assert.False(plan.IsSupported);
        Assert.Null(plan.MutationPlan);
    }

    private static WindowsContextMenuModePlanner CreatePlanner(
        InMemoryRegistryReader reader,
        int build = 26200) => new(reader, new FakeBuildProvider(build), View);

    private sealed class FakeBuildProvider(int build) : IWindowsBuildProvider
    {
        public int Build { get; } = build;
    }
}
