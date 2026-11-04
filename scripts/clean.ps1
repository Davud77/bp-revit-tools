param([switch]$Preview)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$queue = [Collections.Generic.Queue[string]]::new()
foreach ($folder in 'src','modules','tests','installer') { $queue.Enqueue((Join-Path $root $folder)) }
$targets = [Collections.Generic.List[string]]::new()
while ($queue.Count) {
    foreach ($item in Get-ChildItem -LiteralPath $queue.Dequeue() -Directory -Force) {
        if ($item.LinkType -or $item.Name -in @('node_modules','.git')) { continue }
        if ($item.Name -in @('bin','obj','__pycache__','.pytest_cache')) { $targets.Add($item.FullName) }
        else { $queue.Enqueue($item.FullName) }
    }
}
foreach ($spec in @(
    @{Folder='Выходные данные/Сборочные данные'; Filter='setup-*'},
    @{Folder='Выходные данные/Локальные данные'; Filter='payload-*'}
)) {
    $folder = Join-Path $root $spec.Folder
    if (Test-Path -LiteralPath $folder) {
        foreach ($item in Get-ChildItem -LiteralPath $folder -Directory -Filter $spec.Filter) {
            if ($item.LinkType) { throw "Refusing linked cleanup target: $($item.FullName)" }
            $targets.Add($item.FullName)
        }
    }
}
foreach ($target in $targets) {
    $absolute = [IO.Path]::GetFullPath($target)
    if (!$absolute.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw "Target outside workspace: $absolute" }
    if (Get-ChildItem -LiteralPath $absolute -Recurse -Force -Attributes ReparsePoint) { throw "Linked content in cleanup target: $absolute" }
    if ($Preview) { Write-Output $absolute }
    else { Remove-Item -LiteralPath $absolute -Recurse -Force; Write-Output "Removed: $absolute" }
}
