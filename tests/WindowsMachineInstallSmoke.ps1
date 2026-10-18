param(
    [Parameter(Mandatory = $true)]
    [string]$ExecutablePath
)

$ErrorActionPreference = 'Stop'
Import-Module NetSecurity -ErrorAction Stop

function Get-RegistryValueSnapshot($Hive, [string]$SubKeyPath, [string]$ValueName) {
    $key = $Hive.OpenSubKey($SubKeyPath, $false)
    if ($null -eq $key) {
        return [pscustomobject]@{ KeyExists = $false; ValueExists = $false; Value = $null; Kind = $null }
    }
    try {
        $exists = $key.GetValueNames() -contains $ValueName
        if (!$exists) { return [pscustomobject]@{ KeyExists = $true; ValueExists = $false; Value = $null; Kind = $null } }
        return [pscustomobject]@{
            KeyExists = $true
            ValueExists = $true
            Value = $key.GetValue($ValueName, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
            Kind = $key.GetValueKind($ValueName)
        }
    }
    finally { $key.Dispose() }
}

function Restore-RegistryValueSnapshot($Hive, [string]$SubKeyPath, [string]$ValueName, $Snapshot) {
    if ($Snapshot.ValueExists) {
        $key = $Hive.CreateSubKey($SubKeyPath, $true)
        if ($null -eq $key) { throw "Could not recreate registry key $SubKeyPath" }
        try { $key.SetValue($ValueName, $Snapshot.Value, $Snapshot.Kind) }
        finally { $key.Dispose() }
        return
    }

    $key = $Hive.OpenSubKey($SubKeyPath, $true)
    if ($null -eq $key) { return }
    $removeKey = $false
    try {
        $key.DeleteValue($ValueName, $false)
        $removeKey = !$Snapshot.KeyExists -and $key.ValueCount -eq 0 -and $key.SubKeyCount -eq 0
    }
    finally { $key.Dispose() }
    if ($removeKey) { $Hive.DeleteSubKey($SubKeyPath, $false) }
}

function Invoke-ProductCommand([string]$Path, [string]$Argument, [int]$TimeoutSeconds) {
    $process = Start-Process -FilePath $Path -ArgumentList $Argument -PassThru -WindowStyle Hidden
    try {
        if (!$process.WaitForExit($TimeoutSeconds * 1000)) {
            try { $process.Kill($true) } catch { }
            try { $process.WaitForExit(3000) } catch { }
            throw "$Argument did not exit within $TimeoutSeconds seconds."
        }
        if ($process.ExitCode -ne 0) { throw "$Argument returned exit code $($process.ExitCode)." }
    }
    finally { $process.Dispose() }
}

function Get-InstalledAppProcesses([string]$ExpectedExecutable) {
    $matches = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()
    foreach ($process in [System.Diagnostics.Process]::GetProcessesByName([IO.Path]::GetFileNameWithoutExtension($ExpectedExecutable))) {
        try {
            $image = $process.MainModule.FileName
            if ([String]::Equals([IO.Path]::GetFullPath($image), $ExpectedExecutable, [StringComparison]::OrdinalIgnoreCase)) {
                $matches.Add($process)
                continue
            }
        }
        catch { }
        $process.Dispose()
    }
    return $matches
}

function Remove-InstalledAppProcesses([string]$ExpectedExecutable) {
    foreach ($process in (Get-InstalledAppProcesses $ExpectedExecutable)) {
        try { if (!$process.HasExited) { $process.Kill($true) } }
        catch { }
        finally { $process.Dispose() }
    }
}

$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
try {
    $principal = [System.Security.Principal.WindowsPrincipal]::new($identity)
    if (!$principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Machine install smoke requires the administrator token provided by the Windows runner.'
    }
}
finally { $identity.Dispose() }

$source = (Resolve-Path -LiteralPath $ExecutablePath).Path
$programFiles = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
$machineDirectory = Join-Path $programFiles 'Remote Desktop'
$machineExecutable = Join-Path $machineDirectory 'RemoteDesktop.exe'
$serviceName = 'PersonalRemoteDesktop'
$firewallName = 'Personal Remote Desktop'
$commonPrograms = [Environment]::GetFolderPath([Environment+SpecialFolder]::CommonPrograms)
$shortcut = Join-Path $commonPrograms 'Remote Desktop.lnk'
$runKeyPath = 'Software\Microsoft\Windows\CurrentVersion\Run'
$policyKeyPath = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'
$installStateKeyPath = 'SOFTWARE\PersonalRemoteDesktop'
$runValue = 'PersonalRemoteDesktop'
$policyValue = 'SoftwareSASGeneration'
$previousPolicyValue = 'SoftwareSASGenerationPrevious'

if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) { throw 'A Personal Remote Desktop service already exists on this runner.' }
if (Test-Path -LiteralPath $machineDirectory) { throw 'The machine install directory already exists on this runner.' }
if (Test-Path -LiteralPath $shortcut) { throw 'The machine Start Menu shortcut already exists on this runner.' }
if (@(Get-NetFirewallRule -Name $firewallName -ErrorAction SilentlyContinue).Count -gt 0) { throw 'The application firewall rule already exists on this runner.' }

$localMachine = [Microsoft.Win32.Registry]::LocalMachine
$currentUser = [Microsoft.Win32.Registry]::CurrentUser
$runSnapshot = Get-RegistryValueSnapshot $currentUser $runKeyPath $runValue
$policySnapshot = Get-RegistryValueSnapshot $localMachine $policyKeyPath $policyValue
$previousPolicySnapshot = Get-RegistryValueSnapshot $localMachine $installStateKeyPath $previousPolicyValue
$mutationStarted = $false

try {
    $mutationStarted = $true
    Invoke-ProductCommand $source '--install-machine' 120

    $service = Get-Service -Name $serviceName -ErrorAction Stop
    if ([string]$service.Status -ne 'Running') {
        throw "Machine service state is $($service.Status), not Running."
    }

    $serviceConfiguration = & (Join-Path $env:WINDIR 'System32\sc.exe') qc $serviceName 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0 -or !$serviceConfiguration.Contains($machineExecutable, [StringComparison]::OrdinalIgnoreCase)) {
        throw "The service is not configured to run $machineExecutable."
    }

    $shell = $null
    try {
        $shell = New-Object -ComObject WScript.Shell
        $link = $shell.CreateShortcut($shortcut)
        $linkTarget = [System.IO.Path]::GetFullPath([string]$link.TargetPath)
        if (![String]::Equals($linkTarget, $machineExecutable, [StringComparison]::OrdinalIgnoreCase)) {
            throw "The machine shortcut points to $linkTarget instead of $machineExecutable."
        }
    }
    finally { if ($shell) { [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($shell) } }

    $firewallRule = Get-NetFirewallRule -Name $firewallName -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $firewallRule -or !$firewallRule.Enabled) { throw 'The machine install did not enable its firewall rule.' }
    $applicationFilter = Get-NetFirewallApplicationFilter -AssociatedNetFirewallRule $firewallRule
    if (![String]::Equals([IO.Path]::GetFullPath($applicationFilter.Program), $machineExecutable, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The firewall rule does not name the installed executable.'
    }
    $portFilter = Get-NetFirewallPortFilter -AssociatedNetFirewallRule $firewallRule
    if ([string]$portFilter.Protocol -notin @('6', 'TCP') -or [string]$portFilter.LocalPort -in @('', 'Any')) {
        throw 'The machine firewall rule does not permit a specific TCP port.'
    }

    Invoke-ProductCommand $machineExecutable '--uninstall-machine' 90

    if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) { throw 'The machine service remains after uninstall.' }
    if (Get-NetFirewallRule -Name $firewallName -ErrorAction SilentlyContinue) { throw 'The machine firewall rule remains after uninstall.' }
    if (Test-Path -LiteralPath $shortcut) { throw 'The machine shortcut remains after uninstall.' }

    $deleteDeadline = [DateTime]::UtcNow.AddSeconds(35)
    while ((Test-Path -LiteralPath $machineDirectory) -and [DateTime]::UtcNow -lt $deleteDeadline) {
        Remove-InstalledAppProcesses $machineExecutable
        Start-Sleep -Milliseconds 250
    }
    if (Test-Path -LiteralPath $machineDirectory) { throw 'The machine install directory was not removed after its processes exited.' }
    Write-Output 'Windows machine service install/uninstall smoke passed.'
}
finally {
    if ($mutationStarted) {
        if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
            & (Join-Path $env:WINDIR 'System32\sc.exe') stop $serviceName | Out-Null
            $stopDeadline = [DateTime]::UtcNow.AddSeconds(20)
            while ((Get-Service -Name $serviceName -ErrorAction SilentlyContinue) -and [DateTime]::UtcNow -lt $stopDeadline) {
                $state = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
            if (!$state -or [string]$state.Status -eq 'Stopped') { break }
                Start-Sleep -Milliseconds 250
            }
            & (Join-Path $env:WINDIR 'System32\sc.exe') delete $serviceName | Out-Null
        }
        try { Remove-InstalledAppProcesses $machineExecutable }
        catch { Write-Warning "Could not terminate an installed UI process during cleanup: $_" }
        try {
            if (Get-NetFirewallRule -Name $firewallName -ErrorAction SilentlyContinue) {
                & (Join-Path $env:WINDIR 'System32\netsh.exe') advfirewall firewall delete rule "name=`"$firewallName`"" | Out-Null
                if ($LASTEXITCODE -ne 0) { Write-Warning 'Could not remove the smoke-test firewall rule during cleanup.' }
            }
        }
        catch { Write-Warning "Could not inspect or remove the smoke-test firewall rule: $_" }
        if (Test-Path -LiteralPath $shortcut) { Remove-Item -LiteralPath $shortcut -Force -ErrorAction SilentlyContinue }
        try { Restore-RegistryValueSnapshot $currentUser $runKeyPath $runValue $runSnapshot }
        catch { Write-Warning "Could not restore the test user's auto-start registry value: $_" }
        try { Restore-RegistryValueSnapshot $localMachine $policyKeyPath $policyValue $policySnapshot }
        catch { Write-Warning "Could not restore the secure-attention policy registry value: $_" }
        try { Restore-RegistryValueSnapshot $localMachine $installStateKeyPath $previousPolicyValue $previousPolicySnapshot }
        catch { Write-Warning "Could not restore the installation-state registry value: $_" }
        if (Test-Path -LiteralPath $machineDirectory) { Remove-Item -LiteralPath $machineDirectory -Recurse -Force -ErrorAction SilentlyContinue }
    }
}
