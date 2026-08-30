using System.Security.Cryptography;
using System.Text;
using RightMenuCheck.Core.Backup;
using RightMenuCheck.Core.Inventory;
using RightMenuCheck.Probe.Protocol;
using RightMenuCheck.Windows.Management;

namespace RightMenuCheck.Windows.Elevation;

public static class ElevationRequestValidator
{
    public static ElevationValidationResult ValidateEnvelope(
        ElevationRequest? request,
        string expectedNonce)
    {
        if (request is null)
        {
            return Invalid("Request is missing.");
        }

        if (request.ProtocolVersion != ElevationProtocol.CurrentVersion ||
            request.RequestId == Guid.Empty ||
            request.ExpectedBackupId == Guid.Empty)
        {
            return Invalid("Protocol version or request identity is invalid.");
        }

        if (!IsValidNonce(request.Nonce) || !IsValidNonce(expectedNonce) ||
            !FixedTimeEquals(request.Nonce, expectedNonce))
        {
            return Invalid("Nonce validation failed.");
        }

        if (string.IsNullOrWhiteSpace(request.BackupPath) ||
            !Path.IsPathFullyQualified(request.BackupPath) ||
            !Path.GetExtension(request.BackupPath).Equals(
                ".rmcbak",
                StringComparison.OrdinalIgnoreCase))
        {
            return Invalid("Backup path must be an absolute .rmcbak path.");
        }

        return request.Operation switch
        {
            ElevationOperation.StateChange => ValidateStateChange(request),
            ElevationOperation.Restore => ValidateRestore(request),
            ElevationOperation.RemoveRegistration => ValidateRemoval(request),
            _ => Invalid("Elevation operation is unsupported."),
        };
    }

    private static ElevationValidationResult ValidateStateChange(ElevationRequest request)
    {
        var plan = request.StateMutationPlan;
        if (plan is null || plan.OperationId == Guid.Empty || plan.Mutations.Count is 0 or > 16 ||
            request.RestoreMode is not null || request.RemovalRegistrationId is not null ||
            !Path.GetFullPath(plan.BackupPath).Equals(
                Path.GetFullPath(request.BackupPath),
                StringComparison.OrdinalIgnoreCase))
        {
            return Invalid("State-change plan shape is invalid.");
        }

        foreach (var mutation in plan.Mutations)
        {
            try
            {
                RegistryMutationPolicy.Validate(mutation.Source);
            }
            catch (InvalidOperationException)
            {
                return Invalid("State-change path is outside the registry allowlist.");
            }

            if (mutation.Source.Hive != RegistryHiveKind.LocalMachine ||
                mutation.ValueName is not "LegacyDisable" ||
                mutation.Kind is not (
                    RegistryMutationKind.SetValue or RegistryMutationKind.DeleteValue))
            {
                return Invalid("Elevated state changes are limited to HKLM LegacyDisable.");
            }

            if (mutation.Kind == RegistryMutationKind.SetValue &&
                (mutation.Value is not
                    {
                        Name: "LegacyDisable",
                        Kind: BackupRegistryValueKind.Text,
                        Text: "",
                    } ||
                 mutation.KeyTree is not null))
            {
                return Invalid("LegacyDisable set-value payload is invalid.");
            }

            if (mutation.Kind == RegistryMutationKind.DeleteValue &&
                (mutation.Value is not null || mutation.KeyTree is not null))
            {
                return Invalid("LegacyDisable delete-value payload is invalid.");
            }
        }

        return Valid();
    }

    private static ElevationValidationResult ValidateRestore(ElevationRequest request)
    {
        if (request.StateMutationPlan is not null || request.RestoreMode is null ||
            request.RemovalRegistrationId is not null ||
            !Enum.IsDefined(request.RestoreMode.Value))
        {
            return Invalid("Restore request shape is invalid.");
        }

        return Valid();
    }

    private static ElevationValidationResult ValidateRemoval(ElevationRequest request)
    {
        if (request.StateMutationPlan is not null || request.RestoreMode is not null ||
            request.AcceptRestoreConflicts ||
            string.IsNullOrWhiteSpace(request.RemovalRegistrationId) ||
            request.RemovalRegistrationId.Length > 512)
        {
            return Invalid("Removal request shape is invalid.");
        }

        return Valid();
    }

    private static bool IsValidNonce(string? nonce)
    {
        if (string.IsNullOrWhiteSpace(nonce))
        {
            return false;
        }

        try
        {
            return Convert.FromBase64String(nonce).Length == ProbeProtocol.NonceSizeBytes;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length &&
               CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static ElevationValidationResult Valid() => new(IsValid: true, Error: null);

    private static ElevationValidationResult Invalid(string error) => new(IsValid: false, error);
}
