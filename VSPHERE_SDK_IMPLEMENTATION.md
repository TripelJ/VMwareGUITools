# vSphere SDK for .NET Implementation Guide

## Overview

This document describes the implementation of VMware vSphere SDK for .NET to provide accurate iSCSI dead path monitoring, replacing the simulated checks in the REST API implementation.

## Why vSphere SDK for .NET?

### Problems with REST API Approach
1. **Limited Storage Information**: The vSphere REST API doesn't provide direct endpoints for detailed storage adapter and path information
2. **No iSCSI Path Details**: Cannot access real-time iSCSI path states through REST API
3. **Simulation Required**: The REST API implementation had to simulate iSCSI checks based on host connectivity

### Benefits of vSphere SDK Approach
1. **Direct Access to Managed Objects**: Access to `HostStorageSystem` managed objects
2. **Real Path Information**: Get actual iSCSI path states (active, dead, standby)
3. **Detailed Adapter Information**: Access to HBA details, portal addresses, and multipath configuration
4. **No PowerShell Dependencies**: Avoids PowerShell execution policy issues

## Implementation Components

### 1. NuGet Package Added
- **VMware.Vim**: Version 8.3.0.24145081
  - This is the official VMware vSphere SDK for .NET
  - Provides managed object access to vSphere infrastructure

### 2. Service Architecture

```
IVSphereSDKService (Interface)
├── VSphereSDKService (Implementation)
├── VimClientConnection (Connection wrapper)
└── Result Models:
    ├── ISCSIPathResult
    ├── ISCSIAdapterInfo
    ├── PathInfo
    ├── StorageAdapterInfo
    └── MultipathInfo
```

### 3. Key Features

#### Real iSCSI Path Monitoring
- Accesses `HostStorageSystem` managed objects
- Queries `HostStorageDeviceInfo` for HBA and path details
- Provides real-time path states: active, dead, standby
- Reports accurate path counts and health status

#### Detailed Adapter Information
- Discovers iSCSI adapters (HostInternetScsiHba)
- Retrieves portal addresses and target configurations
- Reports adapter online/offline status

#### Multipath Analysis
- Analyzes multipath configurations
- Reports path redundancy and failover status
- Identifies optimal vs. sub-optimal paths

## Usage in Check Engine

### Integration with RestAPICheckEngine

The `RestAPICheckEngine` has been enhanced to:

1. **Detect iSCSI Checks**: Automatically identifies iSCSI dead path checks by name pattern
2. **Route to SDK**: Routes iSCSI checks to the vSphere SDK service instead of REST API
3. **Fallback Support**: Continues using REST API for other check types

### Check Detection Logic

```csharp
private static bool IsISCSIPathCheck(CheckDefinition checkDefinition)
{
    var name = checkDefinition.Name.ToLower();
    return name.Contains("iscsi") && name.Contains("dead") && name.Contains("path");
}
```

### Threshold Evaluation

The SDK implementation supports threshold-based evaluation:
- `maxDeadPaths`: Maximum number of dead paths before failure
- Configurable via check definition parameters

## Example Output

### Successful Check
```
iSCSI Path Check Results for Host: dkaz3-kol01-esx-m01.dk.sentia.net
Check Time: 2025-01-27 10:30:00 UTC
Host MORef ID: host-90915

=== Path Summary ===
Total iSCSI Adapters: 2
Total Paths: 4
Active Paths: 4
Dead Paths: 0
Standby Paths: 0
Threshold (Max Dead Paths): 0
Status: PASS

=== iSCSI Adapter Details ===
Adapter: vmhba64
  Type: iSCSI
  Online: True
  Portal Addresses: 192.168.1.100:3260, 192.168.1.101:3260
  Path Count: 2

Adapter: vmhba65
  Type: iSCSI
  Online: True
  Portal Addresses: 192.168.2.100:3260, 192.168.2.101:3260
  Path Count: 2

=== Path Details ===
vmhba64:C0:T0:L0 -> ACTIVE (iscsi)
  Target: T0, LUN: 0
vmhba64:C0:T1:L0 -> ACTIVE (iscsi)
  Target: T1, LUN: 0
vmhba65:C0:T0:L0 -> ACTIVE (iscsi)
  Target: T0, LUN: 0
vmhba65:C0:T1:L0 -> ACTIVE (iscsi)
  Target: T1, LUN: 0
```

### Failed Check (Dead Paths Detected)
```
iSCSI Path Check Results for Host: dkaz3-kol01-esx-m01.dk.sentia.net
Check Time: 2025-01-27 10:35:00 UTC
Host MORef ID: host-90915

=== Path Summary ===
Total iSCSI Adapters: 2
Total Paths: 4
Active Paths: 2
Dead Paths: 2
Standby Paths: 0
Threshold (Max Dead Paths): 0
Status: FAIL

=== Path Details ===
vmhba64:C0:T0:L0 -> ACTIVE (iscsi)
  Target: T0, LUN: 0
vmhba64:C0:T1:L0 -> DEAD (iscsi)
  Target: T1, LUN: 0
vmhba65:C0:T0:L0 -> ACTIVE (iscsi)
  Target: T0, LUN: 0
vmhba65:C0:T1:L0 -> DEAD (iscsi)
  Target: T1, LUN: 0

Error: Found 2 dead paths, exceeding threshold of 0
```

## Configuration

### Service Registration

Both services are now registered in dependency injection:

```csharp
// Service/Program.cs and UI/App.xaml.cs
services.AddScoped<IVSphereRestAPIService, VSphereRestAPIService>();
services.AddScoped<IVSphereSDKService, VSphereSDKService>();
services.AddScoped<ICheckEngine, RestAPICheckEngine>();
```

### Check Definition Parameters

Example check definition for iSCSI path monitoring:

```json
{
  "name": "iSCSI Dead Path Check",
  "script": "Check for dead iSCSI paths",
  "parameters": {
    "maxDeadPaths": 0,
    "timeout": 60
  }
}
```

## Technical Details

### Connection Management

The SDK service manages VIM client connections:
- Creates `VimClientImpl` instances
- Handles session management and cleanup
- Implements proper disposal patterns

### Security Considerations

- Uses the same credential service as REST API
- Supports certificate validation bypass for self-signed certificates
- Maintains session security through proper logout

### Error Handling

- Comprehensive exception handling
- Graceful degradation on connection failures
- Detailed error reporting in check results

## Migration from Simulated Checks

### Before (REST API Simulation)
- Host connectivity-based simulation
- No real path information
- Generic error messages
- Limited troubleshooting data

### After (vSphere SDK)
- Real-time path status monitoring
- Detailed adapter and path information
- Accurate dead path detection
- Comprehensive troubleshooting output

## Troubleshooting

### Common Issues

1. **Connection Failures**
   - Verify vCenter credentials
   - Check network connectivity to vCenter
   - Ensure vCenter API is accessible

2. **No iSCSI Adapters Found**
   - Verify host has iSCSI configuration
   - Check if adapters are properly configured
   - Ensure host is not in maintenance mode

3. **Performance Considerations**
   - SDK calls may be slower than REST API
   - Connection pooling implemented for efficiency
   - Consider timeout adjustments for large environments

### Logging

The implementation provides detailed logging:
- Connection establishment and cleanup
- iSCSI adapter discovery
- Path status changes
- Error conditions and exceptions

## Future Enhancements

1. **Additional Storage Checks**: Extend to other storage adapter types (FC, SAS)
2. **Performance Metrics**: Add latency and throughput monitoring
3. **Alerting Integration**: Enhanced notification for path failures
4. **Caching**: Implement intelligent caching for frequently accessed data
5. **Batch Operations**: Optimize for multiple host checks

## Conclusion

The vSphere SDK for .NET implementation provides accurate, real-time iSCSI path monitoring without the limitations of PowerShell execution policies or REST API constraints. This solution delivers enterprise-grade storage monitoring capabilities essential for production vSphere environments. 