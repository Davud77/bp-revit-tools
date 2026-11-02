param([string]$RevitInstallDir = 'C:\Program Files\Autodesk\Revit 2026')
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    dotnet build BotPlus.RT.slnx -c Release -v minimal "-p:RevitInstallDir=$RevitInstallDir" "-p:Revit2026Dir=$RevitInstallDir"
    if ($LASTEXITCODE -ne 0) { throw 'BotPlus RT build failed.' }
} finally { Pop-Location }
