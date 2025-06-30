#Requires -RunAsAdministrator

<#
.SYNOPSIS
    Installs and configures the VMware GUI Tools Windows Service
.DESCRIPTION
    This script installs the VMware GUI Tools service, configures PowerShell execution policy,
    and sets up the service for PowerCLI execution in the service context.
.EXAMPLE
    .\Install-VMwareService.ps1 -Action Install
    .\Install-VMwareService.ps1 -Action Uninstall
    .\Install-VMwareService.ps1 -Action Start
#>

param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("Install", "Uninstall", "Start", "Stop", "Restart", "Status")]
    [string]$Action,
    
    [string]$ServiceName = "VMwareGUIToolsService",
    [string]$ServiceDisplayName = "VMware GUI Tools Service",
    [string]$ServiceDescription = "VMware vSphere Infrastructure Management Service with PowerCLI Support",
    [string]$BinaryPath = ".\src\VMwareGUITools.Service\bin\Release\net8.0\VMwareGUITools.Service.exe",
    [string]$WorkingDirectory = $PWD.Path
)

function Write-Log {
    param([string]$Message, [string]$Level = "INFO")
    $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    Write-Host "[$timestamp] [$Level] $Message" -ForegroundColor $(
        switch ($Level) {
            "ERROR" { "Red" }
            "WARN" { "Yellow" }
            "SUCCESS" { "Green" }
            default { "White" }
        }
    )
}

function Test-ServiceExists {
    param([string]$Name)
    return (Get-Service -Name $Name -ErrorAction SilentlyContinue) -ne $null
}

function Install-Service {
    Write-Log "Installing VMware GUI Tools Service..."
    
    # Check if service already exists
    if (Test-ServiceExists -Name $ServiceName) {
        Write-Log "Service '$ServiceName' already exists. Uninstalling first..." -Level "WARN"
        Uninstall-Service
    }
    
    # Verify binary exists
    $fullBinaryPath = Join-Path $WorkingDirectory $BinaryPath
    if (-not (Test-Path $fullBinaryPath)) {
        Write-Log "Service binary not found at: $fullBinaryPath" -Level "ERROR"
        Write-Log "Please build the solution first: dotnet build -c Release" -Level "ERROR"
        exit 1
    }
    
    try {
        # Install the service
        Write-Log "Creating service '$ServiceName'..."
        New-Service -Name $ServiceName `
                   -DisplayName $ServiceDisplayName `
                   -Description $ServiceDescription `
                   -BinaryPathName $fullBinaryPath `
                   -StartupType Manual
        
        # Configure service for PowerCLI execution
        Configure-ServiceForPowerCLI
        
        Write-Log "Service '$ServiceName' installed successfully!" -Level "SUCCESS"
        Write-Log "You can now start the service with: .\Install-VMwareService.ps1 -Action Start"
        
    } catch {
        Write-Log "Failed to install service: $($_.Exception.Message)" -Level "ERROR"
        exit 1
    }
}

function Uninstall-Service {
    Write-Log "Uninstalling VMware GUI Tools Service..."
    
    if (-not (Test-ServiceExists -Name $ServiceName)) {
        Write-Log "Service '$ServiceName' does not exist." -Level "WARN"
        return
    }
    
    try {
        # Stop service if running
        $service = Get-Service -Name $ServiceName
        if ($service.Status -eq "Running") {
            Write-Log "Stopping service..."
            Stop-Service -Name $ServiceName -Force
            Start-Sleep -Seconds 3
        }
        
        # Remove service using sc.exe (compatible with all PowerShell versions)
        Write-Log "Removing service..."
        
        # Try Remove-Service first (PowerShell 6.0+), fallback to sc.exe for older versions
        try {
            if (Get-Command Remove-Service -ErrorAction SilentlyContinue) {
                Remove-Service -Name $ServiceName -ErrorAction Stop
            } else {
                # Fallback to sc.exe for Windows PowerShell 5.1 and older
                $scResult = & sc.exe delete $ServiceName
                if ($LASTEXITCODE -ne 0) {
                    throw "sc.exe delete failed with exit code: $LASTEXITCODE. Output: $($scResult -join ' ')"
                }
            }
        } catch {
            # Final fallback using WMI
            Write-Log "Remove-Service and sc.exe failed, trying WMI method..." -Level "WARN"
            $service = Get-WmiObject -Class Win32_Service -Filter "Name='$ServiceName'"
            if ($service) {
                $deleteResult = $service.Delete()
                if ($deleteResult.ReturnValue -ne 0) {
                    throw "WMI service deletion failed with return code: $($deleteResult.ReturnValue)"
                }
            } else {
                throw "Service not found for WMI deletion"
            }
        }
        
        Write-Log "Service '$ServiceName' uninstalled successfully!" -Level "SUCCESS"
        
    } catch {
        Write-Log "Failed to uninstall service: $($_.Exception.Message)" -Level "ERROR"
        exit 1
    }
}

function Start-VMwareService {
    Write-Log "Starting VMware GUI Tools Service..."
    
    if (-not (Test-ServiceExists -Name $ServiceName)) {
        Write-Log "Service '$ServiceName' is not installed." -Level "ERROR"
        exit 1
    }
    
    try {
        Start-Service -Name $ServiceName
        Start-Sleep -Seconds 2
        
        $service = Get-Service -Name $ServiceName
        if ($service.Status -eq "Running") {
            Write-Log "Service started successfully!" -Level "SUCCESS"
        } else {
            Write-Log "Service failed to start. Status: $($service.Status)" -Level "ERROR"
        }
        
    } catch {
        Write-Log "Failed to start service: $($_.Exception.Message)" -Level "ERROR"
        exit 1
    }
}

function Stop-VMwareService {
    Write-Log "Stopping VMware GUI Tools Service..."
    
    if (-not (Test-ServiceExists -Name $ServiceName)) {
        Write-Log "Service '$ServiceName' is not installed." -Level "ERROR"
        exit 1
    }
    
    try {
        Stop-Service -Name $ServiceName -Force
        Write-Log "Service stopped successfully!" -Level "SUCCESS"
        
    } catch {
        Write-Log "Failed to stop service: $($_.Exception.Message)" -Level "ERROR"
        exit 1
    }
}

function Restart-VMwareService {
    Stop-VMwareService
    Start-Sleep -Seconds 2
    Start-VMwareService
}

function Get-ServiceStatus {
    Write-Log "Checking VMware GUI Tools Service status..."
    
    if (-not (Test-ServiceExists -Name $ServiceName)) {
        Write-Log "Service '$ServiceName' is not installed." -Level "WARN"
        return
    }
    
    $service = Get-Service -Name $ServiceName
    Write-Log "Service Status: $($service.Status)"
    Write-Log "Service Start Type: $($service.StartType)"
    
    # Check recent logs
    $logPath = Join-Path $WorkingDirectory "logs"
    if (Test-Path $logPath) {
        $latestLog = Get-ChildItem -Path $logPath -Filter "vmware-service-*.txt" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($latestLog) {
            Write-Log "Latest log file: $($latestLog.FullName)"
            Write-Log "Last modified: $($latestLog.LastWriteTime)"
        }
    }
}

function Configure-ServiceForPowerCLI {
    Write-Log "Configuring PowerShell execution policy for service context..."
    
    try {
        # Set execution policy for LocalMachine (affects service accounts)
        Set-ExecutionPolicy -ExecutionPolicy RemoteSigned -Scope LocalMachine -Force
        Write-Log "Execution policy set to RemoteSigned for LocalMachine scope" -Level "SUCCESS"
        
        # Verify PowerCLI is available
        $powerCLIModule = Get-Module -ListAvailable -Name VMware.VimAutomation.Core
        if ($powerCLIModule) {
            Write-Log "PowerCLI module found: Version $($powerCLIModule.Version)" -Level "SUCCESS"
        } else {
            Write-Log "PowerCLI module not found. Install with: Install-Module VMware.PowerCLI -Scope AllUsers" -Level "WARN"
        }
        
    } catch {
        Write-Log "Failed to configure PowerShell for service: $($_.Exception.Message)" -Level "ERROR"
    }
}

# Main execution
Write-Log "VMware GUI Tools Service Management Script"
Write-Log "Action: $Action"

switch ($Action) {
    "Install" { Install-Service }
    "Uninstall" { Uninstall-Service }
    "Start" { Start-VMwareService }
    "Stop" { Stop-VMwareService }
    "Restart" { Restart-VMwareService }
    "Status" { Get-ServiceStatus }
    default { 
        Write-Log "Invalid action: $Action" -Level "ERROR"
        exit 1
    }
}

Write-Log "Operation completed." 