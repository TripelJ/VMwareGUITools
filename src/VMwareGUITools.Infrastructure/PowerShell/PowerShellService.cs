using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace VMwareGUITools.Infrastructure.PowerShell;

/// <summary>
/// Implementation of PowerShell service for executing PowerCLI commands
/// </summary>
public class PowerShellService : IPowerShellService, IDisposable
{
    private readonly ILogger<PowerShellService> _logger;
    private readonly PowerShellOptions _options;
    private RunspacePool? _runspacePool;
    private bool _isInitialized = false;
    private readonly SemaphoreSlim _initializationSemaphore = new(1, 1);

    public PowerShellService(ILogger<PowerShellService> logger, IOptions<PowerShellOptions> options)
    {
        _logger = logger;
        _options = options.Value;
    }

    public async Task<PowerShellResult> ExecuteScriptAsync(string script, Dictionary<string, object>? parameters = null, int timeoutSeconds = 300, CancellationToken cancellationToken = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = new PowerShellResult();

        try
        {
            await EnsureInitializedAsync(cancellationToken);

            using var powerShell = System.Management.Automation.PowerShell.Create();
            powerShell.RunspacePool = _runspacePool;

            // Add the script
            powerShell.AddScript(script);

            // Add parameters if provided
            if (parameters != null)
            {
                foreach (var param in parameters)
                {
                    powerShell.AddParameter(param.Key, param.Value);
                }
            }

            // Set up timeout
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            _logger.LogDebug("Executing PowerShell script: {Script}", script.Length > 100 ? script.Substring(0, 100) + "..." : script);

            // Execute the script
            var invokeTask = Task.Run(() =>
            {
                var psObjects = powerShell.Invoke();
                return psObjects;
            }, combinedCts.Token);

            var psResults = await invokeTask;

            // Collect results
            result.Objects = psResults?.Cast<object>().ToList() ?? new List<object>();
            result.Output = string.Join(Environment.NewLine, result.Objects.Select(o => o?.ToString() ?? ""));

            // Collect warnings and verbose output
            if (powerShell.Streams.Warning.Count > 0)
            {
                result.Warnings = powerShell.Streams.Warning.Select(w => w.Message).ToList();
            }

            if (powerShell.Streams.Verbose.Count > 0)
            {
                result.Verbose = powerShell.Streams.Verbose.Select(v => v.Message).ToList();
            }

            // Check for errors
            if (powerShell.Streams.Error.Count > 0)
            {
                var errors = powerShell.Streams.Error.Select(e => e.ToString()).ToList();
                result.ErrorOutput = string.Join(Environment.NewLine, errors);
                result.IsSuccess = false;
                _logger.LogWarning("PowerShell script execution completed with errors: {Errors}", string.Join(", ", errors));
            }
            else
            {
                result.IsSuccess = true;
                _logger.LogDebug("PowerShell script executed successfully in {ElapsedMs}ms", stopwatch.ElapsedMilliseconds);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            result.IsSuccess = false;
            result.ErrorOutput = "Script execution was cancelled";
            _logger.LogWarning("PowerShell script execution was cancelled");
        }
        catch (OperationCanceledException)
        {
            result.IsSuccess = false;
            result.ErrorOutput = $"Script execution timed out after {timeoutSeconds} seconds";
            _logger.LogWarning("PowerShell script execution timed out after {TimeoutSeconds} seconds", timeoutSeconds);
        }
        catch (Exception ex)
        {
            result.IsSuccess = false;
            result.ErrorOutput = ex.Message;
            result.Exception = ex;
            _logger.LogError(ex, "PowerShell script execution failed");
        }
        finally
        {
            result.ExecutionTime = stopwatch.Elapsed;
        }

        return result;
    }

    public async Task<PowerShellResult> ExecutePowerCLICommandAsync(string command, Dictionary<string, object>? parameters = null, int timeoutSeconds = 300, CancellationToken cancellationToken = default)
    {
        // Ensure PowerCLI is available before executing commands
        if (!await IsPowerCLIAvailableAsync())
        {
            return new PowerShellResult
            {
                IsSuccess = false,
                ErrorOutput = "PowerCLI modules are not available. Please install VMware PowerCLI.",
                ExecutionTime = TimeSpan.Zero
            };
        }

        var script = new StringBuilder();
        
        // Import required modules if not already loaded
        script.AppendLine("Import-Module VMware.VimAutomation.Core -ErrorAction SilentlyContinue");
        script.AppendLine("Import-Module VMware.VimAutomation.Vds -ErrorAction SilentlyContinue");
        script.AppendLine("Import-Module VMware.VimAutomation.Storage -ErrorAction SilentlyContinue");
        
        // Add the actual command
        script.AppendLine(command);

        return await ExecuteScriptAsync(script.ToString(), parameters, timeoutSeconds, cancellationToken);
    }

    public async Task<bool> IsPowerCLIAvailableAsync()
    {
        try
        {
            var script = @"
                $modules = @('VMware.VimAutomation.Core', 'VMware.VimAutomation.Vds', 'VMware.VimAutomation.Storage')
                $available = $true
                foreach ($module in $modules) {
                    if (-not (Get-Module -ListAvailable -Name $module)) {
                        $available = $false
                        break
                    }
                }
                return $available
            ";

            var result = await ExecuteScriptAsync(script, timeoutSeconds: 30);
            
            if (result.IsSuccess && result.Objects.Count > 0)
            {
                if (bool.TryParse(result.Objects[0]?.ToString(), out bool available))
                {
                    return available;
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check PowerCLI availability");
            return false;
        }
    }

    public async Task<PowerCLIVersionInfo> GetPowerCLIVersionAsync()
    {
        var versionInfo = new PowerCLIVersionInfo();

        try
        {
            var script = @"
                $modules = @('VMware.VimAutomation.Core', 'VMware.VimAutomation.Vds', 'VMware.VimAutomation.Storage', 'VMware.VimAutomation.Common')
                $moduleInfo = @()
                
                foreach ($moduleName in $modules) {
                    $module = Get-Module -ListAvailable -Name $moduleName | Select-Object -First 1
                    if ($module) {
                        $moduleInfo += [PSCustomObject]@{
                            Name = $module.Name
                            Version = $module.Version.ToString()
                            IsLoaded = (Get-Module -Name $moduleName) -ne $null
                            IsAvailable = $true
                        }
                    } else {
                        $moduleInfo += [PSCustomObject]@{
                            Name = $moduleName
                            Version = 'Not Found'
                            IsLoaded = $false
                            IsAvailable = $false
                        }
                    }
                }
                
                return $moduleInfo
            ";

            var result = await ExecuteScriptAsync(script, timeoutSeconds: 30);

            if (result.IsSuccess)
            {
                foreach (var obj in result.Objects)
                {
                    if (obj is PSObject psObj)
                    {
                        var moduleInfo = new PowerCLIModuleInfo
                        {
                            Name = psObj.Properties["Name"]?.Value?.ToString() ?? "",
                            Version = psObj.Properties["Version"]?.Value?.ToString() ?? "",
                            IsLoaded = bool.Parse(psObj.Properties["IsLoaded"]?.Value?.ToString() ?? "false"),
                            IsAvailable = bool.Parse(psObj.Properties["IsAvailable"]?.Value?.ToString() ?? "false")
                        };
                        versionInfo.Modules.Add(moduleInfo);
                    }
                }

                // Get overall version from the core module
                var coreModule = versionInfo.Modules.FirstOrDefault(m => m.Name == "VMware.VimAutomation.Core");
                if (coreModule != null && coreModule.IsAvailable)
                {
                    versionInfo.Version = coreModule.Version;
                    versionInfo.IsCompatible = true;
                    versionInfo.CompatibilityMessage = "PowerCLI is available and compatible";
                }
                else
                {
                    versionInfo.IsCompatible = false;
                    versionInfo.CompatibilityMessage = "PowerCLI core module is not available";
                }
            }
            else
            {
                versionInfo.IsCompatible = false;
                versionInfo.CompatibilityMessage = $"Failed to check PowerCLI version: {result.ErrorOutput}";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get PowerCLI version");
            versionInfo.IsCompatible = false;
            versionInfo.CompatibilityMessage = $"Error checking PowerCLI: {ex.Message}";
        }

        return versionInfo;
    }

    public async Task<bool> InitializePowerCLIAsync(CancellationToken cancellationToken = default)
    {
        await _initializationSemaphore.WaitAsync(cancellationToken);
        try
        {
            if (_isInitialized)
                return true;

            _logger.LogInformation("Initializing PowerCLI session...");

            // Create runspace pool for better performance
            var initialSessionState = InitialSessionState.CreateDefault();
            
            // Set execution policy for the session
            initialSessionState.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
            
            // Add additional PowerShell commands to handle execution policy
            initialSessionState.Commands.Add(new SessionStateCmdletEntry("Set-ExecutionPolicy", typeof(Microsoft.PowerShell.Commands.SetExecutionPolicyCommand), null));

            // Create runspace pool
            _runspacePool = RunspaceFactory.CreateRunspacePool(1, _options.MaxConcurrentSessions, initialSessionState, null);
            _runspacePool.Open();

            // Test basic PowerShell functionality and set execution policy
            var initScript = @"
                # Set execution policy for this session
                try {
                    Set-ExecutionPolicy -ExecutionPolicy Bypass -Scope Process -Force -ErrorAction SilentlyContinue
                } catch {
                    Write-Warning ""Could not set execution policy: $($_.Exception.Message)""
                }
                
                # Test basic functionality
                Get-Date
            ";
            
            var result = await ExecuteScriptAsync(initScript, timeoutSeconds: 30, cancellationToken: cancellationToken);

            if (result.IsSuccess)
            {
                // Test PowerCLI module availability with better error reporting
                var moduleTestScript = @"
                    try {
                        # Check current execution policy
                        $currentPolicy = Get-ExecutionPolicy -Scope Process
                        Write-Output ""Current execution policy (Process): $currentPolicy""
                        
                        $userPolicy = Get-ExecutionPolicy -Scope CurrentUser
                        Write-Output ""Current execution policy (CurrentUser): $userPolicy""
                        
                        $systemPolicy = Get-ExecutionPolicy -Scope LocalMachine
                        Write-Output ""Current execution policy (LocalMachine): $systemPolicy""
                        
                        # Try to import VMware modules
                        $modules = @('VMware.VimAutomation.Core', 'VMware.VimAutomation.Common')
                        $moduleResults = @()
                        
                        foreach ($moduleName in $modules) {
                            try {
                                $module = Get-Module -ListAvailable -Name $moduleName -ErrorAction Stop | Select-Object -First 1
                                if ($module) {
                                    Import-Module $moduleName -ErrorAction Stop
                                    $moduleResults += ""$moduleName - Successfully loaded (Version: $($module.Version))""
                                } else {
                                    $moduleResults += ""$moduleName - Not found""
                                }
                            } catch {
                                $moduleResults += ""$moduleName - Failed to load: $($_.Exception.Message)""
                            }
                        }
                        
                        return [PSCustomObject]@{
                            Success = $true
                            ExecutionPolicies = @{
                                Process = $currentPolicy
                                CurrentUser = $userPolicy
                                LocalMachine = $systemPolicy
                            }
                            ModuleResults = $moduleResults
                        }
                    } catch {
                        return [PSCustomObject]@{
                            Success = $false
                            Error = $_.Exception.Message
                            ExecutionPolicies = @{}
                            ModuleResults = @()
                        }
                    }
                ";
                
                var moduleResult = await ExecuteScriptAsync(moduleTestScript, timeoutSeconds: 60, cancellationToken: cancellationToken);
                
                if (moduleResult.IsSuccess && moduleResult.Objects.Count > 0 && moduleResult.Objects[0] is PSObject psObj)
                {
                    var success = bool.Parse(psObj.Properties["Success"]?.Value?.ToString() ?? "false");
                    var moduleResultsArray = psObj.Properties["ModuleResults"]?.Value as object[];
                    var moduleStatuses = moduleResultsArray?.Cast<string>().ToArray() ?? Array.Empty<string>();
                    
                    foreach (var moduleStatus in moduleStatuses)
                    {
                        _logger.LogInformation("PowerCLI Module Status: {ModuleStatus}", moduleStatus);
                    }
                    
                    if (!success)
                    {
                        var error = psObj.Properties["Error"]?.Value?.ToString();
                        _logger.LogError("PowerCLI initialization failed: {Error}", error);
                        _logger.LogError("This usually indicates a PowerShell execution policy issue. Please run 'Set-ExecutionPolicy -ExecutionPolicy RemoteSigned -Scope CurrentUser -Force' in PowerShell as an administrator.");
                    }
                }

                _isInitialized = true;
                _logger.LogInformation("PowerCLI session initialized successfully");
                return true;
            }
            else
            {
                _logger.LogError("Failed to initialize PowerCLI session: {Error}", result.ErrorOutput);
                _logger.LogError("This usually indicates a PowerShell execution policy issue. Please run 'Set-ExecutionPolicy -ExecutionPolicy RemoteSigned -Scope CurrentUser -Force' in PowerShell.");
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize PowerCLI session");
            _logger.LogError("This usually indicates a PowerShell execution policy issue. Please run 'Set-ExecutionPolicy -ExecutionPolicy RemoteSigned -Scope CurrentUser -Force' in PowerShell.");
            return false;
        }
        finally
        {
            _initializationSemaphore.Release();
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken = default)
    {
        if (!_isInitialized)
        {
            await InitializePowerCLIAsync(cancellationToken);
        }
    }

    public void Dispose()
    {
        _runspacePool?.Dispose();
        _initializationSemaphore?.Dispose();
    }
}

/// <summary>
/// Configuration options for PowerShell service
/// </summary>
public class PowerShellOptions
{
    public const string SectionName = "PowerShell";

    public int MaxConcurrentSessions { get; set; } = 5;
    public int DefaultTimeoutSeconds { get; set; } = 300;
    public string PowerCLIModulePath { get; set; } = string.Empty;
    public bool EnableVerboseLogging { get; set; } = false;
} 