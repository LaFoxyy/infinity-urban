$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$apiRoot = Join-Path $projectRoot 'src\Stayline.Api'
$serverExe = [IO.Path]::GetFullPath((Join-Path $apiRoot 'bin\Debug\net10.0\Stayline.Api.exe'))
$stateFile = Join-Path $apiRoot '.local\server-state.json'
$mutex = $null
$ownsMutex = $false

function Get-VerifiedServer($identity) {
    $candidate = Get-Process -Id ([int]$identity.processId) -ErrorAction SilentlyContinue
    if ($null -eq $candidate) { return $null }
    $expectedStart = [DateTime]::Parse($identity.startTimeUtc, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind)
    if (-not [string]::Equals([IO.Path]::GetFullPath($candidate.Path), $serverExe, [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals([IO.Path]::GetFullPath($identity.executablePath), $serverExe, [StringComparison]::OrdinalIgnoreCase) -or
        $candidate.StartTime.ToUniversalTime().Ticks -ne $expectedStart.ToUniversalTime().Ticks) {
        throw 'The saved server identity no longer matches this project. No process was stopped. Stop an older server from its original terminal instead.'
    }
    return $candidate
}

try {
    $hash = [Security.Cryptography.SHA256]::Create()
    try { $lockId = ([BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($projectRoot.ToLowerInvariant())))).Replace('-', '').Substring(0, 24) }
    finally { $hash.Dispose() }
    $mutex = New-Object Threading.Mutex($false, ('Local\Stayline-' + $lockId))
    try { $ownsMutex = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $ownsMutex = $true }
    if (-not $ownsMutex) { throw 'Another Stayline start or stop operation is already running. Wait for it to finish.' }
    if (-not (Test-Path -LiteralPath $stateFile -PathType Leaf)) {
        Write-Host 'No background Stayline server is recorded. If it was started with dotnet run, stop it in that terminal with Ctrl+C.'
        exit 0
    }
    $identity = Get-Content -LiteralPath $stateFile -Raw | ConvertFrom-Json
    $server = Get-VerifiedServer $identity
    if ($null -ne $server) {
        if ($server.MainWindowHandle -ne [IntPtr]::Zero -and $server.CloseMainWindow()) { $null = $server.WaitForExit(5000) }
        $server = Get-VerifiedServer $identity
        if ($null -ne $server) {
            Stop-Process -Id $server.Id -ErrorAction Stop
            $null = $server.WaitForExit(10000)
            if (-not $server.HasExited) { throw 'Stayline has not exited yet. Wait a moment before starting it again.' }
        }
    }
    Remove-Item -LiteralPath $stateFile -Force
    Write-Host 'Stayline is stopped. Your property data and credentials remain saved.'
    exit 0
} catch {
    Write-Host ('Stayline could not stop: ' + $_.Exception.Message) -ForegroundColor Red
    exit 1
} finally {
    if ($ownsMutex) { $mutex.ReleaseMutex() }
    if ($null -ne $mutex) { $mutex.Dispose() }
}
