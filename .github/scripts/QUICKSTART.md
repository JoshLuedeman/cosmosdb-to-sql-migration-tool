# Quick Reference: Running the GitHub Setup

## Prerequisites Check
```powershell
# 1. Verify GitHub CLI is installed
gh --version

# 2. Check authentication
gh auth status

# 3. Navigate to scripts directory
cd .github/scripts
```

## Running the Setup

### Recommended: Dry Run First
```powershell
.\setup-github-project.ps1 -DryRun
```

### Full Setup
```powershell
.\setup-github-project.ps1
```

## What You'll See

```
🚀 GitHub Project Setup for JoshLuedeman/cosmosdb-to-sql-migration-tool
============================================================

📋 Creating Labels...
  ✅ Created label: priority: critical
  ✅ Created label: priority: high
  ... (27 labels total)

🎯 Creating Milestones...
  ✅ Created milestone: v1.2.0 - Core Improvements
  ... (4 milestones total)

📝 Creating Issues...
  ✅ Created issue: Implement SQL transformation logic
     https://github.com/JoshLuedeman/cosmosdb-to-sql-migration-tool/issues/1
  ... (14 issues total)

📊 Creating Project Board...
  ℹ️  Project creation requires manual setup through GitHub UI

✅ Setup complete!

Next steps:
  1. Visit https://github.com/JoshLuedeman/cosmosdb-to-sql-migration-tool/issues
  2. Create project boards at https://github.com/.../projects
  3. Configure automation rules in project settings
  4. Start working on Sprint 1 issues!
```

## Post-Setup Verification

```powershell
# List all labels
gh label list --limit 30

# List all milestones
gh api repos/JoshLuedeman/cosmosdb-to-sql-migration-tool/milestones --jq '.[].title'

# List all created issues
gh issue list --limit 20

# View specific issue
gh issue view 1
```

## Common Issues

**Error: "gh: command not found"**
- Install from https://cli.github.com/

**Error: "Not authenticated"**
- Run: `gh auth login`

**Error: "HTTP 404"**
- Check repository name and access permissions

## Manual Steps Required

After the script completes:

1. **Create Project Board**
   - Go to: https://github.com/JoshLuedeman/cosmosdb-to-sql-migration-tool/projects
   - Click "New project" → Table view
   - Name: "Product Roadmap"

2. **Add Issues to Project**
   - Open project → Add items
   - Add all 14 created issues

3. **Configure Automation**
   - Project Settings → Workflows
   - Set up auto-add and status transitions

See [README.md](README.md) for detailed instructions.
