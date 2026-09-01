[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)]
    [ValidateNotNullOrEmpty()]
    [string]$Version,

    [string]$SigningPrivateKeyPath
)

$ErrorActionPreference = 'Stop'
$semanticVersionPattern = `
    '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)' + `
    '(?:-((?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)' + `
    '(?:\.(?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*))*))?' + `
    '(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$'
if (-not [Text.RegularExpressions.Regex]::IsMatch(
        $Version,
        $semanticVersionPattern,
        [Text.RegularExpressions.RegexOptions]::CultureInvariant)) {
    throw "Version must be a canonical Semantic Version 2.0 value: $Version"
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$serviceRoot = Join-Path $repoRoot 'services\RightMenuCheck.Telemetry'
$packagingRoot = Join-Path $repoRoot 'packaging\linux'
$keyPath = if ([string]::IsNullOrWhiteSpace($SigningPrivateKeyPath)) {
    Join-Path $repoRoot '.secrets\update-signing-private.pem'
}
elseif ([IO.Path]::IsPathRooted($SigningPrivateKeyPath)) {
    [IO.Path]::GetFullPath($SigningPrivateKeyPath)
}
else {
    [IO.Path]::GetFullPath((Join-Path $repoRoot $SigningPrivateKeyPath))
}
if (-not (Test-Path -LiteralPath $keyPath -PathType Leaf)) {
    throw "Telemetry package signing key is missing: $keyPath"
}
$publicKeyPath = Join-Path $repoRoot 'distribution\update-public-key.pem'
if (-not (Test-Path -LiteralPath $publicKeyPath -PathType Leaf)) {
    throw "Telemetry package verification key is missing: $publicKeyPath"
}

$signingKey = [Security.Cryptography.ECDsa]::Create()
$verificationKey = [Security.Cryptography.ECDsa]::Create()
try {
    $signingKey.ImportFromPem([IO.File]::ReadAllText($keyPath))
    $verificationKey.ImportFromPem([IO.File]::ReadAllText($publicKeyPath))
}
catch {
    $signingKey.Dispose()
    $verificationKey.Dispose()
    throw [InvalidDataException]::new(
        'Telemetry package signing key is invalid.',
        $_.Exception)
}

$outputRoot = [IO.Path]::GetFullPath(
    (Join-Path $repoRoot 'artifacts\packages\telemetry'))
$allowedOutputRoot = [IO.Path]::GetFullPath(
    (Join-Path $repoRoot 'artifacts\packages\telemetry'))
if (-not $outputRoot.Equals($allowedOutputRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Package output path validation failed: $outputRoot"
}

New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$targets = @(
    @{ GoArch = 'amd64'; AssetArch = 'amd64' },
    @{ GoArch = 'arm64'; AssetArch = 'arm64' }
)
$previousGoOs = $env:GOOS
$previousGoArch = $env:GOARCH
$previousCgoEnabled = $env:CGO_ENABLED
$assets = [Collections.Generic.List[object]]::new()
try {
    foreach ($target in $targets) {
        $assetName = "rightmenucheck-telemetry-linux-$($target.AssetArch).tar.gz"
        $archivePath = Join-Path $outputRoot $assetName
        $checksumPath = "$archivePath.sha256"
        $signaturePath = "$checksumPath.sig"
        $stageRoot = Join-Path $outputRoot "stage-$($target.AssetArch)"
        if (-not [IO.Path]::GetFullPath($stageRoot).StartsWith(
                $outputRoot + [IO.Path]::DirectorySeparatorChar,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "Stage path escaped package output: $stageRoot"
        }

        if (Test-Path -LiteralPath $stageRoot) {
            Remove-Item -LiteralPath $stageRoot -Recurse -Force
        }
        Remove-Item -LiteralPath $archivePath -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $checksumPath -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $signaturePath -Force -ErrorAction SilentlyContinue
        New-Item -ItemType Directory -Path $stageRoot -Force | Out-Null

        $env:GOOS = 'linux'
        $env:GOARCH = $target.GoArch
        $env:CGO_ENABLED = '0'
        $binaryPath = Join-Path $stageRoot 'rightmenucheck-telemetry'
        $ldFlags = "-s -w -X rightmenucheck.local/telemetry/internal/buildinfo.Version=$Version"
        & go -C $serviceRoot build `
            -trimpath `
            -buildvcs=false `
            -ldflags $ldFlags `
            -o $binaryPath `
            '.\cmd\rightmenucheck-telemetry'
        if ($LASTEXITCODE -ne 0) {
            throw "Telemetry build failed for linux/$($target.GoArch)."
        }

        Copy-Item -LiteralPath `
            (Join-Path $packagingRoot 'rightmenucheck-telemetry.service') `
            -Destination $stageRoot
        Copy-Item -LiteralPath `
            (Join-Path $packagingRoot 'rightmenucheck-telemetry.nginx.conf.template') `
            -Destination $stageRoot
        Set-Content -LiteralPath (Join-Path $stageRoot 'VERSION') `
            -Value $Version -Encoding utf8NoBOM -NoNewline

        & tar -czf $archivePath -C $stageRoot .
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $archivePath -PathType Leaf)) {
            throw "Telemetry archive creation failed for linux/$($target.GoArch)."
        }

        $file = Get-Item -LiteralPath $archivePath
        $sha256 = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
        Set-Content -LiteralPath $checksumPath `
            -Value "$sha256  $assetName" -Encoding ascii
        $signature = $signingKey.SignData(
            ($checksumBytes = [IO.File]::ReadAllBytes($checksumPath)),
            [Security.Cryptography.HashAlgorithmName]::SHA256,
            [Security.Cryptography.DSASignatureFormat]::Rfc3279DerSequence)
        if (-not $verificationKey.VerifyData(
                $checksumBytes,
                $signature,
                [Security.Cryptography.HashAlgorithmName]::SHA256,
                [Security.Cryptography.DSASignatureFormat]::Rfc3279DerSequence)) {
            throw "Telemetry package signing key does not match the distribution public key."
        }
        [IO.File]::WriteAllBytes($signaturePath, $signature)
        $assets.Add([ordered]@{
            architecture = $target.AssetArch
            assetName = $assetName
            sizeBytes = $file.Length
            sha256 = $sha256
            checksumSignatureName = "$assetName.sha256.sig"
        })
        Remove-Item -LiteralPath $stageRoot -Recurse -Force
    }
}
finally {
    $env:GOOS = $previousGoOs
    $env:GOARCH = $previousGoArch
    $env:CGO_ENABLED = $previousCgoEnabled
    $signingKey.Dispose()
    $verificationKey.Dispose()
}

$manifest = [ordered]@{
    schemaVersion = 1
    version = $Version
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString(
        'O',
        [Globalization.CultureInfo]::InvariantCulture)
    assets = $assets
}
$manifestPath = Join-Path $outputRoot 'telemetry-packages.json'
$manifest | ConvertTo-Json -Depth 5 | Set-Content `
    -LiteralPath $manifestPath -Encoding utf8NoBOM

Write-Output $manifestPath
