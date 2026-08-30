using RightMenuCheck.Core.Backup;

namespace RightMenuCheck.Windows.Backup;

public sealed class BackupIncompleteException : InvalidOperationException
{
    public BackupIncompleteException(IReadOnlyList<BackupCaptureIssue> issues)
        : base("Registry backup capture is incomplete; the requested operation must not continue.")
    {
        Issues = issues;
    }

    public IReadOnlyList<BackupCaptureIssue> Issues { get; }
}
