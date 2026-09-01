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
    public async Task ResponseRoundTripsMenuInspectionEvidence()
    {
        var response = new ProbeResponse(
            ProbeProtocol.CurrentVersion,
            Guid.Parse("22222222-3333-4444-5555-666666666666"),
            "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=",
            ProbeOutcome.Success,
            WorkerProcessId: 123,
            WorkerArchitecture: "X64",
            new DateTimeOffset(2026, 9, 2, 0, 0, 0, TimeSpan.Zero),
            TotalDurationMilliseconds: 12.5,
            Phases:
            [
                new ProbePhaseTiming(
                    ProbePhase.MenuConstruction,
                    DurationMilliseconds: 4,
                    HResult: 1,
                    Succeeded: true),
            ],
            new ProbeMenuSnapshot(
                CommandIdCount: 1,
                Items:
                [
                    new ProbeMenuItem(
                        ProbeMenuItemKind.Command,
                        "Inspect",
                        Depth: 0,
                        CommandId: 1,
                        CanonicalVerb: "inspect",
                        HelpText: "Inspect safely",
                        IsDisabled: false,
                        IsHidden: false),
                ],
                Truncated: true,
                Limitation: "fixture limit"),
            Error: null);
        await using var stream = new MemoryStream();

        await ProbeMessageSerializer.WriteResponseAsync(stream, response, CancellationToken.None);
        stream.Position = 0;
        var restored = await ProbeMessageSerializer.ReadResponseAsync(
            stream,
            CancellationToken.None);

        Assert.Equal(response.ProtocolVersion, restored.ProtocolVersion);
        Assert.Equal(response.RequestId, restored.RequestId);
        Assert.Equal(response.Nonce, restored.Nonce);
        Assert.Equal(response.Outcome, restored.Outcome);
        Assert.Equal(response.WorkerProcessId, restored.WorkerProcessId);
        Assert.Equal(response.WorkerArchitecture, restored.WorkerArchitecture);
        Assert.Equal(response.StartedAt, restored.StartedAt);
        Assert.Equal(response.TotalDurationMilliseconds, restored.TotalDurationMilliseconds);
        Assert.Equal(response.Phases, restored.Phases);
        Assert.NotNull(restored.Menu);
        Assert.Equal(response.Menu!.CommandIdCount, restored.Menu.CommandIdCount);
        Assert.Equal(response.Menu.Items, restored.Menu.Items);
        Assert.Equal(response.Menu.Truncated, restored.Menu.Truncated);
        Assert.Equal(response.Menu.Limitation, restored.Menu.Limitation);
        Assert.Null(restored.Error);
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

    [Fact]
    public void ValidateAllowsAggregateRequestWithoutHandlerClsid()
    {
        var request = CreateRequest() with
        {
            Operation = ProbeOperation.AggregatedContextMenu,
            HandlerClsid = string.Empty,
        };

        var result = ProbeRequestValidator.Validate(request, request.Nonce);

        Assert.True(result.IsValid);
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
