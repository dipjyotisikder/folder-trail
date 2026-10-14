<#
.SYNOPSIS
    Builds the Microsoft Store (MSIX) package of FolderTrail.

.DESCRIPTION
    Publishes the self-contained win-x64 build, lays it out with the manifest and logo assets, and packs
    artifacts/msix/FolderTrail_<version>_x64.msix. makeappx/makepri come from an installed Windows SDK, or are
    downloaded once (Microsoft.Windows.SDK.BuildTools from NuGet) into artifacts/tools.

    Store upload: pass the identity values from Partner Center -> Product identity. The result is unsigned
    (the Store signs it). Local test install: add -SignForSideload, which creates a self-signed certificate
    and signs the package with it; trusting that certificate (an elevated step) is printed at the end.

.EXAMPLE
    ./scripts/package-msix.ps1 -IdentityName 12345Publisher.FolderTrail -Publisher 'CN=ABCD-1234' -PublisherDisplayName 'Your Name'
    ./scripts/package-msix.ps1 -SignForSideload
#>
param(
    [string]$IdentityName = 'FolderTrail.Dev',
    [string]$Publisher = 'CN=FolderTrail Dev',
    [string]$PublisherDisplayName = 'FolderTrail',
    [switch]$SignForSideload,
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

[xml]$props = Get-Content 'Directory.Build.props'
$version = ($props.Project.PropertyGroup.Version | Select-Object -First 1)
$packageVersion = "$version.0"   # MSIX versions are four-part; the Store reserves the last part for itself (keep 0).

$publishDir = Join-Path $root 'artifacts/publish/win-x64'
$out = Join-Path $root 'artifacts/msix'
$layout = Join-Path $out 'layout'
$toolsDir = Join-Path $root 'artifacts/tools'
$buildToolsVersion = '10.0.28000.1839'

function Invoke-Step([string]$title, [scriptblock]$action) {
    Write-Host "==> $title"
    & $action
    if ($LASTEXITCODE -ne 0) { throw "$title failed with exit code $LASTEXITCODE" }
}

function Find-SdkTool([string]$name) {
    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin'
    $found = $null
    if (Test-Path $kits) {
        $found = Get-ChildItem $kits -Recurse -Filter $name -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '[\\/]x64[\\/]' } | Sort-Object FullName -Descending | Select-Object -First 1
    }
    if (-not $found) {
        $cached = Join-Path $toolsDir "buildtools-$buildToolsVersion"
        if (-not (Test-Path $cached)) {
            Write-Host "==> Downloading Microsoft.Windows.SDK.BuildTools $buildToolsVersion"
            New-Item -ItemType Directory -Force $toolsDir | Out-Null
            $nupkg = Join-Path $toolsDir 'buildtools.zip'
            Invoke-WebRequest "https://api.nuget.org/v3-flatcontainer/microsoft.windows.sdk.buildtools/$buildToolsVersion/microsoft.windows.sdk.buildtools.$buildToolsVersion.nupkg" -OutFile $nupkg
            Expand-Archive $nupkg -DestinationPath $cached -Force
            Remove-Item $nupkg
        }
        $found = Get-ChildItem $cached -Recurse -Filter $name | Where-Object { $_.FullName -match '[\\/]x64[\\/]' } | Select-Object -First 1
    }
    if (-not $found) { throw "$name not found." }
    return $found.FullName
}

function Save-Logo($image, [string]$path, [int]$width, [int]$height, [double]$fill) {
    $bitmap = New-Object System.Drawing.Bitmap $width, $height
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $side = [int]([math]::Min($width, $height) * $fill)
    $g.DrawImage($image, [int](($width - $side) / 2), [int](($height - $side) / 2), $side, $side)
    $g.Dispose()
    $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
}

function New-LogoSet([string]$source, [string]$target) {
    Add-Type -AssemblyName System.Drawing
    $image = [System.Drawing.Image]::FromFile($source)
    New-Item -ItemType Directory -Force $target | Out-Null
    foreach ($scale in 100, 200, 400) {
        $f = $scale / 100
        Save-Logo $image (Join-Path $target "Square44x44Logo.scale-$scale.png") ([int](44 * $f)) ([int](44 * $f)) 1.0
        Save-Logo $image (Join-Path $target "Square150x150Logo.scale-$scale.png") ([int](150 * $f)) ([int](150 * $f)) 0.8
        Save-Logo $image (Join-Path $target "Wide310x150Logo.scale-$scale.png") ([int](310 * $f)) ([int](150 * $f)) 0.8
        Save-Logo $image (Join-Path $target "StoreLogo.scale-$scale.png") ([int](50 * $f)) ([int](50 * $f)) 1.0
    }
    foreach ($size in 16, 24, 32, 48, 256) {
        Save-Logo $image (Join-Path $target "Square44x44Logo.targetsize-$size.png") $size $size 1.0
        Save-Logo $image (Join-Path $target "Square44x44Logo.targetsize-${size}_altform-unplated.png") $size $size 1.0
    }
    $image.Dispose()
}

$makeappx = Find-SdkTool 'makeappx.exe'
$makepri = Find-SdkTool 'makepri.exe'

if (-not $SkipPublish) {
    Invoke-Step 'Publishing self-contained win-x64 build' {
        dotnet publish 'src/FolderTrail.App/FolderTrail.App.csproj' -c Release -r win-x64 --self-contained true `
            -p:PublishSingleFile=false -o $publishDir --nologo
    }
}
if (-not (Test-Path (Join-Path $publishDir 'FolderTrail.exe'))) { throw "No published build in $publishDir." }

Write-Host '==> Laying out package'
Remove-Item -Recurse -Force $out -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $layout | Out-Null
Copy-Item (Join-Path $publishDir '*') $layout -Recurse
New-LogoSet (Join-Path $root 'src/FolderTrail.App/Assets/foldertrail-256.png') (Join-Path $layout 'Assets')

$manifest = Get-Content (Join-Path $root 'packaging/AppxManifest.xml') -Raw
$manifest = $manifest.Replace('{{IdentityName}}', $IdentityName).Replace('{{Publisher}}', $Publisher).
    Replace('{{PublisherDisplayName}}', $PublisherDisplayName).Replace('{{Version}}', $packageVersion)
Set-Content -Path (Join-Path $layout 'AppxManifest.xml') -Value $manifest -Encoding utf8

Invoke-Step 'Indexing logo resources (resources.pri)' {
    $priConfig = Join-Path $out 'priconfig.xml'
    & $makepri createconfig /cf $priConfig /dq en-US /o | Out-Null
    & $makepri new /pr $layout /cf $priConfig /of (Join-Path $layout 'resources.pri') /o | Out-Null
}

$msix = Join-Path $out "FolderTrail_${packageVersion}_x64.msix"
Invoke-Step 'Packing MSIX' { & $makeappx pack /d $layout /p $msix /o /nv | Select-String -Pattern 'error|succeeded' }

if ($SignForSideload) {
    $signtool = Find-SdkTool 'signtool.exe'
    $cert = New-SelfSignedCertificate -Type Custom -Subject $Publisher -KeyUsage DigitalSignature -FriendlyName 'FolderTrail dev' `
        -CertStoreLocation 'Cert:\CurrentUser\My' -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
    $cer = Join-Path $out 'FolderTrail-dev.cer'
    Export-Certificate -Cert $cert -FilePath $cer | Out-Null
    Invoke-Step 'Signing with the development certificate' { & $signtool sign /fd SHA256 /sha1 $cert.Thumbprint $msix }
    Write-Host "Trust it once (elevated): Import-Certificate -FilePath '$cer' -CertStoreLocation Cert:\LocalMachine\TrustedPeople"
}

$size = [math]::Round((Get-Item $msix).Length / 1MB, 1)
$note = if ($SignForSideload) { '' } else { ' - unsigned; upload to Partner Center, which signs it.' }
Write-Host "Package: $msix  ($size MB)$note"
