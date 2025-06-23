using System.Management.Automation;
using System.Management.Automation.Runspaces;
using Microsoft.Extensions.Logging;

namespace VMwareGUITools.Infrastructure.PowerShell;

/// <summary>
/// PowerCLI diagnostics and repair utility
/// </summary>
public class PowerCLIDiagnostics
{
    private readonly ILogger<PowerCLIDiagnostics> _logger;

    public PowerCLIDiagnostics(ILogger<PowerCLIDiagnostics> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Runs comprehensive PowerCLI diagnostics
    /// </summary>
    public async Task<PowerCLIDiagnosticResult> RunDiagnosticsAsync()
    {
        var result = new PowerCLIDiagnosticResult();
        
        try
        {
            _logger.LogInformation("Running PowerCLI diagnostics...");

            // Test 1: PowerShell execution policy
            await CheckExecutionPolicyAsync(result);

            // Test 2: PowerCLI module availability
            await CheckPowerCLIModulesAsync(result);

            // Test 3: Module version conflicts
            await CheckModuleVersionConflictsAsync(result);

            // Test 4: PowerShell version compatibility
            await CheckPowerShellVersionAsync(result);

            // Test 5: Basic PowerCLI functionality
            await TestBasicPowerCLIFunctionalityAsync(result);

            // Test 6: Network and proxy settings
            await CheckNetworkSettingsAsync(result);

            result.OverallStatus = result.Issues.Count == 0 ? "Healthy" : "Issues Found";
            result.CompletedAt = DateTime.UtcNow;

            _logger.LogInformation("PowerCLI diagnostics completed. Status: {Status}, Issues: {IssueCount}", 
                result.OverallStatus, result.Issues.Count);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PowerCLI diagnostics failed");
            result.Issues.Add(new DiagnosticIssue
            {
                Severity = IssueSeverity.Critical,
                Category = "Diagnostics",
                Description = $"Diagnostics failed: {ex.Message}",
                Recommendation = "Check system configuration and try again"
            });
            result.OverallStatus = "Failed";
            return result;
        }
    }

    /// <summary>
    /// Attempts to automatically fix common PowerCLI issues
    /// </summary>
    public async Task<PowerCLIRepairResult> RepairIssuesAsync(List<DiagnosticIssue> issues)
    {
        var result = new PowerCLIRepairResult();
        
        try
        {
            _logger.LogInformation("Starting PowerCLI repair for {IssueCount} issues", issues.Count);

            foreach (var issue in issues.Where(i => i.CanAutoFix))
            {
                try
                {
                    var repairSuccess = await RepairIssueAsync(issue);
                    if (repairSuccess)
                    {
                        result.ActionsPerformed.Add($"Fixed: {issue.Description}");
                        _logger.LogInformation("Successfully repaired issue: {Description}", issue.Description);
                    }
                    else
                    {
                        result.Issues.Add($"Failed to repair: {issue.Description}");
                        _logger.LogWarning("Failed to repair issue: {Description}", issue.Description);
                    }
                }
                catch (Exception ex)
                {
                    result.Issues.Add($"Error repairing '{issue.Description}': {ex.Message}");
                    _logger.LogError(ex, "Error repairing issue: {Description}", issue.Description);
                }
            }

            result.IsSuccessful = result.Issues.Count == 0;
            _logger.LogInformation("PowerCLI repair completed. Success: {Success}, Actions: {ActionCount}", 
                result.IsSuccessful, result.ActionsPerformed.Count);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PowerCLI repair failed");
            result.IsSuccessful = false;
            result.ErrorMessage = ex.Message;
            return result;
        }
    }

    private async Task CheckExecutionPolicyAsync(PowerCLIDiagnosticResult result)
    {
        try
        {
            using var powerShell = System.Management.Automation.PowerShell.Create();
            powerShell.AddScript(@"
                $policies = @{
                    'CurrentUser' = Get-ExecutionPolicy -Scope CurrentUser
                    'LocalMachine' = Get-ExecutionPolicy -Scope LocalMachine
                    'Process' = Get-ExecutionPolicy -Scope Process
                }
                return $policies
            ");

            var psResults = powerShell.Invoke();
            
            if (psResults != null && psResults.Count > 0 && psResults[0] is Hashtable policies)
            {
                var currentUserPolicy = policies["CurrentUser"]?.ToString();
                var localMachinePolicy = policies["LocalMachine"]?.ToString();

                if (currentUserPolicy == "Restricted" || currentUserPolicy == "Undefined")
                {
                    result.Issues.Add(new DiagnosticIssue
                    {
                        Severity = IssueSeverity.High,
                        Category = "Execution Policy",
                        Description = $"CurrentUser execution policy is '{currentUserPolicy}' which prevents PowerCLI modules from loading",
                        Recommendation = "Set execution policy to RemoteSigned for CurrentUser scope",
                        CanAutoFix = true,
                        FixCommand = "Set-ExecutionPolicy -ExecutionPolicy RemoteSigned -Scope CurrentUser -Force"
                    });
                }

                result.Details.Add("Execution Policy", new Dictionary<string, object>
                {
                    ["CurrentUser"] = currentUserPolicy ?? "Unknown",
                    ["LocalMachine"] = localMachinePolicy ?? "Unknown"
                });
            }
        }
        catch (Exception ex)
        {
            result.Issues.Add(new DiagnosticIssue
            {
                Severity = IssueSeverity.Medium,
                Category = "Execution Policy",
                Description = $"Failed to check execution policy: {ex.Message}",
                Recommendation = "Manually check PowerShell execution policy"
            });
        }
    }

    private async Task CheckPowerCLIModulesAsync(PowerCLIDiagnosticResult result)
    {
        try
        {
            using var powerShell = System.Management.Automation.PowerShell.Create();
            powerShell.AddScript(@"
                $modules = Get-Module -ListAvailable VMware.* | Select-Object Name, Version, Path
                $loadedModules = Get-Module VMware.* | Select-Object Name, Version
                
                return @{
                    Available = $modules
                    Loaded = $loadedModules
                }
            ");

            var psResults = powerShell.Invoke();
            
            if (psResults != null && psResults.Count > 0 && psResults[0] is Hashtable moduleInfo)
            {
                var availableModules = ((object[])moduleInfo["Available"]).Cast<PSObject>().ToList();
                var loadedModules = ((object[])moduleInfo["Loaded"]).Cast<PSObject>().ToList();

                if (!availableModules.Any(m => m.Properties["Name"]?.Value?.ToString() == "VMware.VimAutomation.Core"))
                {
                    result.Issues.Add(new DiagnosticIssue
                    {
                        Severity = IssueSeverity.Critical,
                        Category = "PowerCLI Modules",
                        Description = "VMware.VimAutomation.Core module is not installed",
                        Recommendation = "Install PowerCLI modules",
                        CanAutoFix = true,
                        FixCommand = "Install-Module VMware.PowerCLI -Scope CurrentUser -AllowClobber -Force"
                    });
                }

                // Check for version conflicts
                var coreModules = availableModules
                    .Where(m => m.Properties["Name"]?.Value?.ToString() == "VMware.VimAutomation.Core")
                    .Select(m => m.Properties["Version"]?.Value?.ToString())
                    .Distinct()
                    .ToList();

                if (coreModules.Count > 1)
                {
                    result.Issues.Add(new DiagnosticIssue
                    {
                        Severity = IssueSeverity.Medium,
                        Category = "PowerCLI Modules",
                        Description = $"Multiple PowerCLI Core versions found: {string.Join(", ", coreModules)}",
                        Recommendation = "Clean up old PowerCLI versions to avoid conflicts",
                        CanAutoFix = false
                    });
                }

                result.Details.Add("PowerCLI Modules", new Dictionary<string, object>
                {
                    ["AvailableCount"] = availableModules.Count,
                    ["LoadedCount"] = loadedModules.Count,
                    ["CoreVersions"] = coreModules
                });
            }
        }
        catch (Exception ex)
        {
            result.Issues.Add(new DiagnosticIssue
            {
                Severity = IssueSeverity.High,
                Category = "PowerCLI Modules",
                Description = $"Failed to check PowerCLI modules: {ex.Message}",
                Recommendation = "Manually verify PowerCLI installation"
            });
        }
    }

    private async Task CheckModuleVersionConflictsAsync(PowerCLIDiagnosticResult result)
    {
        try
        {
            using var powerShell = System.Management.Automation.PowerShell.Create();
            powerShell.AddScript(@"
                $vmwareModules = Get-Module -ListAvailable VMware.* | Group-Object Name
                $conflicts = @()
                
                foreach ($moduleGroup in $vmwareModules) {
                    if ($moduleGroup.Count -gt 1) {
                        $versions = $moduleGroup.Group | Select-Object -ExpandProperty Version | Sort-Object -Descending
                        $conflicts += [PSCustomObject]@{
                            Name = $moduleGroup.Name
                            Versions = $versions
                            Count = $moduleGroup.Count
                        }
                    }
                }
                
                return $conflicts
            ");

            var psResults = powerShell.Invoke();
            
            if (psResults != null && psResults.Count > 0)
            {
                var conflicts = psResults.Cast<PSObject>().ToList();
                
                foreach (var conflict in conflicts)
                {
                    var moduleName = conflict.Properties["Name"]?.Value?.ToString();
                    var count = Convert.ToInt32(conflict.Properties["Count"]?.Value);
                    
                    result.Issues.Add(new DiagnosticIssue
                    {
                        Severity = IssueSeverity.Medium,
                        Category = "Module Conflicts",
                        Description = $"Module '{moduleName}' has {count} versions installed which may cause conflicts",
                        Recommendation = "Remove older versions or use specific version imports",
                        CanAutoFix = false
                    });
                }
            }
        }
        catch (Exception ex)
        {
            result.Issues.Add(new DiagnosticIssue
            {
                Severity = IssueSeverity.Low,
                Category = "Module Conflicts",
                Description = $"Failed to check for module conflicts: {ex.Message}",
                Recommendation = "Manually check for PowerCLI version conflicts"
            });
        }
    }

    private async Task CheckPowerShellVersionAsync(PowerCLIDiagnosticResult result)
    {
        try
        {
            using var powerShell = System.Management.Automation.PowerShell.Create();
            powerShell.AddScript("$PSVersionTable");

            var psResults = powerShell.Invoke();
            
            if (psResults != null && psResults.Count > 0 && psResults[0] is Hashtable versionTable)
            {
                var psVersion = versionTable["PSVersion"]?.ToString();
                var psEdition = versionTable["PSEdition"]?.ToString();

                result.Details.Add("PowerShell Version", new Dictionary<string, object>
                {
                    ["Version"] = psVersion ?? "Unknown",
                    ["Edition"] = psEdition ?? "Unknown"
                });

                // Check if using PowerShell Core (7+) which might have compatibility issues
                if (psEdition == "Core" && psVersion?.StartsWith("7") == true)
                {
                    result.Issues.Add(new DiagnosticIssue
                    {
                        Severity = IssueSeverity.Medium,
                        Category = "PowerShell Version",
                        Description = "PowerShell 7+ (Core) may have compatibility issues with some PowerCLI features",
                        Recommendation = "Consider using Windows PowerShell 5.1 for maximum compatibility",
                        CanAutoFix = false
                    });
                }
            }
        }
        catch (Exception ex)
        {
            result.Issues.Add(new DiagnosticIssue
            {
                Severity = IssueSeverity.Low,
                Category = "PowerShell Version",
                Description = $"Failed to check PowerShell version: {ex.Message}",
                Recommendation = "Manually verify PowerShell version compatibility"
            });
        }
    }

    private async Task TestBasicPowerCLIFunctionalityAsync(PowerCLIDiagnosticResult result)
    {
        try
        {
            using var powerShell = System.Management.Automation.PowerShell.Create();
            powerShell.AddScript(@"
                try {
                    Import-Module VMware.VimAutomation.Core -ErrorAction Stop
                    $version = Get-PowerCLIVersion -ErrorAction Stop
                    return @{
                        Success = $true
                        Version = $version.PowerCLIVersion
                        Build = $version.Build
                    }
                } catch {
                    return @{
                        Success = $false
                        Error = $_.Exception.Message
                    }
                }
            ");

            var psResults = powerShell.Invoke();
            
            if (psResults != null && psResults.Count > 0 && psResults[0] is Hashtable testResult)
            {
                var success = (bool)testResult["Success"];
                
                if (success)
                {
                    var version = testResult["Version"]?.ToString();
                    result.Details.Add("PowerCLI Test", new Dictionary<string, object>
                    {
                        ["Status"] = "Success",
                        ["Version"] = version ?? "Unknown"
                    });
                }
                else
                {
                    var error = testResult["Error"]?.ToString();
                    result.Issues.Add(new DiagnosticIssue
                    {
                        Severity = IssueSeverity.Critical,
                        Category = "PowerCLI Functionality",
                        Description = $"PowerCLI basic functionality test failed: {error}",
                        Recommendation = "Reinstall PowerCLI or check module integrity",
                        CanAutoFix = false
                    });
                }
            }
        }
        catch (Exception ex)
        {
            result.Issues.Add(new DiagnosticIssue
            {
                Severity = IssueSeverity.High,
                Category = "PowerCLI Functionality",
                Description = $"Failed to test PowerCLI functionality: {ex.Message}",
                Recommendation = "Check PowerCLI installation and configuration"
            });
        }
    }

    private async Task CheckNetworkSettingsAsync(PowerCLIDiagnosticResult result)
    {
        try
        {
            using var powerShell = System.Management.Automation.PowerShell.Create();
            powerShell.AddScript(@"
                try {
                    # Check proxy settings
                    $proxy = [System.Net.WebRequest]::DefaultWebProxy
                    $proxyAddress = if ($proxy -and $proxy.Address) { $proxy.Address.ToString() } else { 'None' }
                    
                    # Check TLS settings
                    $tls = [System.Net.ServicePointManager]::SecurityProtocol
                    
                    return @{
                        ProxyAddress = $proxyAddress
                        TLSProtocol = $tls.ToString()
                        Success = $true
                    }
                } catch {
                    return @{
                        Success = $false
                        Error = $_.Exception.Message
                    }
                }
            ");

            var psResults = powerShell.Invoke();
            
            if (psResults != null && psResults.Count > 0 && psResults[0] is Hashtable networkInfo)
            {
                var success = (bool)networkInfo["Success"];
                
                if (success)
                {
                    result.Details.Add("Network Settings", new Dictionary<string, object>
                    {
                        ["Proxy"] = networkInfo["ProxyAddress"]?.ToString() ?? "Unknown",
                        ["TLS"] = networkInfo["TLSProtocol"]?.ToString() ?? "Unknown"
                    });
                }
            }
        }
        catch (Exception ex)
        {
            result.Issues.Add(new DiagnosticIssue
            {
                Severity = IssueSeverity.Low,
                Category = "Network Settings",
                Description = $"Failed to check network settings: {ex.Message}",
                Recommendation = "Manually verify network and proxy configuration"
            });
        }
    }

    private async Task<bool> RepairIssueAsync(DiagnosticIssue issue)
    {
        if (string.IsNullOrEmpty(issue.FixCommand))
            return false;

        try
        {
            using var powerShell = System.Management.Automation.PowerShell.Create();
            powerShell.AddScript(issue.FixCommand);
            
            var psResults = powerShell.Invoke();
            
            return powerShell.Streams.Error.Count == 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute repair command: {Command}", issue.FixCommand);
            return false;
        }
    }
}

/// <summary>
/// Result of PowerCLI diagnostics
/// </summary>
public class PowerCLIDiagnosticResult
{
    public string OverallStatus { get; set; } = "Running";
    public List<DiagnosticIssue> Issues { get; set; } = new();
    public Dictionary<string, Dictionary<string, object>> Details { get; set; } = new();
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}

/// <summary>
/// Represents a diagnostic issue found during PowerCLI diagnostics
/// </summary>
public class DiagnosticIssue
{
    public IssueSeverity Severity { get; set; }
    public string Category { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Recommendation { get; set; } = string.Empty;
    public bool CanAutoFix { get; set; }
    public string? FixCommand { get; set; }
}

/// <summary>
/// Severity levels for diagnostic issues
/// </summary>
public enum IssueSeverity
{
    Low,
    Medium,
    High,
    Critical
} 