using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;

namespace RightMenuCheck.App.Services;

public sealed record UpdateReadyEndpoint(
    string PipeName,
    string Nonce,
    NamedPipeServerStream Server) : IDisposable
{
    public void Dispose() => Server.Dispose();
}

public static class UpdateReadyHandshake
{
    public static UpdateReadyEndpoint Create()
    {
        var pipeName = $"RightMenuCheck.Update.Ready.{Guid.NewGuid():N}";
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.In,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        return new UpdateReadyEndpoint(pipeName, nonce, server);
    }

    public static async Task WaitAsync(
        UpdateReadyEndpoint endpoint,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await endpoint.Server.WaitForConnectionAsync(timeoutSource.Token).ConfigureAwait(false);
            using var reader = new StreamReader(endpoint.Server, leaveOpen: true);
            var response = await reader.ReadLineAsync(timeoutSource.Token).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.UTF8.GetBytes(response ?? string.Empty),
                    System.Text.Encoding.UTF8.GetBytes(endpoint.Nonce)))
            {
                throw new InvalidDataException("Updater ready handshake nonce did not match.");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Updater did not become ready before the timeout.");
        }
    }
}
