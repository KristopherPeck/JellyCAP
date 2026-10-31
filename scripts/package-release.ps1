[CmdletBinding()]
param(
    [Parameter()]
    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')]
    [string]$Version,

    [Parameter()]
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    [Parameter()]
    [switch]$SkipBuild,

    [Parameter()]
    [switch]$UpdateManifest
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectPath = Join-Path $repositoryRoot 'Jellyfin.Plugin.JellyCAP.csproj'
$artifactsRoot = Join-Path $repositoryRoot 'artifacts'

[xml]$project = Get-Content -Raw -LiteralPath $projectPath
$projectVersion = [string]$project.Project.PropertyGroup.Version

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = $projectVersion
}

if ($SkipBuild -and $UpdateManifest) {
    throw '-UpdateManifest requires a fresh build. Remove -SkipBuild when producing the final release candidate.'
}

if ($Version -ne $projectVersion) {
    throw "Requested version '$Version' does not match project version '$projectVersion'."
}

foreach ($versionProperty in @('FileVersion', 'AssemblyVersion')) {
    $value = [string]$project.Project.PropertyGroup.$versionProperty
    if ($value -ne $Version) {
        throw "Project $versionProperty '$value' does not match package version '$Version'."
    }
}

$publishRoot = Join-Path $artifactsRoot (Join-Path 'publish' $Version)
$stageRoot = Join-Path $artifactsRoot (Join-Path 'stage' ("JellyCAP_{0}" -f $Version))
$archiveName = "JellyCAP_{0}.zip" -f $Version
$archivePath = Join-Path $artifactsRoot $archiveName
$checksumPath = Join-Path $artifactsRoot ("JellyCAP_{0}.md5.txt" -f $Version)
$sha256Path = Join-Path $artifactsRoot ("JellyCAP_{0}.sha256.txt" -f $Version)

function Assert-SafeArtifactPath {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $rootWithSeparator = $artifactsRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($rootWithSeparator, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify a path outside the repository artifacts directory: $fullPath"
    }
}

function ConvertFrom-ManifestJson {
    param(
        [Parameter(Mandatory)]
        [string]$Json
    )

    $convertFromJson = Get-Command ConvertFrom-Json
    $parsed = $null
    if ($convertFromJson.Parameters.ContainsKey('DateKind')) {
        $parsed = ConvertFrom-Json -InputObject $Json -DateKind String
    }
    else {
        $parsed = ConvertFrom-Json -InputObject $Json
    }

    # Emit root-array entries individually. Returning the ConvertFrom-Json command
    # directly wraps a single-element JSON array as { value, Count } on some
    # PowerShell versions when ConvertTo-Json later receives it via -InputObject.
    foreach ($entry in $parsed) {
        Write-Output $entry
    }
}

function Assert-ManifestShape {
    param(
        [Parameter(Mandatory)]
        [object[]]$Entries
    )

    if ($Entries.Count -eq 0) {
        throw 'manifest.json must contain at least one plugin entry.'
    }

    foreach ($entry in $Entries) {
        $hasGuid = $null -ne $entry -and $entry.PSObject.Properties.Name -contains 'guid'
        $hasVersions = $null -ne $entry -and $entry.PSObject.Properties.Name -contains 'versions'
        if (-not $hasGuid -or -not $hasVersions) {
            throw 'manifest.json root entries must be plugin objects containing guid and versions properties.'
        }
    }
}

function ConvertTo-ManifestTimestamp {
    param(
        [Parameter()]
        [AllowNull()]
        [object]$Value
    )

    if ($Value -is [DateTimeOffset]) {
        return $Value.ToUniversalTime().ToString(
            "yyyy-MM-dd'T'HH:mm:ss'Z'",
            [System.Globalization.CultureInfo]::InvariantCulture)
    }

    if ($Value -is [DateTime]) {
        return $Value.ToUniversalTime().ToString(
            "yyyy-MM-dd'T'HH:mm:ss'Z'",
            [System.Globalization.CultureInfo]::InvariantCulture)
    }

    return [string]$Value
}

New-Item -ItemType Directory -Path $artifactsRoot -Force | Out-Null

foreach ($path in @($stageRoot, $archivePath, $checksumPath, $sha256Path)) {
    Assert-SafeArtifactPath -Path $path
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Recurse -Force
    }
}

if (-not $SkipBuild) {
    Assert-SafeArtifactPath -Path $publishRoot
    if (Test-Path -LiteralPath $publishRoot) {
        Remove-Item -LiteralPath $publishRoot -Recurse -Force
    }

    & dotnet restore $projectPath
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet restore failed with exit code $LASTEXITCODE."
    }

    & dotnet publish $projectPath --configuration $Configuration --no-restore --output $publishRoot
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }
}
elseif (-not (Test-Path -LiteralPath $publishRoot -PathType Container)) {
    throw "Publish output was not found at '$publishRoot'. Remove -SkipBuild or provide an existing publish output."
}

$framework = [string]$project.Project.PropertyGroup.TargetFramework
$buildRoot = Join-Path $repositoryRoot (Join-Path 'bin' (Join-Path $Configuration $framework))
$requiredFiles = @(
    @{ Source = (Join-Path $publishRoot 'Jellyfin.Plugin.JellyCAP.dll'); Name = 'Jellyfin.Plugin.JellyCAP.dll' },
    @{ Source = (Join-Path $publishRoot 'Jellyfin.Plugin.JellyCAP.deps.json'); Name = 'Jellyfin.Plugin.JellyCAP.deps.json' },
    @{ Source = (Join-Path $buildRoot 'Microsoft.Data.Sqlite.dll'); Name = 'Microsoft.Data.Sqlite.dll' },
    @{ Source = (Join-Path $buildRoot 'SQLitePCLRaw.core.dll'); Name = 'SQLitePCLRaw.core.dll' },
    @{ Source = (Join-Path $buildRoot 'SQLitePCLRaw.provider.e_sqlite3.dll'); Name = 'SQLitePCLRaw.provider.e_sqlite3.dll' },
    @{ Source = (Join-Path $repositoryRoot 'LICENSE.md'); Name = 'LICENSE.md' },
    @{ Source = (Join-Path $repositoryRoot 'THIRD-PARTY-NOTICES.md'); Name = 'THIRD-PARTY-NOTICES.md' }
)

foreach ($file in $requiredFiles) {
    if (-not (Test-Path -LiteralPath $file.Source -PathType Leaf)) {
        throw "Required package file was not found: $($file.Source)"
    }
}

$pluginAssemblyPath = Join-Path $publishRoot 'Jellyfin.Plugin.JellyCAP.dll'
$pluginAssemblyVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($pluginAssemblyPath).FileVersion
if ($pluginAssemblyVersion -ne $Version) {
    throw "Published plugin file version '$pluginAssemblyVersion' does not match package version '$Version'."
}

New-Item -ItemType Directory -Path $stageRoot -Force | Out-Null
foreach ($file in $requiredFiles) {
    Copy-Item -LiteralPath $file.Source -Destination (Join-Path $stageRoot $file.Name)
}

foreach ($optionalName in @('Jellyfin.Plugin.JellyCAP.xml')) {
    $optionalPath = Join-Path $publishRoot $optionalName
    if (Test-Path -LiteralPath $optionalPath -PathType Leaf) {
        Copy-Item -LiteralPath $optionalPath -Destination (Join-Path $stageRoot $optionalName)
    }
}

$prohibitedNames = @(
    'Jellyfin.Plugin.JellyCAP.pdb',
    'e_sqlite3.dll',
    'libe_sqlite3.so',
    'libe_sqlite3.dylib',
    'SQLitePCLRaw.batteries_v2.dll'
)
$prohibitedFiles = Get-ChildItem -LiteralPath $stageRoot -Recurse -File |
    Where-Object { $_.Name -in $prohibitedNames }
$runtimeDirectories = Get-ChildItem -LiteralPath $stageRoot -Recurse -Directory |
    Where-Object { $_.Name -eq 'runtimes' }

if ($prohibitedFiles -or $runtimeDirectories) {
    throw 'The staged package contains prohibited debug symbols, a native SQLite runtime, or a runtimes directory.'
}

Compress-Archive -Path (Join-Path $stageRoot '*') -DestinationPath $archivePath -CompressionLevel Optimal
$hash = Get-FileHash -LiteralPath $archivePath -Algorithm MD5
$sha256Hash = Get-FileHash -LiteralPath $archivePath -Algorithm SHA256
$releaseTimestamp = [DateTimeOffset]::UtcNow.ToString(
    "yyyy-MM-dd'T'HH:mm:ss'Z'",
    [System.Globalization.CultureInfo]::InvariantCulture)
Set-Content -LiteralPath $checksumPath -Value ("{0}  {1}" -f $hash.Hash, $archiveName) -Encoding ascii
Set-Content -LiteralPath $sha256Path -Value ("{0}  {1}" -f $sha256Hash.Hash, $archiveName) -Encoding ascii

Write-Host "Created $archivePath"
Write-Host "MD5: $($hash.Hash)"
Write-Host "SHA-256: $($sha256Hash.Hash)"
Write-Host "Manifest timestamp: $releaseTimestamp"

$manifestPath = Join-Path $repositoryRoot 'manifest.json'
$manifestEntries = @(ConvertFrom-ManifestJson -Json (Get-Content -Raw -LiteralPath $manifestPath))
Assert-ManifestShape -Entries $manifestEntries
$manifestMatches = @(
    foreach ($pluginEntry in $manifestEntries) {
        foreach ($versionEntry in @($pluginEntry.versions)) {
            if ([string]$versionEntry.version -eq $Version) {
                [pscustomobject]@{
                    Plugin = $pluginEntry
                    Version = $versionEntry
                }
            }
        }
    }
)

if ($manifestMatches.Count -eq 0) {
    $message = "manifest.json has no entry for version $Version."
    if ($UpdateManifest) {
        throw "$message Add the release entry before packaging with -UpdateManifest."
    }

    Write-Warning $message
}
elseif ($manifestMatches.Count -gt 1) {
    $message = "manifest.json contains $($manifestMatches.Count) entries for version $Version."
    if ($UpdateManifest) {
        throw "$message Refusing to update an ambiguous release."
    }

    Write-Warning $message
}
elseif ($UpdateManifest) {
    $manifestVersion = $manifestMatches[0].Version
    $expectedArchiveSuffix = "/$archiveName"
    $manifestSourceUrl = [string]$manifestVersion.sourceUrl
    if (-not $manifestSourceUrl.EndsWith($expectedArchiveSuffix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Manifest sourceUrl '$($manifestVersion.sourceUrl)' does not end with '$expectedArchiveSuffix'."
    }

    $manifestVersion.checksum = $hash.Hash
    $manifestVersion.timestamp = $releaseTimestamp
    $manifestJson = ConvertTo-Json -InputObject $manifestEntries -Depth 10
    $utf8WithoutBom = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText(
        $manifestPath,
        $manifestJson + [System.Environment]::NewLine,
        $utf8WithoutBom)

    $savedManifest = @(ConvertFrom-ManifestJson -Json (Get-Content -Raw -LiteralPath $manifestPath))
    Assert-ManifestShape -Entries $savedManifest
    $savedVersion = @(
        foreach ($pluginEntry in $savedManifest) {
            foreach ($versionEntry in @($pluginEntry.versions)) {
                if ([string]$versionEntry.version -eq $Version) {
                    $versionEntry
                }
            }
        }
    )
    if ($savedVersion.Count -ne 1) {
        throw "manifest.json verification found $($savedVersion.Count) entries for version $Version; expected exactly one."
    }

    $savedChecksum = [string]$savedVersion[0].checksum
    $savedTimestamp = ConvertTo-ManifestTimestamp -Value $savedVersion[0].timestamp
    if ($savedChecksum -ne $hash.Hash) {
        throw "manifest.json checksum verification failed. Expected '$($hash.Hash)' but read '$savedChecksum'."
    }

    if ($savedTimestamp -ne $releaseTimestamp) {
        throw "manifest.json timestamp verification failed. Expected '$releaseTimestamp' but read '$savedTimestamp'."
    }

    Write-Host "Updated $manifestPath"
    Write-Host "Manifest checksum: $($hash.Hash)"
    Write-Host "Manifest timestamp: $releaseTimestamp"
}
elseif ([string]$manifestMatches[0].Version.checksum -eq $hash.Hash) {
    Write-Host 'The generated archive checksum matches manifest.json.'
}
else {
    Write-Warning "manifest.json checksum '$($manifestMatches[0].Version.checksum)' does not match the generated archive. When producing the final candidate, rerun with -UpdateManifest and test that generated ZIP before publishing it."
}
