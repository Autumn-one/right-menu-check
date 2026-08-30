using RightMenuCheck.App.Services;
using RightMenuCheck.Windows.Diagnostics;

namespace RightMenuCheck.IntegrationTests.Distribution;

public sealed class AnnouncementStateStoreTests
{
    [Fact]
    public async Task PersistsOnlyHighestAcknowledgedRevision()
    {
        using var fixture = new DistributionDocumentClientTests.TemporaryDirectory();
        var path = Path.Combine(fixture.Path, "state.json");
        using var store = new AnnouncementStateStore(NullAppLogger.Instance, path);

        await store.MarkShownAsync("message", 2, CancellationToken.None);
        await store.MarkShownAsync("message", 1, CancellationToken.None);
        var state = await store.ReadAsync(CancellationToken.None);

        Assert.Equal(2, state["message"]);
    }

    [Fact]
    public async Task CorruptStateReturnsEmptyWithoutThrowing()
    {
        using var fixture = new DistributionDocumentClientTests.TemporaryDirectory();
        var path = Path.Combine(fixture.Path, "state.json");
        File.WriteAllText(path, "not json");
        using var store = new AnnouncementStateStore(NullAppLogger.Instance, path);

        var state = await store.ReadAsync(CancellationToken.None);

        Assert.Empty(state);
    }
}
