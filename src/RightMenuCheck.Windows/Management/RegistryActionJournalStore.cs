using System.Text.Json;
using RightMenuCheck.Windows.Backup;

namespace RightMenuCheck.Windows.Management;

public interface IRegistryActionJournalStore
{
    Task<string> WriteAsync(
        RegistryActionJournal journal,
        CancellationToken cancellationToken = default);
}

public sealed class FileRegistryActionJournalStore : IRegistryActionJournalStore
{
    private readonly string _directory;

    public FileRegistryActionJournalStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
    }

    public async Task<string> WriteAsync(
        RegistryActionJournal journal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(journal);
        Directory.CreateDirectory(_directory);
        var destination = Path.Combine(_directory, $"{journal.OperationId:D}.json");
        var temporary = Path.Combine(
            _directory,
            $".{journal.OperationId:D}.{Guid.NewGuid():N}.tmp");
        var content = JsonSerializer.SerializeToUtf8Bytes(journal, BackupJson.Options);

        try
        {
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 81920,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (File.Exists(destination))
            {
                File.Replace(temporary, destination, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporary, destination);
            }

            return destination;
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
