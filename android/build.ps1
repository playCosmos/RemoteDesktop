param(
    [string]$Sdk = "$env:LOCALAPPDATA\Android\Sdk",
    [string]$Jdk = 'C:\Program Files\Java\jdk-25.0.4'
)
$ErrorActionPreference = 'Stop'
$androidRoot = $PSScriptRoot
$output = Join-Path $androidRoot 'build'
$buildTools = Join-Path $Sdk 'build-tools\35.0.0'
$platform = Join-Path $Sdk 'platforms\android-35\android.jar'
$env:JAVA_HOME = $Jdk
function Run([string]$exe, [string[]]$toolArgs) {
    & $exe @toolArgs
    if ($LASTEXITCODE -ne 0) { throw "$exe failed: $LASTEXITCODE" }
}
New-Item -ItemType Directory -Force -Path $output,"$output\classes","$output\dex" | Out-Null
$sources = @(Get-ChildItem -LiteralPath "$androidRoot\src" -Recurse -Filter '*.java' | ForEach-Object { $_.FullName })
Run "$Jdk\bin\javac.exe" (@('-encoding','UTF-8','--release','8','-classpath',$platform,'-d',"$output\classes") + $sources)
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
