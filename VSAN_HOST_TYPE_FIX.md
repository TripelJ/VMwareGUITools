# vSAN Host Type Detection Fix

## Problem
Hosts in vSAN-enabled clusters were being detected as "STD" (Standard) instead of "vSAN" in the Infrastructure Overview, even though they were known to be vSAN nodes.

## Root Cause
The host discovery logic was hardcoded to always return `HostType.Standard` in multiple places:

1. **EnhancedVMwareConnectionService.cs** - PowerShell script returned hardcoded `'Standard'` type
2. **VSphereRestAPIService.cs** - Missing host type detection logic entirely  
3. **RestVMwareConnectionService.cs** - Hardcoded `HostType.Standard` in sample data

## Solution
Updated all three VMware connection services to properly detect vSAN hosts:

### EnhancedVMwareConnectionService.cs
- Added vSAN cluster detection in PowerShell script using `Get-VsanClusterConfiguration`
- Modified host discovery to set type based on cluster vSAN status: `if ($vsanEnabled) { 'VsanNode' } else { 'Standard' }`
- Added proper enum mapping from string to `HostType` enum
- Enhanced logging to show vSAN host count

### VSphereRestAPIService.cs
- Added vSAN detection by checking for vSAN datastores (type = "VSAN")
- Updated both `GetHostsInClusterAsync` and `GetHostDetailInfoAsync` methods
- Added proper host type detection logic for REST API calls

### RestVMwareConnectionService.cs
- Updated simulation logic to detect vSAN based on naming conventions
- Modified both host discovery and detail methods

## How It Works
1. **PowerCLI Method**: Checks if cluster has vSAN enabled using `Get-VsanClusterConfiguration`
2. **REST API Method**: Detects vSAN by looking for datastores with type "VSAN"
3. **Host Type Assignment**: Sets `HostType.VsanNode` for hosts in vSAN clusters, `HostType.Standard` for others

## UI Display
- vSAN hosts now show "vSAN" indicator with purple color (#9C27B0)
- Standard hosts continue to show "STD" indicator with blue-gray color (#607D8B)

## Testing
1. Connect to a vCenter with vSAN-enabled clusters
2. Navigate to Infrastructure Overview
3. Verify that hosts in vSAN clusters now show "vSAN" instead of "STD"
4. Check logs for vSAN detection messages

## Fallback Behavior
- If vSAN detection fails for any reason, hosts default to `HostType.Standard`
- PowerShell errors are caught and logged without breaking host discovery
- REST API failures fall back to Standard type with debug logging 