namespace RightMenuCheck.Updater;

public interface IUpdateHealthMonitor
{
    string GetMarkerPath(string healthToken);

    Task<bool> WaitForHealthyAsync(
        string healthToken,
        UpdateProcessHandle process,
        IUpdateProcessController processController,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

public sealed class FileUpdateHealthMonitor : IUpdateHealthMonitor
{
    public string GetMarkerPath(string healthToken)
    {
        ValidateToken(healthToken);
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RightMenuCheck",
            "Updates",
            $"health-{healthToken}.ok");
    }

    public async Task<bool> WaitForHealthyAsync(
        string healthToken,
        UpdateProcessHandle process,
        IUpdateProcessController processController,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var markerPath = GetMarkerPath(healthToken);
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(markerPath))
            {
                return true;
            }

            if (processController.HasExited(process))
            {
                return false;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken)
                .ConfigureAwait(false);
        }

        return false;
    }

    private static void ValidateToken(string healthToken)
    {
        if (!Guid.TryParseExact(healthToken, "N", out _))
        {
            throw new ArgumentException("Update health token is invalid.", nameof(healthToken));
        }
    }
}
