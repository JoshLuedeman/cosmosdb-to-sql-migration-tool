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

## 🔒 Security Guidelines

### Authentication
- Use `DefaultAzureCredential` for all Azure authentication
- Never hardcode credentials or connection strings
- Support multiple authentication methods

### Data Handling
- No sensitive data should be logged
- Minimize data retention in memory
- Use secure temporary files when needed

### Configuration
- Keep sensitive settings out of `appsettings.json`
- Use command-line arguments for environment-specific values
- Validate all input parameters

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
