[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$InputDirectory,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$componentVersion = '3.0.23.1-3.10.1'
$packageName = "SwitchBoard-LibVLC-$componentVersion-win-x64.zip"
$checksumName = "SwitchBoard-LibVLC-$componentVersion-win-x64.sha256.txt"
$resolvedInput = (Resolve-Path -LiteralPath $InputDirectory -ErrorAction Stop).Path
$resolvedOutput = [System.IO.Path]::GetFullPath($OutputDirectory)
$nativeSource = Join-Path $resolvedInput 'libvlc\win-x64'

if (-not (Test-Path -LiteralPath $nativeSource -PathType Container)) {
    throw "The win-x64 LibVLC runtime was not found in '$nativeSource'."
}

New-Item -ItemType Directory -Path $resolvedOutput -Force | Out-Null
$stagingDirectory = Join-Path $resolvedOutput ".libvlc-package-$([Guid]::NewGuid().ToString('N'))"

try {
    New-Item -ItemType Directory -Path $stagingDirectory -Force | Out-Null

    $managedFiles = @(
        'ComponentManifest.json',
        'SwitchBoard.LibVlcPlugin.dll',
        'SwitchBoard.LibVlcPlugin.deps.json',
        'SwitchBoard.LibVlcPlugin.runtimeconfig.json',
        'LibVLCSharp.dll',
        'LibVLCSharp.WPF.dll'
    )
    foreach ($file in $managedFiles) {
        $source = Join-Path $resolvedInput $file
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
            throw "The LibVLC plugin publish is missing '$file'."
        }
        Copy-Item -LiteralPath $source -Destination (Join-Path $stagingDirectory $file)
    }

    foreach ($file in @('libvlc.dll', 'libvlccore.dll')) {
        $source = Join-Path $nativeSource $file
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
            throw "The LibVLC runtime is missing '$file'."
        }
        Copy-Item -LiteralPath $source -Destination (Join-Path $stagingDirectory $file)
    }

    foreach ($directory in @('plugins', 'lua', 'hrtfs')) {
        $source = Join-Path $nativeSource $directory
        if (-not (Test-Path -LiteralPath $source -PathType Container)) {
            throw "The LibVLC runtime is missing '$directory'."
        }
        Copy-Item -LiteralPath $source -Destination (Join-Path $stagingDirectory $directory) -Recurse
    }

    $packagePath = Join-Path $resolvedOutput $packageName
    if (Test-Path -LiteralPath $packagePath) {
        Remove-Item -LiteralPath $packagePath -Force
    }
    Compress-Archive -Path (Join-Path $stagingDirectory '*') -DestinationPath $packagePath -CompressionLevel Optimal
    if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf)) {
        throw "The LibVLC component package was not created: '$packagePath'."
    }

    $checksumPath = Join-Path $resolvedOutput $checksumName
    $hash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash *$packageName" | Set-Content -LiteralPath $checksumPath -Encoding ascii

    [PSCustomObject]@{
        PackagePath = $packagePath
        ChecksumPath = $checksumPath
        PackageBytes = (Get-Item -LiteralPath $packagePath).Length
    }
}
finally {
    if (Test-Path -LiteralPath $stagingDirectory -PathType Container) {
        Remove-Item -LiteralPath $stagingDirectory -Recurse -Force
    }
}
