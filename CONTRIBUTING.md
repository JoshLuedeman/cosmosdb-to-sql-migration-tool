# Contributing to Cosmos DB to SQL Migration Assessment Tool

Thank you for your interest in contributing! This document provides guidelines and information for contributors.

## 🚀 Quick Start

1. **Fork the repository** on GitHub
2. **Clone your fork** locally
3. **Create a feature branch** from `develop`
4. **Make your changes** following our coding standards
5. **Test thoroughly** using the testing guidelines below
6. **Submit a pull request** with a clear description

## 🏗️ Development Environment

### Prerequisites
- .NET 8.0 SDK or later
- Azure CLI (for authentication testing)
- Access to an Azure Cosmos DB account (for integration testing)
- Git

### Setup
```bash
git clone https://github.com/JoshLuedeman/cosmosdb-to-sql-migration-tool.git
cd cosmosdb-to-sql-migration-tool
dotnet restore
dotnet build
```

### Testing Your Changes
```bash
# Test basic functionality
dotnet run -- --help

# Test with a real Cosmos DB account (if available)
dotnet run -- --endpoint "https://your-test-account.documents.azure.com:443/" --database "test-db" --output "./test-reports"
```

## 📋 Contribution Guidelines

### Code Style
- Follow C# coding conventions
- Use meaningful variable and method names
- Add XML documentation comments for public APIs
- Keep methods focused and single-purpose
- Use async/await for all I/O operations

### Commit Messages
Use conventional commit format:
```
type(scope): description

feat(analysis): add support for nested document analysis
fix(reports): resolve Excel worksheet naming conflicts
docs(readme): update installation instructions
```

Types: `feat`, `fix`, `docs`, `style`, `refactor`, `test`, `chore`

### Branch Naming
- `feature/feature-name` for new features
- `fix/bug-description` for bug fixes
- `docs/documentation-updates` for documentation
- `refactor/code-improvements` for refactoring

### Pull Requests
- Use the pull request template
- Ensure all CI checks pass
- Include tests for new functionality
- Update documentation as needed
- Add screenshots for UI-related changes

## 🧪 Testing

### Manual Testing Scenarios
Before submitting a PR, test these scenarios:

1. **Single Database Analysis**
   ```bash
   dotnet run -- --endpoint "https://account.documents.azure.com:443/" --database "test-db" --output "./reports"
   ```

2. **Multi-Database Analysis**
   ```bash
   dotnet run -- --endpoint "https://account.documents.azure.com:443/" --all-databases --output "./reports"
   ```

3. **Error Handling**
   - Invalid endpoint URLs
   - Non-existent databases
   - Authentication failures
   - Network connectivity issues

4. **Report Generation**
   - Verify Excel files open correctly
   - Verify Word documents have proper heading styles
   - Check file naming conventions

### Integration Testing
If you have access to a Cosmos DB account:
- Test with different database sizes
- Test with various container configurations
- Test authentication methods (Azure CLI, Managed Identity)
- Verify Azure Monitor integration (if configured)

## 📚 Architecture Overview

### Key Components
- **CosmosDbAnalysisService**: Cosmos DB data analysis
- **SqlMigrationAssessmentService**: SQL migration recommendations  
- **DataFactoryEstimateService**: Migration time and cost estimates
- **ReportGenerationService**: Excel and Word report generation

### Adding New Features
1. Consider which service should own the functionality
2. Update the relevant data models in `Models/`
3. Add appropriate configuration options
4. Update command-line arguments if needed
5. Ensure proper error handling and logging

## Security Requirements

Every PR to `main` must pass four security checks. The thresholds and full triage matrix live in [SECURITY.md](SECURITY.md); this section is the developer-facing summary.

### Required CI checks
| Check | Workflow | Fails on |
| --- | --- | --- |
| CodeQL SAST (csharp + actions) | [`codeql.yml`](.github/workflows/codeql.yml) | Build/analysis errors only — alerts surface in the **Security → Code scanning** tab |
| Dependency Review | [`dependency-review.yml`](.github/workflows/dependency-review.yml) | A PR-diff dep change introducing a High/Critical CVE |
| NuGet Audit | [`dependency-review.yml`](.github/workflows/dependency-review.yml) | A High/Critical CVE anywhere in resolved deps (incl. transitive) |
| Secret Scan (gitleaks) | [`secret-scan.yml`](.github/workflows/secret-scan.yml) | Any leaked secret in git history |

### Running scans locally before pushing

**NuGet vulnerability audit** — mirrors the `nuget-audit` CI job:
```bash
dotnet list CosmosToSqlAssessment.sln package --vulnerable --include-transitive
```
> ℹ️ This command is informational and may exit `0` even when vulnerabilities are listed. Read the output; CI is the authoritative gate.

**Secret scan** — mirrors the `secret-scan` CI job:
```bash
# Linux/macOS (Docker)
docker run --rm -v "$(pwd):/repo" zricethezav/gitleaks:latest detect --source /repo

# Or, if gitleaks is installed locally
gitleaks detect --source .
```
```powershell
# Windows PowerShell (Docker)
docker run --rm -v "${PWD}:/repo" zricethezav/gitleaks:latest detect --source /repo
```

**CodeQL** — impractical to reproduce on a developer laptop. View alerts in the GitHub UI under **Security → Code scanning** after pushing. PR-introduced alerts also appear inline as review comments.

**Dependency Review** — only meaningful against the PR diff, so it cannot be fully reproduced locally. Inspect your dependency changes manually and rely on the CI check for the final verdict.

### Fixing a failing check

- **CodeQL alert** — open the alert in the Security tab, follow **Show paths** to understand the dataflow, then either fix the code or **Dismiss** with a documented reason (`False positive`, `Used in tests`, `Won't fix`). Never dismiss without a reason.
- **Dependency Review fail** — bump the offending direct dependency to a patched version (`dotnet add package <name>`), push, re-run.
- **NuGet Audit fail** — same as above. If the vuln is in a transitive dep, bump the parent direct dep or pin the transitive via an explicit `<PackageReference>` in the project file.
- **Gitleaks fail** — **rotate the leaked credential immediately** — assume any value committed to git history is compromised. Then either remove the offending file or, only if the match is a genuine false positive (e.g., a documentation example), add an `[[allowlists]]` entry as shown below.

### Adding a gitleaks allowlist entry

Edit [`.gitleaks.toml`](.gitleaks.toml) and add a `[[allowlists]]` table-of-tables block (the legacy `[allowlist]` form is deprecated in gitleaks v8):

```toml
[[allowlists]]
description = "Sample API key in onboarding docs — not a real credential"
paths = ['''^docs/getting-started\.md$''']
regexes = ['''AKIA[0-9A-Z]{16}EXAMPLE''']
```

**Allowlist discipline:**
- Keep entries narrow. Prefer specific `paths`, `regexes`, or `commits`. Never use broad wildcards.
- Always include a `description` explaining *why* the match is safe — the description is the audit trail.
- If the value is a real secret, **rotate first, then remove from history**. Allowlisting a real secret is not an acceptable fix.

### Reporting a vulnerability

Do not open a public GitHub issue. Use the private reporting flow described in [SECURITY.md → Reporting a Vulnerability](SECURITY.md#reporting-a-vulnerability).

### Secure coding

In addition to passing the automated scans, keep the following in mind:

**Authentication**
- Use `DefaultAzureCredential` for all Azure authentication.
- Never hardcode credentials or connection strings.
- Support multiple authentication methods.

**Data handling**
- No sensitive data should be logged.
- Minimize data retention in memory.
- Use secure temporary files when needed.

**Configuration**
- Keep sensitive settings out of `appsettings.json`.
- Use command-line arguments for environment-specific values.
- Validate all input parameters.

## 📖 Documentation

### When to Update Documentation
- Adding new command-line options
- Changing configuration structure
- Adding new features
- Fixing significant bugs

### Documentation Files
- `README.md` - Overview and quick start
- `docs/getting-started.md` - Installation and setup
- `docs/usage.md` - Command-line usage
- `docs/configuration.md` - Configuration options
- `docs/architecture.md` - Technical architecture
- `docs/troubleshooting.md` - Common issues

## 🐛 Bug Reports

When reporting bugs:
1. Use the bug report template
2. Include the exact command used
3. Provide error messages and stack traces
4. Specify your environment details
5. Include steps to reproduce

## 💡 Feature Requests

When requesting features:
1. Use the feature request template
2. Explain the problem you're trying to solve
3. Describe your proposed solution
4. Consider alternative approaches
5. Provide use case examples

## 🚀 Release Process

### Versioning
We use [Semantic Versioning](https://semver.org/):
- `MAJOR.MINOR.PATCH`
- MAJOR: Breaking changes
- MINOR: New features (backward compatible)
- PATCH: Bug fixes (backward compatible)

### Release Workflow
1. Create a release branch from `main`
2. Update version numbers
3. Create a tag `v1.2.3`
4. Push the tag to trigger release pipeline
5. GitHub Actions builds and publishes releases automatically

## 📞 Getting Help

- **GitHub Discussions**: For general questions and ideas
- **GitHub Issues**: For bugs and feature requests
- **Pull Request Comments**: For code review discussions

## 🎉 Recognition

Contributors will be acknowledged in:
- GitHub contributor lists
- Release notes for significant contributions
- Project documentation

Thank you for contributing to make this tool better! 🚀
