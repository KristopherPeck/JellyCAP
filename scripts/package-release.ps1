[CmdletBinding()]
param(
    [Parameter()]
    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')]
    [string]$Version,

    [Parameter()]
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    [Parameter()]
    [switch]$SkipBuild
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

New-Item -ItemType Directory -Path $artifactsRoot -Force | Out-Null

foreach ($path in @($stageRoot, $archivePath, $checksumPath)) {
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
Set-Content -LiteralPath $checksumPath -Value ("{0}  {1}" -f $hash.Hash, $archiveName) -Encoding ascii

Write-Host "Created $archivePath"
Write-Host "MD5: $($hash.Hash)"

$manifestPath = Join-Path $repositoryRoot 'manifest.json'
$manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
$manifestVersion = $manifest.versions | Where-Object { $_.version -eq $Version } | Select-Object -First 1
if ($null -eq $manifestVersion) {
    Write-Warning "manifest.json has no entry for version $Version."
}
elseif ([string]$manifestVersion.checksum -eq $hash.Hash) {
    Write-Host 'The generated archive checksum matches manifest.json.'
}
else {
    Write-Warning "manifest.json checksum '$($manifestVersion.checksum)' does not match the generated archive. Update it to '$($hash.Hash)' only after the ZIP passes testing."
}
