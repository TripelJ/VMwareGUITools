# PowerCLI Execution Policy Fix Script
# This script addresses execution policy issues that prevent PowerCLI modules from loading

param(
    [switch]$RunAsCurrentUser,
    [switch]$RunAsAdmin,
    [switch]$ShowStatus
)

Write-Host "PowerCLI Execution Policy Fix Script" -ForegroundColor Green
Write-Host "====================================" -ForegroundColor Green
Write-Host ""

# Function to check if running as administrator
function Test-Administrator {
    $user = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($user)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

# Function to display current execution policies
function Show-ExecutionPolicyStatus {
    Write-Host "Current Execution Policy Status:" -ForegroundColor Yellow
    Write-Host "================================" -ForegroundColor Yellow
    
    $scopes = @('Process', 'CurrentUser', 'LocalMachine', 'MachinePolicy', 'UserPolicy')
    foreach ($scope in $scopes) {
        try {
            $policy = Get-ExecutionPolicy -Scope $scope -ErrorAction SilentlyContinue
            $color = switch ($policy) {
                'Restricted' { 'Red' }
                'AllSigned' { 'Yellow' }
                'RemoteSigned' { 'Green' }
                'Unrestricted' { 'Green' }
                'Bypass' { 'Green' }
                default { 'White' }
            }
            Write-Host "  $($scope.PadRight(15)): $policy" -ForegroundColor $color
        } catch {
            Write-Host "  $($scope.PadRight(15)): Unable to read" -ForegroundColor Gray
        }
    }
    Write-Host ""
}

# Show current status if requested
if ($ShowStatus) {
    Show-ExecutionPolicyStatus
    exit 0
}

# Check current admin status
$isAdmin = Test-Administrator
Write-Host "Running as Administrator: $isAdmin" -ForegroundColor $(if ($isAdmin) { 'Green' } else { 'Yellow' })
Write-Host ""

# Show current execution policies
Show-ExecutionPolicyStatus

# Determine what actions to take
$restrictivePolicies = @('Restricted', 'AllSigned')
$needsFix = $false

$currentProcess = Get-ExecutionPolicy -Scope Process -ErrorAction SilentlyContinue
$currentUser = Get-ExecutionPolicy -Scope CurrentUser -ErrorAction SilentlyContinue
$localMachine = Get-ExecutionPolicy -Scope LocalMachine -ErrorAction SilentlyContinue

if ($currentProcess -in $restrictivePolicies -or 
    $currentUser -in $restrictivePolicies -or 
    ($localMachine -in $restrictivePolicies -and $isAdmin)) {
    $needsFix = $true
}

if (-not $needsFix) {
    Write-Host "✅ Execution policies appear to be properly configured for PowerCLI!" -ForegroundColor Green
    Write-Host ""
    Write-Host "If you're still experiencing issues, they may be related to:" -ForegroundColor Yellow
    Write-Host "  • Module version conflicts (use PowerCLI-CleanupVersions.ps1)" -ForegroundColor Yellow
    Write-Host "  • Corrupted module installations" -ForegroundColor Yellow
    Write-Host "  • Network or file system permissions" -ForegroundColor Yellow
    exit 0
}

Write-Host "⚠️  Execution policy fixes needed!" -ForegroundColor Yellow
Write-Host ""

# Apply fixes based on parameters or prompt user
if ($RunAsCurrentUser -or (-not $RunAsAdmin -and -not $isAdmin)) {
    Write-Host "Applying CurrentUser execution policy fix..." -ForegroundColor Yellow
    try {
        Set-ExecutionPolicy -ExecutionPolicy RemoteSigned -Scope CurrentUser -Force
        Write-Host "✅ Set CurrentUser execution policy to RemoteSigned" -ForegroundColor Green
    } catch {
        Write-Host "❌ Failed to set CurrentUser execution policy: $($_.Exception.Message)" -ForegroundColor Red
    }
}

if ($RunAsAdmin -or $isAdmin) {
    Write-Host "Applying LocalMachine execution policy fix (requires admin)..." -ForegroundColor Yellow
    try {
        Set-ExecutionPolicy -ExecutionPolicy RemoteSigned -Scope LocalMachine -Force
        Write-Host "✅ Set LocalMachine execution policy to RemoteSigned" -ForegroundColor Green
    } catch {
        Write-Host "❌ Failed to set LocalMachine execution policy: $($_.Exception.Message)" -ForegroundColor Red
        if (-not $isAdmin) {
            Write-Host "   (This is expected when not running as Administrator)" -ForegroundColor Gray
        }
    }
}

# Always set process scope (doesn't require admin)
try {
    Set-ExecutionPolicy -ExecutionPolicy Bypass -Scope Process -Force
    Write-Host "✅ Set Process execution policy to Bypass" -ForegroundColor Green
} catch {
    Write-Host "❌ Failed to set Process execution policy: $($_.Exception.Message)" -ForegroundColor Red
}

Write-Host ""
Write-Host "Updated Execution Policy Status:" -ForegroundColor Yellow
Write-Host "==============================" -ForegroundColor Yellow
Show-ExecutionPolicyStatus

# Test PowerCLI module loading
Write-Host "Testing PowerCLI module loading..." -ForegroundColor Yellow
try {
    $vmwareModules = Get-Module -ListAvailable -Name '*VMware*' | Select-Object Name, Version -First 5
    if ($vmwareModules) {
        Write-Host "✅ PowerCLI modules found:" -ForegroundColor Green
        foreach ($module in $vmwareModules) {
            Write-Host "   • $($module.Name) v$($module.Version)" -ForegroundColor Gray
        }
        
        # Try to import a core module
        try {
            Import-Module VMware.VimAutomation.Core -Force -ErrorAction Stop
            Write-Host "✅ Successfully imported VMware.VimAutomation.Core" -ForegroundColor Green
        } catch {
            Write-Host "⚠️  Could not import VMware.VimAutomation.Core: $($_.Exception.Message)" -ForegroundColor Yellow
            Write-Host "   This may indicate version conflicts. Run PowerCLI-CleanupVersions.ps1" -ForegroundColor Yellow
        }
    } else {
        Write-Host "❌ No PowerCLI modules found. Install with:" -ForegroundColor Red
        Write-Host "   Install-Module -Name VMware.PowerCLI -Scope CurrentUser" -ForegroundColor White
    }
} catch {
    Write-Host "❌ Error testing PowerCLI modules: $($_.Exception.Message)" -ForegroundColor Red
}

Write-Host ""
Write-Host "Execution policy fix completed!" -ForegroundColor Green
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Cyan
Write-Host "  1. Restart VMwareGUITools application" -ForegroundColor White
Write-Host "  2. If issues persist, run PowerCLI-CleanupVersions.ps1" -ForegroundColor White
Write-Host "  3. For version conflicts, use the 'Run PowerCLI Cleanup' button in Settings" -ForegroundColor White 