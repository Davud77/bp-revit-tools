$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$lock = Get-Content (Join-Path $PSScriptRoot 'tapir/releases.json') -Raw | ConvertFrom-Json
$cache = Join-Path $root ('Выходные данные/Сборочные данные/tapir/' + $lock.release)
New-Item -ItemType Directory -Force $cache | Out-Null
foreach ($asset in $lock.assets) {
    $file = Join-Path $cache $asset.file
    if (!(Test-Path -LiteralPath $file) -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $asset.sha256) {
        Write-Host "Downloading Tapir $($lock.release) for Archicad $($asset.version)"
        Invoke-WebRequest -Uri $asset.url -OutFile $file
    }
    if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $asset.sha256) { throw "Tapir SHA-256 mismatch: $($asset.file)" }
}
