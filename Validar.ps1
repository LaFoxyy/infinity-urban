$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    $env:URBAN_PORTAL_TEST_ROOT = Join-Path $PSScriptRoot 'src\InfinityUrban.Web'
    $build = Join-Path $PSScriptRoot '.local\checks'
    & dotnet build 'tests/InfinityUrban.Checks/InfinityUrban.Checks.csproj' --nologo -o $build -p:UseAppHost=false -p:StaticWebAssetsEnabled=false
    if ($LASTEXITCODE -ne 0) { throw 'Falha ao compilar as verificacoes.' }
    & dotnet (Join-Path $build 'InfinityUrban.Checks.dll')
    if ($LASTEXITCODE -ne 0) { throw 'Falha nas verificacoes de integracao.' }
    foreach ($file in @('check-accommodations-hierarchy.mjs','check-card-actions-ui.mjs','check-channel-general-ui.mjs')) {
        & node (Join-Path $PSScriptRoot ('tools\interface\' + $file))
        if ($LASTEXITCODE -ne 0) { throw 'Falha na verificacao da interface.' }
    }
    & python (Join-Path $PSScriptRoot 'tools\package.py') --verify-only
    if ($LASTEXITCODE -ne 0) { throw 'Falha na verificacao de integridade.' }
} finally { Pop-Location }
