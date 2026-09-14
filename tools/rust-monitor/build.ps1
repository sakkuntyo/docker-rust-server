param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$monitorRoot = $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $monitorRoot '.runtime'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_NOLOGO = '1'
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $monitorRoot 'artifacts\app' }
dotnet publish (Join-Path $monitorRoot 'src\RustMonitor.csproj') -c Release --self-contained false -o $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Write-Output (Join-Path $OutputDirectory 'RustMonitor.exe')
