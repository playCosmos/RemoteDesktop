param(
    [switch]$Android,
    [switch]$FrameworkDependent,
    [string]$AndroidSdk = '',
    [string]$AndroidJdk = '',
    [switch]$AndroidRelease,
    [string]$AndroidReleaseKeystore = $env:ANDROID_RELEASE_KEYSTORE,
    [string]$AndroidReleaseAlias = $env:ANDROID_RELEASE_KEY_ALIAS,
    [switch]$WindowsSign,
    [string]$WindowsSigningPfx = $env:WINDOWS_SIGNING_PFX,
    [string]$WindowsTimestampUrl = $env:WINDOWS_SIGNING_TIMESTAMP_URL,
    [string]$WindowsSignToolPath = $env:WINDOWS_SIGNTOOL,
    [ValidateRange(30,900)][int]$WindowsSignToolTimeoutSeconds = 180,
    [string]$OutputRoot = ''
)
$ErrorActionPreference = 'Stop'
if ($AndroidRelease) { $Android = $true }

function Resolve-WindowsSignToolPath {
    if (![string]::IsNullOrWhiteSpace($WindowsSignToolPath)) {
        if (![System.IO.Path]::IsPathRooted($WindowsSignToolPath) -or !(Test-Path -LiteralPath $WindowsSignToolPath -PathType Leaf))
            { throw 'WINDOWS_SIGNTOOL must name an existing absolute signtool.exe path.' }
        return [System.IO.Path]::GetFullPath($WindowsSignToolPath)
    }
    $command = Get-Command signtool.exe -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($command -and $command.Source -and (Test-Path -LiteralPath $command.Source -PathType Leaf))
        { return [System.IO.Path]::GetFullPath($command.Source) }

    $programFilesX86 = [Environment]::GetEnvironmentVariable('ProgramFiles(x86)')
    if ([string]::IsNullOrWhiteSpace($programFilesX86)) { throw 'Windows SDK SignTool was not found. Set WINDOWS_SIGNTOOL to signtool.exe.' }
    $sdkBin = Join-Path $programFilesX86 'Windows Kits\10\bin'
    $candidates = @(Get-ChildItem -LiteralPath $sdkBin -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^\d+(\.\d+){2,3}$' } |
        Sort-Object { [version]$_.Name } -Descending |
        ForEach-Object { Join-Path $_.FullName 'x64\signtool.exe' } |
        Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })
    if ($candidates.Count -eq 0) { throw 'Windows SDK SignTool was not found. Set WINDOWS_SIGNTOOL to signtool.exe.' }
    return [System.IO.Path]::GetFullPath($candidates[0])
}

function Invoke-WindowsSignTool([string]$SignToolPath, [string[]]$Arguments, [int]$TimeoutSeconds) {
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $SignToolPath
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $Arguments) { [void]$startInfo.ArgumentList.Add($argument) }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (!$process.Start()) { throw 'SignTool could not be started.' }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit($TimeoutSeconds * 1000)) {
            try { $process.Kill($true) } catch { }
            try { $process.WaitForExit() } catch { }
            try { $null = $stdoutTask.GetAwaiter().GetResult() } catch { }
            try { $null = $stderrTask.GetAwaiter().GetResult() } catch { }
            throw "SignTool exceeded the $TimeoutSeconds second timeout. Check the RFC 3161 timestamp endpoint and certificate store."
        }
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        if (![string]::IsNullOrWhiteSpace($stdout)) { Write-Output $stdout.TrimEnd() }
        if (![string]::IsNullOrWhiteSpace($stderr)) { Write-Output $stderr.TrimEnd() }
        if ($process.ExitCode -ne 0) { throw "SignTool failed with exit code $($process.ExitCode)." }
    }
    finally { $process.Dispose() }
}

function Sign-WindowsExecutable([string]$ExecutablePath, [string]$PfxPath, [string]$PfxPassword, [string]$TimestampUrl, [string]$SignToolPath, [int]$TimeoutSeconds) {
    $securePassword = ConvertTo-SecureString $PfxPassword -AsPlainText -Force
    $pfxCertificate = $null
    $storeCertificate = $null
    $ownsStoreCertificate = $false
    $storePath = $null
    try {
        $pfxCertificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
            $PfxPath,
            $securePassword,
            [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
        if (!$pfxCertificate.HasPrivateKey) { throw 'Windows signing PFX does not contain a private key.' }
        if ($pfxCertificate.NotBefore.ToUniversalTime() -gt [DateTime]::UtcNow -or $pfxCertificate.NotAfter.ToUniversalTime() -le [DateTime]::UtcNow) {
            throw 'Windows signing certificate is outside its validity period.'
        }
        $ekuExtension = $pfxCertificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.37' } | Select-Object -First 1
        if ($null -eq $ekuExtension) { throw 'Windows signing certificate has no code-signing usage.' }
        $eku = [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]$ekuExtension
        $codeSigningOid = '1.3.6.1.5.5.7.3.3'
        $hasCodeSigning = @($eku.EnhancedKeyUsages | Where-Object { $_.Value -eq $codeSigningOid }).Count -gt 0
        if (!$hasCodeSigning) { throw 'Windows signing certificate is not valid for code signing.' }

        $thumbprint = $pfxCertificate.Thumbprint
        $storePath = "Cert:\CurrentUser\My\$thumbprint"
        $storeCertificate = Get-Item -LiteralPath $storePath -ErrorAction SilentlyContinue
        if ($null -eq $storeCertificate) {
            $ownsStoreCertificate = $true
            Import-PfxCertificate -FilePath $PfxPath -CertStoreLocation 'Cert:\CurrentUser\My' -Password $securePassword | Out-Null
            $storeCertificate = Get-Item -LiteralPath $storePath -ErrorAction SilentlyContinue
        }
        if ($null -eq $storeCertificate -or !$storeCertificate.HasPrivateKey) { throw 'The Windows signing certificate could not be loaded into the current-user certificate store.' }

        Invoke-WindowsSignTool $SignToolPath @('sign','/s','My','/sha1',$thumbprint,'/fd','SHA256','/tr',$TimestampUrl,'/td','SHA256','/u',$codeSigningOid,$ExecutablePath) $TimeoutSeconds
        Invoke-WindowsSignTool $SignToolPath @('verify','/pa','/all','/tw','/v',$ExecutablePath) $TimeoutSeconds
    }
    finally {
        if ($storeCertificate) { $storeCertificate.Dispose() }
        if ($pfxCertificate) { $pfxCertificate.Dispose() }
        if ($ownsStoreCertificate -and $storePath -and (Test-Path -LiteralPath $storePath)) {
            Remove-Item -LiteralPath $storePath -DeleteKey -Force
        }
        if ($securePassword) { $securePassword.Dispose() }
    }
}

Push-Location $PSScriptRoot
$originalAndroidStorePassword = $null
$originalAndroidKeyPassword = $null
$androidReleaseStorePassword = $null
$androidReleaseKeyPassword = $null
$originalWindowsSigningPassword = $null
$windowsSigningPassword = $null
try {
    [xml]$project = Get-Content (Join-Path $PSScriptRoot 'src/RemoteDesktop/RemoteDesktop.csproj')
    $version = $project.Project.PropertyGroup.Version
    if ($version -is [System.Array]) { $version = @($version | Where-Object { $_ })[0] }
    if ([string]::IsNullOrWhiteSpace([string]$version)) { throw 'Project version is missing' }
    $originalAndroidStorePassword = [Environment]::GetEnvironmentVariable('ANDROID_RELEASE_STORE_PASSWORD')
    $originalAndroidKeyPassword = [Environment]::GetEnvironmentVariable('ANDROID_RELEASE_KEY_PASSWORD')
    $originalWindowsSigningPassword = [Environment]::GetEnvironmentVariable('WINDOWS_SIGNING_PFX_PASSWORD')
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
    $windowsSignToolPathResolved = $null
    if ($WindowsSign) {
        if ([string]::IsNullOrWhiteSpace($WindowsSigningPfx) -or ![System.IO.Path]::IsPathRooted($WindowsSigningPfx) -or !(Test-Path -LiteralPath $WindowsSigningPfx -PathType Leaf)) {
            throw 'Set WINDOWS_SIGNING_PFX to an absolute path to an existing signing PFX outside the repository.'
        }
        $windowsPfxPath = [System.IO.Path]::GetFullPath($WindowsSigningPfx)
        $repositoryRoot = [System.IO.Path]::GetFullPath($PSScriptRoot)
        if (!$repositoryRoot.EndsWith([System.IO.Path]::DirectorySeparatorChar)) { $repositoryRoot += [System.IO.Path]::DirectorySeparatorChar }
        if ($windowsPfxPath.StartsWith($repositoryRoot, [System.StringComparison]::OrdinalIgnoreCase)) { throw 'The Windows signing PFX must stay outside the source repository.' }
        if ([string]::IsNullOrEmpty($originalWindowsSigningPassword)) { throw 'Set WINDOWS_SIGNING_PFX_PASSWORD in the process environment.' }
        if ([string]::IsNullOrWhiteSpace($WindowsTimestampUrl)) { $WindowsTimestampUrl = 'http://timestamp.digicert.com' }
        $timestampUri = $null
        if (![Uri]::TryCreate($WindowsTimestampUrl, [UriKind]::Absolute, [ref]$timestampUri) -or $timestampUri.Scheme -notin @('http','https')) {
            throw 'WINDOWS_SIGNING_TIMESTAMP_URL must be an absolute HTTP or HTTPS RFC 3161 timestamp URL.'
        }
        $windowsSigningPassword = $originalWindowsSigningPassword
        $WindowsSigningPfx = $windowsPfxPath
        $windowsSignToolPathResolved = Resolve-WindowsSignToolPath
    }
    [Environment]::SetEnvironmentVariable('ANDROID_RELEASE_STORE_PASSWORD', $null)
    [Environment]::SetEnvironmentVariable('ANDROID_RELEASE_KEY_PASSWORD', $null)
    [Environment]::SetEnvironmentVariable('WINDOWS_SIGNING_PFX_PASSWORD', $null)
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
        if ($WindowsSign) { Sign-WindowsExecutable (Join-Path $windowsOutput 'RemoteDesktop.exe') $WindowsSigningPfx $windowsSigningPassword $WindowsTimestampUrl $windowsSignToolPathResolved $WindowsSignToolTimeoutSeconds }
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
        [Environment]::SetEnvironmentVariable('WINDOWS_SIGNING_PFX_PASSWORD', $originalWindowsSigningPassword)
        $androidReleaseStorePassword = $null
        $androidReleaseKeyPassword = $null
        $windowsSigningPassword = $null
    }
}
