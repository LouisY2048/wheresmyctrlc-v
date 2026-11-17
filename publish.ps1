param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64'
)
$ErrorActionPreference = 'Stop'
$projectPath = Join-Path $PSScriptRoot 'src\WheresMyCtrlCV\WheresMyCtrlCV.csproj'
$outputPath = Join-Path $PSScriptRoot "artifacts\$Runtime"
dotnet publish $projectPath -c Release -r $Runtime --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o $outputPath
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
Write-Host "Ready: $outputPath\WheresMyCtrlCV.exe"
