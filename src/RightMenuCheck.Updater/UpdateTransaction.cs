using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RightMenuCheck.Updater;

public enum UpdateTransactionPhase
{
    StagingStarted,
    StagingPrepared,
    StagingCleanupStarted,
    StagingAbandoned,
    BackupMoveStarted,
    BackupMoved,
    ActivationMoveStarted,
    NewActive,
    HealthCheckStarted,
    HealthConfirmed,
    BackupCleanupStarted,
    BackupCleaned,
    PackageCleanupStarted,
    PackageCleaned,
    Completed,
    RollbackStarted,
    RollbackActiveMoved,
    RollbackRestoreStarted,
    RollbackRestored,
    RollbackCleanupStarted,
    RolledBack,
}

public interface IUpdateTransactionObserver
{
    void OnPhasePersisted(UpdateTransactionPhase phase, string targetKey);
}

internal sealed class NullUpdateTransactionObserver : IUpdateTransactionObserver
{
    public static NullUpdateTransactionObserver Instance { get; } = new();

    public void OnPhasePersisted(UpdateTransactionPhase phase, string targetKey)
    {
    }
}

internal sealed record UpdateTransactionJournal(
    [property: JsonPropertyOrder(0)] int SchemaVersion,
    [property: JsonPropertyOrder(1)] string TargetKey,
    [property: JsonPropertyOrder(2)] string TargetDirectory,
    [property: JsonPropertyOrder(3)] string HealthToken,
    [property: JsonPropertyOrder(4)] string StagingDirectory,
    [property: JsonPropertyOrder(5)] string BackupDirectory,
    [property: JsonPropertyOrder(6)] string FailedDirectory,
    [property: JsonPropertyOrder(7)] string ExpectedVersion,
    [property: JsonPropertyOrder(8)] bool HadExistingInstall,
    [property: JsonPropertyOrder(9)] UpdateTransactionPhase Phase,
    [property: JsonPropertyOrder(10)] DateTimeOffset UpdatedAtUtc)
{
    public const int CurrentSchemaVersion = 1;
}

internal static class UpdateTransactionJournalStore
{
    private const long MaximumJournalBytes = 64 * 1024;
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    public static UpdateTransactionJournal? Read(string journalPath)
    {
        if (!File.Exists(journalPath))
        {
            return null;
        }

        using var stream = new FileStream(
            journalPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.SequentialScan);
        if (stream.Length is <= 0 or > MaximumJournalBytes)
        {
            throw new InvalidDataException("Update transaction journal size is invalid.");
        }

        try
        {
            return JsonSerializer.Deserialize<UpdateTransactionJournal>(stream, SerializerOptions) ??
                   throw new InvalidDataException("Update transaction journal is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Update transaction journal is invalid.", exception);
        }
    }

    public static void Write(string journalPath, UpdateTransactionJournal journal)
    {
        var temporaryPath = journalPath + ".tmp";
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.Create,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, Utf8WithoutBom, leaveOpen: true))
            {
                writer.Write(JsonSerializer.Serialize(journal, SerializerOptions));
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, journalPath, overwrite: true);
        }
        finally
        {
            TryDeleteTemporary(temporaryPath);
        }
    }

    public static void Delete(string journalPath)
    {
        if (File.Exists(journalPath))
        {
            File.Delete(journalPath);
        }
    }

    private static JsonSerializerOptions SerializerOptions { get; } = CreateSerializerOptions();

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private static void TryDeleteTemporary(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal sealed class UpdateTargetLock : IDisposable
{
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;
    private readonly FileStream _stream;

    private UpdateTargetLock(FileStream stream)
    {
        _stream = stream;
    }

    public static UpdateTargetLock Acquire(string lockPath)
    {
        try
        {
            return new UpdateTargetLock(new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.WriteThrough));
        }
        catch (IOException exception) when (
            (exception.HResult & 0xFFFF) is ErrorSharingViolation or ErrorLockViolation)
        {
            throw new UpdateTargetBusyException(
                "Another updater is already operating on this installation target.",
                exception);
        }
    }

    public void Dispose() => _stream.Dispose();
}

public sealed class UpdateTargetBusyException : IOException
{
    public UpdateTargetBusyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal static class UpdateTransactionPathPolicy
{
    private const string FilePrefix = ".rightmenucheck-update-";

    public static string ComputeTargetKey(string targetDirectory)
    {
        var canonicalTarget = NormalizeDirectory(targetDirectory).ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalTarget)));
    }

    public static UpdateTargetContext CreateTargetContext(string targetDirectory)
    {
        var target = NormalizeDirectory(targetDirectory);
        var parent = Directory.GetParent(target)?.FullName ??
                     throw new InvalidDataException("Install directory cannot be a root directory.");
        var targetKey = ComputeTargetKey(target);
        var keyForFileName = targetKey.ToLowerInvariant();
        return new UpdateTargetContext(
            target,
            Path.GetFullPath(parent),
            targetKey,
            Path.Combine(parent, $"{FilePrefix}{keyForFileName}.lock"),
            Path.Combine(parent, $"{FilePrefix}{keyForFileName}.json"));
    }

    public static UpdateWorkingPaths CreateWorkingPaths(
        UpdateTargetContext target,
        string healthToken)
    {
        if (!Guid.TryParseExact(healthToken, "N", out _))
        {
            throw new InvalidDataException("Update transaction token is invalid.");
        }

        var installName = Path.GetFileName(target.InstallDirectory);
        return new UpdateWorkingPaths(
            Path.Combine(target.ParentDirectory, $".{installName}.staging-{healthToken}"),
            Path.Combine(target.ParentDirectory, $".{installName}.backup-{healthToken}"),
            Path.Combine(target.ParentDirectory, $".{installName}.failed-{healthToken}"));
    }

    public static void ValidateJournal(
        UpdateTargetContext target,
        UpdateTransactionJournal journal)
    {
        if (journal.SchemaVersion != UpdateTransactionJournal.CurrentSchemaVersion ||
            !Enum.IsDefined(journal.Phase) ||
            string.IsNullOrWhiteSpace(journal.TargetKey) ||
            string.IsNullOrWhiteSpace(journal.TargetDirectory) ||
            string.IsNullOrWhiteSpace(journal.HealthToken) ||
            string.IsNullOrWhiteSpace(journal.StagingDirectory) ||
            string.IsNullOrWhiteSpace(journal.BackupDirectory) ||
            string.IsNullOrWhiteSpace(journal.FailedDirectory) ||
            string.IsNullOrWhiteSpace(journal.ExpectedVersion) ||
            !SemanticVersionTextIsValid(journal.ExpectedVersion) ||
            !Guid.TryParseExact(journal.HealthToken, "N", out _) ||
            journal.TargetKey.Length != 64 ||
            !journal.TargetKey.All(Uri.IsHexDigit) ||
            !Path.IsPathFullyQualified(journal.TargetDirectory) ||
            !Path.IsPathFullyQualified(journal.StagingDirectory) ||
            !Path.IsPathFullyQualified(journal.BackupDirectory) ||
            !Path.IsPathFullyQualified(journal.FailedDirectory) ||
            !FixedTimeEquals(journal.TargetKey, target.TargetKey) ||
            !PathEquals(journal.TargetDirectory, target.InstallDirectory))
        {
            throw new InvalidDataException("Update transaction journal identity is invalid.");
        }

        var derived = CreateWorkingPaths(target, journal.HealthToken);
        if (!PathEquals(journal.StagingDirectory, derived.StagingDirectory) ||
            !PathEquals(journal.BackupDirectory, derived.BackupDirectory) ||
            !PathEquals(journal.FailedDirectory, derived.FailedDirectory))
        {
            throw new InvalidDataException("Update transaction journal paths are invalid.");
        }
    }

    public static bool PathEquals(string left, string right)
    {
        try
        {
            return NormalizeDirectory(left).Equals(
                NormalizeDirectory(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is
                                           ArgumentException or
                                           NotSupportedException or
                                           PathTooLongException)
        {
            return false;
        }
    }

    private static string NormalizeDirectory(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool FixedTimeEquals(string left, string right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(left.ToUpperInvariant()),
            Encoding.ASCII.GetBytes(right.ToUpperInvariant()));
    }

    private static bool SemanticVersionTextIsValid(string value)
    {
        try
        {
            _ = RightMenuCheck.Distribution.SemanticVersion.Parse(value);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

internal sealed record UpdateTargetContext(
    string InstallDirectory,
    string ParentDirectory,
    string TargetKey,
    string LockPath,
    string JournalPath);

internal sealed record UpdateWorkingPaths(
    string StagingDirectory,
    string BackupDirectory,
    string FailedDirectory);
