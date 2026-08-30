using System.Security.Cryptography;
using System.Text;

namespace RightMenuCheck.Probe.Protocol;

public sealed record ProbeValidationResult(bool IsValid, string? Error);

public static class ProbeRequestValidator
{
    public static ProbeValidationResult Validate(ProbeRequest? request, string expectedNonce)
    {
        if (request is null)
        {
            return Invalid("Request is missing.");
        }

        if (request.ProtocolVersion != ProbeProtocol.CurrentVersion)
        {
            return Invalid("Unsupported protocol version.");
        }

        if (request.RequestId == Guid.Empty)
        {
            return Invalid("RequestId must not be empty.");
        }

        if (!IsValidNonce(request.Nonce) || !IsValidNonce(expectedNonce) ||
            !FixedTimeEquals(request.Nonce, expectedNonce))
        {
            return Invalid("Nonce validation failed.");
        }

        if (!Guid.TryParse(request.HandlerClsid, out _))
        {
            return Invalid("HandlerClsid is not a valid GUID.");
        }

        if (string.IsNullOrWhiteSpace(request.TargetPath) ||
            !Path.IsPathFullyQualified(request.TargetPath))
        {
            return Invalid("TargetPath must be an absolute path.");
        }

        if (!Enum.IsDefined(request.Operation) || !Enum.IsDefined(request.TargetKind))
        {
            return Invalid("Operation or TargetKind is not defined by this protocol version.");
        }

        return new ProbeValidationResult(IsValid: true, Error: null);
    }

    public static string CreateNonce()
    {
        var bytes = RandomNumberGenerator.GetBytes(ProbeProtocol.NonceSizeBytes);
        return Convert.ToBase64String(bytes);
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

    private static bool FixedTimeEquals(string first, string second)
    {
        var firstBytes = Encoding.UTF8.GetBytes(first);
        var secondBytes = Encoding.UTF8.GetBytes(second);
        return firstBytes.Length == secondBytes.Length &&
               CryptographicOperations.FixedTimeEquals(firstBytes, secondBytes);
    }

    private static ProbeValidationResult Invalid(string error) =>
        new(IsValid: false, error);
}
