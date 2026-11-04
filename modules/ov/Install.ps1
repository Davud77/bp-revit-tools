param(
    [string]$RevitInstallDir = 'C:\Program Files\Autodesk\Revit 2026',
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'HeatLossViewer\HeatLossViewer.csproj'
if (-not $SkipBuild) {
    & dotnet build $project -c Release --nologo "-p:RevitInstallDir=$RevitInstallDir"
    if ($LASTEXITCODE -ne 0) { throw 'Сборка не выполнена, установка отменена.' }
}
$source = Join-Path $PSScriptRoot '..\..\Выходные данные\Сборочные данные\dotnet\HeatLossViewer\bin\Release\net8.0-windows'
if (-not (Test-Path -LiteralPath (Join-Path $source 'HeatLossViewer.dll'))) { throw 'Сначала соберите Release.' }
$addins = Join-Path $env:APPDATA 'Autodesk\Revit\Addins\2026'
$assemblyHash = (Get-FileHash -LiteralPath (Join-Path $source 'HeatLossViewer.dll') -Algorithm SHA256).Hash.Substring(0,12)
$destination = Join-Path $addins "HeatLossViewer\versions\$assemblyHash"
New-Item -ItemType Directory -Path $destination -Force | Out-Null
foreach ($name in @('HeatLossViewer.dll', 'HeatLossViewer.deps.json', 'HeatLossViewer.runtimeconfig.json', 'HeatLossViewer.pdb')) {
    $file = Join-Path $source $name
    if (Test-Path -LiteralPath $file) { Copy-Item -LiteralPath $file -Destination $destination -Force }
}
[xml]$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'HeatLossViewer\HeatLossViewer.addin') -Raw
$manifest.RevitAddIns.AddIn.Assembly = Join-Path $destination 'HeatLossViewer.dll'
$manifest.Save((Join-Path $addins 'HeatLossViewer.addin'))
Write-Host "Установлено: $destination. Перезапустите Revit 2026."
