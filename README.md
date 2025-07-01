# VMware GUI Tools

A comprehensive VMware vSphere management application built with WPF and .NET 8, featuring automated compliance checking, infrastructure monitoring, and PowerCLI integration.

## 🚀 Recent Architecture Improvements

### Service-First Architecture
The application now follows a strict **service-first architecture** where:

- **GUI Application**: Only communicates with the database, never directly with vCenter
- **Windows Service**: Handles all vCenter connections, data collection, and compliance checks
- **Database**: Central data store for configuration, monitoring data, and inter-service communication

### Enhanced Service Status Monitoring
- **Accurate Service Status**: Fixed false-positive service status by ensuring only the Windows Service updates heartbeat data
- **Real-time Monitoring**: GUI displays actual service status with heartbeat timestamps
- **Data Freshness Indicators**: Visual indicators show how recent the displayed data is with color-coded freshness levels

### Improved Data Flow
1. **vCenter Operations**: GUI sends commands to the Windows Service via database
2. **Data Collection**: Service collects vCenter data and stores it in the database
3. **GUI Updates**: GUI reads processed data from the database and displays real-time freshness status

## 🔧 Architecture Components

### Windows Service (`VMwareGUITools.Service`)
- **Primary Role**: All vCenter connections and operations
- **Responsibilities**:
  - vCenter authentication and connection management
  - Automated data collection (overview, infrastructure, compliance)
  - Scheduled compliance checks and monitoring
  - Command processing from GUI application
  - Service heartbeat maintenance

### GUI Application (`VMwareGUITools.UI`)
- **Primary Role**: User interface and data visualization
- **Responsibilities**:
  - Display vCenter data from database
  - Send operation commands to service
  - Monitor service status and data freshness
  - Configuration management
  - Report generation

### Database Layer (`VMwareGUITools.Data`)
- **Primary Role**: Central data repository
- **Responsibilities**:
  - vCenter configuration storage
  - Monitoring data persistence
  - Service status and heartbeat tracking
  - Inter-service command queue
  - Compliance check results

### Core Models (`VMwareGUITools.Core`)
- **Primary Role**: Shared data models
- **Includes**: vCenter entities, check definitions, service configuration, command types

### Infrastructure Layer (`VMwareGUITools.Infrastructure`)
- **Primary Role**: Business logic and external integrations
- **Services**: VMware APIs, PowerCLI, security, scheduling, notifications

## 🎯 Key Features

### 🔍 Infrastructure Monitoring
- **Real-time Overview**: Resource usage, cluster health, VM counts
- **Infrastructure Tree**: Hierarchical view of clusters, hosts, and datastores
- **Connection Management**: Service-managed vCenter connections
- **Health Monitoring**: Automated status checks and alerts

### ✅ Compliance Checking
- **Automated Checks**: PowerCLI-based compliance validation
- **Scheduled Execution**: Configurable check intervals
- **Custom Rules**: Extensible check framework
- **Report Generation**: Detailed compliance reports

### 🗄️ Database Management
- **Entity Framework**: SQLite database with migrations
- **Service Configuration**: Database-driven service settings
- **Data Freshness**: Timestamped data with visual freshness indicators
- **Command Queue**: Service-GUI communication via database

### ⚡ Performance Features
- **Background Processing**: All heavy operations run in Windows Service
- **Caching**: Database-cached data for fast GUI responsiveness
- **Async Operations**: Non-blocking UI with progress indicators
- **Resource Monitoring**: Memory and CPU usage tracking

## 🚦 Service Status Indicators

The GUI now includes comprehensive status monitoring:

- **🟢 Green**: Service running, data fresh (< 5 minutes)
- **🟡 Yellow**: Service running, data moderately fresh (5-15 minutes)
- **🟠 Orange**: Service running, data stale (15-60 minutes)
- **🔴 Red**: Service stopped or data very stale (> 1 hour)

## 📋 Prerequisites

- .NET 8.0 Runtime
- VMware PowerCLI (automatically validated)
- Windows 10/11 or Windows Server 2019+
- SQL Server or SQLite support

## 🛠️ Installation

1. **Clone Repository**
   ```bash
   git clone https://github.com/your-org/VMwareGUITools.git
   cd VMwareGUITools
   ```

2. **Build Solution**
   ```bash
   dotnet build VMwareGUITools.sln
   ```

3. **Install Windows Service**
   ```powershell
   # Run as Administrator
   .\Install-VMwareService.ps1
   ```

4. **Start GUI Application**
   ```bash
   dotnet run --project src/VMwareGUITools.UI
   ```

## 🔧 Configuration

### Database Configuration
Both GUI and Service use the same database connection string defined in `appsettings.json`:
```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Data Source=vmware-gui-tools.db"
  }
}
```

### Service Configuration
Service settings are managed through the database and can be updated via the GUI:
- PowerCLI settings
- Check execution parameters
- Scheduling configuration
- API timeouts and retry policies

## 🏗️ Development

### Project Structure
```
src/
├── VMwareGUITools.UI/          # WPF GUI Application
├── VMwareGUITools.Service/     # Windows Service
├── VMwareGUITools.Core/        # Shared Models
├── VMwareGUITools.Data/        # Database Context
└── VMwareGUITools.Infrastructure/ # Business Logic
```

### Key Design Patterns
- **MVVM**: Model-View-ViewModel for GUI
- **Repository Pattern**: Data access abstraction
- **Command Pattern**: Service communication
- **Dependency Injection**: Service registration and resolution
- **Background Services**: Long-running tasks in Windows Service

### Database Migrations
```bash
# Add new migration
dotnet ef migrations add MigrationName --project src/VMwareGUITools.Data

# Update database
dotnet ef database update --project src/VMwareGUITools.Data
```

## 📊 Monitoring and Logging

- **Structured Logging**: Serilog with configurable levels
- **Performance Metrics**: Built-in timing and resource monitoring
- **Health Checks**: Automated service and component validation
- **Error Tracking**: Comprehensive exception handling and reporting

## 🔐 Security

- **Credential Encryption**: Secure credential storage using Windows DPAPI
- **Role-based Access**: Support for different user permission levels
- **Secure Communication**: Encrypted service-to-service communication
- **Audit Logging**: Track all configuration changes and operations

## 📝 License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

## 🤝 Contributing

1. Fork the repository
2. Create a feature branch (`git checkout -b feature/amazing-feature`)
3. Commit your changes (`git commit -m 'Add some amazing feature'`)
4. Push to the branch (`git push origin feature/amazing-feature`)
5. Open a Pull Request

## 📞 Support

For support and questions:
- Create an issue in this repository
- Check the [documentation](docs/)
- Review the [troubleshooting guide](TROUBLESHOOTING.md) 