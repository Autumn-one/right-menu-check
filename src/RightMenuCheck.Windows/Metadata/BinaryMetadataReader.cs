using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Security;
using System.Security.Cryptography;
using RightMenuCheck.Core.Metadata;

namespace RightMenuCheck.Windows.Metadata;

public interface IBinaryMetadataReader
{
    BinaryFileMetadata Read(string filePath);
}

public sealed class BinaryMetadataReader : IBinaryMetadataReader
{
    public BinaryFileMetadata Read(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var issues = new List<MetadataIssue>();
        string normalizedPath;
        try
        {
            normalizedPath = Path.GetFullPath(filePath);
        }
        catch (ArgumentException exception)
        {
            return CreateInvalidPathResult(filePath, exception);
        }
        catch (NotSupportedException exception)
        {
            return CreateInvalidPathResult(filePath, exception);
        }
        catch (PathTooLongException exception)
        {
            return CreateInvalidPathResult(filePath, exception);
        }

        if (!File.Exists(normalizedPath))
        {
            issues.Add(new MetadataIssue(
                normalizedPath,
                "ReadBinaryMetadata",
                "FileNotFound",
                "COM server file does not exist."));
            return CreateMissingFileResult(normalizedPath, issues);
        }

        long? size = null;
        DateTimeOffset? lastWriteTime = null;
        string? sha256 = null;
        var architecture = BinaryArchitectureKind.Unknown;
        var isManaged = false;
        FileVersionInfo? versionInfo = null;
        var signature = new AuthenticodeSignatureMetadata(
            SignatureVerificationStatus.Unknown,
            PublisherName: null,
            Subject: null,
            Issuer: null,
            Thumbprint: null,
            ValidFrom: null,
            ValidTo: null,
            TrustErrorCode: null);

        try
        {
            var fileInfo = new FileInfo(normalizedPath);
            size = fileInfo.Length;
            lastWriteTime = fileInfo.LastWriteTimeUtc;

            using var stream = new FileStream(
                normalizedPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 81920,
                FileOptions.SequentialScan);
            sha256 = Convert.ToHexString(SHA256.HashData(stream));
            stream.Position = 0;

            try
            {
                using var peReader = new PEReader(stream, PEStreamOptions.LeaveOpen);
                (architecture, isManaged) = ReadArchitecture(peReader.PEHeaders);
            }
            catch (BadImageFormatException exception)
            {
                issues.Add(new MetadataIssue(
                    normalizedPath,
                    "ReadPeHeaders",
                    exception.GetType().Name,
                    exception.Message));
            }

            versionInfo = FileVersionInfo.GetVersionInfo(normalizedPath);
            signature = AuthenticodeVerifier.Verify(normalizedPath);
        }
        catch (UnauthorizedAccessException exception)
        {
            AddFileIssue(exception);
        }
        catch (SecurityException exception)
        {
            AddFileIssue(exception);
        }
        catch (IOException exception)
        {
            AddFileIssue(exception);
        }
        catch (CryptographicException exception)
        {
            AddFileIssue(exception);
        }

        return new BinaryFileMetadata(
            normalizedPath,
            Exists: true,
            size,
            lastWriteTime,
            sha256,
            architecture,
            isManaged,
            versionInfo?.FileVersion,
            versionInfo?.ProductVersion,
            versionInfo?.ProductName,
            versionInfo?.FileDescription,
            versionInfo?.CompanyName,
            signature,
            issues);

        void AddFileIssue(Exception exception)
        {
            issues.Add(new MetadataIssue(
                normalizedPath,
                "ReadBinaryMetadata",
                exception.GetType().Name,
                exception.Message));
        }
    }

    private static (BinaryArchitectureKind Architecture, bool IsManaged) ReadArchitecture(
        PEHeaders headers)
    {
        var isManaged = headers.CorHeader is not null;
        var machine = headers.CoffHeader.Machine;

        if (isManaged && machine == Machine.I386 && headers.CorHeader is { } corHeader)
        {
            if ((corHeader.Flags & CorFlags.Requires32Bit) != 0)
            {
                return (BinaryArchitectureKind.X86, true);
            }

            if ((corHeader.Flags & CorFlags.Prefers32Bit) != 0)
            {
                return (BinaryArchitectureKind.AnyCpuPrefer32Bit, true);
            }

            if ((corHeader.Flags & CorFlags.ILOnly) != 0)
            {
                return (BinaryArchitectureKind.AnyCpu, true);
            }
        }

        var architecture = machine switch
        {
            Machine.I386 => BinaryArchitectureKind.X86,
            Machine.Amd64 => BinaryArchitectureKind.X64,
            Machine.Arm or Machine.ArmThumb2 => BinaryArchitectureKind.Arm,
            Machine.Arm64 => BinaryArchitectureKind.Arm64,
            _ => BinaryArchitectureKind.Unknown,
        };
        return (architecture, isManaged);
    }

    private static BinaryFileMetadata CreateInvalidPathResult(
        string filePath,
        Exception exception)
    {
        var issues = new[]
        {
            new MetadataIssue(
                filePath,
                "NormalizeBinaryPath",
                exception.GetType().Name,
                exception.Message),
        };
        return CreateMissingFileResult(filePath, issues);
    }

    private static BinaryFileMetadata CreateMissingFileResult(
        string filePath,
        IReadOnlyList<MetadataIssue> issues) =>
        new(
            filePath,
            Exists: false,
            Size: null,
            LastWriteTime: null,
            Sha256: null,
            BinaryArchitectureKind.Unknown,
            IsManaged: false,
            FileVersion: null,
            ProductVersion: null,
            ProductName: null,
            Description: null,
            CompanyName: null,
            new AuthenticodeSignatureMetadata(
                SignatureVerificationStatus.Unknown,
                PublisherName: null,
                Subject: null,
                Issuer: null,
                Thumbprint: null,
                ValidFrom: null,
                ValidTo: null,
                TrustErrorCode: null),
            issues);
}
