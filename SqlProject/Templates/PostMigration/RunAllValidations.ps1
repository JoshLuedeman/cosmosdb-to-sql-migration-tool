<#
.SYNOPSIS
    Runs all post-migration validation SQL scripts in order and produces a
    Markdown + HTML report from dbo.ValidationResults.

.DESCRIPTION
    Generates a RunId GUID, executes 01-RowCountValidation.sql through
    06-ForeignKeyValidation.sql via Invoke-Sqlcmd, then renders the
    ValidationReport.md.template and ValidationReport.html.template
    against the captured results.

    Exits 0 if no FAIL rows were recorded, 1 otherwise. When dot-sourced,
    returns a hashtable of run metadata instead of exiting.

    Per official sqlcmd documentation, -Variable on the command line has
    higher precedence than ':setvar' in the script body, so the orchestrator
    RunId wins over the in-script default sentinel. A post-script smoke
    query verifies this on every run.

.PARAMETER ServerInstance
    SQL Server / Azure SQL DB instance (e.g. 'tcp:myserver.database.windows.net,1433').

.PARAMETER Database
    Target database name.

.PARAMETER ScriptsRoot
    Folder containing the generated SQL scripts and report templates.
    Defaults to the orchestrator's own folder.

.PARAMETER OutputDir
    Folder to write the rendered reports into. Created if missing.
    Defaults to <ScriptsRoot>/Reports.

.PARAMETER Credential
    SQL authentication credentials. Mutually exclusive with -AccessToken.

.PARAMETER AccessToken
    Azure AD access token. Use 'Get-AzAccessToken -ResourceUrl
    https://database.windows.net/' to obtain. Mutually exclusive with
    -Credential. Required for managed-identity / service-principal flows.

.PARAMETER QueryTimeoutSeconds
    Per-statement timeout. Bumped to 1800 s default because the FK
    validation cursor's per-FK orphan scans can dominate runtime on
    large databases.

.PARAMETER ConnectionTimeoutSeconds
    Connection establishment timeout.

.PARAMETER MaxOrphanScanRows
    Forwarded to 06-ForeignKeyValidation.sql. Tables larger than this row
    count have their orphan scan skipped (per FK).

.PARAMETER MissingIndexWarnThreshold
    Forwarded to 04-PerformanceBaseline.sql. Missing-index suggestions
    above this improvement_measure escalate from INFO to WARN.

.PARAMETER MaxScopeTablesForPlanCache
    Forwarded to 04-PerformanceBaseline.sql. Caps the OR-of-CHARINDEX
    predicate used when scanning the plan cache.

.PARAMETER SampleRowCount
    Forwarded to 03-SampleDataComparison.sql. Number of first/last rows
    sampled per table for the JSON diff.

.PARAMETER Reset
    TRUNCATE TABLE dbo.ValidationResults before running. Use to isolate
    runs in a shared environment.

.PARAMETER SkipSampleData
    Skip 03-SampleDataComparison.sql. The sample comparison requires
    Cosmos-side data to have been re-serialized in matching format
    (FOR JSON PATH, INCLUDE_NULL_VALUES) -- skip when that side isn't
    available.

.PARAMETER OnlySections
    Run only the scripts whose Category name appears in this list. Valid:
    RowCount, Checksum, Sample, Performance, Index, ForeignKey.

.EXAMPLE
    .\RunAllValidations.ps1 -ServerInstance 'tcp:my.database.windows.net,1433' `
                            -Database 'TargetDb' `
                            -AccessToken (Get-AzAccessToken -ResourceUrl https://database.windows.net/).Token

.EXAMPLE
    .\RunAllValidations.ps1 -ServerInstance 'localhost' -Database 'TargetDb' `
                            -Credential (Get-Credential) -WhatIf
#>
[CmdletBinding(SupportsShouldProcess = $true, DefaultParameterSetName = 'IntegratedSecurity')]
param(
    [Parameter(Mandatory = $true)] [string] $ServerInstance,
    [Parameter(Mandatory = $true)] [string] $Database,
    [string] $ScriptsRoot = $PSScriptRoot,
    [string] $OutputDir,

    [Parameter(ParameterSetName = 'SqlAuth')]
    [pscredential] $Credential,

    [Parameter(ParameterSetName = 'AadToken')]
    [string] $AccessToken,

    [int] $QueryTimeoutSeconds        = 1800,
    [int] $ConnectionTimeoutSeconds   = 30,
    [int] $MaxOrphanScanRows          = 10000000,
    [int] $MissingIndexWarnThreshold  = 100000,
    [int] $MaxScopeTablesForPlanCache = 50,
    [int] $SampleRowCount             = 100,

    [switch] $Reset,
    [switch] $SkipSampleData,

    [ValidateSet('RowCount', 'Checksum', 'Sample', 'Performance', 'Index', 'ForeignKey')]
    [string[]] $OnlySections
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Map script-file glob prefix -> SQL Category name in dbo.ValidationResults.
$script:CategoryMap = @{
    '01-' = 'RowCount'
    '02-' = 'Checksum'
    '03-' = 'Sample'
    '04-' = 'Performance'
    '05-' = 'Index'
    '06-' = 'ForeignKey'
}

if (-not $OutputDir) {
    $OutputDir = Join-Path $ScriptsRoot 'Reports'
}

# -------------------------------------------------------------------------
# Helpers
# -------------------------------------------------------------------------

function Assert-SqlServerModule {
    if (Get-Module -ListAvailable -Name SqlServer) {
        Import-Module SqlServer -ErrorAction Stop
        return
    }
    throw "The 'SqlServer' PowerShell module is required. Install it with: " +
          "Install-Module SqlServer -Scope CurrentUser -AllowClobber"
}

function Get-SqlcmdSplat {
    $splat = @{
        ServerInstance        = $ServerInstance
        Database              = $Database
        QueryTimeout          = $QueryTimeoutSeconds
        ConnectionTimeout     = $ConnectionTimeoutSeconds
        ErrorAction           = 'Stop'
        TrustServerCertificate = $true
    }
    if ($Credential)  { $splat['Credential']  = $Credential }
    if ($AccessToken) { $splat['AccessToken'] = $AccessToken }
    return $splat
}

function Get-SqlVariables {
    param([string] $RunId)
    return @(
        "RunId=$RunId",
        "MaxOrphanScanRows=$MaxOrphanScanRows",
        "MissingIndexWarnThreshold=$MissingIndexWarnThreshold",
        "MaxScopeTablesForPlanCache=$MaxScopeTablesForPlanCache",
        "SampleRowCount=$SampleRowCount"
    )
}

function Invoke-ValidationScript {
    param(
        [Parameter(Mandatory)] [System.IO.FileInfo] $Script,
        [Parameter(Mandatory)] [string] $RunId,
        [Parameter(Mandatory)] [string] $ExpectedCategory,
        [Parameter(Mandatory)] [hashtable] $SqlcmdSplat
    )

    Write-Host "  > Running $($Script.Name) ..." -ForegroundColor Cyan
    $sw = [System.Diagnostics.Stopwatch]::StartNew()

    try {
        Invoke-Sqlcmd @SqlcmdSplat `
            -InputFile $Script.FullName `
            -Variable (Get-SqlVariables -RunId $RunId) | Out-Null
        $sw.Stop()
        Write-Host ("    ok ({0:N1} s)" -f $sw.Elapsed.TotalSeconds) -ForegroundColor Green
    }
    catch {
        $sw.Stop()
        Write-Host ("    FAILED ({0:N1} s): $_" -f $sw.Elapsed.TotalSeconds) -ForegroundColor Red

        # Record the failure as a FAIL row in ValidationResults so the
        # final report shows the gap explicitly.
        $msg = ($_.Exception.Message -replace "'", "''")
        $insertFail = @"
INSERT dbo.ValidationResults (RunId, Category, CheckName, Status, Details)
VALUES ('$RunId', N'$ExpectedCategory', N'ScriptFailed', N'FAIL',
        N'$($Script.Name) raised an unhandled error: $msg');
"@
        try {
            Invoke-Sqlcmd @SqlcmdSplat -Query $insertFail | Out-Null
        }
        catch {
            Write-Warning "Could not record ScriptFailed row: $_"
        }
    }

    # Defensive smoke check: verify rows landed under the orchestrator's RunId.
    # Skipped if the script is expected to not insert anything (e.g. Sample with no FieldMappings).
    try {
        $check = Invoke-Sqlcmd @SqlcmdSplat -Query @"
SELECT COUNT(*) AS Cnt
FROM   dbo.ValidationResults
WHERE  RunId = '$RunId' AND Category = N'$ExpectedCategory';
"@
        if ($check.Cnt -eq 0) {
            $msg2 = "Script $($Script.Name) completed but inserted 0 rows under RunId $RunId. " +
                    "If this is unexpected, check that Invoke-Sqlcmd -Variable precedence is working " +
                    "(should override in-script :setvar defaults per official sqlcmd docs)."
            Write-Warning $msg2
            $msg2Escaped = $msg2.Replace("'", "''")
            $smokeFail = @"
INSERT dbo.ValidationResults (RunId, Category, CheckName, Status, Details)
VALUES ('$RunId', N'$ExpectedCategory', N'SmokeCheck', N'WARN', N'$msg2Escaped');
"@
            try { Invoke-Sqlcmd @SqlcmdSplat -Query $smokeFail | Out-Null } catch { }
        }
    }
    catch {
        Write-Warning "Smoke check failed for $($Script.Name): $_"
    }
}

function ConvertTo-MdCell {
    param([object] $Value)
    if ($null -eq $Value) { return '' }
    return ($Value.ToString() -replace '\|', '\|' -replace "`r?`n", '<br>')
}

function ConvertTo-HtmlCell {
    param([object] $Value)
    if ($null -eq $Value) { return '' }
    return [System.Net.WebUtility]::HtmlEncode($Value.ToString())
}

function Get-StatusClass {
    param([string] $Status)
    switch ($Status) {
        'PASS' { 'pass' }
        'WARN' { 'warn' }
        'FAIL' { 'fail' }
        default { 'info' }
    }
}

function Build-MarkdownStatusTable {
    param($Rows)
    if (-not $Rows -or $Rows.Count -eq 0) {
        return "_No rows in this section._"
    }
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine('| Category | Schema | Table | Object | Check | Expected | Actual | Details |')
    [void]$sb.AppendLine('|---|---|---|---|---|---|---|---|')
    foreach ($r in $Rows) {
        $line = '| ' + (@(
            (ConvertTo-MdCell $r.Category),
            (ConvertTo-MdCell $r.SchemaName),
            (ConvertTo-MdCell $r.TableName),
            (ConvertTo-MdCell $r.ObjectName),
            (ConvertTo-MdCell $r.CheckName),
            (ConvertTo-MdCell $r.ExpectedValue),
            (ConvertTo-MdCell $r.ActualValue),
            (ConvertTo-MdCell $r.Details)
        ) -join ' | ') + ' |'
        [void]$sb.AppendLine($line)
    }
    return $sb.ToString().TrimEnd()
}

function Build-HtmlStatusTable {
    param($Rows)
    if (-not $Rows -or $Rows.Count -eq 0) {
        return '      <tr><td colspan="8"><em>No rows in this section.</em></td></tr>'
    }
    $sb = New-Object System.Text.StringBuilder
    foreach ($r in $Rows) {
        [void]$sb.Append('      <tr>')
        foreach ($prop in 'Category','SchemaName','TableName','ObjectName','CheckName','ExpectedValue','ActualValue','Details') {
            [void]$sb.Append('<td>')
            [void]$sb.Append((ConvertTo-HtmlCell $r.$prop))
            [void]$sb.Append('</td>')
        }
        [void]$sb.AppendLine('</tr>')
    }
    return $sb.ToString().TrimEnd()
}

function Build-MarkdownCategorySummary {
    param($AllRows)
    if (-not $AllRows -or $AllRows.Count -eq 0) {
        return "_No results captured._"
    }
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine('| Category | PASS | WARN | FAIL | INFO | Total |')
    [void]$sb.AppendLine('|---|---:|---:|---:|---:|---:|')
    $grouped = $AllRows | Group-Object Category | Sort-Object Name
    foreach ($g in $grouped) {
        $pass = ($g.Group | Where-Object { $_.Status -eq 'PASS' }).Count
        $warn = ($g.Group | Where-Object { $_.Status -eq 'WARN' }).Count
        $fail = ($g.Group | Where-Object { $_.Status -eq 'FAIL' }).Count
        $info = ($g.Group | Where-Object { $_.Status -eq 'INFO' }).Count
        [void]$sb.AppendLine("| $($g.Name) | $pass | $warn | $fail | $info | $($g.Count) |")
    }
    return $sb.ToString().TrimEnd()
}

function Build-HtmlCategorySummary {
    param($AllRows)
    if (-not $AllRows -or $AllRows.Count -eq 0) {
        return '      <tr><td colspan="6"><em>No results captured.</em></td></tr>'
    }
    $sb = New-Object System.Text.StringBuilder
    $grouped = $AllRows | Group-Object Category | Sort-Object Name
    foreach ($g in $grouped) {
        $pass = ($g.Group | Where-Object { $_.Status -eq 'PASS' }).Count
        $warn = ($g.Group | Where-Object { $_.Status -eq 'WARN' }).Count
        $fail = ($g.Group | Where-Object { $_.Status -eq 'FAIL' }).Count
        $info = ($g.Group | Where-Object { $_.Status -eq 'INFO' }).Count
        $cat = ConvertTo-HtmlCell $g.Name
        [void]$sb.AppendLine("      <tr><td>$cat</td><td class=`"num`">$pass</td><td class=`"num`">$warn</td><td class=`"num`">$fail</td><td class=`"num`">$info</td><td class=`"num`">$($g.Count)</td></tr>")
    }
    return $sb.ToString().TrimEnd()
}

function Render-Report {
    param(
        [Parameter(Mandatory)] [string] $TemplatePath,
        [Parameter(Mandatory)] [string] $OutputPath,
        [Parameter(Mandatory)] [hashtable] $Replacements
    )
    $content = Get-Content -Raw -LiteralPath $TemplatePath
    foreach ($key in $Replacements.Keys) {
        $token = '{{' + $key + '}}'
        $content = $content.Replace($token, [string]$Replacements[$key])
    }
    $content | Set-Content -LiteralPath $OutputPath -Encoding UTF8
}

# -------------------------------------------------------------------------
# Main
# -------------------------------------------------------------------------

if (-not (Test-Path -LiteralPath $ScriptsRoot)) {
    throw "ScriptsRoot '$ScriptsRoot' does not exist."
}

$allScripts = Get-ChildItem -LiteralPath $ScriptsRoot -Filter '*.sql' |
              Where-Object { $_.Name -match '^\d{2}-' } |
              Sort-Object Name

if (-not $allScripts -or $allScripts.Count -eq 0) {
    throw "No numbered validation SQL scripts found in '$ScriptsRoot'."
}

# Apply filters.
$scriptsToRun = foreach ($s in $allScripts) {
    $prefix = $s.Name.Substring(0, 3)
    if (-not $script:CategoryMap.ContainsKey($prefix)) { continue }
    $category = $script:CategoryMap[$prefix]
    if ($SkipSampleData -and $category -eq 'Sample') { continue }
    if ($OnlySections -and $OnlySections -notcontains $category) { continue }
    [pscustomobject]@{ File = $s; Category = $category }
}

if (-not $scriptsToRun -or $scriptsToRun.Count -eq 0) {
    Write-Warning 'No scripts selected after applying filters; nothing to do.'
    return
}

$runId = [Guid]::NewGuid().ToString()

Write-Host ''
Write-Host '============================================================' -ForegroundColor White
Write-Host 'Post-Migration Validation Run'                                -ForegroundColor White
Write-Host '============================================================' -ForegroundColor White
Write-Host "  RunId   : $runId"
Write-Host "  Server  : $ServerInstance"
Write-Host "  Database: $Database"
Write-Host "  Scripts : $($scriptsToRun.Count) selected ($($scriptsToRun.Category -join ', '))"
Write-Host ''

# -WhatIf: print planned commands and exit without touching the DB or even
# loading the SqlServer module (so dry-runs work in any environment).
if ($WhatIfPreference -or -not $PSCmdlet.ShouldProcess(
        "$Database on $ServerInstance",
        "Run $($scriptsToRun.Count) validation script(s) under RunId $runId")) {
    foreach ($s in $scriptsToRun) {
        Write-Host "  [WhatIf] Invoke-Sqlcmd -InputFile $($s.File.FullName) -Variable @(RunId=$runId, ...)" -ForegroundColor Yellow
    }
    Write-Host '[WhatIf] Report generation would render Markdown + HTML from dbo.ValidationResults.' -ForegroundColor Yellow
    return
}

# Loading the SqlServer module and building the sqlcmd splat are only needed
# for an actual run (not -WhatIf).
Assert-SqlServerModule
$sqlcmdSplat = Get-SqlcmdSplat

# Optional reset.
if ($Reset) {
    Write-Host '  > Resetting dbo.ValidationResults ...' -ForegroundColor Yellow
    Invoke-Sqlcmd @sqlcmdSplat -Query 'IF OBJECT_ID(N''dbo.ValidationResults'', N''U'') IS NOT NULL TRUNCATE TABLE dbo.ValidationResults;'
}

# Execute scripts in order.
foreach ($s in $scriptsToRun) {
    Invoke-ValidationScript -Script $s.File -RunId $runId `
                            -ExpectedCategory $s.Category `
                            -SqlcmdSplat $sqlcmdSplat
}

# -------------------------------------------------------------------------
# Fetch results + render reports.
# -------------------------------------------------------------------------

Write-Host ''
Write-Host '  > Fetching results ...' -ForegroundColor Cyan
$results = Invoke-Sqlcmd @sqlcmdSplat -Query @"
SELECT RunId, Category, SchemaName, TableName, ObjectName, CheckName,
       ExpectedValue, ActualValue, Status, Details, CapturedAt
FROM   dbo.ValidationResults
WHERE  RunId = '$runId'
ORDER  BY Category, CheckName,
          CASE Status WHEN 'FAIL' THEN 1 WHEN 'WARN' THEN 2 WHEN 'PASS' THEN 3 ELSE 4 END;
"@

if (-not $results) { $results = @() }

$resultsArray = @($results)
$pass = ($resultsArray | Where-Object { $_.Status -eq 'PASS' }).Count
$warn = ($resultsArray | Where-Object { $_.Status -eq 'WARN' }).Count
$fail = ($resultsArray | Where-Object { $_.Status -eq 'FAIL' }).Count
$info = ($resultsArray | Where-Object { $_.Status -eq 'INFO' }).Count
$total = $resultsArray.Count

$overall = if ($fail -gt 0) { 'FAIL' } elseif ($warn -gt 0) { 'WARN' } else { 'PASS' }
$overallClass = (Get-StatusClass $overall)

# Subsets for the section tables.
$failRows  = $resultsArray | Where-Object { $_.Status -eq 'FAIL' }
$warnRows  = $resultsArray | Where-Object { $_.Status -eq 'WARN' }
$infoRows  = $resultsArray | Where-Object { $_.Status -eq 'INFO' }

if (-not (Test-Path -LiteralPath $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir | Out-Null
}

$generatedUtc = (Get-Date).ToUniversalTime().ToString('o')

$mdReplacements = @{
    RunId                 = $runId
    Generated             = $generatedUtc
    Server                = $ServerInstance
    Database              = $Database
    OverallStatus         = $overall
    OverallStatusClass    = $overallClass
    TotalChecks           = $total
    PassCount             = $pass
    WarnCount             = $warn
    FailCount             = $fail
    InfoCount             = $info
    CategorySummaryTable  = (Build-MarkdownCategorySummary -AllRows $resultsArray)
    FailuresTable         = (Build-MarkdownStatusTable -Rows $failRows)
    WarningsTable         = (Build-MarkdownStatusTable -Rows $warnRows)
    InfoTable             = (Build-MarkdownStatusTable -Rows $infoRows)
}

$htmlReplacements = @{
    RunId                 = [System.Net.WebUtility]::HtmlEncode($runId)
    Generated             = [System.Net.WebUtility]::HtmlEncode($generatedUtc)
    Server                = [System.Net.WebUtility]::HtmlEncode($ServerInstance)
    Database              = [System.Net.WebUtility]::HtmlEncode($Database)
    OverallStatus         = $overall
    OverallStatusClass    = $overallClass
    TotalChecks           = $total
    PassCount             = $pass
    WarnCount             = $warn
    FailCount             = $fail
    InfoCount             = $info
    CategorySummaryTable  = (Build-HtmlCategorySummary -AllRows $resultsArray)
    FailuresTable         = (Build-HtmlStatusTable -Rows $failRows)
    WarningsTable         = (Build-HtmlStatusTable -Rows $warnRows)
    InfoTable             = (Build-HtmlStatusTable -Rows $infoRows)
}

$mdTemplate   = Join-Path $ScriptsRoot 'ValidationReport.md.template'
$htmlTemplate = Join-Path $ScriptsRoot 'ValidationReport.html.template'
$mdOut        = Join-Path $OutputDir   "ValidationReport-$runId.md"
$htmlOut      = Join-Path $OutputDir   "ValidationReport-$runId.html"

if (Test-Path -LiteralPath $mdTemplate) {
    Render-Report -TemplatePath $mdTemplate   -OutputPath $mdOut   -Replacements $mdReplacements
    Write-Host "  > Wrote $mdOut" -ForegroundColor Green
} else {
    Write-Warning "Markdown template not found at $mdTemplate; skipping Markdown report."
}
if (Test-Path -LiteralPath $htmlTemplate) {
    Render-Report -TemplatePath $htmlTemplate -OutputPath $htmlOut -Replacements $htmlReplacements
    Write-Host "  > Wrote $htmlOut" -ForegroundColor Green
} else {
    Write-Warning "HTML template not found at $htmlTemplate; skipping HTML report."
}

# -------------------------------------------------------------------------
# Console summary.
# -------------------------------------------------------------------------
Write-Host ''
Write-Host '============================================================' -ForegroundColor White
$overallColor = switch ($overall) {
    'PASS'  { 'Green' }
    'WARN'  { 'Yellow' }
    'FAIL'  { 'Red' }
    default { 'Cyan' }
}
Write-Host "Validation summary  ($overall)" -ForegroundColor $overallColor
Write-Host '============================================================' -ForegroundColor White
Write-Host ("  PASS: {0,5}    WARN: {1,5}    FAIL: {2,5}    INFO: {3,5}    Total: {4,5}" -f $pass, $warn, $fail, $info, $total)
Write-Host ''

# -------------------------------------------------------------------------
# Return / exit.
# -------------------------------------------------------------------------
$summary = [pscustomobject]@{
    RunId        = $runId
    Overall      = $overall
    Pass         = $pass
    Warn         = $warn
    Fail         = $fail
    Info         = $info
    Total        = $total
    MarkdownPath = $mdOut
    HtmlPath     = $htmlOut
}

$isDotSourced = ($MyInvocation.InvocationName -eq '.')
if ($isDotSourced) {
    return $summary
}

if ($fail -gt 0) {
    exit 1
} else {
    exit 0
}
