param(
    [string]$Sdk = "$env:LOCALAPPDATA\Android\Sdk",
    [string]$Jdk = 'C:\Program Files\Java\jdk-25.0.4',
    [switch]$Release,
    [string]$ReleaseKeystore = $env:ANDROID_RELEASE_KEYSTORE,
    [string]$ReleaseAlias = $env:ANDROID_RELEASE_KEY_ALIAS,
    [string]$ReleaseStorePassword = $env:ANDROID_RELEASE_STORE_PASSWORD,
    [string]$ReleaseKeyPassword = $env:ANDROID_RELEASE_KEY_PASSWORD
)
$ErrorActionPreference = 'Stop'
$storePasswordVariable = 'ANDROID_RELEASE_STORE_PASSWORD'
$keyPasswordVariable = 'ANDROID_RELEASE_KEY_PASSWORD'
$originalStorePassword = [Environment]::GetEnvironmentVariable($storePasswordVariable)
$originalKeyPassword = [Environment]::GetEnvironmentVariable($keyPasswordVariable)
$releaseKeystorePath = $null
if ($Release) {
    if ([string]::IsNullOrWhiteSpace($ReleaseKeystore) -or ![System.IO.Path]::IsPathRooted($ReleaseKeystore) -or !(Test-Path -LiteralPath $ReleaseKeystore -PathType Leaf)) {
        throw 'Set ANDROID_RELEASE_KEYSTORE to an absolute path to an existing signing keystore outside the repository.'
    }
    if ([string]::IsNullOrWhiteSpace($ReleaseAlias)) { throw 'Set ANDROID_RELEASE_KEY_ALIAS to the release key alias.' }
    if ([string]::IsNullOrEmpty($ReleaseStorePassword)) { throw 'Set ANDROID_RELEASE_STORE_PASSWORD in the process environment.' }
    if ([string]::IsNullOrEmpty($ReleaseKeyPassword)) { throw 'Set ANDROID_RELEASE_KEY_PASSWORD in the process environment.' }
    $releaseKeystorePath = [System.IO.Path]::GetFullPath($ReleaseKeystore)
    $repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
    if (!$repositoryRoot.EndsWith([System.IO.Path]::DirectorySeparatorChar)) { $repositoryRoot += [System.IO.Path]::DirectorySeparatorChar }
    if ($releaseKeystorePath.StartsWith($repositoryRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'The Android release keystore must stay outside the source repository.'
    }
}
[Environment]::SetEnvironmentVariable($storePasswordVariable, $null)
[Environment]::SetEnvironmentVariable($keyPasswordVariable, $null)
try {
$androidRoot = $PSScriptRoot
$output = Join-Path $androidRoot 'build'
$buildTools = Join-Path $Sdk 'build-tools\35.0.0'
$platform = Join-Path $Sdk 'platforms\android-35\android.jar'
$env:JAVA_HOME = $Jdk
function Run([string]$exe, [string[]]$toolArgs) {
    & $exe @toolArgs
    if ($LASTEXITCODE -ne 0) { throw "$exe failed: $LASTEXITCODE" }
}
$buildRoot = [System.IO.Path]::GetFullPath($output).TrimEnd('\') + '\'
New-Item -ItemType Directory -Force -Path $buildRoot | Out-Null
foreach ($generatedName in @('classes','checks','dex')) {
    $generatedPath = [System.IO.Path]::GetFullPath((Join-Path $buildRoot $generatedName))
    if (!$generatedPath.StartsWith($buildRoot, [System.StringComparison]::OrdinalIgnoreCase)) { throw "Android output path escaped build directory: $generatedPath" }
    if (Test-Path -LiteralPath $generatedPath) { Remove-Item -LiteralPath $generatedPath -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $generatedPath | Out-Null
}
$mainSources = @(Get-ChildItem -LiteralPath "$androidRoot\src" -Recurse -Filter '*.java' | Where-Object { $_.Name -notmatch 'Check\.java$' } | ForEach-Object { $_.FullName })
$checkSource = Join-Path $androidRoot 'src\dev\personal\remotedesktop\LocalCursorCheck.java'
$sources = @($mainSources)
Run "$Jdk\bin\javac.exe" (@('-encoding','UTF-8','--release','8','-classpath',$platform,'-d',"$output\classes") + $sources)
$checkOutput = Join-Path $output 'checks'
$checkClass = Join-Path $checkOutput 'dev\personal\remotedesktop'
New-Item -ItemType Directory -Force -Path $checkClass | Out-Null
Run "$Jdk\bin\javac.exe" (@('-encoding','UTF-8','--release','8','-classpath',"$output\classes","-d",$checkOutput) + @($checkSource))
Run "$Jdk\bin\java.exe" @('-cp',"$output\classes;$checkOutput","dev.personal.remotedesktop.LocalCursorCheck")
$h264CheckSource = Join-Path $androidRoot 'src\dev\personal\remotedesktop\H264ConfigurationCheck.java'
Run "$Jdk\bin\javac.exe" (@('-encoding','UTF-8','--release','8','-classpath',"$output\classes","-d",$checkOutput) + @($h264CheckSource))
Run "$Jdk\bin\java.exe" @('-cp',"$output\classes;$checkOutput","dev.personal.remotedesktop.H264ConfigurationCheck")
$h264PictureCheckSource = Join-Path $androidRoot 'src\dev\personal\remotedesktop\H264PictureCheck.java'
Run "$Jdk\bin\javac.exe" (@('-encoding','UTF-8','--release','8','-classpath',"$output\classes;$platform",'-d',$checkOutput) + @($h264PictureCheckSource))
Run "$Jdk\bin\java.exe" @('-cp',"$output\classes;$checkOutput;$platform","dev.personal.remotedesktop.H264PictureCheck")
$sessionPolicyCheckSource = Join-Path $androidRoot 'src\dev\personal\remotedesktop\RemoteSessionPolicyCheck.java'
Run "$Jdk\bin\javac.exe" (@('-encoding','UTF-8','--release','8','-classpath',"$output\classes","-d",$checkOutput) + @($sessionPolicyCheckSource))
Run "$Jdk\bin\java.exe" @('-cp',"$output\classes;$checkOutput","dev.personal.remotedesktop.RemoteSessionPolicyCheck")
$uploadResumeCheckSource = Join-Path $androidRoot 'src\dev\personal\remotedesktop\UploadResumeCheck.java'
Run "$Jdk\bin\javac.exe" (@('-encoding','UTF-8','--release','8','-classpath',"$output\classes","-d",$checkOutput) + @($uploadResumeCheckSource))
Run "$Jdk\bin\java.exe" @('-cp',"$output\classes;$checkOutput","dev.personal.remotedesktop.UploadResumeCheck")
Run "$Jdk\bin\jar.exe" @('cf',"$output\classes.jar",'-C',"$output\classes",'.')
Run "$buildTools\d8.bat" @('--lib',$platform,'--min-api','30','--output',"$output\dex","$output\classes.jar")
Run "$buildTools\aapt.exe" @('package','-f','-M',"$androidRoot\AndroidManifest.xml",'-I',$platform,'-F',"$output\unsigned.apk")
Push-Location "$output\dex"
try { Run "$buildTools\aapt.exe" @('add',"$output\unsigned.apk",'classes.dex') } finally { Pop-Location }
Run "$buildTools\zipalign.exe" @('-f','4',"$output\unsigned.apk","$output\aligned.apk")
if (!(Test-Path -LiteralPath "$output\debug.keystore")) {
    Run "$Jdk\bin\keytool.exe" @('-genkeypair','-keystore',"$output\debug.keystore",'-storepass','android','-keypass','android','-alias','androiddebugkey','-dname','CN=Android Debug','-keyalg','RSA','-validity','10000')
}
Run "$buildTools\apksigner.bat" @('sign','--ks',"$output\debug.keystore",'--ks-pass','pass:android','--out',"$output\RemoteDesktop-debug.apk","$output\aligned.apk")
Run "$buildTools\apksigner.bat" @('verify',"$output\RemoteDesktop-debug.apk")
Write-Output "APK: $output\RemoteDesktop-debug.apk"
if ($Release) {
    $releaseApk = Join-Path $output 'RemoteDesktop-release.apk'
    try {
        [Environment]::SetEnvironmentVariable($storePasswordVariable, $ReleaseStorePassword)
        [Environment]::SetEnvironmentVariable($keyPasswordVariable, $ReleaseKeyPassword)
        Run "$buildTools\apksigner.bat" @('sign','--ks',$releaseKeystorePath,'--ks-key-alias',$ReleaseAlias,'--ks-pass',"env:$storePasswordVariable",'--key-pass',"env:$keyPasswordVariable",'--out',$releaseApk,"$output\aligned.apk")
    }
    finally {
        [Environment]::SetEnvironmentVariable($storePasswordVariable, $null)
        [Environment]::SetEnvironmentVariable($keyPasswordVariable, $null)
    }
    Run "$buildTools\apksigner.bat" @('verify',$releaseApk)
    Write-Output "Release APK: $releaseApk"
}
}
finally {
    [Environment]::SetEnvironmentVariable($storePasswordVariable, $originalStorePassword)
    [Environment]::SetEnvironmentVariable($keyPasswordVariable, $originalKeyPassword)
    $ReleaseStorePassword = $null
    $ReleaseKeyPassword = $null
}
