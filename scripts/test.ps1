param([string]$BuildPython = (Join-Path $PSScriptRoot '..\local\legacy\Ac2Revit\.ifc-env\Scripts\python.exe'))
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
function CheckExit([string]$step) { if ($LASTEXITCODE -ne 0) { throw "$step failed: $LASTEXITCODE" } }
& (Join-Path $root 'build.ps1')
dotnet run --project (Join-Path $root 'tests\HostChecks\HostChecks.csproj') -c Release
CheckExit 'Host lifecycle checks'
dotnet run --project (Join-Path $root 'tests\UpdateChecks\UpdateChecks.csproj') -c Release
CheckExit 'GitHub update checks'
dotnet run --project (Join-Path $root 'modules/codex-mcp/tests/ApprovalSettings.Tests/ApprovalSettings.Tests.csproj') -c Release
CheckExit 'MCP approval settings'
Push-Location (Join-Path $root 'modules\codex-mcp\server')
try { node --test test/*.test.js; CheckExit 'MCP tests' } finally { Pop-Location }
Push-Location $root
try { node --test installer/configure-mcp.test.mjs; CheckExit 'MCP configuration tests' } finally { Pop-Location }
Push-Location (Join-Path $root 'modules\ac2revit')
try {
    & $BuildPython -m unittest discover -s tests -p 'test_*.py'; CheckExit 'Ac2Revit Python tests'
    dotnet run --project tests/ContractCheck/ContractCheck.csproj -c Release; CheckExit 'Ac2Revit contracts'
} finally { Pop-Location }
Push-Location (Join-Path $root 'modules\ov')
try {
    dotnet run --project tests/ExportChecks.csproj -c Release; CheckExit 'OV export checks'
    dotnet run --project tests/ScopeEditorChecks/ScopeEditorChecks.csproj -c Release; CheckExit 'OV floor and room tables'
    foreach ($test in 'calculation-checks','poteri-checks','selection-checks','radiator-checks','library-inputs-checks') {
        node "tests/$test.cjs"; CheckExit $test
    }
} finally { Pop-Location }
