# Cosmos DB to SQL Migration Assessment Tool

## Overview

The Cosmos DB to SQL Migration Assessment Tool is a comprehensive C# console application designed to analyze Azure Cosmos DB databases and provide detailed migration assessments for Azure SQL platforms. This tool helps organizations make informed decisions about migrating from Cosmos DB to SQL by providing performance analysis, cost estimates, and detailed migration recommendations.

## Key Features

- **Deep Cosmos DB Analysis**: 6-month performance metrics collection and analysis
- **Intelligent SQL Migration Assessment**: Platform recommendations based on Azure Well-Architected Framework
- **Azure Data Factory Estimates**: Migration time and cost calculations
- **Professional Reporting**: Excel and Word reports for technical and executive audiences
- **Secure Authentication**: Azure credential-based authentication with multiple methods
- **Configurable Analysis**: Customizable container selection and analysis depth

## Architecture

The application follows enterprise-grade patterns with:

- **Service-Oriented Architecture**: Modular services for analysis, assessment, and reporting
- **Dependency Injection**: Microsoft.Extensions framework for IoC
- **Azure SDK Integration**: Native Azure services connectivity
- **Comprehensive Logging**: Structured logging for monitoring and debugging
- **Configuration Management**: External configuration via appsettings.json

## Quick Start

1. **Prerequisites**: .NET 8.0+, Azure CLI (for authentication)
2. **Clone and Build**: `git clone <repo>` → `dotnet build`
3. **Configure**: Update `appsettings.json` with your Azure details
4. **Authenticate**: `az login` or configure managed identity
5. **Run**: `dotnet run`

## Documentation Structure

- **[Getting Started](getting-started.md)** - Installation and first-time setup
- **[Configuration Guide](configuration.md)** - Detailed configuration options
- **[Usage Guide](usage.md)** - How to run assessments and interpret results
- **[Real-Time Monitoring and Alerting](monitoring.md)** - Stream migration progress metrics, generate alert-rule ARM templates, the `migration status` CLI, and RU/throughput anomaly detection (parent #133)
- **[Continuous-Learning Feedback Loop](feedback-loop.md)** - Opt‑in privacy guide: what is/isn't collected, consent, opt in/out, and how refined recommendations are attributed (parent #132)
- **[SQL Project Generation](sql-project-generation.md)** - Generate SQL Database Projects from assessments
- **[Transformation Logic](transformation-logic.md)** - Data transformation stored procedures and implementation
- **[Architecture Overview](architecture.md)** - Technical architecture and design patterns
- **[Troubleshooting](troubleshooting.md)** - Common issues and solutions
- **[Azure Permissions](azure-permissions.md)** - Required Azure permissions and setup
- **[Production Hardening Guide](production-hardening.md)** - Managed-identity setup for AKS, App Service, Container Apps, VM/VMSS (parent #128)
- **[Secrets Management](secrets-management.md)** - Azure Key Vault patterns for the SQL deployment artifacts the tool generates (parent #128)
- **[Custom RBAC role definitions](security/rbac/README.md)** - Least-privilege Cosmos data-plane, ARM, Monitor, and SQL deploy roles (parent #128)
- **[Secret Rotation and Audit Logging](secret-rotation-and-audit.md)** - Rotation procedures plus diagnostic settings, Defender plans, and KQL detection library (parent #128)
- **[Production-readiness checklist](production-readiness-checklist.md)** - Security-review gate that ties together the four guides above (parent #128)

## Support

For issues, questions, or contributions, please refer to the documentation sections above or check the troubleshooting guide.

## Version

Current Version: 1.0.0  
Last Updated: August 2025
