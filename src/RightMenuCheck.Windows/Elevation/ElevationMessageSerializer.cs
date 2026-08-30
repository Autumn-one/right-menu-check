using System.Buffers.Binary;
using System.Text.Json;
using RightMenuCheck.Windows.Backup;

namespace RightMenuCheck.Windows.Elevation;

public static class ElevationMessageSerializer
{
    public static ValueTask WriteRequestAsync(
        Stream stream,
        ElevationRequest request,
        CancellationToken cancellationToken = default) =>
        WriteAsync(stream, request, cancellationToken);

    public static ValueTask<ElevationRequest> ReadRequestAsync(
        Stream stream,
        CancellationToken cancellationToken = default) =>
        ReadAsync<ElevationRequest>(stream, cancellationToken);

    public static ValueTask WriteResponseAsync(
        Stream stream,
        ElevationResponse response,
        CancellationToken cancellationToken = default) =>
        WriteAsync(stream, response, cancellationToken);

    public static ValueTask<ElevationResponse> ReadResponseAsync(
        Stream stream,
        CancellationToken cancellationToken = default) =>
        ReadAsync<ElevationResponse>(stream, cancellationToken);

    private static async ValueTask WriteAsync<T>(
        Stream stream,
        T message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(message);
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, BackupJson.Options);
        if (payload.Length > ElevationProtocol.MaximumMessageBytes)
        {
            throw new InvalidDataException("Elevation message exceeds the maximum size.");
        }

        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<T> ReadAsync<T>(
        Stream stream,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[sizeof(int)];
        await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > ElevationProtocol.MaximumMessageBytes)
        {
            throw new InvalidDataException("Elevation message length is invalid.");
        }

        var payload = new byte[length];
        await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(payload, BackupJson.Options) ??
               throw new InvalidDataException("Elevation message contains JSON null.");
    }

    private static async ValueTask ReadExactlyAsync(
        Stream stream,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < destination.Length)
        {
            var read = await stream
                .ReadAsync(destination[offset..], cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException(
                    "Elevation stream ended before a complete message was received.");
            }

            offset += read;
        }
    }
}
