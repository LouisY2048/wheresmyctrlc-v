$ErrorActionPreference = 'Stop'
$projectPath = Join-Path $PSScriptRoot 'src\WheresMyCtrlCV\WheresMyCtrlCV.csproj'
dotnet run --project $projectPath -c Release
if ($LASTEXITCODE -ne 0) { throw 'Application exited with an error.' }
