[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)]
    [ValidateNotNullOrEmpty()]
    [string]$Version,

    [switch]$SkipApplicationPublish,

    [string]$SourceRevisionId
)

$ErrorActionPreference = 'Stop'
$semanticVersionPattern = `
    '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)' + `
    '(?:-((?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)' + `
    '(?:\.(?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*))*))?' + `
    '(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$'
$semanticVersion = [Text.RegularExpressions.Regex]::Match(
    $Version,
    $semanticVersionPattern,
    [Text.RegularExpressions.RegexOptions]::CultureInvariant)
if (-not $semanticVersion.Success) {
    throw "Version must be a canonical Semantic Version 2.0 value: $Version"
}

$coreComponents = for ($index = 1; $index -le 3; $index++) {
    $component = 0
    if (-not [int]::TryParse(
            $semanticVersion.Groups[$index].Value,
            [Globalization.NumberStyles]::None,
            [Globalization.CultureInfo]::InvariantCulture,
            [ref]$component)) {
        throw "Version core component exceeds the supported Int32 range: $Version"
    }

    $component
}
$versionPrefix = [string]::Join('.', $coreComponents)
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$publishRoot = [IO.Path]::GetFullPath(
    (Join-Path $repoRoot 'artifacts\publish\RightMenuCheck'))
$outputRoot = [IO.Path]::GetFullPath(
    (Join-Path $repoRoot 'artifacts\packages\windows'))
$allowedOutputRoot = [IO.Path]::GetFullPath(
    (Join-Path $repoRoot 'artifacts\packages\windows'))
if (-not $outputRoot.Equals($allowedOutputRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Windows package output path validation failed: $outputRoot"
}

if (-not $SkipApplicationPublish) {
    $publishArguments = @('-Version', $Version)
    if (-not [string]::IsNullOrWhiteSpace($SourceRevisionId)) {
        $publishArguments += @('-SourceRevisionId', $SourceRevisionId)
    }
    & pwsh -NoLogo -NoProfile -File `
        (Join-Path $repoRoot 'scripts\publish.ps1') `
        @publishArguments
    if ($LASTEXITCODE -ne 0) {
        throw 'Application publish failed before setup packaging.'
    }
}

$buildInfoPath = Join-Path $publishRoot 'build-info.json'
if (-not (Test-Path -LiteralPath $buildInfoPath -PathType Leaf)) {
    throw "Canonical application publish is missing build-info.json: $publishRoot"
}
$buildInfo = Get-Content -LiteralPath $buildInfoPath -Raw | ConvertFrom-Json
if (-not ([string]$buildInfo.product).Equals(
        'RightMenuCheck',
        [StringComparison]::Ordinal) -or
    -not ([string]$buildInfo.version).Equals(
        $Version,
        [StringComparison]::Ordinal) -or
    $buildInfo.selfContained -ne $true) {
    throw "Canonical application publish does not match setup version $Version."
}
if (-not [string]::IsNullOrWhiteSpace($SourceRevisionId) -and
    -not ([string]$buildInfo.commit).Equals(
        $SourceRevisionId.Trim(),
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "Canonical application publish does not match source revision $SourceRevisionId."
}

$requiredPublishFiles = @(
    'RightMenuCheck.App.exe',
    'build-info.json',
    'helpers\RightMenuCheck.Elevated.exe',
    'helpers\updater\RightMenuCheck.Updater.exe',
    'workers\x64\RightMenuCheck.Probe.Worker.exe',
    'workers\x86\RightMenuCheck.Probe.Worker.exe',
    'workers\arm64\RightMenuCheck.Probe.Worker.exe'
)
foreach ($relativePath in $requiredPublishFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $publishRoot $relativePath) -PathType Leaf)) {
        throw "Canonical application publish is missing: $relativePath"
    }
}

$publishEntries = @(Get-ChildItem -LiteralPath $publishRoot -Force -Recurse)
foreach ($entry in $publishEntries) {
    if ($entry.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
        throw "Publish tree contains a reparse point: $($entry.FullName)"
    }
}
$publishFiles = @($publishEntries |
    Where-Object { -not $_.PSIsContainer } |
    Sort-Object FullName)
if ($publishFiles.Count -eq 0) {
    throw 'Canonical application publish is empty.'
}
foreach ($file in $publishFiles) {
    $relativePath = [IO.Path]::GetRelativePath($publishRoot, $file.FullName)
    $segments = $relativePath -split '[\\/]'
    if ($file.Name.Equals('github-conf.json', [StringComparison]::OrdinalIgnoreCase) -or
        $file.Name.Equals('maidian.json', [StringComparison]::OrdinalIgnoreCase) -or
        [bool]($segments | Where-Object {
            $_.Equals('.secrets', [StringComparison]::OrdinalIgnoreCase)
        })) {
        throw "Publish tree contains private configuration: $relativePath"
    }
}

New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$payloadName = "RightMenuCheck-$Version-win-x64.zip"
$payloadPath = Join-Path $outputRoot $payloadName
Remove-Item -LiteralPath $payloadPath -Force -ErrorAction SilentlyContinue
$payloadStream = [IO.FileStream]::new(
    $payloadPath,
    [IO.FileMode]::CreateNew,
    [IO.FileAccess]::ReadWrite,
    [IO.FileShare]::None)
try {
    $archive = [IO.Compression.ZipArchive]::new(
        $payloadStream,
        [IO.Compression.ZipArchiveMode]::Create,
        $true)
    try {
        $entryTimestamp = [DateTimeOffset]::new(
            2000,
            1,
            1,
            0,
            0,
            0,
            [TimeSpan]::Zero)
        foreach ($file in $publishFiles) {
            $relativePath = [IO.Path]::GetRelativePath($publishRoot, $file.FullName)
            $relativePath = $relativePath.Replace([IO.Path]::DirectorySeparatorChar, '/')
            $entry = $archive.CreateEntry(
                $relativePath,
                [IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = $entryTimestamp
            $entryStream = $entry.Open()
            $sourceStream = $file.OpenRead()
            try {
                $sourceStream.CopyTo($entryStream)
            }
            finally {
                $sourceStream.Dispose()
                $entryStream.Dispose()
            }
        }
    }
    finally {
        $archive.Dispose()
    }
}
finally {
    $payloadStream.Dispose()
}

$payloadSha256 = (Get-FileHash -LiteralPath $payloadPath -Algorithm SHA256).Hash
$commit = if ([string]::IsNullOrWhiteSpace($SourceRevisionId)) {
    (& git -C $repoRoot rev-parse HEAD).Trim()
}
else {
    $SourceRevisionId.Trim().ToLowerInvariant()
}
if (($LASTEXITCODE -ne 0 -and [string]::IsNullOrWhiteSpace($SourceRevisionId)) -or
    $commit -notmatch '^[0-9a-f]{40}$') {
    throw 'Unable to resolve the current Git commit.'
}
$commonProperties = @(
    '-p:ContinuousIntegrationBuild=true',
    "-p:SourceRevisionId=$commit",
    "-p:Version=$Version",
    "-p:VersionPrefix=$versionPrefix",
    "-p:InformationalVersion=$Version",
    '-p:IncludeSourceRevisionInInformationalVersion=false',
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:EnableCompressionInSingleFile=true',
    '-p:DebugType=None',
    '-p:DebugSymbols=false'
)

$uninstallerOutput = Join-Path $outputRoot 'uninstaller'
$setupOutput = Join-Path $outputRoot 'setup'
foreach ($directory in @($uninstallerOutput, $setupOutput)) {
    if (Test-Path -LiteralPath $directory) {
        Remove-Item -LiteralPath $directory -Recurse -Force
    }
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
}

& dotnet publish `
    (Join-Path $repoRoot 'src\RightMenuCheck.Uninstaller\RightMenuCheck.Uninstaller.csproj') `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $uninstallerOutput `
    '-p:PublishAot=true' `
    '-p:StripSymbols=true' `
    @commonProperties
if ($LASTEXITCODE -ne 0) {
    throw 'Uninstaller publish failed.'
}
$uninstallerPath = Join-Path $uninstallerOutput 'RightMenuCheck.Uninstaller.exe'
if (-not (Test-Path -LiteralPath $uninstallerPath -PathType Leaf)) {
    throw 'Published uninstaller executable is missing.'
}

$setupProperties = @(
    '-p:BuildingSetupPackage=true',
    "-p:PayloadPath=$payloadPath",
    "-p:PayloadSha256=$payloadSha256",
    "-p:PayloadVersion=$Version",
    "-p:UninstallerPath=$uninstallerPath"
)
& dotnet publish `
    (Join-Path $repoRoot 'src\RightMenuCheck.Installer\RightMenuCheck.Installer.csproj') `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $setupOutput `
    @commonProperties `
    @setupProperties
if ($LASTEXITCODE -ne 0) {
    throw 'Setup publish failed.'
}

$publishedSetupPath = Join-Path $setupOutput 'RightMenuCheck.Installer.exe'
$setupName = "RightMenuCheck-$Version-Setup.exe"
$setupPath = Join-Path $outputRoot $setupName
if (-not (Test-Path -LiteralPath $publishedSetupPath -PathType Leaf)) {
    throw 'Published setup executable is missing.'
}
Copy-Item -LiteralPath $publishedSetupPath -Destination $setupPath -Force
$manifest = [ordered]@{
    schemaVersion = 1
    product = 'RightMenuCheck'
    version = $Version
    commit = $commit
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString(
        'O',
        [Globalization.CultureInfo]::InvariantCulture)
    setup = [ordered]@{
        assetName = $setupName
        sizeBytes = (Get-Item -LiteralPath $setupPath).Length
        sha256 = (Get-FileHash -LiteralPath $setupPath -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    payload = [ordered]@{
        assetName = $payloadName
        sizeBytes = (Get-Item -LiteralPath $payloadPath).Length
        sha256 = $payloadSha256.ToLowerInvariant()
    }
    uninstaller = [ordered]@{
        sizeBytes = (Get-Item -LiteralPath $uninstallerPath).Length
        sha256 = (Get-FileHash -LiteralPath $uninstallerPath -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}
$manifestPath = Join-Path $outputRoot 'windows-packages.json'
$manifest | ConvertTo-Json -Depth 5 | Set-Content `
    -LiteralPath $manifestPath -Encoding utf8NoBOM

Write-Output $setupPath
