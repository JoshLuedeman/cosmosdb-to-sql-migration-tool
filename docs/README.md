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
- **[SQL Project Generation](sql-project-generation.md)** - Generate SQL Database Projects from assessments
- **[Transformation Logic](transformation-logic.md)** - Data transformation stored procedures and implementation
- **[Architecture Overview](architecture.md)** - Technical architecture and design patterns
- **[Troubleshooting](troubleshooting.md)** - Common issues and solutions
- **[Azure Permissions](azure-permissions.md)** - Required Azure permissions and setup

## Support

For issues, questions, or contributions, please refer to the documentation sections above or check the troubleshooting guide.

## Version

Current Version: 1.0.0  
Last Updated: August 2025
