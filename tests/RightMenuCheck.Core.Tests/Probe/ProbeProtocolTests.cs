using System.Buffers.Binary;
using RightMenuCheck.Probe.Protocol;

namespace RightMenuCheck.Core.Tests.Probe;

public sealed class ProbeProtocolTests
{
    [Fact]
    public async Task RequestRoundTripsThroughLengthPrefixedJson()
    {
        var request = CreateRequest();
        await using var stream = new MemoryStream();

        await ProbeMessageSerializer.WriteRequestAsync(stream, request, CancellationToken.None);
        stream.Position = 0;
        var restored = await ProbeMessageSerializer.ReadRequestAsync(
            stream,
            CancellationToken.None);

        Assert.Equal(request, restored);
    }

    [Fact]
    public async Task ReadRejectsOversizedMessageBeforeAllocatingPayload()
    {
        await using var stream = new MemoryStream();
        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(
            header,
            ProbeProtocol.MaximumMessageBytes + 1);
        await stream.WriteAsync(header, CancellationToken.None);
        stream.Position = 0;

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await ProbeMessageSerializer.ReadRequestAsync(
                stream,
                CancellationToken.None));
    }

    [Fact]
    public void ValidateAcceptsWellFormedRequestAndMatchingNonce()
    {
        var request = CreateRequest();

        var result = ProbeRequestValidator.Validate(request, request.Nonce);

        Assert.True(result.IsValid);
        Assert.Null(result.Error);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("request-id")]
    [InlineData("nonce")]
    [InlineData("clsid")]
    [InlineData("path")]
    public void ValidateRejectsMalformedSecurityFields(string field)
    {
        var request = CreateRequest();
        request = field switch
        {
            "version" => request with { ProtocolVersion = ProbeProtocol.CurrentVersion + 1 },
            "request-id" => request with { RequestId = Guid.Empty },
            "nonce" => request with { Nonce = ProbeRequestValidator.CreateNonce() },
            "clsid" => request with { HandlerClsid = "not-a-guid" },
            "path" => request with { TargetPath = "relative.txt" },
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, null),
        };

        var result = ProbeRequestValidator.Validate(request, CreateRequest().Nonce);

        Assert.False(result.IsValid);
        Assert.NotNull(result.Error);
    }

    private static ProbeRequest CreateRequest()
    {
        const string nonce = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";
        return new ProbeRequest(
            ProbeProtocol.CurrentVersion,
            Guid.Parse("11111111-2222-3333-4444-555555555555"),
            nonce,
            ProbeOperation.ClassicContextMenu,
            ProbeTargetKind.File,
            "{AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}",
            "C:\\Samples\\item.txt",
            "*");
    }
}
