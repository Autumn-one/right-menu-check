using System.IO.Pipes;

namespace RightMenuCheck.Updater;

public interface IUpdateReadySignal
{
    Task SignalAsync(
        string pipeName,
        string nonce,
        CancellationToken cancellationToken);
}

public sealed class NamedPipeUpdateReadySignal : IUpdateReadySignal
{
    public async Task SignalAsync(
        string pipeName,
        string nonce,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(pipeName) ||
            pipeName.Any(static character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '-'))
        {
            throw new InvalidDataException("Updater ready pipe name is invalid.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(nonce);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(TimeSpan.FromSeconds(20));
        await using var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.Out,
            PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(timeoutSource.Token).ConfigureAwait(false);
            await using var writer = new StreamWriter(pipe, leaveOpen: true)
            {
                AutoFlush = true,
            };
            await writer.WriteLineAsync(nonce.AsMemory(), timeoutSource.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Updater could not connect to the ready pipe.");
        }
    }
}
