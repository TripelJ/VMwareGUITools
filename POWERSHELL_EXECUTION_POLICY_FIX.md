# PowerShell Execution Policy Fix - Migration Guide

## ✅ Problem Solved

The VMware GUI Tools now uses **External PowerShell Execution** to completely bypass PowerShell execution policy issues. This eliminates the need for:
- Setting execution policies with Group Policy
- Running as administrator to change LocalMachine policies  
- Worrying about "unconfigured" execution policy errors
- Complex embedded PowerShell runspace management

## 🚀 What Changed

### 🆕 New Services (Recommended)

1. **`ExternalPowerShellService`** - Executes PowerShell via external process with `-ExecutionPolicy Bypass`
2. **`PowerShellServiceV2`** - Hybrid service that tries external first, falls back to embedded
3. **`RestVMwareConnectionService`** - Alternative that uses vSphere REST API (no PowerShell needed)

### ❌ Removed/Deprecated Services

1. **`PowerShellService`** - ~~Problematic embedded runspace~~ (kept as fallback only)
2. **`VMwareConnectionService`** - ~~Had execution policy issues~~ (replaced with Enhanced)

## 📋 Migration Steps Completed

### ✅ 1. Dependency Injection Updated

**Before:**
```csharp
// OLD - Had execution policy issues
services.AddScoped<IPowerShellService, PowerShellService>();
services.AddScoped<IVMwareConnectionService, VMwareConnectionService>();
```

**After:**
```csharp
// NEW - Uses external PowerShell execution
services.AddVMwareGUIToolsInfrastructure(configuration);
```

### ✅ 2. Configuration Added

Added to `appsettings.json`:
```json
{
  "ExternalPowerShell": {
    "InheritEnvironmentVariables": true,
    "DefaultTimeoutSeconds": 300,
    "UseWindowsPowerShell": true,
    "CustomPowerShellPath": null
  },
  "PowerShellV2": {
    "UseExternalExecution": true,
    "FallbackToEmbedded": true,
    "ExternalTimeoutSeconds": 300,
    "LogExternalOutput": false
  }
}
```

### ✅ 3. Service Registration Simplified

The new `ServiceCollectionExtensions.AddVMwareGUIToolsInfrastructure()` method automatically:
- Registers the external PowerShell service
- Configures the hybrid fallback service
- Sets up all VMware connection services
- Maintains backward compatibility

## 🎯 How It Works Now

### External PowerShell Execution Process

1. **App needs to run PowerCLI command**
2. **Creates temporary `.ps1` script file**
3. **Launches:** `powershell.exe -ExecutionPolicy Bypass -File script.ps1`
4. **Captures output and cleans up**

### Key Benefits

✅ **No execution policy dependency** - `-ExecutionPolicy Bypass` overrides all settings  
✅ **Inherits user context** - Runs as the same user who launched the app  
✅ **No Group Policy needed** - Works entirely within user permissions  
✅ **Automatic fallback** - Falls back to embedded if external fails  
✅ **Better performance** - Faster startup, no runspace initialization overhead  

## 🧪 Testing

You can test the external execution approach:

```bash
# Test external PowerShell execution
powershell -ExecutionPolicy Bypass -File TestExternalPowerShell.ps1
```

## 🔧 Alternative Options

### Option 1: External PowerShell (Default)
- **Best for:** Most environments, bypasses execution policy completely
- **Uses:** `ExternalPowerShellService` with `PowerShellServiceV2`

### Option 2: REST API Only
```csharp
// Use this in ConfigureServices if you want to eliminate PowerShell entirely
services.AddVMwareGUIToolsInfrastructureWithRestAPI(configuration);
```
- **Best for:** High-security environments that block PowerShell
- **Uses:** vSphere REST APIs directly, no PowerShell dependency

### Option 3: Legacy (Debugging Only)
```csharp
// DON'T USE - Only for comparison/debugging
services.AddVMwareGUIToolsInfrastructureLegacy(configuration);
```

## 🛠 Troubleshooting

### If External Execution Fails
1. Check if PowerShell is in PATH
2. Verify write permissions to temp directory
3. Check if process execution is blocked by security software
4. The service will automatically fallback to embedded execution

### If You Still Get Execution Policy Errors
1. Verify you're using the new `AddVMwareGUIToolsInfrastructure()` method
2. Check configuration - ensure `UseExternalExecution: true`
3. Enable fallback: `FallbackToEmbedded: true`
4. Check logs for specific error messages

### Debug Mode
```json
{
  "PowerShellV2": {
    "LogExternalOutput": true  // Enable detailed logging
  }
}
```

## 📖 Usage Examples

### Basic PowerCLI Command
```csharp
// This now uses external execution automatically
var result = await powerShellService.ExecutePowerCLICommandAsync("Get-PowerCLIVersion");
```

### VMware Connection
```csharp
// This now uses external PowerCLI automatically
var connectionResult = await vmwareService.TestConnectionAsync(vcenterUrl, username, password);
```

### Force External/Embedded Mode
```csharp
// Force external mode (for troubleshooting)
var hybridService = (PowerShellServiceV2)powerShellService;
hybridService.ResetToExternalExecution();

// Force embedded mode (for comparison)
hybridService.ForceEmbeddedExecution();
```

## 🎉 Result

✅ **No more execution policy errors**  
✅ **No more "unconfigured" policy messages**  
✅ **No administrator privileges needed for PowerShell execution**  
✅ **Works regardless of Group Policy settings**  
✅ **Better reliability and performance**  

Your VMware GUI Tools should now work consistently across all environments without PowerShell execution policy headaches! 