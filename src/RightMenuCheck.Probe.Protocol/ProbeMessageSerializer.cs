using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RightMenuCheck.Probe.Protocol;

public static class ProbeMessageSerializer
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16,
    };

    public static ValueTask WriteRequestAsync(
        Stream stream,
        ProbeRequest request,
        CancellationToken cancellationToken = default) =>
        WriteAsync(stream, request, cancellationToken);

    public static ValueTask<ProbeRequest> ReadRequestAsync(
        Stream stream,
        CancellationToken cancellationToken = default) =>
        ReadAsync<ProbeRequest>(stream, cancellationToken);

    public static ValueTask WriteResponseAsync(
        Stream stream,
        ProbeResponse response,
        CancellationToken cancellationToken = default) =>
        WriteAsync(stream, response, cancellationToken);

    public static ValueTask<ProbeResponse> ReadResponseAsync(
        Stream stream,
        CancellationToken cancellationToken = default) =>
        ReadAsync<ProbeResponse>(stream, cancellationToken);

    private static async ValueTask WriteAsync<T>(
        Stream stream,
        T message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(message);

        var payload = JsonSerializer.SerializeToUtf8Bytes(message, SerializerOptions);
        if (payload.Length > ProbeProtocol.MaximumMessageBytes)
        {
            throw new InvalidDataException("Probe message exceeds the maximum payload size.");
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
        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (payloadLength <= 0 || payloadLength > ProbeProtocol.MaximumMessageBytes)
        {
            throw new InvalidDataException("Probe message length is outside the allowed range.");
        }

        var payload = new byte[payloadLength];
        await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(payload, SerializerOptions) ??
               throw new InvalidDataException("Probe message contains JSON null.");
    }

    private static async ValueTask ReadExactlyAsync(
        Stream stream,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < destination.Length)
        {
            var read = await stream
                .ReadAsync(destination[totalRead..], cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("Probe stream ended before a complete message was received.");
            }

            totalRead += read;
        }
    }
}
