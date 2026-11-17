$ErrorActionPreference = 'Stop'
$taskDotnet = Join-Path $PSScriptRoot '.sdk\dotnet.exe'
if (-not (Test-Path -LiteralPath $taskDotnet)) { $taskDotnet = 'dotnet' }
$taskOutput = Join-Path $PSScriptRoot 'artifacts\MyFences'
& $taskDotnet publish (Join-Path $PSScriptRoot 'MyFences.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $taskOutput
if ($LASTEXITCODE -ne 0) { throw 'Сборка не выполнена.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination $taskOutput -Force
Compress-Archive -Path (Join-Path $taskOutput '*') -DestinationPath (Join-Path $PSScriptRoot 'artifacts\MyFences-1.0.2-win-x64.zip') -Force
Write-Host "Готово: $taskOutput"
