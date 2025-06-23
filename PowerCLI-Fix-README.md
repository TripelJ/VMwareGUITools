# PowerCLI Version Conflict Fix

## Problem Description

You're experiencing a PowerCLI module version conflict where `VMware.Vim` v9.0.0 requires `VMware.VimAutomation.Common` but cannot load it properly due to multiple versions being installed.

**Your system has:**
- VMware.VimAutomation.Common: v13.3.0 and v13.2.0
- VMware.Vim: v9.0.0 and v8.2.0
- Multiple other VMware modules with conflicting versions

**The error:**
```
Failed to load PowerCLI: The required module 'VMware.VimAutomation.Common' is not loaded. 
Load the module or remove the module from 'RequiredModules' in the file 
'C:\Program Files\WindowsPowerShell\Modules\VMware.Vim\9.0.0.24798382\VMware.Vim.psd1'.
```

## Solutions

### Quick Fix (Recommended)

1. **Run the automated fix script:**
   ```powershell
   # Run PowerShell as Administrator
   .\PowerCLI-VersionFix.ps1
   ```
   
   Choose option 1 for a clean reinstall.

### Manual Fix

1. **Clean reinstall PowerCLI (Run as Administrator):**
   ```powershell
   # Remove all VMware modules
   Uninstall-Module VMware.PowerCLI -AllVersions -Force
   Uninstall-Module VMware.Vim -AllVersions -Force -ErrorAction SilentlyContinue
   
   # Remove any remaining VMware modules
   Get-Module -ListAvailable | Where-Object { $_.Name -like '*VMware*' } | ForEach-Object { 
       Uninstall-Module $_.Name -RequiredVersion $_.Version -Force -ErrorAction SilentlyContinue 
   }
   
   # Install fresh PowerCLI
   Install-Module VMware.PowerCLI -AllowClobber -Force
   
   # Test installation
   Import-Module VMware.VimAutomation.Core
   Get-PowerCLIVersion
   ```

2. **Alternative: Load specific compatible versions:**
   ```powershell
   # Load compatible v13.2.0 modules
   Import-Module VMware.VimAutomation.Common -RequiredVersion 13.2.0 -Force
   Import-Module VMware.VimAutomation.Core -RequiredVersion 13.2.0 -Force
   ```

## Code Changes Made

The following enhancements were made to handle version conflicts:

### 1. Enhanced PowerShell Service (`PowerShellService.cs`)

- Added `LoadPowerCLIWithVersionCompatibilityAsync()` method
- Enhanced module loading logic to find compatible version pairs
- Improved error handling with specific guidance for version conflicts
- Automatic cleanup of conflicting modules before loading

### 2. Improved Diagnostics (`SettingsViewModel.cs`)

- Enhanced diagnostic script to detect version conflicts
- Added version analysis showing all available module versions
- Improved error reporting with specific guidance for your issue
- Better solution recommendations based on detected problems

### 3. Automated Fix Script (`PowerCLI-VersionFix.ps1`)

- Interactive script to diagnose and fix PowerCLI issues
- Multiple fix options (clean reinstall, compatible loading, manual guidance)
- Safe execution with proper error handling
- Works for both administrator and standard user scenarios

## How the Fix Works

1. **Detection:** The code now detects when multiple VMware module versions exist
2. **Compatibility Matching:** It finds module versions that have matching major.minor version numbers
3. **Clean Loading:** Removes conflicting modules before loading compatible ones
4. **Fallback Options:** Provides multiple solutions if automatic fixes fail

## Testing the Fix

After applying the fix, test PowerCLI functionality:

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

1. **Use single PowerCLI installation:** Don't install multiple versions
2. **Regular updates:** Use `Update-Module VMware.PowerCLI` instead of installing new versions
3. **Clean installs:** When upgrading, uninstall old versions first

## Support

If you continue experiencing issues:

1. Run the diagnostic tool in the application settings
2. Check the enhanced error messages for specific guidance
3. Use the automated fix script for step-by-step resolution 