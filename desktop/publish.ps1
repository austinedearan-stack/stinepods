# Builds the Windows app as one self-contained StinePods.exe (no .NET install needed) into ..\dist\windows.
# Usage: powershell -ExecutionPolicy Bypass -File desktop\publish.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root 'dist\windows'
dotnet publish (Join-Path $PSScriptRoot 'StinePods.Windows\StinePods.Windows.csproj') -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:PublishTrimmed=true -p:DebugType=none -o $out
Get-Item (Join-Path $out 'StinePods.exe') | Select-Object Name, @{n='MB'; e={[math]::Round($_.Length / 1MB, 1)}}
