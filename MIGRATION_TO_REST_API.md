# Migration Guide: PowerShell/PowerCLI to vSphere REST API

## Overview

This guide outlines the migration from PowerShell/PowerCLI-based vCenter monitoring to the modern vSphere REST API approach. This change eliminates PowerShell execution policy issues and provides better cross-platform compatibility.

## What Changed

### 1. Configuration Updates

**Before (PowerShell/PowerCLI):**
```json
{
  "PowerShell": {
    "ExecutionPolicy": "RemoteSigned",
    "TimeoutSeconds": 300,
    "EnableVerboseLogging": true
  },
  "PowerCLI": {
    "ConnectionTimeoutSeconds": 60,
    "CommandTimeoutSeconds": 300,
    "IgnoreInvalidCertificates": true,
    "PreferredPowerCLIVersion": "13.4.0"
  }
}
```

**After (vSphere REST API):**
```json
{
  "vSphereRestAPI": {
    "ConnectionTimeoutSeconds": 60,
    "RequestTimeoutSeconds": 300,
    "IgnoreInvalidCertificates": true,
    "EnableVerboseLogging": true,
    "MaxConcurrentConnections": 10,
    "EnableSessionPooling": true,
    "SessionPoolSize": 5,
    "SessionIdleTimeoutMinutes": 30,
    "RetryAttempts": 3,
    "RetryDelaySeconds": 2
  }
}
```

### 2. Service Registration Changes

**Before:**
```csharp
// PowerShell/PowerCLI Services
services.Configure<PowerCLIOptions>(configuration.GetSection(PowerCLIOptions.SectionName));
services.AddSingleton<IPowerCLIService, PowerCLIService>();
services.AddScoped<IVMwareConnectionService, EnhancedVMwareConnectionService>();
services.AddScoped<ICheckEngine, PowerCLICheckEngine>();
```

**After:**
```csharp
// vSphere REST API Services
services.Configure<VSphereRestAPIOptions>(configuration.GetSection(VSphereRestAPIOptions.SectionName));
services.AddHttpClient();
services.AddScoped<IVSphereRestAPIService, VSphereRestAPIService>();
services.AddScoped<IVMwareConnectionService, RestVMwareConnectionService>();
services.AddScoped<ICheckEngine, RestAPICheckEngine>();
```

### 3. Check Definition Updates

**ExecutionType Enum:**
- Added: `vSphereRestAPI`
- Deprecated: `PowerCLI` (marked with `[Obsolete]`)

**Check Script Migration:**
PowerShell scripts are now mapped to REST API check types:
- CPU/Memory/Performance scripts → `host-performance`
- Hardware/BIOS/Firmware scripts → `host-hardware`
- Network/VNIC/VMkernel scripts → `host-networking`
- Storage/Datastore/VMFS scripts → `host-storage`
- Security/Firewall/Certificate scripts → `host-security`
- Configuration/Settings/Policy scripts → `host-configuration`

## Benefits of Migration

### 1. **No PowerShell Dependencies**
- ❌ No more PowerShell execution policy issues
- ❌ No more PowerCLI module installation requirements
- ❌ No more Windows PowerShell vs PowerShell Core compatibility issues

### 2. **Improved Performance**
- ✅ Direct HTTP/REST API calls are faster than PowerShell execution
- ✅ Session pooling reduces authentication overhead
- ✅ Concurrent connections improve throughput

### 3. **Better Reliability**
- ✅ More predictable error handling
- ✅ Built-in retry mechanisms
- ✅ Session management and auto-reconnection

### 4. **Enhanced Security**
- ✅ Encrypted credential storage
- ✅ Session-based authentication
- ✅ Configurable certificate validation

### 5. **Cross-Platform Compatibility**
- ✅ Works on Windows, Linux, and macOS
- ✅ No platform-specific dependencies
- ✅ Container-friendly deployment

## Migration Steps

### Step 1: Update Configuration Files
1. Update both `appsettings.json` files (UI and Service projects)
2. Remove PowerShell/PowerCLI sections
3. Add vSphere REST API configuration

### Step 2: Update Check Definitions
1. Review existing check definitions
2. Update `ExecutionType` from `PowerCLI` to `vSphereRestAPI`
3. No script changes needed - automatic mapping handles conversion

### Step 3: Test Connectivity
1. Test vCenter connections using new REST API service
2. Verify authentication works correctly
3. Confirm SSL certificate handling

### Step 4: Validate Check Execution
1. Run existing checks with new engine
2. Verify results are equivalent
3. Update any custom thresholds if needed

### Step 5: Monitor and Optimize
1. Monitor performance improvements
2. Adjust connection pooling settings
3. Fine-tune timeout values

## Troubleshooting

### Common Issues

**1. SSL Certificate Errors**
```
Solution: Set "IgnoreInvalidCertificates": true in configuration
```

**2. Authentication Failures**
```
Solution: Verify credentials and vCenter URL format (https://vcenter.example.com)
```

**3. Session Timeouts**
```
Solution: Adjust SessionIdleTimeoutMinutes in configuration
```

**4. Network Connectivity**
```
Solution: Ensure vCenter is accessible on port 443 (HTTPS)
```

### Debugging

Enable verbose logging:
```json
{
  "Logging": {
    "LogLevel": {
      "VMwareGUITools.Infrastructure.VMware": "Debug"
    }
  },
  "vSphereRestAPI": {
    "EnableVerboseLogging": true
  }
}
```

## API Endpoint Reference

The new implementation uses these vSphere REST API endpoints:

### Authentication
- `POST /api/session` - Create session
- `DELETE /api/session` - Delete session

### Discovery
- `GET /api/vcenter/cluster` - List clusters
- `GET /api/vcenter/host` - List hosts
- `GET /api/vcenter/host/{host-id}` - Get host details

### Host Information
- `GET /api/vcenter/host/{host-id}/hardware/cpu` - CPU information
- `GET /api/vcenter/host/{host-id}/hardware/memory` - Memory information
- `GET /api/vcenter/host/{host-id}/hardware` - Hardware details
- `GET /api/vcenter/host/{host-id}/networking` - Network configuration
- `GET /api/vcenter/host/{host-id}/storage` - Storage configuration
- `GET /api/vcenter/host/{host-id}/services` - Services/Security

### System Information
- `GET /api/appliance/system/version` - vCenter version

## Performance Optimizations

### Session Pooling
```json
{
  "vSphereRestAPI": {
    "EnableSessionPooling": true,
    "SessionPoolSize": 5,
    "SessionIdleTimeoutMinutes": 30
  }
}
```

### Concurrent Connections
```json
{
  "vSphereRestAPI": {
    "MaxConcurrentConnections": 10
  }
}
```

### HTTP Client Settings
```json
{
  "HttpClient": {
    "DefaultTimeoutSeconds": 300,
    "MaxResponseContentBufferSize": 10485760,
    "EnableCompression": true
  }
}
```

## Rollback Plan

If needed, you can rollback by:

1. Reverting configuration files
2. Changing service registrations back to PowerCLI services
3. Updating check definitions back to `PowerCLI` execution type

**Note:** Keep PowerCLI-related files until migration is fully tested and validated.

## Future Enhancements

With REST API foundation in place, future enhancements include:

1. **Real-time Monitoring**: WebSocket connections for live data
2. **Advanced Queries**: Complex filtering and aggregation
3. **Batch Operations**: Multiple checks in single API call
4. **Custom Dashboards**: Rich data visualization
5. **API Extensions**: Custom endpoint development

## Support

For issues or questions:
1. Check logs with verbose logging enabled
2. Verify vCenter REST API documentation
3. Test connectivity using REST API tools (Postman, curl)
4. Review VMware vSphere API documentation

---

**Migration completed!** 🎉 

Your VMware GUI Tools solution now uses modern vSphere REST API instead of PowerShell/PowerCLI, providing better performance, reliability, and cross-platform compatibility. 