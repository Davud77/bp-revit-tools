param(
    [string]$BuildPython = (Join-Path $PSScriptRoot 'local\legacy\Ac2Revit\.ifc-env\Scripts\python.exe'),
    [string]$RevitInstallDir = 'C:\Program Files\Autodesk\Revit 2026',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'dist')
)
$ErrorActionPreference = 'Stop'
if (!(Test-Path -LiteralPath $BuildPython)) { throw 'Specify -BuildPython pointing to a CPython 3.12 environment with requirements.txt installed.' }
& (Join-Path $PSScriptRoot 'build.ps1') -RevitInstallDir $RevitInstallDir
Push-Location (Join-Path $PSScriptRoot 'modules\codex-mcp\server')
try {
    pnpm install --frozen-lockfile
    if ($LASTEXITCODE -ne 0) { throw 'MCP dependency installation failed.' }
} finally { Pop-Location }
$stage = Join-Path $PSScriptRoot ('artifacts\setup-' + [guid]::NewGuid().ToString('N'))
$payload = Join-Path $stage 'payload'
New-Item -ItemType Directory -Force $stage,$OutputDirectory | Out-Null
node (Join-Path $PSScriptRoot 'scripts\package.mjs') $PSScriptRoot $payload $BuildPython
if ($LASTEXITCODE -ne 0) { throw 'Packaging failed.' }
# Include the complete corresponding source for AGPL distribution.
$source = Join-Path $stage 'source'
Push-Location $PSScriptRoot
try { $files = @(git -c core.quotepath=false ls-files --cached --others --exclude-standard) }
finally { Pop-Location }
foreach ($relative in $files) {
    $inputFile = Join-Path $PSScriptRoot $relative
    if (!(Test-Path -LiteralPath $inputFile -PathType Leaf)) { continue }
    $destination = Join-Path $source $relative
    New-Item -ItemType Directory -Force (Split-Path $destination) | Out-Null
    Copy-Item -LiteralPath $inputFile -Destination $destination
}
$sourceZip = Join-Path $OutputDirectory 'BotPlus.RT-1.1.0-source.zip'
Compress-Archive -Path (Join-Path $source '*') -DestinationPath $sourceZip -Force
New-Item -ItemType Directory -Force (Join-Path $payload 'source') | Out-Null
Copy-Item -LiteralPath $sourceZip -Destination (Join-Path $payload 'source')
$zip = Join-Path $stage 'payload.zip'
Compress-Archive -Path (Join-Path $payload '*') -DestinationPath $zip -CompressionLevel Optimal
dotnet publish (Join-Path $PSScriptRoot 'installer\BotPlus.RT.Setup.csproj') -c Release -o (Join-Path $stage 'publish') "-p:PayloadPath=$zip" -v minimal
if ($LASTEXITCODE -ne 0) { throw 'Installer publish failed.' }
$setup = Join-Path $OutputDirectory 'BotPlus.RT-1.1.0-Setup.exe'
Copy-Item -LiteralPath (Join-Path $stage 'publish\BotPlus.RT.Setup.exe') -Destination $setup -Force
$hashes = foreach ($file in $setup,$sourceZip) { '{0}  {1}' -f (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant(),(Split-Path $file -Leaf) }
[IO.File]::WriteAllLines((Join-Path $OutputDirectory 'SHA256SUMS.txt'), $hashes)
Write-Host "Installer: $setup"
