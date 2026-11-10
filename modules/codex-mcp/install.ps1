param([switch]$EnableAutoConfirm)
$ErrorActionPreference = 'Stop'
$assembly = Join-Path $PSScriptRoot '..\..\Выходные данные\Сборочные данные\dotnet\BP.Revit.AI\bin\Release\net8.0-windows\BP.Revit.AI.dll'
$projectFile = Join-Path $PSScriptRoot 'plugin\BP.Revit.AI.csproj'
$manifestName = 'BP.Revit.AI.addin'
$addinId = [guid]'F0BB1CE2-6DA1-4F67-AB04-4C05F06397C4'
$addinsDirectory = Join-Path $env:APPDATA 'Autodesk\Revit\Addins\2026'
$pluginDirectory = Join-Path $addinsDirectory 'BP.Revit.AI'
$project = [xml](Get-Content -LiteralPath $projectFile -Raw)
$version = $project.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Некорректная версия надстройки в $($projectFile): '$version'." }
$versionDirectory = Join-Path $pluginDirectory "versions\$version"
$installedAssembly = Join-Path $versionDirectory 'BP.Revit.AI.dll'
$manifestPath = Join-Path $addinsDirectory $manifestName

$machineAddins = Join-Path $env:ProgramData 'Autodesk\Revit\Addins\2026'
foreach ($directory in $addinsDirectory, $machineAddins) {
    if (!(Test-Path -LiteralPath $directory -PathType Container)) { continue }
    foreach ($file in Get-ChildItem -LiteralPath $directory -Filter '*.addin' -File) {
        if ($file.FullName -eq $manifestPath) { continue }
        try { $registration = [xml](Get-Content -LiteralPath $file.FullName -Raw) }
        catch { continue }
        foreach ($entry in $registration.RevitAddIns.AddIn) {
            $id = [guid]::Empty
            if ([guid]::TryParse($entry.AddInId, [ref]$id) -and $id -eq $addinId) {
                throw "Обнаружена другая регистрация MCP. Удалите её перед установкой: $($file.FullName)"
            }
        }
    }
}

if (-not (Test-Path $assembly)) {
    throw "Не найден $assembly. Сначала выполните .\build.ps1."
}

$revitRunning = [bool](Get-Process -Name Revit -ErrorAction SilentlyContinue)
if ($revitRunning -and (Test-Path -LiteralPath $installedAssembly)) {
    throw "Revit запущен, а сборка версии $version уже установлена. Закройте Revit перед повторной установкой этой версии."
}

New-Item -ItemType Directory -Force -Path $versionDirectory | Out-Null
Copy-Item -LiteralPath $assembly -Destination $installedAssembly -Force

$assemblyPath = [System.Security.SecurityElement]::Escape($installedAssembly)
$manifest = @"
<?xml version="1.0" encoding="utf-8" standalone="no"?>
<RevitAddIns>
  <AddIn Type="Application">
    <Name>BP.Revit.AI</Name>
    <Assembly>$assemblyPath</Assembly>
    <AddInId>$addinId</AddInId>
    <FullClassName>BP.Revit.AI.PluginApplication</FullClassName>
    <VendorId>BTPL</VendorId>
    <VendorDescription>BP.Revit.AI local MCP bridge</VendorDescription>
  </AddIn>
</RevitAddIns>
"@

New-Item -ItemType Directory -Force -Path $addinsDirectory | Out-Null
Set-Content -LiteralPath $manifestPath -Value $manifest -Encoding UTF8
if ($EnableAutoConfirm) {
    $settingsDirectory = Join-Path $env:LOCALAPPDATA 'BP.Revit.AI'
    $settingsPath = Join-Path $settingsDirectory 'settings.json'
    New-Item -ItemType Directory -Force -Path $settingsDirectory | Out-Null
    $settings = if (Test-Path -LiteralPath $settingsPath) {
        Get-Content -Raw -LiteralPath $settingsPath | ConvertFrom-Json
    } else { [pscustomobject]@{} }
    $settings | Add-Member -NotePropertyName autoConfirm -NotePropertyValue $true -Force
    [IO.File]::WriteAllText($settingsPath, ($settings | ConvertTo-Json -Depth 20))
    Write-Host 'Автоподтверждение команд apply включено в локальных настройках.'
}
if ($revitRunning) {
    Write-Warning "Версия $version подготовлена для следующего запуска; уже открытый Revit продолжит работу с загруженным состоянием."
} else {
    Write-Host "Версия $version установлена. Запустите Revit 2026 и разрешите загрузку надстройки, если появится запрос."
}
