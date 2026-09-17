param([string]$MapImage)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.runtime'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_NOLOGO = '1'
$monitorCheckOutput = Join-Path $PSScriptRoot ('artifacts\checks-' + [DateTime]::Now.ToString('yyyyMMdd-HHmmss'))
$monitorCheckArgs = @($monitorCheckOutput)
if ($MapImage) { $monitorCheckArgs += (Resolve-Path -LiteralPath $MapImage).Path }
dotnet run --project (Join-Path $PSScriptRoot 'tests\RustMonitor.Checks.csproj') -c Release -- @monitorCheckArgs
if ($LASTEXITCODE -ne 0) { throw 'Checks failed.' }
dotnet run --project (Join-Path $PSScriptRoot 'tests\admin-plugin\AdminPlugin.Checks.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw 'Admin plugin checks failed.' }
Write-Output $monitorCheckOutput
