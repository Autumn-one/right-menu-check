[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)]
    [ValidateNotNullOrEmpty()]
    [string]$Version
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
$allowedPublishRoot = [IO.Path]::GetFullPath(
    (Join-Path $repoRoot 'artifacts\publish\RightMenuCheck'))

if (-not $publishRoot.Equals($allowedPublishRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Publish path validation failed: $publishRoot"
}

if (Test-Path -LiteralPath $publishRoot) {
    Remove-Item -LiteralPath $publishRoot -Recurse -Force
}

New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null
$commit = (& git -C $repoRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-f]{40}$') {
    throw 'Unable to resolve the current Git commit.'
}

$commonPublishArguments = @(
    '--configuration', 'Release',
    '--self-contained', 'true',
    '-p:ContinuousIntegrationBuild=true',
    "-p:SourceRevisionId=$commit",
    "-p:Version=$Version",
    "-p:VersionPrefix=$versionPrefix",
    "-p:InformationalVersion=$Version",
    '-p:IncludeSourceRevisionInInformationalVersion=false'
)

& dotnet publish `
    (Join-Path $repoRoot 'src\RightMenuCheck.App\RightMenuCheck.App.csproj') `
    --runtime win-x64 `
    --output $publishRoot `
    @commonPublishArguments
if ($LASTEXITCODE -ne 0) { throw 'Application publish failed.' }

$workerTargets = @(
    @{ Runtime = 'win-x64'; Directory = 'x64' },
    @{ Runtime = 'win-x86'; Directory = 'x86' },
    @{ Runtime = 'win-arm64'; Directory = 'arm64' }
)
foreach ($target in $workerTargets) {
    $workerOutput = Join-Path $publishRoot "workers\$($target.Directory)"
    & dotnet publish `
        (Join-Path $repoRoot 'src\RightMenuCheck.Probe.Worker\RightMenuCheck.Probe.Worker.csproj') `
        --runtime $target.Runtime `
        --output $workerOutput `
        @commonPublishArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Probe worker publish failed for $($target.Runtime)."
    }
}

$helperOutput = Join-Path $publishRoot 'helpers'
& dotnet publish `
    (Join-Path $repoRoot 'src\RightMenuCheck.Elevated\RightMenuCheck.Elevated.csproj') `
    --runtime win-x64 `
    --output $helperOutput `
    @commonPublishArguments
if ($LASTEXITCODE -ne 0) { throw 'Elevated helper publish failed.' }

$updaterOutput = Join-Path $publishRoot 'helpers\updater'
& dotnet publish `
    (Join-Path $repoRoot 'src\RightMenuCheck.Updater\RightMenuCheck.Updater.csproj') `
    --runtime win-x64 `
    --output $updaterOutput `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    @commonPublishArguments
if ($LASTEXITCODE -ne 0) { throw 'Updater publish failed.' }

$buildInfo = [ordered]@{
    product = 'RightMenuCheck'
    version = $Version
    commit = $commit
    builtAtUtc = [DateTimeOffset]::UtcNow.ToString('O', [Globalization.CultureInfo]::InvariantCulture)
    applicationRuntime = 'win-x64'
    workerRuntimes = @('win-x64', 'win-x86', 'win-arm64')
    updaterRuntime = 'win-x64'
    selfContained = $true
}
$buildInfo | ConvertTo-Json -Depth 4 | Set-Content `
    -LiteralPath (Join-Path $publishRoot 'build-info.json') `
    -Encoding utf8NoBOM

$requiredFiles = @(
    (Join-Path $publishRoot 'RightMenuCheck.App.exe'),
    (Join-Path $publishRoot 'workers\x64\RightMenuCheck.Probe.Worker.exe'),
    (Join-Path $publishRoot 'workers\x86\RightMenuCheck.Probe.Worker.exe'),
    (Join-Path $publishRoot 'workers\arm64\RightMenuCheck.Probe.Worker.exe'),
    (Join-Path $publishRoot 'helpers\RightMenuCheck.Elevated.exe'),
    (Join-Path $publishRoot 'helpers\updater\RightMenuCheck.Updater.exe'),
    (Join-Path $publishRoot 'build-info.json')
)
foreach ($requiredFile in $requiredFiles) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
        throw "Required publish artifact is missing: $requiredFile"
    }
}

Write-Output $publishRoot
