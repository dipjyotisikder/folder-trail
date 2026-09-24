<#
.SYNOPSIS
    Builds, tests and packages FolderTrail for Windows x64.

.DESCRIPTION
    Produces two self-contained builds (no .NET installation needed on the target machine):
      artifacts/publish/win-x64         folder build: FolderTrail.exe plus its libraries
      artifacts/publish/win-x64-single  single-file build: one FolderTrail.exe
    and zips each into artifacts/release with a SHA-256 checksum file.
    The executables are not code signed.

.EXAMPLE
    ./scripts/publish.ps1
    ./scripts/publish.ps1 -SkipTests
#>
param(
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$project = 'src/FolderTrail.App/FolderTrail.App.csproj'
[xml]$props = Get-Content 'Directory.Build.props'
$version = $props.Project.PropertyGroup.Version | Select-Object -First 1
$publish = Join-Path $root 'artifacts/publish'
$release = Join-Path $root 'artifacts/release'

function Invoke-Step([string]$title, [scriptblock]$action) {
    Write-Host "==> $title"
    & $action
    if ($LASTEXITCODE -ne 0) { throw "$title failed with exit code $LASTEXITCODE" }
}

if (-not $SkipTests) {
    Invoke-Step 'Running tests' { dotnet test FolderTrail.slnx -c Release --nologo }
}

Remove-Item -Recurse -Force $publish, $release -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $release | Out-Null

Invoke-Step 'Publishing self-contained folder build' {
    dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o (Join-Path $publish 'win-x64') --nologo
}
Invoke-Step 'Publishing self-contained single-file build' {
    dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
        -o (Join-Path $publish 'win-x64-single') --nologo
}

$checksums = @()
foreach ($variant in 'win-x64', 'win-x64-single') {
    $zip = Join-Path $release "FolderTrail-$version-$variant.zip"
    Compress-Archive -Path (Join-Path $publish "$variant/*") -DestinationPath $zip -Force
    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    $checksums += "$hash  $(Split-Path -Leaf $zip)"
    $size = [math]::Round((Get-Item $zip).Length / 1MB, 1)
    Write-Host "    $(Split-Path -Leaf $zip)  $size MB  sha256 $hash"
}
$checksums | Set-Content -Encoding ascii (Join-Path $release 'SHA256SUMS.txt')

Write-Host "Release files are in $release (not code signed)."
