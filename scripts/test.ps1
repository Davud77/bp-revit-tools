param(
    [string]$BuildPython = (Join-Path $PSScriptRoot '..\Входные данные\Первоначальный архив\Ac2Revit\.ifc-env\Scripts\python.exe'),
    [string]$Node = 'node'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$env:PYTHONDONTWRITEBYTECODE = '1'
if (!(Get-Command $Node -ErrorAction SilentlyContinue)) { throw "Node.js not found: $Node. Install Node.js or pass -Node <path to node.exe>." }
if (!(Test-Path -LiteralPath $BuildPython)) { throw "Python not found: $BuildPython. Pass -BuildPython <CPython 3.12 with requirements.txt>." }

# Native tools report progress on stderr; Windows PowerShell 5.1 would turn that into a terminating error
# under 'Stop'. Each step therefore runs with 'Continue' and is judged only by its exit code.
function Step([string]$name, [string]$directory, [scriptblock]$command) {
    Push-Location $directory
    $previous = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & $command 2>&1 | ForEach-Object { "$_" }
        $code = $LASTEXITCODE
    } finally { $ErrorActionPreference = $previous; Pop-Location }
    if ($code -ne 0) { throw "$name failed: $code" }
    Write-Output "PASS $name"
}

& (Join-Path $PSScriptRoot 'check-workspace.ps1')
Step 'Build' $root { & (Join-Path $root 'build.ps1') }
Step 'Host lifecycle checks' $root { dotnet run --project tests\HostChecks\HostChecks.csproj -c Release }
Step 'GitHub update checks' $root { dotnet run --project tests\UpdateChecks\UpdateChecks.csproj -c Release }
Step 'Update window states' $root { dotnet run --project tests\UpdateUiChecks\UpdateUiChecks.csproj -c Release -- (Join-Path $root 'Выходные данные\Проверки\Обновления') }
Step 'Opening workflow and exchange checks' $root { dotnet run --project tests\OpeningChecks\OpeningChecks.csproj -c Release }
Step 'MCP approval settings' $root { dotnet run --project modules\codex-mcp\tests\ApprovalSettings.Tests\ApprovalSettings.Tests.csproj -c Release }
Step 'MCP tests' (Join-Path $root 'modules\codex-mcp\server') { & $Node --test test/*.test.js }
Step 'MCP configuration tests' $root { & $Node --test installer/configure-mcp.test.mjs }
$ac2revit = Join-Path $root 'modules\ac2revit'
Step 'Ac2Revit Python tests' $ac2revit { & $BuildPython -m unittest discover -s tests -p 'test_*.py' }
Step 'Ac2Revit contracts' $ac2revit { dotnet run --project tests/ContractCheck/ContractCheck.csproj -c Release }
$ov = Join-Path $root 'modules\ov'
Step 'OV export checks' $ov { dotnet run --project tests/ExportChecks.csproj -c Release }
Step 'OV floor and room tables' $ov { dotnet run --project tests/ScopeEditorChecks/ScopeEditorChecks.csproj -c Release }
foreach ($test in 'calculation-checks','poteri-checks','selection-checks','radiator-checks','library-inputs-checks') {
    Step $test $ov { & $Node "tests/$test.cjs" }
}
Write-Output 'PASS all checks'
