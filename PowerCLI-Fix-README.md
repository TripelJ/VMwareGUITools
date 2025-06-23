# PowerCLI Fix Scripts for VMwareGUITools

This directory contains scripts to help resolve PowerCLI issues commonly encountered with VMware PowerCLI, including version conflicts, execution policy problems, and installation issues.

## Scripts Overview

### PowerCLI-CleanupVersions.ps1
Comprehensive script that:
- Detects and resolves PowerCLI version conflicts
- Uninstalls conflicting versions
- Reinstalls the latest compatible PowerCLI version
- Validates the installation

### PowerCLI-VersionFix.ps1  
Quick fix script that:
- Attempts to resolve common version conflicts
- Provides guided troubleshooting
- Can be run without full uninstall/reinstall

### PowerCLI-Fix-ExecutionPolicy.ps1 ⭐ **NEW**
Execution policy fix script that:
- Diagnoses execution policy issues
- Applies appropriate policy fixes
- Tests PowerCLI module loading
- Provides specific guidance for VMwareGUITools

## Common Issues Addressed

### 1. **Version Conflicts** (e.g., VMware.Vim v9.0 vs VMware.VimAutomation.Common v13.x)
**Symptoms**: "Version conflict detected: VMware.Vim v9.0.0.24798382 is incompatible with VMware.VimAutomation.Common v13.3.0.24145081"

**Solutions**:
```powershell
# Option 1: Use the cleanup script
.\PowerCLI-CleanupVersions.ps1

# Option 2: Use VMwareGUITools Settings
# Go to Settings > PowerCLI > "Run PowerCLI Cleanup" button
```

### 2. **Execution Policy Issues**
**Symptoms**: "cannot be loaded because running scripts is disabled on this system"

**Solutions**:
```powershell
# Option 1: Use the execution policy fix script
.\PowerCLI-Fix-ExecutionPolicy.ps1

# Option 2: Manual fix (as Administrator)
Set-ExecutionPolicy -Scope LocalMachine RemoteSigned

# Option 3: User-level fix (no admin required)
Set-ExecutionPolicy -Scope CurrentUser RemoteSigned
```

### 3. **VMware.Vim v9.0 Compatibility Issue**
**Special Note**: VMwareGUITools has been enhanced to work around the specific VMware.Vim v9.0 compatibility issue. The application can function without this module for most operations.

**How it works**:
- Application automatically detects version conflicts
- Falls back to compatible module combinations
- Skips problematic VMware.Vim module when necessary
- Most VMware operations will work correctly

## Usage Instructions

### Quick Execution Policy Fix
```powershell
# For current user only (no admin required)
.\PowerCLI-Fix-ExecutionPolicy.ps1 -RunAsCurrentUser

# For system-wide fix (requires admin)
.\PowerCLI-Fix-ExecutionPolicy.ps1 -RunAsAdmin

# Just check current status
.\PowerCLI-Fix-ExecutionPolicy.ps1 -ShowStatus
```

### Complete PowerCLI Reset
```powershell
# Run as Administrator for best results
.\PowerCLI-CleanupVersions.ps1
```

### Using VMwareGUITools Built-in Diagnostics
1. Open VMwareGUITools
2. Go to **Settings**
3. Click **"Test PowerCLI"** for detailed diagnostics
4. Use **"Run PowerCLI Cleanup"** for automatic fixes

## Prerequisites

- Windows PowerShell 5.1 or PowerShell 7+
- Internet connection for downloading modules
- Administrator privileges (recommended for some operations)

## Enhanced Error Handling in VMwareGUITools

The application now provides:
- **Smart Version Conflict Detection**: Automatically identifies incompatible module combinations
- **Fallback Loading**: Works around VMware.Vim v9.0 issues
- **Enhanced Diagnostics**: Detailed error reporting with specific solutions
- **Execution Policy Bypass**: Automatically sets appropriate policies within the application

## Troubleshooting Guide

### Error: "Meta-module load failed" + "execution policy"
1. Run: `.\PowerCLI-Fix-ExecutionPolicy.ps1`
2. Restart VMwareGUITools
3. If still failing, run as Administrator

### Error: "Version conflict detected" + VMware.Vim v9.0
1. Use VMwareGUITools Settings > "Run PowerCLI Cleanup"
2. **OR** run: `.\PowerCLI-CleanupVersions.ps1`
3. **Note**: Application will try to work around this automatically

### Error: "No VMware modules found"
```powershell
Install-Module -Name VMware.PowerCLI -Scope CurrentUser -Force
```

### Manual Reset (if all scripts fail)
```powershell
# Uninstall all VMware modules
Get-InstalledModule VMware.* | Uninstall-Module -AllVersions -Force

# Remove any remaining module directories
$modulePaths = $env:PSModulePath -split ';'
foreach ($path in $modulePaths) {
    if (Test-Path $path) {
        Get-ChildItem $path -Directory | Where-Object Name -like 'VMware*' | Remove-Item -Recurse -Force
    }
}

# Install latest PowerCLI
Install-Module -Name VMware.PowerCLI -Force -AllowClobber -Scope CurrentUser
```

## Technical Details

### Enhanced PowerShell Service Features

The PowerShell service in VMwareGUITools now includes:

1. **Enhanced Module Loading**: Multiple fallback strategies for loading PowerCLI modules
2. **Version Conflict Resolution**: Automatic detection and workaround for incompatible modules
3. **Execution Policy Management**: Automatic policy setting for the application session
4. **Assembly Loading**: Direct DLL loading for critical modules when standard loading fails

### Specific VMware.Vim v9.0 Handling

The application uses a sophisticated approach to handle the VMware.Vim v9.0 compatibility issue:

```csharp
// Detects version conflicts
if (vimMajorMinor == '9.0' && commonMajorMinor != '9.0') {
    // Strategy 1: Try to find compatible older versions
    // Strategy 2: Skip VMware.Vim entirely if needed
    // Strategy 3: Load without problematic module
}
```

### Execution Policy Bypass

The application automatically:
- Sets execution policy to `Bypass` for the current process
- Attempts to set `RemoteSigned` for current user (if possible)
- Provides detailed diagnostics about policy restrictions

## Support

### Built-in Application Support
- Use **Settings > Test PowerCLI** for detailed diagnostics
- Check application logs for specific error details
- Use **Settings > Run PowerCLI Cleanup** for automated fixes

### Script Support
If you continue to experience issues after running these scripts:

1. Check execution policy: `Get-ExecutionPolicy -List`
2. Verify PowerShell version: `$PSVersionTable.PSVersion`
3. Review module paths: `$env:PSModulePath -split ';'`
4. Check for Windows Updates and install the latest .NET Framework

### Special Cases

**VMware.Vim v9.0 Issue**: If this specific version conflict cannot be resolved, VMwareGUITools will automatically work around it. Most functionality will remain available.

**Corporate Environments**: If execution policies are locked by Group Policy, contact your IT administrator or use the application's built-in policy bypass features.

## Testing the Fix

After applying any fix, test PowerCLI functionality:

```powershell
# Test basic PowerCLI loading
Import-Module VMware.VimAutomation.Core
Get-PowerCLIVersion

# Test VMware operations (if you have a vCenter to connect to)
Set-PowerCLIConfiguration -InvalidCertificateAction Ignore -Confirm:$false
Connect-VIServer -Server your-vcenter.domain.com
```

## Prevention

To avoid future conflicts:

1. **Use single PowerCLI installation**: Don't install multiple versions
2. **Regular updates**: Use `Update-Module VMware.PowerCLI` instead of installing new versions
3. **Clean installs**: When upgrading, uninstall old versions first
4. **Set execution policy**: `Set-ExecutionPolicy -Scope CurrentUser RemoteSigned` 