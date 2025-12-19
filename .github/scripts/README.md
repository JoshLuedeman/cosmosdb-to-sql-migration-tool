# GitHub Project Setup Scripts

This directory contains scripts and configuration to set up the complete GitHub project structure including labels, milestones, issues, and project boards.

## 📋 Prerequisites

1. **GitHub CLI** - Install from https://cli.github.com/
   ```powershell
   winget install --id GitHub.cli
   ```

2. **Authentication** - Login to GitHub
   ```powershell
   gh auth login
   ```

3. **Repository Access** - Ensure you have write access to the repository

## 🚀 Quick Start

### Option 1: Full Setup (Recommended)

Run the main setup script to create everything:

```powershell
cd .github/scripts
.\setup-github-project.ps1
```

### Option 2: Dry Run First

Preview what will be created without making changes:

```powershell
.\setup-github-project.ps1 -DryRun
```

### Option 3: Custom Repository

Run against a different repository:

```powershell
.\setup-github-project.ps1 -Owner "YourUsername" -Repo "your-repo"
```

## 📁 Configuration Files

### `labels.json`
Defines all labels with colors and descriptions:
- **Priority labels**: critical, high, medium, low
- **Type labels**: bug, enhancement, documentation, technical-debt, testing
- **Area labels**: cosmos-analysis, sql-assessment, reporting, etc.
- **Status labels**: needs-design, ready-to-code, in-progress, blocked, needs-review
- **Size labels**: XS, S, M, L, XL (story points)

### `milestones.json`
Defines release milestones:
- **v1.2.0 - Core Improvements** (March 2026)
- **v1.3.0 - Enhanced Migration Features** (June 2026)
- **v2.0.0 - Advanced Features** (September 2026)
- **Documentation & Quality** (Ongoing)

### `issues.json`
Defines 14 issues across all priorities:
- 5 HIGH priority issues (v1.2.0)
- 6 MEDIUM priority issues (v1.3.0)
- 3 Documentation/Quality issues

### `project.json`
Project board configuration (requires manual setup through GitHub UI)

## 🔧 What Gets Created

### Labels (27 total)
```
✅ Priority Labels (4)
   🔴 priority: critical
   🟠 priority: high
   🟡 priority: medium
   🟢 priority: low

✅ Type Labels (5)
   🐛 type: bug
   ✨ type: enhancement
   📚 type: documentation
   🧹 type: technical-debt
   🧪 type: testing

✅ Area Labels (7)
   area: cosmos-analysis
   area: sql-assessment
   area: reporting
   area: sql-project
   area: data-factory
   area: cli
   area: authentication
   area: cicd

✅ Status Labels (5)
   status: needs-design
   status: ready-to-code
   status: in-progress
   status: blocked
   status: needs-review

✅ Size Labels (5)
   size: XS (1-2 hours)
   size: S (2-4 hours)
   size: M (1-2 days)
   size: L (3-5 days)
   size: XL (1-2 weeks)
```

### Milestones (4 total)
```
✅ v1.2.0 - Core Improvements (March 2026)
✅ v1.3.0 - Enhanced Migration Features (June 2026)
✅ v2.0.0 - Advanced Features (September 2026)
✅ Documentation & Quality (Ongoing)
```

### Issues (14 total)
```
v1.2.0 (5 issues):
  #1 - Implement SQL transformation logic (L)
  #2 - Detect NestedObject vs Array (M)
  #3 - Add EstimatedRowCount to model (M)
  #4 - Add cancellation token support (M)
  #5 - Implement --test-connection (S)

v1.3.0 (6 issues):
  #6 - Pre-migration data quality checks (L)
  #7 - Post-migration validation scripts (L)
  #8 - Incremental migration support (XL)
  #9 - Azure Monitor auto-discovery (M)
  #10 - Generate ADF pipeline JSON (L)
  #11 - Add interactive wizard mode (M)

Documentation & Quality (3 issues):
  #12 - Create API reference docs (M)
  #13 - Security scanning in CI/CD (S)
  #14 - Performance benchmarking (M)
```

## 📊 Manual Steps After Running Script

### 1. Create Project Board

The script cannot create Projects v2 automatically. Follow these steps:

1. Go to https://github.com/JoshLuedeman/cosmosdb-to-sql-migration-tool/projects
2. Click **"New project"**
3. Select **"Table"** view
4. Name it **"Product Roadmap"**
5. Add custom fields:
   - **Priority** (dropdown): Critical, High, Medium, Low
   - **Sprint** (number)
   - **Size** (dropdown): XS, S, M, L, XL
   - **Area** (dropdown): All area values
6. Configure workflow automation:
   - Auto-add new issues
   - Move to "In Progress" when assigned
   - Move to "Done" when closed

### 2. Add Issues to Project

1. Go to the project board
2. Click **"+ Add item"**
3. Search for created issues
4. Add all 14 issues
5. Set custom field values based on issue labels

### 3. Configure Automation Rules

Set up GitHub Actions workflows for:
- Auto-labeling based on content
- Auto-assigning to milestones
- Linking related issues
- Moving cards between columns

### 4. Set Up Branch Protection Rules

Recommended settings for `main`:
- ✅ Require pull request reviews (1 approver)
- ✅ Require status checks to pass
- ✅ Require conversation resolution
- ✅ Require signed commits
- ✅ Include administrators

## 🎯 Sprint Planning

### Sprint 1 (Weeks 1-2)
Focus: Critical implementations
- Issue #1: SQL transformations (5 days)
- Issue #2: Child table detection (2 days)
- Issue #5: Test connection (4 hours)

### Sprint 2 (Weeks 3-4)
Focus: Data quality
- Issue #3: Row count estimation (2 days)
- Issue #4: Cancellation tokens (2 days)
- Issue #6: Data quality checks (5 days)

### Sprint 3 (Weeks 5-7)
Focus: Migration features
- Issue #7: Validation scripts (5 days)
- Issue #8: Incremental migration (10 days)
- Issue #9: Auto-discovery (2 days)

### Sprint 4 (Weeks 8-9)
Focus: Automation & docs
- Issue #10: ADF generation (5 days)
- Issue #11: Interactive mode (2 days)
- Issue #12: API docs (2 days)

## 🔍 Verifying Setup

After running the script, verify:

```powershell
# Check labels
gh label list --repo JoshLuedeman/cosmosdb-to-sql-migration-tool

# Check milestones
gh api repos/JoshLuedeman/cosmosdb-to-sql-migration-tool/milestones

# Check issues
gh issue list --repo JoshLuedeman/cosmosdb-to-sql-migration-tool --limit 20
```

## 🐛 Troubleshooting

### "gh: command not found"
Install GitHub CLI: https://cli.github.com/

### "Not authenticated"
Run: `gh auth login`

### "Resource not accessible by integration"
Ensure you have write access to the repository

### Labels already exist
The script will update existing labels with new colors/descriptions

### Issues already created
Re-running won't create duplicates. Delete manually if needed:
```powershell
gh issue delete <issue-number> --repo JoshLuedeman/cosmosdb-to-sql-migration-tool
```

## 📚 Additional Resources

- [GitHub CLI Manual](https://cli.github.com/manual/)
- [GitHub Projects Documentation](https://docs.github.com/en/issues/planning-and-tracking-with-projects)
- [Issue Automation](https://docs.github.com/en/issues/planning-and-tracking-with-projects/automating-your-project)
- [GitHub Actions for Projects](https://github.com/marketplace?type=actions&query=project)

## 🤝 Contributing

When working on issues:

1. **Assign yourself** to the issue
2. **Create a branch**: `feature/issue-1-sql-transformations`
3. **Update status** labels as you progress
4. **Link PR** to issue using `Closes #1` in PR description
5. **Request review** when ready

## 📞 Support

Questions about the setup?
- Open a discussion: https://github.com/JoshLuedeman/cosmosdb-to-sql-migration-tool/discussions
- Contact maintainers via issue comments

---

**Last Updated**: December 2025  
**Version**: 1.0
