param([switch]$NoBrowser)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$apiRoot = Join-Path $projectRoot 'src\Stayline.Api'
$projectFile = Join-Path $apiRoot 'Stayline.Api.csproj'
$serverExe = [IO.Path]::GetFullPath((Join-Path $apiRoot 'bin\Debug\net10.0\Stayline.Api.exe'))
$localRoot = Join-Path $apiRoot '.local'
$stateFile = Join-Path $localRoot 'server-state.json'
$serverUrl = 'http://127.0.0.1:5080'
$serverState = $null
$client = $null
$mutex = $null
$ownsMutex = $false

function Get-ListenerProcessIds {
    $netstat = Join-Path $env:SystemRoot 'System32\netstat.exe'
    if (-not (Test-Path -LiteralPath $netstat)) { throw 'Windows netstat was not found; unable to verify port ownership.' }
    $lines = & $netstat -ano -p tcp
    if ($LASTEXITCODE -ne 0) { throw 'Unable to verify port ownership. No process was started or stopped.' }
    @($lines | ForEach-Object { if ($_ -match '^\s*TCP\s+\S+:5080\s+\S+\s+LISTENING\s+(\d+)\s*$') { [int]$Matches[1] } } | Select-Object -Unique)
}
function Get-VerifiedServer($identity) {
    $candidate = Get-Process -Id ([int]$identity.processId) -ErrorAction SilentlyContinue
    if ($null -eq $candidate) { return $null }
    $expectedStart = [DateTime]::Parse($identity.startTimeUtc, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind)
    if (-not [string]::Equals([IO.Path]::GetFullPath($candidate.Path), $serverExe, [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals([IO.Path]::GetFullPath($identity.executablePath), $serverExe, [StringComparison]::OrdinalIgnoreCase) -or
        $candidate.StartTime.ToUniversalTime().Ticks -ne $expectedStart.ToUniversalTime().Ticks) {
        throw 'The saved server identity no longer matches this project. No process was stopped. Use the original terminal to stop an older server.'
    }
    return $candidate
}

function Save-ServerState($server, $stdoutLog, $stderrLog, [bool]$background = $true) {
    $identity = [pscustomobject]@{
        processId = $server.Id
        executablePath = $serverExe
        startTimeUtc = $server.StartTime.ToUniversalTime().ToString('o')
        stdoutLog = $stdoutLog
        stderrLog = $stderrLog
        background = $background
    }
    $identity | ConvertTo-Json | Set-Content -LiteralPath $stateFile -Encoding UTF8
    return $identity
}

try {
    if (-not (Test-Path -LiteralPath $projectFile -PathType Leaf)) { throw 'Stayline.Api.csproj was not found beside this launcher.' }
    $hash = [Security.Cryptography.SHA256]::Create()
    try { $lockId = ([BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($projectRoot.ToLowerInvariant())))).Replace('-', '').Substring(0, 24) }
    finally { $hash.Dispose() }
    $mutex = New-Object Threading.Mutex($false, ('Local\Stayline-' + $lockId))
    try { $ownsMutex = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $ownsMutex = $true }
    if (-not $ownsMutex) { throw 'Another Stayline start or stop operation is already running. Wait for it to finish.' }
    New-Item -ItemType Directory -Path $localRoot -Force | Out-Null

    $server = $null
    if (Test-Path -LiteralPath $stateFile -PathType Leaf) {
        $serverState = Get-Content -LiteralPath $stateFile -Raw | ConvertFrom-Json
        $server = Get-VerifiedServer $serverState
    }
    $listeners = @(Get-ListenerProcessIds)
    foreach ($listenerId in $listeners) {
        $listener = Get-Process -Id $listenerId -ErrorAction Stop
        if ([string]::IsNullOrWhiteSpace($listener.Path) -or -not [string]::Equals([IO.Path]::GetFullPath($listener.Path), $serverExe, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Port 5080 belongs to another application or a server started with dotnet. Stop that server in its own terminal with Ctrl+C, then start Stayline again. Nothing was stopped.'
        }
        if ($null -ne $server -and $server.Id -ne $listener.Id) { throw 'More than one Stayline process was found. Stop the older server in its own terminal first. Nothing was stopped.' }
        if ($null -eq $server) {
            $serverState = Save-ServerState $listener '' '' $false
            $server = Get-VerifiedServer $serverState
        }
    }

    if ($null -eq $server) {
        Write-Host 'Building Stayline...'
        Push-Location -LiteralPath $projectRoot
        try {
            & dotnet build $projectFile --configuration Debug --nologo -p:DisableBuildCompression=true
            if ($LASTEXITCODE -ne 0) { throw 'The build failed. Review the build errors above; no server was started.' }
        } finally { Pop-Location }
        if (-not (Test-Path -LiteralPath $serverExe -PathType Leaf)) { throw 'The build did not produce Stayline.Api.exe.' }
        $logStamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
        $stdoutLog = Join-Path $localRoot ('server-' + $logStamp + '.stdout.log')
        $stderrLog = Join-Path $localRoot ('server-' + $logStamp + '.stderr.log')
        $oldAspnetEnvironment = $env:ASPNETCORE_ENVIRONMENT
        $oldDotnetEnvironment = $env:DOTNET_ENVIRONMENT
        try {
            $env:ASPNETCORE_ENVIRONMENT = 'Production'
            $env:DOTNET_ENVIRONMENT = 'Production'
            $server = Start-Process -FilePath $serverExe -ArgumentList @('--urls', $serverUrl, '--contentRoot', ('"' + $apiRoot + '"')) -WorkingDirectory $apiRoot -WindowStyle Hidden -RedirectStandardOutput $stdoutLog -RedirectStandardError $stderrLog -PassThru
        } finally {
            $env:ASPNETCORE_ENVIRONMENT = $oldAspnetEnvironment
            $env:DOTNET_ENVIRONMENT = $oldDotnetEnvironment
        }
        $serverState = Save-ServerState $server $stdoutLog $stderrLog
    } else {
        Write-Host 'This Stayline server is already running. To load code changes, use Stop-Stayline first, then Start-Stayline.'
    }

    Add-Type -AssemblyName System.Net.Http
    $handler = New-Object Net.Http.HttpClientHandler
    $handler.UseProxy = $false
    $client = New-Object Net.Http.HttpClient($handler)
    $client.Timeout = [TimeSpan]::FromSeconds(2)
    $ready = $false
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($null -eq (Get-VerifiedServer $serverState)) { throw 'Stayline exited during startup. Check the server logs in src\Stayline.Api\.local.' }
        try {
            $response = $client.GetAsync($serverUrl + '/api/status').GetAwaiter().GetResult()
            try {
                if ($response.IsSuccessStatusCode) {
                    $status = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
                    if ($status.name -eq 'Stayline' -and $status.environment -eq 'production') {
                        $readyOwners = @(Get-ListenerProcessIds)
                        if ($readyOwners.Count -eq 1 -and $readyOwners[0] -eq $serverState.processId) { $ready = $true; break }
                    }
                }
            } finally { $response.Dispose() }
        } catch { }
        Start-Sleep -Milliseconds 400
    }
    if (-not $ready) { throw 'Stayline did not become ready within 30 seconds. Check the server logs in src\Stayline.Api\.local.' }
    Write-Host ('Stayline is ready: ' + $serverUrl)
    Write-Host ('Server logs: ' + $localRoot)
    if ($serverState.PSObject.Properties['background'] -and $serverState.background) {
        Write-Host 'The server runs in the background. You can close this window. Use Stop-Stayline to stop it.'
    } else {
        Write-Host 'This launcher window can close. The server was started elsewhere, so keep its original terminal open. Use Stop-Stayline then Start-Stayline to switch it to the background.'
    }
    if (-not $NoBrowser) { Start-Process ($serverUrl + '/#properties') | Out-Null }
    exit 0
} catch {
    Write-Host ('Stayline could not start: ' + $_.Exception.Message) -ForegroundColor Red
    Write-Host ('Server logs: ' + $localRoot)
    exit 1
} finally {
    if ($null -ne $client) { $client.Dispose() }
    if ($ownsMutex) { $mutex.ReleaseMutex() }
    if ($null -ne $mutex) { $mutex.Dispose() }
}
