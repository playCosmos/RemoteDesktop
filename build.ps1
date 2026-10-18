param(
    [switch]$Android,
    [switch]$FrameworkDependent,
    [string]$AndroidSdk = '',
    [string]$OutputRoot = ''
)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
        [xml]$project = Get-Content (Join-Path $PSScriptRoot 'src/RemoteDesktop/RemoteDesktop.csproj')
        $version = $project.Project.PropertyGroup.Version
        if ($version -is [System.Array]) { $version = @($version | Where-Object { $_ })[0] }
        if ([string]::IsNullOrWhiteSpace([string]$version)) { throw 'Project version is missing' }
        $OutputRoot = Join-Path 'artifacts' ([string]$version)
    }
    $selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }
    function Publish-Windows([string]$Name) {
        $windowsOutput = Join-Path $OutputRoot $Name
        dotnet publish src/RemoteDesktop/RemoteDesktop.csproj -c Release -r win-x64 --self-contained $selfContained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=$(!$FrameworkDependent) -p:DebugType=None -o $windowsOutput
        if ($LASTEXITCODE -ne 0) { throw "$Name Windows publish failed" }
    }
    $windowsName = if ($FrameworkDependent) { 'windows-framework-dependent' } else { 'windows-portable' }
    Publish-Windows $windowsName
    if ($Android) {
        $androidBuildArgs = @{}
        if (![string]::IsNullOrWhiteSpace($AndroidSdk)) { $androidBuildArgs.Sdk = $AndroidSdk }
        & "$PSScriptRoot/android/build.ps1" @androidBuildArgs
        if ($LASTEXITCODE -ne 0) { throw 'Android build failed' }
        New-Item -ItemType Directory -Force -Path (Join-Path $OutputRoot 'android') | Out-Null
        Copy-Item -LiteralPath android/build/RemoteDesktop-debug.apk -Destination (Join-Path $OutputRoot 'android/RemoteDesktop-debug.apk')
    }
} finally { Pop-Location }
