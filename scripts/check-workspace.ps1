$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
foreach ($old in 'local','dist','artifacts','output','modules/ac2revit/artifacts','modules/ov/artifacts') {
    if (Test-Path -LiteralPath (Join-Path $root $old)) { throw "Obsolete workspace path: $old" }
}
$queue = [Collections.Generic.Queue[string]]::new()
foreach ($folder in 'src','modules','tests','installer','scripts','docs') { $queue.Enqueue((Join-Path $root $folder)) }
while ($queue.Count) {
    foreach ($item in Get-ChildItem -LiteralPath $queue.Dequeue() -Force) {
        # pnpm owns its dependency layout, which is excluded from Git and copied without links into the payload.
        if ($item.Name -in @('node_modules','.git')) { continue }
        if ($item.LinkType) { throw "Link in product sources: $($item.FullName)" }
        if ($item.PSIsContainer) {
            if ($item.Name -in @('bin','obj','__pycache__','.pytest_cache')) { throw "Generated files in product sources: $($item.FullName)" }
            $queue.Enqueue($item.FullName)
        }
    }
}
Push-Location $root
try {
    $files = @(git -c core.quotepath=false ls-files --cached --others --exclude-standard)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect source export list.' }
    foreach ($file in $files) {
        # Windows PowerShell 5.1 reads BOM-less scripts in the ANSI code page and breaks Cyrillic paths.
        if ($file -like '*.ps1' -and (Test-Path -LiteralPath $file)) {
            $bytes = [IO.File]::ReadAllBytes((Join-Path $root $file))
            $bom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
            if (!$bom -and ($bytes | Where-Object { $_ -gt 127 } | Select-Object -First 1)) { throw "PowerShell script with non-ASCII text needs UTF-8 BOM: $file" }
        }
        if ($file -match '^(Входные данные|Выходные данные|local|dist|artifacts|output)/|(^|/)(node_modules|bin|obj)/|\.(rvt|rte|pln|bpn|ifc|ifczip|pdf|exe|zip|token)$|(^|/)(RevitAPI(UI)?\.dll|settings\.json|bridge\.token)$') {
            throw "Private or generated file in source export: $file"
        }
    }
    Write-Output "PASS physical workspace directories; no migration links, generated source folders or private files in $($files.Count) source export entries"
} finally { Pop-Location }
