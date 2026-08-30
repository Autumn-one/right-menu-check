using RightMenuCheck.Core.Backup;
using RightMenuCheck.Core.Inventory;
using RightMenuCheck.Probe.Protocol;
using RightMenuCheck.Windows.Elevation;
using RightMenuCheck.Windows.Management;

namespace RightMenuCheck.Windows.Tests.Elevation;

public sealed class ElevationProtocolTests
{
    private const string Nonce = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";

    [Fact]
    public async Task StateChangeRequestRoundTripsThroughBoundedSerializer()
    {
        var request = CreateStateRequest(CreateLegacyDisableMutation(
            RegistryHiveKind.LocalMachine,
            "LegacyDisable",
            string.Empty));
        await using var stream = new MemoryStream();

        await ElevationMessageSerializer.WriteRequestAsync(
            stream,
            request,
            CancellationToken.None);
        stream.Position = 0;
        var restored = await ElevationMessageSerializer.ReadRequestAsync(
            stream,
            CancellationToken.None);

        Assert.Equal(request.ProtocolVersion, restored.ProtocolVersion);
        Assert.Equal(request.RequestId, restored.RequestId);
        Assert.Equal(request.ExpectedBackupId, restored.ExpectedBackupId);
        Assert.Equal(request.Operation, restored.Operation);
        Assert.NotNull(restored.StateMutationPlan);
        Assert.Single(restored.StateMutationPlan.Mutations);
    }

    [Fact]
    public void ValidateAcceptsStrictHklmLegacyDisableAndRestoreShapes()
    {
        var stateRequest = CreateStateRequest(CreateLegacyDisableMutation(
            RegistryHiveKind.LocalMachine,
            "LegacyDisable",
            string.Empty));
        var restoreRequest = new ElevationRequest(
            ElevationProtocol.CurrentVersion,
            Guid.NewGuid(),
            Nonce,
            ElevationOperation.Restore,
            Guid.NewGuid(),
            "C:\\Backups\\restore.rmcbak",
            StateMutationPlan: null,
            RegistryRestoreMode.Exact,
            AcceptRestoreConflicts: true);

        Assert.True(ElevationRequestValidator.ValidateEnvelope(stateRequest, Nonce).IsValid);
        Assert.True(ElevationRequestValidator.ValidateEnvelope(restoreRequest, Nonce).IsValid);
    }

    [Theory]
    [InlineData(RegistryHiveKind.CurrentUser, "LegacyDisable", "")]
    [InlineData(RegistryHiveKind.LocalMachine, "ArbitraryValue", "")]
    [InlineData(RegistryHiveKind.LocalMachine, "LegacyDisable", "not-empty")]
    public void ValidateRejectsBroaderRegistryStateChanges(
        RegistryHiveKind hive,
        string valueName,
        string text)
    {
        var request = CreateStateRequest(CreateLegacyDisableMutation(hive, valueName, text));

        var result = ElevationRequestValidator.ValidateEnvelope(request, Nonce);

        Assert.False(result.IsValid);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void ValidateRejectsNonceMismatch()
    {
        var request = CreateStateRequest(CreateLegacyDisableMutation(
            RegistryHiveKind.LocalMachine,
            "LegacyDisable",
            string.Empty));

        var result = ElevationRequestValidator.ValidateEnvelope(
            request,
            ProbeRequestValidator.CreateNonce());

        Assert.False(result.IsValid);
        Assert.Equal("Nonce validation failed.", result.Error);
    }

    private static ElevationRequest CreateStateRequest(RegistryMutation mutation)
    {
        const string backupPath = "C:\\Backups\\state.rmcbak";
        return new ElevationRequest(
            ElevationProtocol.CurrentVersion,
            Guid.NewGuid(),
            Nonce,
            ElevationOperation.StateChange,
            Guid.NewGuid(),
            backupPath,
            new RegistryMutationPlan(
                Guid.NewGuid(),
                "State change",
                backupPath,
                [mutation]),
            RestoreMode: null,
            AcceptRestoreConflicts: false);
    }

    private static RegistryMutation CreateLegacyDisableMutation(
        RegistryHiveKind hive,
        string valueName,
        string text) =>
        new(
            RegistryMutationKind.SetValue,
            new RegistrySource(
                hive,
                RegistryViewKind.Registry64,
                "Software\\Classes\\*\\shell\\test"),
            valueName,
            new RegistryValueSnapshot(
                valueName,
                BackupRegistryValueKind.Text,
                text,
                TextItems: null,
                Base64Data: null,
                NumericValue: null),
            KeyTree: null);
}
