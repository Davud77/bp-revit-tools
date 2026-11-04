param(
    [string]$BuildPython = (Join-Path $PSScriptRoot 'Входные данные\Первоначальный архив\Ac2Revit\.ifc-env\Scripts\python.exe'),
    [string]$RevitInstallDir = 'C:\Program Files\Autodesk\Revit 2026',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'Выходные данные/Установщики')
)
$ErrorActionPreference = 'Stop'
if (!(Test-Path -LiteralPath $BuildPython)) { throw 'Specify -BuildPython pointing to a CPython 3.12 environment with requirements.txt installed.' }
# One version source: the installer project. File names and the self-check below follow it.
$version = @(([xml](Get-Content -LiteralPath (Join-Path $PSScriptRoot 'installer\BotPlus.RT.Setup.csproj') -Raw)).Project.PropertyGroup.Version | Where-Object { $_ })[0]
if (!$version) { throw 'Version not found in installer\BotPlus.RT.Setup.csproj.' }
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

# ZIP entries always use '/': the installer and standard tools expect it, while Compress-Archive
# in Windows PowerShell 5.1 writes '\' (the first 1.6.4 build could not be unpacked because of it).
function New-Zip([string]$folder, [string]$zipPath) {
    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
    $root = [IO.Path]::GetFullPath($folder).TrimEnd('\') + '\'
    $archive = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in Get-ChildItem -LiteralPath $folder -Recurse -File -Force) {
            $name = $file.FullName.Substring($root.Length).Replace('\', '/')
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $name, [IO.Compression.CompressionLevel]::Optimal)
        }
    } finally { $archive.Dispose() }
}

& (Join-Path $PSScriptRoot 'build.ps1') -RevitInstallDir $RevitInstallDir
Push-Location (Join-Path $PSScriptRoot 'modules\codex-mcp\server')
try {
    pnpm install --frozen-lockfile
    if ($LASTEXITCODE -ne 0) { throw 'MCP dependency installation failed.' }
} finally { Pop-Location }
$stage = Join-Path $PSScriptRoot ('Выходные данные/Сборочные данные/setup-' + [guid]::NewGuid().ToString('N'))
try {
$payload = Join-Path $stage 'payload'
New-Item -ItemType Directory -Force $stage,$OutputDirectory | Out-Null
& (Join-Path $PSScriptRoot 'installer\prepare-tapir.ps1')
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
$sourceZip = Join-Path $OutputDirectory "BotPlus.RT-$version-source.zip"
New-Zip $source $sourceZip
New-Item -ItemType Directory -Force (Join-Path $payload 'source') | Out-Null
Copy-Item -LiteralPath $sourceZip -Destination (Join-Path $payload 'source')
$zip = Join-Path $stage 'payload.zip'
New-Zip $payload $zip
dotnet publish (Join-Path $PSScriptRoot 'installer\BotPlus.RT.Setup.csproj') -c Release -o (Join-Path $PSScriptRoot 'Выходные данные/Сборочные данные/dotnet/BotPlus.RT.Setup/publish') "-p:PayloadPath=$zip" -v minimal
if ($LASTEXITCODE -ne 0) { throw 'Installer publish failed.' }
$setup = Join-Path $OutputDirectory "BotPlus.RT-$version-Setup.exe"
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Выходные данные/Сборочные данные/dotnet/BotPlus.RT.Setup/publish/BotPlus.RT.Setup.exe') -Destination $setup -Force

# Self-check: the finished EXE must unpack its own payload with the installer code.
$verify = Join-Path $stage 'verify'
$check = Start-Process -FilePath $setup -ArgumentList ('"--verify-payload=' + $verify + '"') -Wait -PassThru -WindowStyle Hidden
if ($check.ExitCode -ne 0) { throw "Installer payload verification failed: $($check.ExitCode). See %LOCALAPPDATA%\BotPlus.RT\setup.log." }
$unpacked = Get-Content -LiteralPath (Join-Path $verify 'release.json') -Raw | ConvertFrom-Json
if ($unpacked.version -ne $version) { throw "Payload version $($unpacked.version) does not match $version." }
Write-Host "Payload verified: $((Get-ChildItem -LiteralPath $verify -Recurse -File).Count) files, version $version"

$hashes = foreach ($file in $setup,$sourceZip) { '{0}  {1}' -f (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant(),(Split-Path $file -Leaf) }
[IO.File]::WriteAllLines((Join-Path $OutputDirectory 'SHA256SUMS.txt'), $hashes)
Write-Host "Installer: $setup"
} finally {
    $resolvedStage = [IO.Path]::GetFullPath($stage)
    $stageRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'Выходные данные/Сборочные данные')) + [IO.Path]::DirectorySeparatorChar
    if (!$resolvedStage.StartsWith($stageRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe staging path.' }
    if (Test-Path -LiteralPath $resolvedStage) {
        if ((Get-Item -LiteralPath $resolvedStage).LinkType -or (Get-ChildItem -LiteralPath $resolvedStage -Recurse -Force -Attributes ReparsePoint)) { throw 'Linked content in staging directory.' }
        Remove-Item -LiteralPath $resolvedStage -Recurse -Force
    }
}
