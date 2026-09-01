namespace RightMenuCheck.Updater;

internal sealed record UpdateProgressSnapshot(double Percentage, string Status);

internal static class UpdateProgressMapper
{
    public static UpdateProgressSnapshot FromPhase(UpdateTransactionPhase phase) => phase switch
    {
        UpdateTransactionPhase.StagingStarted => new(38, "正在校验并展开更新文件…"),
        UpdateTransactionPhase.StagingPrepared => new(52, "更新文件已准备，等待当前版本退出…"),
        UpdateTransactionPhase.StagingCleanupStarted => new(18, "正在清理未完成的更新…"),
        UpdateTransactionPhase.StagingAbandoned => new(24, "正在重新准备更新…"),
        UpdateTransactionPhase.BackupMoveStarted => new(58, "正在备份当前版本…"),
        UpdateTransactionPhase.BackupMoved => new(65, "当前版本已安全备份。"),
        UpdateTransactionPhase.ActivationMoveStarted => new(69, "正在启用新版本…"),
        UpdateTransactionPhase.NewActive => new(76, "新版本文件已就位。"),
        UpdateTransactionPhase.HealthCheckStarted => new(82, "新版本已启动，正在确认运行状态…"),
        UpdateTransactionPhase.HealthConfirmed => new(92, "新版本运行正常，正在清理旧文件…"),
        UpdateTransactionPhase.BackupCleanupStarted => new(94, "正在清理旧版本备份…"),
        UpdateTransactionPhase.BackupCleaned => new(96, "旧版本备份已清理。"),
        UpdateTransactionPhase.PackageCleanupStarted => new(97, "正在清理更新包…"),
        UpdateTransactionPhase.PackageCleaned => new(98, "更新包已清理。"),
        UpdateTransactionPhase.Completed => new(99, "正在完成更新…"),
        UpdateTransactionPhase.RollbackStarted => new(84, "新版本未通过验证，正在恢复原版本…"),
        UpdateTransactionPhase.RollbackActiveMoved => new(88, "正在移出未通过验证的新版本…"),
        UpdateTransactionPhase.RollbackRestoreStarted => new(91, "正在恢复原版本…"),
        UpdateTransactionPhase.RollbackRestored => new(95, "原版本已恢复，正在清理临时文件…"),
        UpdateTransactionPhase.RollbackCleanupStarted => new(97, "正在清理回滚文件…"),
        UpdateTransactionPhase.RolledBack => new(99, "原版本已经恢复。"),
        _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, null),
    };
}

internal sealed class UpdateProgressObserver(UpdateProgressWindow window)
    : IUpdateTransactionObserver
{
    private readonly UpdateProgressWindow _window = window ??
                                                    throw new ArgumentNullException(nameof(window));

    public void OnPhasePersisted(UpdateTransactionPhase phase, string targetKey)
    {
        _ = targetKey;
        _window.Report(UpdateProgressMapper.FromPhase(phase));
    }
}
