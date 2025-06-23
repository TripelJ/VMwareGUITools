# PowerCLI Version Conflict Fix Script
# This script resolves version conflicts with VMware PowerCLI modules
# Run this script as Administrator for best results

Write-Host "=" * 60 -ForegroundColor Cyan
Write-Host "VMware PowerCLI Version Conflict Fix" -ForegroundColor Yellow
Write-Host "=" * 60 -ForegroundColor Cyan
Write-Host ""

# Check if running as Administrator
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole] "Administrator")

if (-not $isAdmin) {
    Write-Warning "This script is not running as Administrator. Some operations may fail."
    Write-Host "For best results, run PowerShell as Administrator and re-run this script." -ForegroundColor Yellow
    Write-Host ""
}

Write-Host "Current PowerShell Version: $($PSVersionTable.PSVersion)" -ForegroundColor Green
Write-Host "Running as Administrator: $isAdmin" -ForegroundColor Green
Write-Host ""

# Step 1: Analyze current VMware modules
Write-Host "Step 1: Analyzing current VMware modules..." -ForegroundColor Cyan

try {
    $vmwareModules = Get-Module -ListAvailable | Where-Object { $_.Name -like '*VMware*' } | 
                     Sort-Object Name, Version -Descending
    
    if ($vmwareModules) {
        Write-Host "Found VMware modules:" -ForegroundColor Yellow
        $vmwareModules | Group-Object Name | ForEach-Object {
            $moduleName = $_.Name
            $versions = $_.Group | Select-Object -ExpandProperty Version | Sort-Object -Descending
            
            Write-Host "  $moduleName" -ForegroundColor White
            foreach ($version in $versions) {
                Write-Host "    - v$version" -ForegroundColor Gray
            }
            
            if ($versions.Count -gt 1) {
                Write-Host "    ⚠️  Multiple versions detected!" -ForegroundColor Red
            }
        }
    } else {
        Write-Host "No VMware modules found." -ForegroundColor Red
        Write-Host "You may need to install PowerCLI first." -ForegroundColor Yellow
    }
} catch {
    Write-Error "Failed to analyze modules: $($_.Exception.Message)"
}

Write-Host ""

# Step 2: Test current PowerCLI loading
Write-Host "Step 2: Testing current PowerCLI loading..." -ForegroundColor Cyan

try {
    # Remove any loaded VMware modules first
    $loadedModules = Get-Module | Where-Object { $_.Name -like '*VMware*' }
    if ($loadedModules) {
        Write-Host "Removing currently loaded VMware modules..."
        $loadedModules | Remove-Module -Force
    }
    
    # Try to load PowerCLI
    Import-Module VMware.VimAutomation.Core -ErrorAction Stop
    $version = Get-PowerCLIVersion -ErrorAction Stop
    Write-Host "✅ PowerCLI loaded successfully: $($version.ProductLine)" -ForegroundColor Green
    $needsFix = $false
} catch {
    Write-Host "❌ Failed to load PowerCLI: $($_.Exception.Message)" -ForegroundColor Red
    $needsFix = $true
}

Write-Host ""

# Step 3: Offer solutions
if ($needsFix) {
    Write-Host "Step 3: Fixing PowerCLI module conflicts..." -ForegroundColor Cyan
    
    $choice = Read-Host @"
Choose a fix option:
1. Clean reinstall (removes all VMware modules and reinstalls PowerCLI)
2. Try loading compatible versions only
3. Manual cleanup guidance
4. Exit without changes

Enter your choice (1-4)
"@

    switch ($choice) {
        "1" {
            Write-Host "Performing clean reinstall..." -ForegroundColor Yellow
            
            try {
                # Remove all VMware modules
                Write-Host "Removing all VMware modules..."
                $allVMwareModules = Get-Module -ListAvailable | Where-Object { $_.Name -like '*VMware*' }
                
                foreach ($module in $allVMwareModules) {
                    try {
                        Write-Host "  Removing $($module.Name) v$($module.Version)..."
                        Uninstall-Module -Name $module.Name -RequiredVersion $module.Version -Force -ErrorAction SilentlyContinue
                    } catch {
                        Write-Warning "Could not remove $($module.Name) v$($module.Version): $($_.Exception.Message)"
                    }
                }
                
                # Install PowerCLI
                Write-Host "Installing PowerCLI..."
                Install-Module -Name VMware.PowerCLI -AllowClobber -Force -Scope CurrentUser
                
                # Test the installation
                Import-Module VMware.VimAutomation.Core -Force
                $version = Get-PowerCLIVersion
                Write-Host "✅ PowerCLI installed and loaded successfully: $($version.ProductLine)" -ForegroundColor Green
                
            } catch {
                Write-Error "Failed during clean reinstall: $($_.Exception.Message)"
                Write-Host "You may need to run this script as Administrator." -ForegroundColor Yellow
            }
        }
        
        "2" {
            Write-Host "Attempting to load compatible versions..." -ForegroundColor Yellow
            
            try {
                # Get available modules
                $coreModules = Get-Module -ListAvailable -Name 'VMware.VimAutomation.Core' | Sort-Object Version -Descending
                $commonModules = Get-Module -ListAvailable -Name 'VMware.VimAutomation.Common' | Sort-Object Version -Descending
                
                $loaded = $false
                foreach ($coreModule in $coreModules) {
                    $majorMinor = "$($coreModule.Version.Major).$($coreModule.Version.Minor)"
                    $compatibleCommon = $commonModules | Where-Object { 
                        "$($_.Version.Major).$($_.Version.Minor)" -eq $majorMinor 
                    } | Select-Object -First 1
                    
                    if ($compatibleCommon) {
                        try {
                            Write-Host "  Trying Core v$($coreModule.Version) with Common v$($compatibleCommon.Version)..."
                            
                            # Remove loaded modules
                            Get-Module | Where-Object { $_.Name -like '*VMware*' } | Remove-Module -Force -ErrorAction SilentlyContinue
                            
                            # Load compatible versions
                            Import-Module $compatibleCommon.Path -Force -ErrorAction Stop
                            Import-Module $coreModule.Path -Force -ErrorAction Stop
                            
                            $version = Get-PowerCLIVersion -ErrorAction Stop
                            Write-Host "✅ Successfully loaded compatible PowerCLI versions!" -ForegroundColor Green
                            Write-Host "   Core: v$($coreModule.Version)" -ForegroundColor Green
                            Write-Host "   Common: v$($compatibleCommon.Version)" -ForegroundColor Green
                            Write-Host "   PowerCLI: $($version.ProductLine)" -ForegroundColor Green
                            
                            $loaded = $true
                            break
                        } catch {
                            Write-Host "  Failed: $($_.Exception.Message)" -ForegroundColor Red
                            continue
                        }
                    }
                }
                
                if (-not $loaded) {
                    Write-Host "❌ Could not find compatible module versions." -ForegroundColor Red
                    Write-Host "Consider option 1 (clean reinstall) instead." -ForegroundColor Yellow
                }
                
            } catch {
                Write-Error "Failed to load compatible versions: $($_.Exception.Message)"
            }
        }
        
        "3" {
            Write-Host @"

Manual Cleanup Guidance:
========================

1. Open PowerShell as Administrator

2. List all VMware modules:
   Get-Module -ListAvailable | Where-Object { `$_.Name -like '*VMware*' } | Select-Object Name, Version, Path

3. Remove specific problematic versions:
   Uninstall-Module VMware.Vim -RequiredVersion 9.0.0.24798382 -Force
   
4. Or remove all and reinstall:
   Get-Module -ListAvailable | Where-Object { `$_.Name -like '*VMware*' } | ForEach-Object { 
       Uninstall-Module `$_.Name -RequiredVersion `$_.Version -Force -ErrorAction SilentlyContinue 
   }
   Install-Module VMware.PowerCLI -AllowClobber -Force

5. Test the installation:
   Import-Module VMware.VimAutomation.Core
   Get-PowerCLIVersion

"@ -ForegroundColor Yellow
        }
        
        "4" {
            Write-Host "Exiting without making changes." -ForegroundColor Yellow
        }
        
        default {
            Write-Host "Invalid choice. Exiting." -ForegroundColor Red
        }
    }
} else {
    Write-Host "✅ PowerCLI is working correctly. No fixes needed!" -ForegroundColor Green
}

Write-Host ""
Write-Host "Script completed. Press Enter to exit..." -ForegroundColor Cyan
Read-Host 