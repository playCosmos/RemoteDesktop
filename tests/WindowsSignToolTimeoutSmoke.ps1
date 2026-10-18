$ErrorActionPreference = 'Stop'
$signToolProcessScript = Join-Path (Split-Path -Parent $PSScriptRoot) 'scripts/SignToolProcess.ps1'
. $signToolProcessScript

$powerShell = Join-Path $PSHOME 'pwsh.exe'
if (!(Test-Path -LiteralPath $powerShell -PathType Leaf)) { throw "PowerShell process was not found: $powerShell" }
$timer = [System.Diagnostics.Stopwatch]::StartNew()
$timedOut = $false
$processStopped = $false
try {
    Invoke-WindowsSignTool $powerShell @('-NoLogo','-NoProfile','-NonInteractive','-Command','Start-Sleep -Seconds 60') 1
}
catch {
    if ($_.Exception.Message -notmatch 'SignTool exceeded the 1 second timeout') { throw }
    $timedOut = $true
    if ($_.Exception.Message -notmatch 'exited=True') { throw 'SignTool process did not stop within the bounded cleanup wait.' }
    $processIdMatch = [regex]::Match($_.Exception.Message, 'ProcessId=(\d+)')
    if (!$processIdMatch.Success) { throw 'The timeout error did not identify its child process.' }
    $processStopped = $null -eq (Get-Process -Id ([int]$processIdMatch.Groups[1].Value) -ErrorAction SilentlyContinue)
}

if (!$timedOut -or !$processStopped -or $timer.Elapsed -gt [TimeSpan]::FromSeconds(10)) {
    throw "SignTool timeout cleanup failed; stopped=$processStopped elapsed=$($timer.Elapsed)."
}
Write-Output "SignTool timeout cleanup passed in $([math]::Round($timer.Elapsed.TotalSeconds, 2)) seconds."
