# VMware GUI Tools - Database & Data Storage

## Overview
This document describes where and how VMware GUI Tools stores its data, including vCenters, settings, and operational data.

## Database Configuration

### Database Type
- **Database Engine**: SQLite
- **Database File**: `vmware-gui-tools.db`
- **Location**: Application root directory
- **Entity Framework**: Used for data access with Code First approach

### Connection String
```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Data Source=vmware-gui-tools.db"
  }
}
```

## Data Storage Locations

### 1. Application Database (`vmware-gui-tools.db`)
Stores all persistent application data:

#### **VCenters Table**
- vCenter server configurations
- Encrypted connection credentials
- Connection status and metadata
- Last scan timestamps

#### **Clusters Table**
- VMware cluster information discovered from vCenters
- Cluster configuration and status
- Relationships to vCenter and host profiles

#### **Hosts Table**
- ESXi host information
- IP addresses, versions, and hardware details
- Health status and maintenance mode status
- Relationships to clusters and check results

#### **HostProfiles Table**
- Host configuration profiles
- Check execution schedules and parameters
- Profile types (Standard, vSAN Node, etc.)

#### **CheckCategories Table**
- Check category definitions (Configuration, Health, Security)
- Category metadata and sorting information

#### **CheckDefinitions Table**
- Individual check definitions and scripts
- PowerCLI execution parameters
- Thresholds and validation rules

#### **CheckResults Table**
- Historical check execution results
- Performance data and error messages
- Execution timestamps and status

### 2. Configuration Files

#### **appsettings.json** (UI Project)
```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Data Source=vmware-gui-tools.db"
  },
  "VMwareGUITools": {
    "UseMachineLevelEncryption": false,
    "ConnectionTimeoutSeconds": 30,
    "DefaultCheckTimeoutSeconds": 300,
    "EnableAutoDiscovery": true,
    "PowerCLIModulePath": "",
    "CheckScriptsPath": "Scripts",
    "ReportsPath": "Reports"
  }
}
```

#### **appsettings.json** (Service Project)
- Service-specific configuration
- Scheduling and background job settings
- PowerShell execution parameters

### 3. Log Files
- **Location**: `logs/` directory
- **UI Logs**: `vmware-gui-tools-{date}.log`
- **Service Logs**: `vmware-service-{date}.txt`
- **Retention**: 30 days for UI logs, configurable for service

### 4. Scripts and Reports
- **Check Scripts**: `Scripts/` directory (configurable)
- **Generated Reports**: `Reports/` directory (configurable)

## Database Schema

### Key Relationships
```
VCenter (1) ──→ (N) Cluster ──→ (N) Host ──→ (N) CheckResult
    │                │              │
    └─────────────────┼──────────────┘
                      │
HostProfile (1) ──→ (N) Host
                      │
CheckDefinition (1) ──→ (N) CheckResult
    │
CheckCategory (1) ──→ (N) CheckDefinition
```

### Data Encryption
- **vCenter Credentials**: Encrypted using Windows DPAPI (configurable)
- **Machine-Level**: Optional machine-level encryption for credentials
- **User-Level**: Default user-level encryption

## Database Status Monitoring

### Visual Indicators
The application provides real-time database connectivity status:

- **Green Icon**: Database connected and accessible
- **Red Icon**: Database connection issues
- **Location**: Main window left panel, below PowerCLI status

### Health Checks
- Database connection test on application startup
- Periodic connectivity validation
- Automatic retry logic for transient failures

## Database Initialization

### Automatic Setup
1. Database file created automatically on first run
2. Schema created using Entity Framework migrations
3. Default data seeded (check categories, host profiles)
4. No manual database setup required

### Migration Support
- Automatic migration execution on startup
- Schema version tracking
- Safe upgrade path for future versions

## Backup and Recovery

### Backup Strategy
- SQLite database file can be backed up by copying `vmware-gui-tools.db`
- Backup during application shutdown for consistency
- Include configuration files in backup

### Recovery Process
1. Stop the application and service
2. Replace database file with backup
3. Restart application - migrations will run if needed
4. Verify data integrity through UI

## Troubleshooting

### Common Issues

#### Database Lock Errors
- Ensure only one instance of the application is running
- Check for antivirus interference
- Verify file permissions

#### Missing Database File
- Application will create new database automatically
- Check working directory and file permissions
- Review log files for initialization errors

#### Performance Issues
- SQLite performs well for typical usage
- Consider connection pooling for high-load scenarios
- Monitor database file size and implement cleanup if needed

### Diagnostic Commands

#### Check Database Connectivity
The application automatically tests database connectivity on startup and displays status in the main window.

#### View Database Contents
Use any SQLite browser tool to inspect the database:
```bash
sqlite3 vmware-gui-tools.db
.tables
.schema VCenters
SELECT * FROM VCenters;
```

## Security Considerations

### Credential Storage
- vCenter passwords encrypted at rest
- Encryption keys tied to user/machine context
- No plaintext passwords in database or logs

### Database Access
- SQLite file protected by OS file permissions
- No network access to database (file-based)
- Consider additional encryption for sensitive environments

### Audit Trail
- All check executions logged with timestamps
- Configuration changes tracked in logs
- No automatic data purging (manual cleanup required) 