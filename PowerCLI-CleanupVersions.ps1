# PowerCLI Version Cleanup and Reinstall Script
# This script helps resolve version conflicts in VMware PowerCLI modules
# Run this as Administrator for best results

Write-Host "PowerCLI Version Cleanup and Reinstall Script" -ForegroundColor Green
Write-Host "=============================================" -ForegroundColor Green
Write-Host ""

# Check if running as Administrator
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole] 'Administrator')
if (-not $isAdmin) {
    Write-Warning "Not running as Administrator. Some operations may fail."
    Write-Host "For best results, run this script as Administrator." -ForegroundColor Yellow
    Write-Host ""
}

# Function to safely uninstall modules
function Uninstall-VMwareModuleSafely {
    param(
        [string]$ModuleName,
        [switch]$AllVersions
    )
    
    try {
        $installedModules = Get-InstalledModule -Name $ModuleName -AllVersions -ErrorAction SilentlyContinue
        if ($installedModules) {
            Write-Host "Uninstalling $ModuleName..." -ForegroundColor Yellow
            
            if ($AllVersions) {
                $installedModules | ForEach-Object {
                    try {
                        Write-Host "  Removing version $($_.Version)..." -ForegroundColor Gray
                        Uninstall-Module -Name $_.Name -RequiredVersion $_.Version -Force -ErrorAction Stop
                    } catch {
                        Write-Warning "  Failed to uninstall $($_.Name) v$($_.Version): $($_.Exception.Message)"
                    }
                }
            } else {
                try {
                    Uninstall-Module -Name $ModuleName -Force -ErrorAction Stop
                } catch {
                    Write-Warning "Failed to uninstall $ModuleName : $($_.Exception.Message)"
                }
            }
        } else {
            Write-Host "$ModuleName not found in installed modules" -ForegroundColor Gray
        }
    } catch {
        Write-Warning "Error checking/uninstalling $ModuleName : $($_.Exception.Message)"
    }
}

# Function to remove modules from file system
function Remove-VMwareModuleFiles {
    param([string]$ModuleName)
    
    $modulePaths = $env:PSModulePath -split ';'
    foreach ($path in $modulePaths) {
        $moduleDir = Join-Path $path $ModuleName
        if (Test-Path $moduleDir) {
            try {
                Write-Host "Removing directory: $moduleDir" -ForegroundColor Gray
                Remove-Item $moduleDir -Recurse -Force -ErrorAction Stop
            } catch {
                Write-Warning "Failed to remove directory $moduleDir : $($_.Exception.Message)"
            }
        }
    }
}

Write-Host "Step 1: Analyzing current VMware module installations..." -ForegroundColor Cyan
$vmwareModules = Get-Module -ListAvailable | Where-Object { $_.Name -like '*VMware*' } | Group-Object Name
Write-Host "Found $($vmwareModules.Count) unique VMware module types installed" -ForegroundColor Green

foreach ($moduleGroup in $vmwareModules) {
    $versions = $moduleGroup.Group | Select-Object Version | Sort-Object Version
    Write-Host "  $($moduleGroup.Name): $($versions.Count) versions" -ForegroundColor Gray
    $versions | ForEach-Object { Write-Host "    - v$($_.Version)" -ForegroundColor DarkGray }
}

Write-Host ""
Write-Host "Step 2: Removing old/conflicting VMware modules..." -ForegroundColor Cyan

# List of VMware modules to clean up (order matters for dependencies)
$modulesToCleanup = @(
    'VMware.PowerCLI',
    'VMware.Vim',
    'VMware.VimAutomation.Core',
    'VMware.VimAutomation.Common',
    'VMware.VimAutomation.Sdk',
    'VMware.VimAutomation.Cis.Core',
    'VMware.VimAutomation.Storage',
    'VMware.VimAutomation.Vds',
    'VMware.VimAutomation.License',
    'VMware.VimAutomation.Cloud',
    'VMware.VimAutomation.PCloud',
    'VMware.VimAutomation.vROps',
    'VMware.VimAutomation.Nsxt',
    'VMware.VimAutomation.HorizonView',
    'VMware.VimAutomation.Hcx',
    'VMware.VimAutomation.Srm',
    'VMware.VimAutomation.Security',
    'VMware.VimAutomation.Vmc',
    'VMware.VimAutomation.WorkloadManagement',
    'VMware.CloudServices',
    'VMware.ImageBuilder',
    'VMware.DeployAutomation',
    'VMware.VumAutomation'
)

# Get all VMware modules dynamically (in case new ones exist)
$allVMwareModules = Get-InstalledModule | Where-Object { $_.Name -like '*VMware*' } | Select-Object -ExpandProperty Name -Unique
$allModulesToCleanup = ($modulesToCleanup + $allVMwareModules) | Select-Object -Unique

foreach ($module in $allModulesToCleanup) {
    Uninstall-VMwareModuleSafely -ModuleName $module -AllVersions
}

Write-Host ""
Write-Host "Step 3: Cleaning up any remaining module files..." -ForegroundColor Cyan
foreach ($module in $allModulesToCleanup) {
    Remove-VMwareModuleFiles -ModuleName $module
}

Write-Host ""
Write-Host "Step 4: Installing latest PowerCLI..." -ForegroundColor Cyan

try {
    # Ensure PowerShell Gallery is trusted
    $psGallery = Get-PSRepository -Name PSGallery
    if ($psGallery.InstallationPolicy -ne 'Trusted') {
        Write-Host "Setting PSGallery as trusted..." -ForegroundColor Yellow
        Set-PSRepository -Name PSGallery -InstallationPolicy Trusted
    }
    
    Write-Host "Installing VMware.PowerCLI (this may take a few minutes)..." -ForegroundColor Yellow
    Install-Module -Name VMware.PowerCLI -AllowClobber -Force -SkipPublisherCheck
    
    Write-Host "PowerCLI installation completed!" -ForegroundColor Green
    
} catch {
    Write-Error "Failed to install PowerCLI: $($_.Exception.Message)"
    Write-Host "You may need to run this manually:" -ForegroundColor Yellow
    Write-Host "Install-Module -Name VMware.PowerCLI -AllowClobber -Force" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "Step 5: Verifying installation..." -ForegroundColor Cyan

try {
    Import-Module VMware.PowerCLI -Force
    $version = Get-PowerCLIVersion
    Write-Host "✅ PowerCLI successfully installed and loaded!" -ForegroundColor Green
    Write-Host "Version: $($version.PowerCLIVersion)" -ForegroundColor Green
    Write-Host "Build: $($version.Build)" -ForegroundColor Green
    
    Write-Host ""
    Write-Host "Installed modules:" -ForegroundColor Green
    Get-Module -Name "*VMware*" | Sort-Object Name | ForEach-Object {
        Write-Host "  ✓ $($_.Name) v$($_.Version)" -ForegroundColor Green
    }
    
} catch {
    Write-Warning "Verification failed: $($_.Exception.Message)"
    Write-Host "You may need to restart PowerShell and try importing PowerCLI manually." -ForegroundColor Yellow
}

Write-Host ""
Write-Host "Cleanup and reinstallation completed!" -ForegroundColor Green
Write-Host "If you're still experiencing issues, restart PowerShell/your application and try again." -ForegroundColor Yellow 