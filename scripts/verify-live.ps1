#Requires -Version 7.0
<#
.SYNOPSIS
    Verifies TabularTools' live features against the synthetic LiveCheck model in your own workspace. Read-only.

.DESCRIPTION
    Runs DAX queries through the TabularTools CLI against fixtures/live/LiveCheck.SemanticModel deployed to a Power BI
    or Fabric workspace, and writes a Markdown report with the expected and actual result of each check. It answers the
    questions M4 couldn't answer without a tenant (ARCHITECTURE §11.2, risk R3):

      - Does XMLA-over-HTTPS with our own Entra ID token work?
      - Do model measures that reference an overridden measure use the query's definition? (differential tests rely on it)
      - Can DEFINE COLUMN override an existing calculated column? (decides whether tests.columnOverrides can be on)
      - Does a query-scoped measure clash with a model measure of the same name on another table?
      - Do RLS roles, row caps, the query guard, and the full test run (with recording and offline replay) work?

    Nothing is written to the model: every query is a DAX query or a $SYSTEM DMV select. Files are only written to a
    temporary folder and to the report.

    Before running: deploy fixtures/live/LiveCheck.SemanticModel (see fixtures/live/README.md) and refresh it once.

.PARAMETER Endpoint
    The workspace XMLA endpoint, e.g. powerbi://api.powerbi.com/v1.0/myorg/TabularTools%20Test

.PARAMETER Database
    The semantic model name. Default: LiveCheck.

.PARAMETER Auth
    interactive (default), azureCli, deviceCode, servicePrincipal or default.

.PARAMETER Report
    Where to write the Markdown report. Default: live-verification.md in the current folder.

.PARAMETER Cli
    Path to TabularTools.Cli.dll. Default: the Release build in this repository (built if missing).

.EXAMPLE
    ./scripts/verify-live.ps1 -Endpoint 'powerbi://api.powerbi.com/v1.0/myorg/TabularTools%20Test'
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Endpoint,
    [string] $Database = 'LiveCheck',
    [ValidateSet('interactive', 'azureCli', 'deviceCode', 'servicePrincipal', 'default')] [string] $Auth = 'interactive',
    [string] $Report = (Join-Path (Get-Location) 'live-verification.md'),
    [string] $Cli
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (-not $Cli) {
    $Cli = Join-Path $repo 'src/TabularTools.Cli/bin/Release/net10.0/TabularTools.Cli.dll'
    if (-not (Test-Path $Cli)) {
        dotnet build (Join-Path $repo 'src/TabularTools.Cli/TabularTools.Cli.csproj') -c Release | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    }
}
foreach ($tool in 'dotnet', 'git') {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { throw "$tool isn't on PATH." }
}

$work = Join-Path ([IO.Path]::GetTempPath()) ('tabulartools-live-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $work | Out-Null
$config = Join-Path $work 'tabulartools.yaml'
@"
schemaVersion: 1
profiles:
  livecheck:
    endpoint: '$($Endpoint.Replace("'", "''"))'
    database: '$($Database.Replace("'", "''"))'
    auth: $Auth
  replay:
    recordings: live.recordings.yaml
tests:
  profile: livecheck
"@ | Set-Content -Path $config -Encoding utf8NoBOM

function Invoke-Cli {
    param([string[]] $Arguments, [string] $WorkingDirectory = $work)
    $errFile = Join-Path $work 'stderr.txt'
    Push-Location $WorkingDirectory
    try {
        $out = & dotnet $Cli @Arguments 2> $errFile | Out-String
        $code = $LASTEXITCODE
    }
    finally { Pop-Location }
    [pscustomobject]@{ Exit = $code; Out = $out; Err = (Get-Content $errFile -Raw -ErrorAction SilentlyContinue) }
}

function Invoke-Dax {
    param([string] $Query, [string] $Role, [int] $MaxRows = 500)
    $arguments = @('dax', '--config', $config, '--profile', 'livecheck', '--query', $Query, '--format', 'json', '--max-rows', $MaxRows)
    if ($Role) { $arguments += @('--role', $Role) }
    $r = Invoke-Cli $arguments
    $json = if ($r.Exit -eq 0) { $r.Out | ConvertFrom-Json } else { $null }
    [pscustomobject]@{ Exit = $r.Exit; Json = $json; Error = ($r.Err ?? '').Trim() }
}

$results = [System.Collections.Generic.List[object]]::new()
function Add-Result {
    param([string] $Check, [string] $Expected, [string] $Actual, [ValidateSet('PASS', 'FAIL', 'INFO')] [string] $Status, [string] $Meaning = '')
    $results.Add([pscustomobject]@{ Check = $Check; Expected = $Expected; Actual = $Actual; Status = $Status; Meaning = $Meaning })
    $color = @{ PASS = 'Green'; FAIL = 'Red'; INFO = 'Yellow' }[$Status]
    Write-Host ("[{0}] {1}: {2}" -f $Status, $Check, $Actual) -ForegroundColor $color
}

function Get-Scalar($r) { if ($r.Exit -ne 0) { "error (exit $($r.Exit)): $($r.Error)" } else { [string]$r.Json.rows[0][0] } }

function Test-Scalar {
    param([string] $Check, [string] $Query, [string] $Expected, [string] $Role, [string] $Meaning = '')
    $r = Invoke-Dax -Query $Query -Role $Role
    $actual = Get-Scalar $r
    $ok = $r.Exit -eq 0 -and [decimal]::TryParse($actual, [ref]$null) -and [decimal]$actual -eq [decimal]$Expected
    Add-Result $Check $Expected $actual ($ok ? 'PASS' : 'FAIL') $Meaning
    return $r
}

Write-Host "TabularTools live verification against $Endpoint / $Database ($Auth)" -ForegroundColor Cyan

# 1. Connection and token.
$first = Test-Scalar 'Connect with our own token' 'EVALUATE ROW("one", 1)' '1' -Meaning 'XMLA over HTTPS works with a token from Azure.Identity.'
if ($first.Exit -ne 0) {
    Write-Host 'Cannot connect; stopping. Check the endpoint, XMLA read access on the capacity, and Build permission on the model.' -ForegroundColor Red
}
else {
    # 2. DMV.
    $dmv = Invoke-Dax 'SELECT [Name] FROM $SYSTEM.TMSCHEMA_TABLES'
    $tables = if ($dmv.Exit -eq 0) { ($dmv.Json.rows | ForEach-Object { $_[0] } | Sort-Object) -join ', ' } else { "error: $($dmv.Error)" }
    Add-Result '$SYSTEM DMV select' 'Facts, Years' $tables ($tables -eq 'Facts, Years' ? 'PASS' : 'FAIL')

    # 3. Baseline values (also checks the model was refreshed after deployment).
    Test-Scalar 'Baseline [Base Amount]' 'EVALUATE ROW("v", [Base Amount])' '670' -Meaning 'The model is deployed and refreshed.' | Out-Null
    Test-Scalar 'Baseline [Dependent]' 'EVALUATE ROW("v", [Dependent])' '1340' | Out-Null

    # 4. Query-scoped overrides (risk R3).
    Test-Scalar 'DEFINE MEASURE overrides a model measure' 'DEFINE MEASURE Facts[Base Amount] = 1000 EVALUATE ROW("v", [Base Amount])' '1000' | Out-Null
    $dep = Invoke-Dax 'DEFINE MEASURE Facts[Base Amount] = 1000 EVALUATE ROW("v", [Dependent])'
    $depValue = Get-Scalar $dep
    $depStatus = $depValue -eq '2000' ? 'PASS' : 'FAIL'
    Add-Result 'Model measures use an overridden measure (R3)' '2000' $depValue $depStatus `
        ($depStatus -eq 'PASS' ? 'Differential tests can rely on dependents picking up overrides.' : 'Dependents do NOT pick up overrides: the planner must also override every dependent.')

    $col = Invoke-Dax 'DEFINE COLUMN Facts[Double Amount] = Facts[Amount] * 3 EVALUATE ROW("v", SUM(Facts[Double Amount]))'
    $colValue = Get-Scalar $col
    $colMeaning = switch ($colValue) {
        '2010' { 'DEFINE COLUMN overrides existing calculated columns: tests.columnOverrides can be turned on.' }
        '1340' { 'The model column wins over DEFINE COLUMN: keep tests.columnOverrides off.' }
        default { 'DEFINE COLUMN can''t redefine an existing column: keep tests.columnOverrides off.' }
    }
    Add-Result 'DEFINE COLUMN on an existing calculated column (R3)' '2010 if supported' $colValue 'INFO' $colMeaning
    Test-Scalar 'DEFINE COLUMN adds a new column' 'DEFINE COLUMN Facts[Triple] = Facts[Amount] * 3 EVALUATE ROW("v", SUM(Facts[Triple]))' '2010' | Out-Null

    $moved = Invoke-Dax 'DEFINE MEASURE Years[Base Amount] = 1 EVALUATE ROW("v", [Base Amount])'
    Add-Result 'Query measure named like a model measure on another table' 'informational' (Get-Scalar $moved) 'INFO' `
        'Tells whether moved measures could be overridden on their new table (today they are left to the model).'

    # 5. RLS, row cap, guard.
    Test-Scalar 'Evaluate as an RLS role' 'EVALUATE ROW("v", [Base Amount])' '220' -Role 'East Only' -Meaning 'asRole works (needs admin rights on the model).' | Out-Null
    $capped = Invoke-Dax 'EVALUATE Facts' -MaxRows 2
    $cap = if ($capped.Exit -eq 0) { "$($capped.Json.rowCount) rows, truncated=$($capped.Json.truncated)" } else { "error: $($capped.Error)" }
    Add-Result 'Row cap' '2 rows, truncated=True' $cap ($cap -eq '2 rows, truncated=True' ? 'PASS' : 'FAIL')
    $command = '{"refresh":{"type":"calculate","objects":[{"database":"' + $Database + '"}]}}'
    $tmsl = Invoke-Cli @('dax', '--config', $config, '--profile', 'livecheck', '--query', $command)
    $guarded = $tmsl.Exit -eq 2 -and $tmsl.Err -match "aren't allowed"
    Add-Result 'TMSL is rejected before connecting' 'exit 2' "exit $($tmsl.Exit)" ($guarded ? 'PASS' : 'FAIL')

    # 6. End to end: a git repo with the model, a test file and an edit, run differential tests live, record, replay offline.
    $e2e = Join-Path $work 'repo'
    New-Item -ItemType Directory -Path $e2e | Out-Null
    Copy-Item -Recurse (Join-Path $repo 'fixtures/live/LiveCheck.SemanticModel') $e2e
    Copy-Item (Join-Path $repo 'fixtures/live/livecheck.measuretests.yaml') $e2e
    Copy-Item $config $e2e
    git -C $e2e init -q -b main | Out-Null
    git -C $e2e -c user.name=verify -c user.email=verify@example.invalid add -A | Out-Null
    git -C $e2e -c user.name=verify -c user.email=verify@example.invalid -c commit.gpgsign=false commit -q -m LiveCheck | Out-Null

    $clean = Invoke-Cli @('test', 'run', 'livecheck.measuretests.yaml', '--format', 'json') $e2e
    $cleanSummary = if ($clean.Out) { ($clean.Out | ConvertFrom-Json).summary } else { $null }
    $cleanText = $cleanSummary ? "passed $($cleanSummary.passed), failed $($cleanSummary.failed), errored $($cleanSummary.errored)" : "exit $($clean.Exit): $($clean.Err)"
    Add-Result 'Test run, no changes' 'passed 4, failed 0, errored 0' $cleanText ($cleanText -eq 'passed 4, failed 0, errored 0' ? 'PASS' : 'FAIL')

    $facts = Join-Path $e2e 'LiveCheck.SemanticModel/definition/tables/Facts.tmdl'
    (Get-Content $facts -Raw).Replace("measure 'Base Amount' = SUM(Facts[Amount])", "measure 'Base Amount' = SUM(Facts[Amount]) + 1") | Set-Content $facts -NoNewline -Encoding utf8NoBOM
    $edited = Invoke-Cli @('test', 'run', 'livecheck.measuretests.yaml', '--format', 'json', '--record', 'live.recordings.yaml') $e2e
    $editedJson = if ($edited.Out) { $edited.Out | ConvertFrom-Json } else { $null }
    $outcome = if ($editedJson) {
        $diff = $editedJson.files[0].results | Where-Object { $_.caseId -eq 'amounts-by-region-year' -and $_.assertion -eq 'differential' }
        ($diff.measures | ForEach-Object { "$($_.measure)=$($_.change)" }) -join ', '
    }
    else { "exit $($edited.Exit): $($edited.Err)" }
    $expectedOutcome = 'Base Amount=expectedChange, Dependent=regression, Margin=regression'
    Add-Result 'Differential run after editing [Base Amount]' $expectedOutcome $outcome ($outcome -eq $expectedOutcome ? 'PASS' : 'FAIL') `
        'Base and head definitions ran against the same live data; the dependents changed through the override.'
    $editedSummary = $editedJson ? "passed $($editedJson.summary.passed), failed $($editedJson.summary.failed), errored $($editedJson.summary.errored)" : '-'
    Add-Result 'Edited run summary (snapshots see the edit too)' 'passed 1, failed 3, errored 0' $editedSummary ($editedSummary -eq 'passed 1, failed 3, errored 0' ? 'PASS' : 'FAIL')

    $replay = Invoke-Cli @('test', 'run', 'livecheck.measuretests.yaml', '--format', 'json', '--profile', 'replay') $e2e
    $replaySummary = if ($replay.Out) { $s = ($replay.Out | ConvertFrom-Json).summary; "passed $($s.passed), failed $($s.failed), errored $($s.errored)" } else { "exit $($replay.Exit): $($replay.Err)" }
    Add-Result 'Offline replay of the recorded run' $editedSummary $replaySummary ($replaySummary -eq $editedSummary ? 'PASS' : 'FAIL') 'Recordings captured from the service replay identically.'
    $recordings = Join-Path $e2e 'live.recordings.yaml'
    if (Test-Path $recordings) { Copy-Item $recordings (Join-Path (Split-Path $Report -Parent) 'livecheck.recordings.yaml') -Force }
}

# Report.
$version = (Invoke-Cli @('--version')).Out.Trim()
$lines = @(
    '# TabularTools live verification',
    '',
    "- Date: $((Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm')) UTC",
    "- TabularTools: $version",
    "- OS: $([System.Runtime.InteropServices.RuntimeInformation]::OSDescription)",
    "- Endpoint host: $(([uri]$Endpoint.Replace('powerbi://', 'https://').Replace('asazure://', 'https://')).Host) · auth: $Auth",
    '',
    '| Status | Check | Expected | Actual | What it means |',
    '|---|---|---|---|---|'
)
foreach ($r in $results) {
    $cells = @($r.Status, $r.Check, $r.Expected, $r.Actual, $r.Meaning) | ForEach-Object { ($_ ?? '').Replace('|', '\|').Replace("`r", ' ').Replace("`n", ' ') }
    $lines += '| ' + ($cells -join ' | ') + ' |'
}
$lines += ''
$lines += 'Paste this report into docs/milestones/M4.md under "Live verification". The recordings file next to it holds only the synthetic LiveCheck values.'
$lines -join "`n" | Set-Content -Path $Report -Encoding utf8NoBOM
Write-Host "Report: $Report" -ForegroundColor Cyan
Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
if ($results | Where-Object Status -eq 'FAIL') { exit 1 }
