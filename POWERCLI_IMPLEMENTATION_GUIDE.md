# Enhanced PowerCLI Implementation Guide

## Overview

The VMware GUI Tools application has been enhanced with a new PowerCLI service architecture that provides:

- **Improved Session Management**: Persistent PowerCLI sessions that reduce connection overhead
- **Better Error Handling**: Comprehensive error classification and user-friendly messages
- **Automatic Diagnostics**: Built-in PowerCLI environment validation and repair
- **Enhanced Reliability**: Robust connection testing and session recovery

## Architecture Changes

### New Services

1. **IPowerCLIService** - Main interface for PowerCLI operations
2. **PowerCLIService** - Implementation with session management
3. **EnhancedVMwareConnectionService** - Improved VMware connection handling
4. **PowerCLIDiagnosticsService** - PowerCLI troubleshooting utilities

### Key Features

#### Session Management
- Persistent sessions per vCenter connection
- Automatic session cleanup and timeout handling
- Session reuse for improved performance

#### Error Classification
- `AUTHENTICATION_FAILED` - Invalid credentials
- `CERTIFICATE_ERROR` - SSL/TLS issues
- `NETWORK_ERROR` - Connectivity problems
- `TIMEOUT` - Connection timeouts
- `POWERCLI_INVALID` - PowerCLI environment issues

#### Diagnostics and Repair
- Execution policy validation and repair
- PowerCLI module installation checks
- Version conflict detection
- Automatic repair capabilities

## Usage Examples

### Basic Connection Testing

```csharp
// Inject the service
private readonly IPowerCLIService _powerCLIService;

// Test connection
var result = await _powerCLIService.TestConnectionAsync(
    "https://vcenter.example.com", 
    "administrator@vsphere.local", 
    "password123"
);

if (result.IsSuccessful)
{
    Console.WriteLine($"Connected to vCenter {result.ServerVersion}");
}
else
{
    Console.WriteLine($"Connection failed: {result.ErrorMessage}");
    Console.WriteLine($"Error code: {result.ErrorCode}");
}
```

### Persistent Session Usage

```csharp
// Establish session
var session = await _powerCLIService.ConnectAsync(
    "https://vcenter.example.com", 
    "administrator@vsphere.local", 
    "password123"
);

try
{
    // Execute multiple commands using the same session
    var clusterResult = await _powerCLIService.ExecuteCommandAsync(
        session, 
        "Get-Cluster | Select-Object Name, DrsEnabled"
    );

    var hostResult = await _powerCLIService.ExecuteCommandAsync(
        session, 
        "Get-VMHost | Select-Object Name, ConnectionState"
    );
}
finally
{
    // Always disconnect
    await _powerCLIService.DisconnectAsync(session);
}
```

### PowerCLI Diagnostics

```csharp
// Inject the diagnostics service
private readonly PowerCLIDiagnosticsService _diagnostics;

// Run diagnostics
var validation = await _diagnostics.RunDiagnosticsAsync();

if (!validation.IsValid)
{
    Console.WriteLine("PowerCLI Issues Found:");
    foreach (var issue in validation.Issues)
    {
        Console.WriteLine($"- {issue}");
    }
    
    Console.WriteLine("\nSuggested Fixes:");
    foreach (var suggestion in validation.Suggestions)
    {
        Console.WriteLine($"- {suggestion}");
    }
    
    // Attempt automatic repair
    if (validation.RequiresRepair)
    {
        var repairResult = await _diagnostics.RepairPowerCLIAsync();
        
        if (repairResult.IsSuccessful)
        {
            Console.WriteLine("PowerCLI repair completed successfully!");
            Console.WriteLine("Actions performed:");
            foreach (var action in repairResult.ActionsPerformed)
            {
                Console.WriteLine($"- {action}");
            }
        }
    }
}
```

## Configuration

### PowerCLI Options

The new service uses configuration from `appsettings.json`:

```json
{
  "PowerCLI": {
    "ConnectionTimeoutSeconds": 60,
    "CommandTimeoutSeconds": 300,
    "IgnoreInvalidCertificates": true,
    "EnableVerboseLogging": false,
    "PreferredPowerCLIVersion": ""
  }
}
```

### Dependency Injection Setup

The services are automatically registered in both Service and UI projects:

```csharp
// PowerCLI Services
services.Configure<PowerCLIOptions>(configuration.GetSection(PowerCLIOptions.SectionName));
services.AddSingleton<IPowerCLIService, PowerCLIService>();
services.AddScoped<PowerCLIDiagnosticsService>();

// Enhanced VMware Connection Service
services.AddScoped<IVMwareConnectionService, EnhancedVMwareConnectionService>();
```

## Migration from Old Implementation

The enhanced service maintains compatibility with existing interfaces:

- `IVMwareConnectionService` interface remains unchanged
- Existing ViewModels and UI components work without modification
- Enhanced error messages provide better user feedback
- Session management happens transparently

## Benefits

### For Users
- **Better Error Messages**: Clear explanations with troubleshooting steps
- **Faster Operations**: Session reuse reduces connection overhead
- **Self-Healing**: Automatic PowerCLI environment repair
- **Improved Reliability**: Better handling of connection failures

### For Developers
- **Cleaner Code**: Simplified PowerCLI operations
- **Better Testing**: Comprehensive error classification
- **Easier Debugging**: Detailed logging and diagnostics
- **Future-Proof**: Extensible architecture for new features

## Troubleshooting

### Common Issues

1. **PowerShell Execution Policy**
   - **Problem**: Scripts cannot execute due to policy restrictions
   - **Solution**: Automatic repair sets `RemoteSigned` policy for current user

2. **PowerCLI Module Missing**
   - **Problem**: VMware PowerCLI modules not installed
   - **Solution**: Automatic installation via `Install-Module VMware.PowerCLI`

3. **Version Conflicts**
   - **Problem**: Multiple PowerCLI versions causing conflicts
   - **Solution**: Use PowerShell cleanup scripts in project root

4. **Certificate Errors**
   - **Problem**: Self-signed certificates on vCenter
   - **Solution**: Service automatically ignores invalid certificates

### Manual Repair

If automatic repair fails, use the provided PowerShell scripts:

```powershell
# Fix execution policy issues
.\PowerCLI-Fix-ExecutionPolicy.ps1

# Clean up version conflicts
.\PowerCLI-CleanupVersions.ps1
```

## Future Enhancements

The new architecture enables:

- Connection pooling for multiple vCenters
- Background session keep-alive
- Advanced PowerCLI script validation
- Performance monitoring and metrics
- Custom PowerCLI module management

## Testing

To test the implementation:

1. **Start the application**
2. **Add a vCenter server** - The enhanced service will automatically validate PowerCLI
3. **Test connection** - Improved error messages guide troubleshooting
4. **Monitor logs** - Detailed logging shows session management
5. **Use diagnostics** - Run PowerCLI validation from Settings

The new PowerCLI service provides a solid foundation for reliable VMware operations with comprehensive error handling and automatic problem resolution. 