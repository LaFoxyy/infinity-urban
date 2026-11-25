param([switch]$NoBrowser)
$ErrorActionPreference = 'Stop'
$portalRoot = Join-Path $PSScriptRoot 'src\InfinityUrban.Web'
$privateSettings = Join-Path $portalRoot '.local\channex-secrets.json'
if (-not (Test-Path -LiteralPath $privateSettings) -and -not $env:Channex__ApiKey) { throw 'Configure a chave de producao no arquivo privado src\InfinityUrban.Web\.local\channex-secrets.json.' }
$listener = Get-NetTCPConnection -LocalPort 5189 -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
if ($listener) {
    $running = Get-CimInstance Win32_Process -Filter "ProcessId = $($listener.OwningProcess)"
    if ($running.CommandLine -and $running.CommandLine.Contains($portalRoot) -and $running.CommandLine.Contains('InfinityUrban.Web.dll')) {
        Write-Host 'Infinity Urban ja esta aberto: http://localhost:5189/login'
        if (-not $NoBrowser) { Start-Process 'http://localhost:5189/login' }
        return
    }
    throw 'A porta 5189 esta ocupada por outra aplicacao. Nenhum processo foi encerrado.'
}
if (Get-NetTCPConnection -LocalPort 5080 -State Listen -ErrorAction SilentlyContinue) { throw 'Feche a versao antiga antes de iniciar o Infinity Urban.' }
$runtimeRoot = Join-Path $portalRoot '.local'
New-Item -ItemType Directory -Path $runtimeRoot -Force | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$buildRoot = Join-Path $runtimeRoot ('app-' + $stamp)
& dotnet build (Join-Path $portalRoot 'InfinityUrban.Web.csproj') --nologo --output $buildRoot -p:UseAppHost=false -p:StaticWebAssetsEnabled=false
if ($LASTEXITCODE -ne 0) { throw 'A compilacao falhou; o site nao foi iniciado.' }
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:DOTNET_ENVIRONMENT = 'Development'
$env:Channex__Environment = 'production'
$env:Channex__Enabled = 'true'
$process = Start-Process -FilePath 'dotnet' -ArgumentList @(('"' + (Join-Path $buildRoot 'InfinityUrban.Web.dll') + '"'),'--urls','http://localhost:5189','--contentRoot',('"' + $portalRoot + '"')) -WorkingDirectory $portalRoot -WindowStyle Hidden -RedirectStandardOutput (Join-Path $runtimeRoot ($stamp+'.stdout.log')) -RedirectStandardError (Join-Path $runtimeRoot ($stamp+'.stderr.log')) -PassThru
$ready = $false
for ($attempt=0; $attempt -lt 40; $attempt++) {
    Start-Sleep -Milliseconds 1000
    $process.Refresh()
    if ($process.HasExited) { throw 'O site encerrou durante a inicializacao. Consulte os registros privados em src\InfinityUrban.Web\.local.' }
    try { Invoke-RestMethod 'http://localhost:5189/api/auth/form' -TimeoutSec 1 | Out-Null; $ready=$true; break } catch { }
}
if (-not $ready) { throw 'A inicializacao nao terminou a tempo.' }
Write-Host 'Infinity Urban: http://localhost:5189/login'
Write-Host 'Aplicacao unica com Channex de PRODUCAO.'
if (-not $NoBrowser) { Start-Process 'http://localhost:5189/login' }
