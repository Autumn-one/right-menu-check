using System.IO.Pipes;
using RightMenuCheck.App.Services;

namespace RightMenuCheck.IntegrationTests.Distribution;

public sealed class UpdateReadyHandshakeTests
{
    [Fact]
    public async Task CurrentUserPipeAcceptsMatchingNonce()
    {
        using var endpoint = UpdateReadyHandshake.Create();
        var client = SendAsync(endpoint.PipeName, endpoint.Nonce);

        await UpdateReadyHandshake.WaitAsync(
            endpoint,
            TimeSpan.FromSeconds(3),
            CancellationToken.None);
        await client;
    }

    [Fact]
    public async Task PipeRejectsWrongNonce()
    {
        using var endpoint = UpdateReadyHandshake.Create();
        var client = SendAsync(endpoint.PipeName, "wrong-nonce");

        await Assert.ThrowsAsync<InvalidDataException>(() => UpdateReadyHandshake.WaitAsync(
            endpoint,
            TimeSpan.FromSeconds(3),
            CancellationToken.None));
        await client;
    }

    private static async Task SendAsync(string pipeName, string nonce)
    {
        await using var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.Out,
            PipeOptions.Asynchronous);
        await pipe.ConnectAsync(CancellationToken.None);
        await using var writer = new StreamWriter(pipe)
        {
            AutoFlush = true,
        };
        await writer.WriteLineAsync(nonce);
    }
}
