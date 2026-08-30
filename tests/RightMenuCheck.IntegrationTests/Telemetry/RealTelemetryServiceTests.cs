using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using RightMenuCheck.App.Services;

namespace RightMenuCheck.IntegrationTests.Telemetry;

public sealed class RealTelemetryServiceTests
{
    private const string ServiceUrlEnvironmentVariable = "RMC_TELEMETRY_E2E_URL";
    private const string AdminTokenEnvironmentVariable = "RMC_TELEMETRY_E2E_ADMIN_TOKEN";

    [Fact]
    [Trait("Category", "External")]
    public async Task ConfiguredGoServiceRecordsRealClientLifecycle()
    {
        var configuredUrl = Environment.GetEnvironmentVariable(ServiceUrlEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configuredUrl))
        {
            return;
        }

        var baseAddress = new Uri(configuredUrl, UriKind.Absolute);
        using var adminClient = new HttpClient
        {
            BaseAddress = baseAddress,
            Timeout = TimeSpan.FromSeconds(3),
        };
        var adminToken = Environment.GetEnvironmentVariable(AdminTokenEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(adminToken))
        {
            adminClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", adminToken);
        }

        var before = await GetSummaryAsync(adminClient);
        var identityProvider = new MachineIdentityProvider();
        var machineId = await identityProvider.GetMachineIdAsync();
        using var telemetryHttpClient = new HttpClient
        {
            BaseAddress = baseAddress,
            Timeout = Timeout.InfiniteTimeSpan,
        };
        await using var telemetryClient = new AppTelemetryClient(
            telemetryHttpClient,
            new TelemetryClientOptions(
                baseAddress,
                heartbeatInterval: TimeSpan.FromMilliseconds(75),
                requestTimeout: TimeSpan.FromSeconds(2)),
            identityProvider);

        await telemetryClient.StartAsync();
        var active = await WaitForSummaryAsync(
            adminClient,
            summary => summary.StartupCount == before.StartupCount + 1 &&
                       summary.ActiveSessionCount == before.ActiveSessionCount + 1);
        Assert.Equal(before.SessionCount + 1, active.SessionCount);

        await Task.Delay(TimeSpan.FromMilliseconds(250));
        await telemetryClient.StopAsync();

        var after = await WaitForSummaryAsync(
            adminClient,
            summary => summary.NormalSessionCount == before.NormalSessionCount + 1 &&
                       summary.ActiveSessionCount == before.ActiveSessionCount);
        Assert.Equal(before.StartupCount + 1, after.StartupCount);
        Assert.Equal(before.SessionCount + 1, after.SessionCount);
        Assert.True(after.TotalDurationMilliseconds >= before.TotalDurationMilliseconds + 100);
        Assert.True(await MachineExistsAsync(adminClient, machineId));
    }

    private static async Task<TelemetrySummary> WaitForSummaryAsync(
        HttpClient httpClient,
        Func<TelemetrySummary, bool> predicate)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var summary = await GetSummaryAsync(httpClient);
            if (predicate(summary))
            {
                return summary;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        throw new TimeoutException("The local telemetry service did not reach the expected state.");
    }

    private static async Task<TelemetrySummary> GetSummaryAsync(HttpClient httpClient)
    {
        using var response = await httpClient.GetAsync("v1/admin/summary");
        response.EnsureSuccessStatusCode();
        await using var content = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(content);
        var root = document.RootElement;
        return new TelemetrySummary(
            root.GetProperty("startupCount").GetInt64(),
            root.GetProperty("sessionCount").GetInt64(),
            root.GetProperty("activeSessionCount").GetInt64(),
            root.GetProperty("normalSessionCount").GetInt64(),
            root.GetProperty("totalDurationMilliseconds").GetInt64());
    }

    private static async Task<bool> MachineExistsAsync(
        HttpClient httpClient,
        string expectedMachineId)
    {
        using var response = await httpClient.GetAsync("v1/admin/machines?limit=500&offset=0");
        response.EnsureSuccessStatusCode();
        await using var content = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(content);
        return document.RootElement
            .GetProperty("items")
            .EnumerateArray()
            .Any(item => string.Equals(
                item.GetProperty("machineId").GetString(),
                expectedMachineId,
                StringComparison.Ordinal));
    }

    private sealed record TelemetrySummary(
        long StartupCount,
        long SessionCount,
        long ActiveSessionCount,
        long NormalSessionCount,
        long TotalDurationMilliseconds);
}
