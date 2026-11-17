$ErrorActionPreference = 'Stop'
$taskWix = Join-Path $PSScriptRoot '.tools\wix.exe'
$taskDotnet = Join-Path $PSScriptRoot '.sdk\dotnet.exe'
if (-not (Test-Path -LiteralPath $taskDotnet)) { $taskDotnet = 'dotnet' }
if (-not (Test-Path -LiteralPath $taskWix)) {
    & $taskDotnet tool install wix --version 6.0.2 --tool-path (Join-Path $PSScriptRoot '.tools') --configfile (Join-Path $PSScriptRoot 'NuGet.Config')
    if ($LASTEXITCODE -ne 0) { throw 'Не удалось установить WiX.' }
}
if (Test-Path -LiteralPath (Join-Path $PSScriptRoot '.sdk\dotnet.exe')) { $env:DOTNET_ROOT = Join-Path $PSScriptRoot '.sdk' }
$taskPublish = Join-Path $PSScriptRoot 'artifacts\MyFences'
if (-not (Test-Path -LiteralPath (Join-Path $taskPublish 'MyFences.exe'))) { & (Join-Path $PSScriptRoot 'Build.ps1') }
& $taskWix build (Join-Path $PSScriptRoot 'Installer\MyFences.wxs') -arch x64 -d "ProjectRoot=$PSScriptRoot" -d "PublishDir=$taskPublish" -o (Join-Path $PSScriptRoot 'artifacts\MyFences-1.0.1-win-x64.msi')
if ($LASTEXITCODE -ne 0) { throw 'Сборка MSI не выполнена.' }
