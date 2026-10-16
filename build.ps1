param(
    [switch]$Android,
    [switch]$FrameworkDependent,
    [string]$OutputRoot = 'artifacts/0.9.25'
)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    $selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }
    function Publish-Windows([string]$Name) {
        $windowsOutput = Join-Path $OutputRoot $Name
        dotnet publish src/RemoteDesktop/RemoteDesktop.csproj -c Release -r win-x64 --self-contained $selfContained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=$(!$FrameworkDependent) -p:DebugType=None -o $windowsOutput
        if ($LASTEXITCODE -ne 0) { throw "$Name Windows publish failed" }
    }
    $windowsName = if ($FrameworkDependent) { 'windows-framework-dependent' } else { 'windows-portable' }
    Publish-Windows $windowsName
    if ($Android) {
        & "$PSScriptRoot/android/build.ps1"
        if ($LASTEXITCODE -ne 0) { throw 'Android build failed' }
        New-Item -ItemType Directory -Force -Path (Join-Path $OutputRoot 'android') | Out-Null
        Copy-Item -LiteralPath android/build/RemoteDesktop-debug.apk -Destination (Join-Path $OutputRoot 'android/RemoteDesktop-debug.apk')
    }
} finally { Pop-Location }
