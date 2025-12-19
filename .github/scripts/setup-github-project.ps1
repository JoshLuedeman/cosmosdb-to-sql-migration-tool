<#
.SYNOPSIS
    Sets up GitHub repository structure with issues, milestones, labels, and projects.

.DESCRIPTION
    This script creates a complete GitHub project structure including:
    - Labels for categorization
    - Milestones for release planning
    - Issues with proper organization
    - Project boards for tracking
    
    Requires GitHub CLI (gh) to be installed and authenticated.

.PARAMETER Owner
    Repository owner (default: JoshLuedeman)

.PARAMETER Repo
    Repository name (default: cosmosdb-to-sql-migration-tool)

.PARAMETER DryRun
    If specified, shows what would be created without actually creating anything

.EXAMPLE
    .\setup-github-project.ps1
    
.EXAMPLE
    .\setup-github-project.ps1 -DryRun
#>

[CmdletBinding()]
param(
    [string]$Owner = "JoshLuedeman",
    [string]$Repo = "cosmosdb-to-sql-migration-tool",
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"
$RepoPath = "$Owner/$Repo"
$ScriptDir = $PSScriptRoot

# Check if GitHub CLI is installed
function Test-GitHubCLI {
    try {
        $null = gh --version
        return $true
    }
    catch {
        Write-Error "GitHub CLI (gh) is not installed. Please install from https://cli.github.com/"
        return $false
    }
}

# Check if authenticated
function Test-GitHubAuth {
    try {
        $null = gh auth status 2>&1
        return $true
    }
    catch {
        Write-Error "Not authenticated with GitHub. Run 'gh auth login' first."
        return $false
    }
}

# Load configuration files
function Get-Configuration {
    param([string]$ConfigFile)
    
    $configPath = Join-Path $ScriptDir $ConfigFile
    if (-not (Test-Path $configPath)) {
        Write-Error "Configuration file not found: $configPath"
        return $null
    }
    
    return Get-Content $configPath -Raw | ConvertFrom-Json
}

# Create labels
function New-GitHubLabels {
    param([array]$Labels)
    
    Write-Host "`n📋 Creating Labels..." -ForegroundColor Cyan
    
    foreach ($label in $Labels) {
        $labelName = $label.name
        $color = $label.color.TrimStart('#')
        $description = $label.description
        
        if ($DryRun) {
            Write-Host "  [DRY RUN] Would create label: $labelName" -ForegroundColor Yellow
            continue
        }
        
        try {
            # Check if label exists
            $existing = gh label list --repo $RepoPath --json name | ConvertFrom-Json | Where-Object { $_.name -eq $labelName }
            
            if ($existing) {
                Write-Host "  ⚠️  Updating existing label: $labelName" -ForegroundColor Yellow
                gh label edit $labelName --repo $RepoPath --color $color --description $description 2>&1 | Out-Null
            }
            else {
                Write-Host "  ✅ Created label: $labelName" -ForegroundColor Green
                gh label create $labelName --repo $RepoPath --color $color --description $description 2>&1 | Out-Null
            }
        }
        catch {
            Write-Host "  ❌ Failed to create label: $labelName - $_" -ForegroundColor Red
        }
    }
}

# Create milestones
function New-GitHubMilestones {
    param([array]$Milestones)
    
    Write-Host "`n🎯 Creating Milestones..." -ForegroundColor Cyan
    
    foreach ($milestone in $Milestones) {
        $title = $milestone.title
        $description = $milestone.description
        $dueDate = $milestone.due_date
        
        if ($DryRun) {
            Write-Host "  [DRY RUN] Would create milestone: $title" -ForegroundColor Yellow
            continue
        }
        
        try {
            # Check if milestone exists
            $existing = gh api "repos/$RepoPath/milestones" --jq ".[] | select(.title == `"$title`")" 2>$null
            
            if ($existing) {
                Write-Host "  ⚠️  Milestone already exists: $title" -ForegroundColor Yellow
            }
            else {
                $body = @{
                    title       = $title
                    description = $description
                    due_on      = $dueDate
                } | ConvertTo-Json
                
                gh api "repos/$RepoPath/milestones" -X POST --input - <<< $body 2>&1 | Out-Null
                Write-Host "  ✅ Created milestone: $title" -ForegroundColor Green
            }
        }
        catch {
            Write-Host "  ❌ Failed to create milestone: $title - $_" -ForegroundColor Red
        }
    }
}

# Create issues
function New-GitHubIssues {
    param([array]$Issues)
    
    Write-Host "`n📝 Creating Issues..." -ForegroundColor Cyan
    
    foreach ($issue in $Issues) {
        $title = $issue.title
        $body = $issue.body
        $labels = $issue.labels -join ","
        $milestone = $issue.milestone
        
        if ($DryRun) {
            Write-Host "  [DRY RUN] Would create issue: $title" -ForegroundColor Yellow
            continue
        }
        
        try {
            $cmd = "gh issue create --repo $RepoPath --title `"$title`" --body `"$body`""
            
            if ($labels) {
                $cmd += " --label `"$labels`""
            }
            
            if ($milestone) {
                $cmd += " --milestone `"$milestone`""
            }
            
            $issueUrl = Invoke-Expression $cmd
            Write-Host "  ✅ Created issue: $title" -ForegroundColor Green
            Write-Host "     $issueUrl" -ForegroundColor Gray
        }
        catch {
            Write-Host "  ❌ Failed to create issue: $title - $_" -ForegroundColor Red
        }
    }
}

# Create project board
function New-GitHubProject {
    param([object]$ProjectConfig)
    
    Write-Host "`n📊 Creating Project Board..." -ForegroundColor Cyan
    
    $title = $ProjectConfig.title
    $description = $ProjectConfig.description
    
    if ($DryRun) {
        Write-Host "  [DRY RUN] Would create project: $title" -ForegroundColor Yellow
        return
    }
    
    try {
        # Note: Projects v2 require different API calls
        Write-Host "  ℹ️  Project creation requires manual setup through GitHub UI" -ForegroundColor Yellow
        Write-Host "     Project name: $title" -ForegroundColor Gray
        Write-Host "     Description: $description" -ForegroundColor Gray
    }
    catch {
        Write-Host "  ❌ Failed to create project: $title - $_" -ForegroundColor Red
    }
}

# Main execution
function Main {
    Write-Host "🚀 GitHub Project Setup for $RepoPath" -ForegroundColor Cyan
    Write-Host "=" * 60
    
    # Validate prerequisites
    if (-not (Test-GitHubCLI)) { return }
    if (-not (Test-GitHubAuth)) { return }
    
    if ($DryRun) {
        Write-Host "`n⚠️  DRY RUN MODE - No changes will be made`n" -ForegroundColor Yellow
    }
    
    # Load configurations
    $labels = Get-Configuration "labels.json"
    $milestones = Get-Configuration "milestones.json"
    $issues = Get-Configuration "issues.json"
    $project = Get-Configuration "project.json"
    
    if (-not $labels -or -not $milestones -or -not $issues) {
        Write-Error "Failed to load configuration files"
        return
    }
    
    # Create resources
    New-GitHubLabels -Labels $labels
    New-GitHubMilestones -Milestones $milestones
    New-GitHubIssues -Issues $issues
    
    if ($project) {
        New-GitHubProject -ProjectConfig $project
    }
    
    Write-Host "`n✅ Setup complete!" -ForegroundColor Green
    Write-Host "`nNext steps:" -ForegroundColor Cyan
    Write-Host "  1. Visit https://github.com/$RepoPath/issues to see created issues"
    Write-Host "  2. Create project boards at https://github.com/$RepoPath/projects"
    Write-Host "  3. Configure automation rules in project settings"
    Write-Host "  4. Start working on Sprint 1 issues!`n"
}

# Run main function
Main
