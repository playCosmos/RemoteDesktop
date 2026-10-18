function Invoke-WindowsSignTool([string]$SignToolPath, [string[]]$Arguments, [int]$TimeoutSeconds) {
    if ($TimeoutSeconds -lt 1) { throw [ArgumentOutOfRangeException]::new('TimeoutSeconds') }
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
        $finished = $process.WaitForExit($TimeoutSeconds * 1000)
        if (!$finished -and $process.HasExited) { $finished = $true }
        if (!$finished) {
            $terminated = $false
            try
            {
                if (!$process.HasExited) { $process.Kill($true); $terminated = $true }
            }
            catch { }
            $exitedAfterTimeout = $false
            try { $exitedAfterTimeout = $process.WaitForExit(5000) }
            catch { }
            if (!$exitedAfterTimeout) { Write-Warning 'Timed-out SignTool remained active after the bounded termination wait.' }
            try { $process.StandardOutput.Close() } catch { }
            try { $process.StandardError.Close() } catch { }
            try { [void][Threading.Tasks.Task]::WhenAll([Threading.Tasks.Task[]]@($stdoutTask, $stderrTask)).Wait(2000) } catch { }
            throw "SignTool exceeded the $TimeoutSeconds second timeout. ProcessId=$($process.Id); terminated=$terminated; exited=$exitedAfterTimeout. Check the RFC 3161 timestamp endpoint and certificate store."
        }
        $outputTasks = [Threading.Tasks.Task]::WhenAll([Threading.Tasks.Task[]]@($stdoutTask, $stderrTask))
        if (!$outputTasks.Wait(5000))
        {
            try { $process.Kill($true) } catch { }
            try { [void]$process.WaitForExit(5000) } catch { }
            try { $process.StandardOutput.Close() } catch { }
            try { $process.StandardError.Close() } catch { }
            throw 'SignTool exited but its redirected output did not close within 5 seconds.'
        }
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        if (![string]::IsNullOrWhiteSpace($stdout)) { Write-Output $stdout.TrimEnd() }
        if (![string]::IsNullOrWhiteSpace($stderr)) { Write-Output $stderr.TrimEnd() }
        if ($process.ExitCode -ne 0) { throw "SignTool failed with exit code $($process.ExitCode)." }
    }
    finally { $process.Dispose() }
}
