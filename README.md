# VMware GUI Tools - vSphere Infrastructure Management

A comprehensive .NET 8 WPF application for managing and monitoring VMware vSphere infrastructure through a modern, user-friendly interface.

## Overview

This application provides system administrators with a centralized tool to:
- Manage multiple vCenter servers with secure credential storage
- Monitor ESXi host health and configuration compliance
- Execute automated checks across different host profiles
- Generate reports and alerts for infrastructure issues
- Schedule background monitoring tasks

## Features

### Core Functionality ✅ (Phase 1 - Completed)
- **Secure vCenter Management**: Add, configure, and connect to multiple vCenter servers
- **Credential Encryption**: Windows DPAPI-based secure credential storage
- **Modern UI**: Material Design-based WPF interface
- **Database Integration**: SQLite database for configuration and results storage
- **Logging**: Comprehensive logging with Serilog

### Advanced Features ✅ (All Phases Completed)
- **Cluster Discovery**: Automatic discovery and management of vSphere clusters
- **Host Monitoring**: ESXi host health and configuration monitoring  
- **Check Engine**: Extensible PowerCLI-based check system with multiple execution engines
- **Host Profiles**: Configurable host profiles for different infrastructure types
- **Reporting**: Comprehensive reporting and alerting system
- **Scheduling**: Background task scheduling with Quartz.NET and cron expressions
- **Notifications**: Multi-channel notifications (Email, Slack, OpsGenie, Webhooks, etc.)

## Architecture

### Project Structure
```
VMwareGUITools/
├── src/
│   ├── VMwareGUITools.Core/         # Domain models and business logic
│   ├── VMwareGUITools.Data/         # Entity Framework DbContext and migrations
│   ├── VMwareGUITools.Infrastructure/ # External services (VMware API, SSH, etc.)
│   ├── VMwareGUITools.UI/           # WPF user interface
│   └── VMwareGUITools.Service/      # Background Windows service (planned)
├── docs/                            # Documentation
└── VMwareGUITools.sln              # Solution file
```

### Technology Stack
- **.NET 8**: Latest .NET framework
- **WPF**: Windows Presentation Foundation for desktop UI
- **Entity Framework Core**: Object-relational mapping with SQLite
- **Material Design**: Modern UI components and styling
- **MVVM Community Toolkit**: Modern MVVM implementation
- **Serilog**: Structured logging
- **PowerCLI**: VMware PowerShell modules (integration planned)
- **SSH.NET**: SSH connectivity for direct ESXi access

### Database Schema
- **VCenters**: vCenter server configurations
- **Clusters**: vSphere cluster information
- **Hosts**: ESXi host details and status
- **HostProfiles**: Configurable host check profiles
- **CheckCategories**: Health and configuration check categories
- **CheckDefinitions**: Individual check definitions and scripts
- **CheckResults**: Historical check execution results

## Getting Started

### Prerequisites
- Windows 10/11 or Windows Server 2019+
- .NET 8 SDK
- Visual Studio 2022 (recommended) or Visual Studio Code
- VMware PowerCLI (for VMware connectivity)

### Installation
1. Clone the repository
```bash
git clone <repository-url>
cd VMwareGUITools
```

2. Restore NuGet packages
```bash
dotnet restore
```

3. Build the solution
```bash
dotnet build
```

4. Run the application
```bash
dotnet run --project src/VMwareGUITools.UI
```

### First Time Setup
1. Launch the application
2. Click "Add vCenter Server" to configure your first vCenter
3. Enter vCenter details and credentials
4. Test the connection and save

## Configuration

### Application Settings (`appsettings.json`)
```json
{
  "VMwareGUITools": {
    "UseMachineLevelEncryption": false,    // User vs machine-level credential encryption
    "ConnectionTimeoutSeconds": 30,        // vCenter connection timeout
    "DefaultCheckTimeoutSeconds": 300,     // Default check execution timeout
    "EnableAutoDiscovery": true,           // Automatic cluster/host discovery
    "PowerCLIModulePath": "",             // Custom PowerCLI module path
    "CheckScriptsPath": "Scripts",        // Check scripts directory
    "ReportsPath": "Reports"              // Report output directory
  }
}
```

### Security
- Credentials are encrypted using Windows DPAPI
- Support for both user-level and machine-level encryption scopes
- Database connection strings are configurable
- Comprehensive audit logging

## Development Roadmap

### Phase 1: Core Infrastructure ✅ (Completed)
- [x] Project structure and solution setup
- [x] Database schema and Entity Framework configuration
- [x] Credential encryption service
- [x] Basic VMware connection service (stub implementation)
- [x] Main application UI with vCenter management
- [x] Add vCenter dialog with connection testing

### Phase 2: VMware Integration ✅ (Completed)
- [x] PowerCLI integration and execution engine
- [x] vCenter discovery functionality (clusters, hosts)
- [x] Real PowerCLI command execution
- [x] Connection testing and validation
- [x] VMware session management

### Phase 3: Check Engine ✅ (Completed)
- [x] Check definition and execution system
- [x] PowerCLI-based check execution engine
- [x] Check result storage and history
- [x] Host profile assignment and management
- [x] Batch execution with concurrency control
- [x] Check validation and testing framework

### Phase 4: Automation & Scheduling ✅ (Completed)
- [x] Quartz.NET scheduling integration
- [x] Automated discovery and checking
- [x] Multi-threaded parallel execution
- [x] Cron-based scheduling with flexible configurations
- [x] Schedule management (create, update, pause, resume, delete)

### Phase 5: Reporting & Notifications ✅ (Completed)
- [x] Multi-channel notification framework
- [x] Execution summary reporting
- [x] Notification channel management
- [x] Extensible notification system architecture
- [x] Batch notification processing

## Contributing

1. Fork the repository
2. Create a feature branch (`git checkout -b feature/amazing-feature`)
3. Commit your changes (`git commit -m 'Add amazing feature'`)
4. Push to the branch (`git push origin feature/amazing-feature`)
5. Open a Pull Request

### Development Guidelines
- Follow MVVM pattern for UI components
- Use dependency injection for service registration
- Implement comprehensive logging
- Write unit tests for business logic
- Follow C# coding conventions and best practices

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

## Support

For issues, questions, or contributions, please use the GitHub issue tracker.

---

**Note**: This application is designed for Windows environments and requires appropriate VMware infrastructure access for full functionality. 