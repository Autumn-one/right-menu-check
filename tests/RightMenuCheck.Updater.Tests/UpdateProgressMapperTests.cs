using RightMenuCheck.Updater;

namespace RightMenuCheck.Updater.Tests;

public sealed class UpdateProgressMapperTests
{
    [Fact]
    public void PersistedPhasesNeverClaimInstallationIsComplete()
    {
        foreach (var phase in Enum.GetValues<UpdateTransactionPhase>())
        {
            var snapshot = UpdateProgressMapper.FromPhase(phase);

            Assert.InRange(snapshot.Percentage, 1, 99);
            Assert.False(string.IsNullOrWhiteSpace(snapshot.Status));
        }
    }

    [Fact]
    public void HealthyTransactionProgressIsMonotonic()
    {
        var phases = new[]
        {
            UpdateTransactionPhase.StagingStarted,
            UpdateTransactionPhase.StagingPrepared,
            UpdateTransactionPhase.BackupMoveStarted,
            UpdateTransactionPhase.BackupMoved,
            UpdateTransactionPhase.ActivationMoveStarted,
            UpdateTransactionPhase.NewActive,
            UpdateTransactionPhase.HealthCheckStarted,
            UpdateTransactionPhase.HealthConfirmed,
            UpdateTransactionPhase.BackupCleanupStarted,
            UpdateTransactionPhase.BackupCleaned,
            UpdateTransactionPhase.PackageCleanupStarted,
            UpdateTransactionPhase.PackageCleaned,
            UpdateTransactionPhase.Completed,
        };

        var progress = phases
            .Select(UpdateProgressMapper.FromPhase)
            .Select(static snapshot => snapshot.Percentage)
            .ToArray();

        Assert.Equal(progress.Order(), progress);
        Assert.Equal(99, progress[^1]);
    }
}
