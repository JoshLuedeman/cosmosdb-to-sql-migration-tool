<#
.SYNOPSIS
    Runs all post-migration validation scripts and generates a summary report.

.DESCRIPTION
    This script orchestrates the execution of all post-migration validation SQL scripts
    against a target SQL database. It captures the output from each script and generates
    a consolidated validation report.

    Validation scripts executed:
        1. Row Count Validation (01-RowCountValidation.sql)
        2. Data Integrity Checks (02-DataIntegrityChecks.sql)
        3. Sample Data Comparison (03-SampleDataComparison.sql)
        4. Performance Baseline (04-PerformanceBaseline.sql)

.PARAMETER ServerName
    SQL Server instance name (default: localhost)

.PARAMETER DatabaseName
    Target database name (required)

.PARAMETER OutputPath
    Directory for validation report output (default: ./ValidationResults)

.PARAMETER UseWindowsAuth
    Use Windows Authentication instead of SQL Authentication

.PARAMETER UserName
    SQL Authentication username (required if not using Windows Auth)

.PARAMETER Password
    SQL Authentication password (required if not using Windows Auth)

.PARAMETER ScriptsPath
    Path to the SQL validation scripts (default: script's directory)

.PARAMETER SkipScripts
    Array of script numbers to skip (e.g., @(3,4) to skip sample data and performance)

.EXAMPLE
    .\RunAllValidations.ps1 -DatabaseName "MyMigratedDB" -UseWindowsAuth

.EXAMPLE
    .\RunAllValidations.ps1 -ServerName "myserver.database.windows.net" -DatabaseName "MyDB" -UserName "admin" -Password "pass"

.EXAMPLE
    .\RunAllValidations.ps1 -DatabaseName "MyDB" -UseWindowsAuth -SkipScripts @(4)
#>

[CmdletBinding()]
param(
    [string]$ServerName = "localhost",
    
    [Parameter(Mandatory = $true)]
    [string]$DatabaseName,
    
    [string]$OutputPath = "./ValidationResults",
    
    [switch]$UseWindowsAuth,
    
    [string]$UserName,
    
    [string]$Password,
    
    [string]$ScriptsPath,
    
    [int[]]$SkipScripts = @()
)

$ErrorActionPreference = "Continue"

# ============================================================================
# Configuration
# ============================================================================
if (-not $ScriptsPath) {
    $ScriptsPath = $PSScriptRoot
}

$scripts = @(
    @{ Number = 1; Name = "Row Count Validation";    File = "01-RowCountValidation.sql" },
    @{ Number = 2; Name = "Data Integrity Checks";   File = "02-DataIntegrityChecks.sql" },
    @{ Number = 3; Name = "Sample Data Comparison";   File = "03-SampleDataComparison.sql" },
    @{ Number = 4; Name = "Performance Baseline";     File = "04-PerformanceBaseline.sql" }
)

# ============================================================================
# Helper Functions
# ============================================================================
function Write-Banner {
    param([string]$Message)
    $line = "=" * 70
    Write-Host ""
    Write-Host $line -ForegroundColor Cyan
    Write-Host " $Message" -ForegroundColor Cyan
    Write-Host $line -ForegroundColor Cyan
    Write-Host ""
}

function Write-Status {
    param(
        [string]$Message,
        [ValidateSet("Info", "Success", "Warning", "Error")]
        [string]$Level = "Info"
    )
    $colors = @{
        Info    = "White"
        Success = "Green"
        Warning = "Yellow"
        Error   = "Red"
    }
    $icons = @{
        Info    = "[i]"
        Success = "[+]"
        Warning = "[!]"
        Error   = "[X]"
    }
    Write-Host "$($icons[$Level]) $Message" -ForegroundColor $colors[$Level]
}

function Test-SqlCmdAvailable {
    try {
        $null = Get-Command "sqlcmd" -ErrorAction Stop
        return $true
    }
    catch {
        # Try Invoke-Sqlcmd (SqlServer module)
        try {
            $null = Get-Command "Invoke-Sqlcmd" -ErrorAction Stop
            return $true
        }
        catch {
            return $false
        }
    }
}

function Invoke-SqlScript {
    param(
        [string]$ScriptPath,
        [string]$Server,
        [string]$Database,
        [switch]$WindowsAuth,
        [string]$User,
        [string]$Pass
    )

    if (Get-Command "Invoke-Sqlcmd" -ErrorAction SilentlyContinue) {
        # Use PowerShell SqlServer module
        $params = @{
            InputFile    = $ScriptPath
            ServerInstance = $Server
            Database     = $Database
            QueryTimeout = 300
            ErrorAction  = "Continue"
        }
        if (-not $WindowsAuth) {
            $secPass = ConvertTo-SecureString $Pass -AsPlainText -Force
            $cred = New-Object System.Management.Automation.PSCredential($User, $secPass)
            $params["Credential"] = $cred
        }
        $result = Invoke-Sqlcmd @params -Verbose 4>&1
        return $result
    }
    else {
        # Fall back to sqlcmd
        $args = @("-S", $Server, "-d", $Database, "-i", $ScriptPath, "-s", "|", "-W")
        if ($WindowsAuth) {
            $args += "-E"
        }
        else {
            $args += @("-U", $User, "-P", $Pass)
        }
        $result = & sqlcmd @args 2>&1
        return $result
    }
}

# ============================================================================
# Main Execution
# ============================================================================
$startTime = (Get-Date).ToUniversalTime()

Write-Banner "Post-Migration Validation Runner"
Write-Status "Server:     $ServerName"
Write-Status "Database:   $DatabaseName"
Write-Status "Auth:       $(if ($UseWindowsAuth) { 'Windows' } else { 'SQL' })"
Write-Status "Scripts:    $ScriptsPath"
Write-Status "Output:     $OutputPath"
Write-Status "Start Time: $($startTime.ToString('yyyy-MM-dd HH:mm:ss'))"

# Validate prerequisites
if (-not (Test-SqlCmdAvailable)) {
    Write-Status "Neither 'sqlcmd' nor 'Invoke-Sqlcmd' found. Install SQL Server tools or the SqlServer PowerShell module." -Level Error
    Write-Status "  Install SqlServer module: Install-Module -Name SqlServer" -Level Info
    Write-Status "  Install sqlcmd: https://learn.microsoft.com/en-us/sql/tools/sqlcmd/sqlcmd-utility" -Level Info
    exit 1
}

if (-not $UseWindowsAuth -and (-not $UserName -or -not $Password)) {
    Write-Status "SQL Authentication requires -UserName and -Password parameters." -Level Error
    exit 1
}

# Create output directory
if (-not (Test-Path $OutputPath)) {
    New-Item -ItemType Directory -Path $OutputPath -Force | Out-Null
    Write-Status "Created output directory: $OutputPath"
}

$timestamp = $startTime.ToString("yyyyMMdd_HHmmss")
$reportFile = Join-Path $OutputPath "ValidationReport_${timestamp}.txt"
$summaryResults = @()

# Initialize report
$reportHeader = @"
================================================================
 Post-Migration Validation Report
 Database:   $DatabaseName
 Server:     $ServerName
 Run Date:   $($startTime.ToString('yyyy-MM-dd HH:mm:ss UTC'))
================================================================

"@
$reportHeader | Out-File -FilePath $reportFile -Encoding UTF8

# Execute each validation script
foreach ($script in $scripts) {
    if ($SkipScripts -contains $script.Number) {
        Write-Status "Skipping: $($script.Name)" -Level Warning
        $summaryResults += @{
            Number = $script.Number
            Name   = $script.Name
            Status = "SKIPPED"
            Duration = "N/A"
        }
        continue
    }

    $scriptFile = Join-Path $ScriptsPath $script.File
    
    if (-not (Test-Path $scriptFile)) {
        Write-Status "Script not found: $scriptFile" -Level Error
        $summaryResults += @{
            Number = $script.Number
            Name   = $script.Name
            Status = "NOT FOUND"
            Duration = "N/A"
        }
        continue
    }

    Write-Banner "Running: $($script.Name)"
    
    $scriptStart = (Get-Date).ToUniversalTime()
    $scriptOutputFile = Join-Path $OutputPath "$($script.File -replace '\.sql$', '')_${timestamp}.txt"
    
    try {
        $output = Invoke-SqlScript `
            -ScriptPath $scriptFile `
            -Server $ServerName `
            -Database $DatabaseName `
            -WindowsAuth:$UseWindowsAuth `
            -User $UserName `
            -Pass $Password
        
        $scriptEnd = (Get-Date).ToUniversalTime()
        $duration = $scriptEnd - $scriptStart
        
        # Save individual script output
        $output | Out-File -FilePath $scriptOutputFile -Encoding UTF8
        
        # Append to consolidated report
        "`n$('=' * 70)" | Out-File -FilePath $reportFile -Append -Encoding UTF8
        " $($script.Name)" | Out-File -FilePath $reportFile -Append -Encoding UTF8
        " Duration: $($duration.ToString('mm\:ss\.fff'))" | Out-File -FilePath $reportFile -Append -Encoding UTF8
        "$('=' * 70)`n" | Out-File -FilePath $reportFile -Append -Encoding UTF8
        $output | Out-File -FilePath $reportFile -Append -Encoding UTF8
        
        # Check for FAIL in output
        $outputText = $output | Out-String
        $status = if ($outputText -match "OVERALL RESULT: FAIL") {
            "FAIL"
        }
        elseif ($outputText -match "OVERALL RESULT: PASS WITH WARNINGS") {
            "WARNING"
        }
        else {
            "PASS"
        }
        
        $summaryResults += @{
            Number   = $script.Number
            Name     = $script.Name
            Status   = $status
            Duration = $duration.ToString('mm\:ss\.fff')
        }
        
        Write-Status "Completed: $($script.Name) [$status] ($($duration.ToString('mm\:ss\.fff')))" -Level $(
            switch ($status) {
                "PASS"    { "Success" }
                "WARNING" { "Warning" }
                "FAIL"    { "Error" }
                default   { "Info" }
            }
        )
        Write-Status "Output saved: $scriptOutputFile"
    }
    catch {
        $scriptEnd = (Get-Date).ToUniversalTime()
        $duration = $scriptEnd - $scriptStart
        
        Write-Status "Error running $($script.Name): $_" -Level Error
        
        $summaryResults += @{
            Number   = $script.Number
            Name     = $script.Name
            Status   = "ERROR"
            Duration = $duration.ToString('mm\:ss\.fff')
        }
        
        # Log error to report
        "`n$('=' * 70)" | Out-File -FilePath $reportFile -Append -Encoding UTF8
        " $($script.Name) - ERROR" | Out-File -FilePath $reportFile -Append -Encoding UTF8
        "$('=' * 70)`n" | Out-File -FilePath $reportFile -Append -Encoding UTF8
        "Error: $_" | Out-File -FilePath $reportFile -Append -Encoding UTF8
    }
}

# ============================================================================
# Summary Report
# ============================================================================
$endTime = (Get-Date).ToUniversalTime()
$totalDuration = $endTime - $startTime

Write-Banner "Validation Summary"

# Display summary table
$summaryTable = $summaryResults | ForEach-Object {
    [PSCustomObject]@{
        '#'       = $_.Number
        Script    = $_.Name
        Status    = $_.Status
        Duration  = $_.Duration
    }
}
$summaryTable | Format-Table -AutoSize

# Write summary to report
"`n$('=' * 70)" | Out-File -FilePath $reportFile -Append -Encoding UTF8
" VALIDATION SUMMARY" | Out-File -FilePath $reportFile -Append -Encoding UTF8
"$('=' * 70)`n" | Out-File -FilePath $reportFile -Append -Encoding UTF8

foreach ($r in $summaryResults) {
    "  [$($r.Status.PadRight(8))] $($r.Name) ($($r.Duration))" | Out-File -FilePath $reportFile -Append -Encoding UTF8
}

"`nTotal Duration: $($totalDuration.ToString('mm\:ss\.fff'))" | Out-File -FilePath $reportFile -Append -Encoding UTF8

# Overall result
$failCount = ($summaryResults | Where-Object { $_.Status -eq "FAIL" -or $_.Status -eq "ERROR" }).Count
$warnCount = ($summaryResults | Where-Object { $_.Status -eq "WARNING" }).Count

$overallStatus = if ($failCount -gt 0) {
    "FAIL"
}
elseif ($warnCount -gt 0) {
    "PASS WITH WARNINGS"
}
else {
    "PASS"
}

"`nOVERALL RESULT: $overallStatus" | Out-File -FilePath $reportFile -Append -Encoding UTF8
"$('=' * 70)" | Out-File -FilePath $reportFile -Append -Encoding UTF8

# Console output
Write-Host ""
$statusLevel = switch ($overallStatus) {
    "PASS"               { "Success" }
    "PASS WITH WARNINGS" { "Warning" }
    "FAIL"               { "Error" }
}
Write-Status "Overall Result: $overallStatus" -Level $statusLevel
Write-Status "Total Duration: $($totalDuration.ToString('mm\:ss\.fff'))"
Write-Status "Report saved:   $reportFile" -Level Success
Write-Host ""

# Return exit code based on results
if ($failCount -gt 0) {
    exit 1
}
exit 0
