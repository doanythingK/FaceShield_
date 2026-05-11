param(
    [ValidateSet("win-x64", "osx-arm64")]
    [string]$RuntimeIdentifier = "win-x64",
    [string]$Configuration = "Release",
    [string]$PublishDir = "",
    [switch]$SkipPublish,
    [switch]$RequireLibomp
)

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ([string]::IsNullOrWhiteSpace($PublishDir)) {
    $PublishDir = Join-Path ([IO.Path]::GetTempPath()) "faceshield-$RuntimeIdentifier-native-verify"
}

if (-not $SkipPublish) {
    dotnet publish (Join-Path $repo "FaceShield.csproj") `
        -c $Configuration `
        -r $RuntimeIdentifier `
        --self-contained true `
        -o $PublishDir

    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}

if (-not (Test-Path $PublishDir)) {
    throw "Publish directory not found: $PublishDir"
}

function Assert-File([string]$Name) {
    $path = Join-Path $PublishDir $Name
    if (-not (Test-Path $path)) {
        throw "Required publish file missing: $Name"
    }

    $size = (Get-Item $path).Length
    if ($size -le 0) {
        throw "Required publish file is empty: $Name"
    }

    Write-Host "[PublishVerify] found $Name ($size bytes)"
}

function Assert-Missing([string]$Name) {
    $path = Join-Path $PublishDir $Name
    if (Test-Path $path) {
        throw "Unexpected publish file present: $Name"
    }

    Write-Host "[PublishVerify] absent $Name"
}

if ($RuntimeIdentifier -eq "win-x64") {
    Assert-File "FaceShield.exe"
    Assert-File "DirectML.dll"
    Assert-File "onnxruntime.dll"
    Assert-File "onnxruntime_providers_shared.dll"
    Assert-File "FaceONNX.dll"
    Assert-File "FaceONNX.Addons.dll"
    Assert-File "avcodec-62.dll"
    Assert-File "avformat-62.dll"
    Assert-File "avutil-60.dll"
    Assert-File "swscale-9.dll"
    Assert-File "swresample-6.dll"
} else {
    Assert-File "FaceShield"
    Assert-File "libonnxruntime.dylib"
    Assert-File "FaceONNX.dll"
    Assert-File "FaceONNX.Addons.dll"
    Assert-File "libavcodec.dylib"
    Assert-File "libavformat.dylib"
    Assert-File "libavutil.dylib"
    Assert-File "libswscale.dylib"
    Assert-File "libswresample.dylib"
    Assert-Missing "onnxruntime.dll"
    Assert-Missing "onnxruntime_providers_shared.dll"

    if ($RequireLibomp) {
        Assert-File "libomp.dylib"
    } elseif (-not (Test-Path (Join-Path $PublishDir "libomp.dylib"))) {
        Write-Warning "libomp.dylib is not bundled. Target Macs must provide libomp, or publish on a Mac with Homebrew libomp available."
    }
}

Write-Host "[PublishVerify] $RuntimeIdentifier native publish verification passed: $PublishDir"
