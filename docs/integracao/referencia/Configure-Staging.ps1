param()

$ErrorActionPreference = 'Stop'
$apiProject = Join-Path $PSScriptRoot 'src\Stayline.Api\Stayline.Api.csproj'
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'Install the .NET 10 SDK first.' }

# Read the value without putting it in source, command arguments, or shell history.
$secureKey = Read-Host 'Channex staging API key (hidden)' -AsSecureString
$keyPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureKey)
try {
    $stagingKey = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($keyPointer)
    if ([string]::IsNullOrWhiteSpace($stagingKey) -or $stagingKey -match '\s') { throw 'Enter a key without whitespace.' }
    $configuration = @{ 'Channex:ApiKey' = $stagingKey; 'Channex:Enabled' = 'true' } | ConvertTo-Json -Compress
    $processInfo = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $processInfo.UseShellExecute = $false
    $processInfo.CreateNoWindow = $true
    $processInfo.RedirectStandardInput = $true
    $processInfo.RedirectStandardOutput = $true
    $processInfo.RedirectStandardError = $true
    foreach ($argument in @('user-secrets','set','--project',$apiProject)) { $processInfo.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($processInfo)
    try {
        $process.StandardInput.WriteLine($configuration)
        $process.StandardInput.Close()
        $outputTask = $process.StandardOutput.ReadToEndAsync()
        $errorTask = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        # Never relay subprocess input or diagnostic output containing secret values.
        $savedToProfile = $process.ExitCode -eq 0
        $null = $outputTask.GetAwaiter().GetResult()
        $null = $errorTask.GetAwaiter().GetResult()
    } finally { $process.Dispose() }
    if ($savedToProfile) {
        Write-Host 'Staging credentials saved in .NET User Secrets outside this project.'
    } else {
        $localDirectory = Join-Path (Split-Path $apiProject -Parent) '.local'
        New-Item -ItemType Directory -Force -Path $localDirectory | Out-Null
        [IO.File]::WriteAllText((Join-Path $localDirectory 'secrets.json'), $configuration)
        Write-Host 'Profile storage was unavailable. Saved to ignored .local/secrets.json, outside browser assets and source archives.'
    }
    Write-Host 'Restart Stayline using: dotnet run --project src/Stayline.Api'
} finally {
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($keyPointer)
    $stagingKey = $null
    $configuration = $null
    $secureKey.Dispose()
}
