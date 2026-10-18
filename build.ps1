param(
    [switch]$Android,
    [switch]$FrameworkDependent,
    [string]$AndroidSdk = '',
    [string]$AndroidJdk = '',
    [switch]$AndroidRelease,
    [string]$AndroidReleaseKeystore = $env:ANDROID_RELEASE_KEYSTORE,
    [string]$AndroidReleaseAlias = $env:ANDROID_RELEASE_KEY_ALIAS,
    [string]$OutputRoot = ''
)
$ErrorActionPreference = 'Stop'
if ($AndroidRelease) { $Android = $true }
Push-Location $PSScriptRoot
$originalAndroidStorePassword = $null
$originalAndroidKeyPassword = $null
$androidReleaseStorePassword = $null
$androidReleaseKeyPassword = $null
try {
    [xml]$project = Get-Content (Join-Path $PSScriptRoot 'src/RemoteDesktop/RemoteDesktop.csproj')
    $version = $project.Project.PropertyGroup.Version
    if ($version -is [System.Array]) { $version = @($version | Where-Object { $_ })[0] }
    if ([string]::IsNullOrWhiteSpace([string]$version)) { throw 'Project version is missing' }
    $originalAndroidStorePassword = [Environment]::GetEnvironmentVariable('ANDROID_RELEASE_STORE_PASSWORD')
    $originalAndroidKeyPassword = [Environment]::GetEnvironmentVariable('ANDROID_RELEASE_KEY_PASSWORD')
    if ($AndroidRelease) {
        if ([string]::IsNullOrWhiteSpace($AndroidReleaseKeystore) -or ![System.IO.Path]::IsPathRooted($AndroidReleaseKeystore) -or !(Test-Path -LiteralPath $AndroidReleaseKeystore -PathType Leaf)) {
            throw 'Set ANDROID_RELEASE_KEYSTORE to an absolute path to an existing signing keystore outside the repository.'
        }
        if ([string]::IsNullOrWhiteSpace($AndroidReleaseAlias)) { throw 'Set ANDROID_RELEASE_KEY_ALIAS to the release key alias.' }
        $keystorePath = [System.IO.Path]::GetFullPath($AndroidReleaseKeystore)
        $repositoryRoot = [System.IO.Path]::GetFullPath($PSScriptRoot)
        if (!$repositoryRoot.EndsWith([System.IO.Path]::DirectorySeparatorChar)) { $repositoryRoot += [System.IO.Path]::DirectorySeparatorChar }
        if ($keystorePath.StartsWith($repositoryRoot, [System.StringComparison]::OrdinalIgnoreCase)) { throw 'The Android release keystore must stay outside the source repository.' }
        if ([string]::IsNullOrEmpty($originalAndroidStorePassword)) { throw 'Set ANDROID_RELEASE_STORE_PASSWORD in the process environment.' }
        if ([string]::IsNullOrEmpty($originalAndroidKeyPassword)) { throw 'Set ANDROID_RELEASE_KEY_PASSWORD in the process environment.' }
        $androidReleaseStorePassword = $originalAndroidStorePassword
        $androidReleaseKeyPassword = $originalAndroidKeyPassword
        $AndroidReleaseKeystore = $keystorePath
    }
    [Environment]::SetEnvironmentVariable('ANDROID_RELEASE_STORE_PASSWORD', $null)
    [Environment]::SetEnvironmentVariable('ANDROID_RELEASE_KEY_PASSWORD', $null)
    if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
        $OutputRoot = Join-Path 'artifacts' ([string]$version)
    }
    if ($Android) {
        [xml]$manifest = Get-Content (Join-Path $PSScriptRoot 'android/AndroidManifest.xml')
        $androidNamespace = 'http://schemas.android.com/apk/res/android'
        $androidVersion = $manifest.manifest.GetAttribute('versionName', $androidNamespace)
        if ($androidVersion -cne [string]$version) { throw "Windows version $version and Android version $androidVersion differ." }
        if ($manifest.manifest.GetAttribute('versionCode', $androidNamespace) -notmatch '^\d+$') { throw 'Android versionCode must be an integer.' }
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
        if (![string]::IsNullOrWhiteSpace($AndroidJdk)) { $androidBuildArgs.Jdk = $AndroidJdk }
        if ($AndroidRelease) {
            $androidBuildArgs.Release = $true
            $androidBuildArgs.ReleaseKeystore = $AndroidReleaseKeystore
            $androidBuildArgs.ReleaseAlias = $AndroidReleaseAlias
            $androidBuildArgs.ReleaseStorePassword = $androidReleaseStorePassword
            $androidBuildArgs.ReleaseKeyPassword = $androidReleaseKeyPassword
        }
        & "$PSScriptRoot/android/build.ps1" @androidBuildArgs
        if ($LASTEXITCODE -ne 0) { throw 'Android build failed' }
        New-Item -ItemType Directory -Force -Path (Join-Path $OutputRoot 'android') | Out-Null
        Copy-Item -LiteralPath android/build/RemoteDesktop-debug.apk -Destination (Join-Path $OutputRoot 'android/RemoteDesktop-debug.apk')
        if ($AndroidRelease) { Copy-Item -LiteralPath android/build/RemoteDesktop-release.apk -Destination (Join-Path $OutputRoot 'android/RemoteDesktop-release.apk') }
    }
} finally {
    try { Pop-Location }
    finally {
        [Environment]::SetEnvironmentVariable('ANDROID_RELEASE_STORE_PASSWORD', $originalAndroidStorePassword)
        [Environment]::SetEnvironmentVariable('ANDROID_RELEASE_KEY_PASSWORD', $originalAndroidKeyPassword)
        $androidReleaseStorePassword = $null
        $androidReleaseKeyPassword = $null
    }
}
