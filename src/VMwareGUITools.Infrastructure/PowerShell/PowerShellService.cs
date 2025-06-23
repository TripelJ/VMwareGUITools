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
        // Ensure PowerCLI is available and properly loaded before executing commands
        var loadResult = await LoadPowerCLIWithVersionCompatibilityAsync();
        if (!loadResult.IsSuccess)
        {
            return loadResult;
        }

        var script = new StringBuilder();
        
        // Enhanced module loading with specific version selection
        script.AppendLine("# Enhanced PowerCLI module loading with version compatibility");
        script.AppendLine("try {");
        script.AppendLine("    # First ensure we have the core modules loaded in a compatible way");
        script.AppendLine("    $coreModule = Get-Module -Name 'VMware.VimAutomation.Core' -ErrorAction SilentlyContinue");
        script.AppendLine("    if (-not $coreModule) {");
        script.AppendLine("        # Try to load compatible versions");
        script.AppendLine("        $availableCore = Get-Module -ListAvailable -Name 'VMware.VimAutomation.Core' | Sort-Object Version -Descending");
        script.AppendLine("        $availableCommon = Get-Module -ListAvailable -Name 'VMware.VimAutomation.Common' | Sort-Object Version -Descending");
        script.AppendLine("        ");
        script.AppendLine("        # Find compatible version sets");
        script.AppendLine("        foreach ($coreVer in $availableCore) {");
        script.AppendLine("            $majorMinor = \"$($coreVer.Version.Major).$($coreVer.Version.Minor)\"");
        script.AppendLine("            $compatibleCommon = $availableCommon | Where-Object { \"$($_.Version.Major).$($_.Version.Minor)\" -eq $majorMinor } | Select-Object -First 1");
        script.AppendLine("            ");
        script.AppendLine("            if ($compatibleCommon) {");
        script.AppendLine("                try {");
        script.AppendLine("                    # Load Common first, then Core");
        script.AppendLine("                    Import-Module $compatibleCommon.Path -Force -ErrorAction Stop");
        script.AppendLine("                    Import-Module $coreVer.Path -Force -ErrorAction Stop");
        script.AppendLine("                    Write-Output \"Successfully loaded PowerCLI Core v$($coreVer.Version) with Common v$($compatibleCommon.Version)\"");
        script.AppendLine("                    break");
        script.AppendLine("                } catch {");
        script.AppendLine("                    Write-Warning \"Failed to load PowerCLI v$($coreVer.Version): $($_.Exception.Message)\"");
        script.AppendLine("                    continue");
        script.AppendLine("                }");
        script.AppendLine("            }");
        script.AppendLine("        }");
        script.AppendLine("    }");
        script.AppendLine("    ");
        script.AppendLine("    # Load additional modules if available");
        script.AppendLine("    @('VMware.VimAutomation.Vds', 'VMware.VimAutomation.Storage') | ForEach-Object {");
        script.AppendLine("        $module = Get-Module -Name $_ -ErrorAction SilentlyContinue");
        script.AppendLine("        if (-not $module) {");
        script.AppendLine("            $available = Get-Module -ListAvailable -Name $_ -ErrorAction SilentlyContinue | Select-Object -First 1");
        script.AppendLine("            if ($available) {");
        script.AppendLine("                try {");
        script.AppendLine("                    Import-Module $available.Path -Force -ErrorAction SilentlyContinue");
        script.AppendLine("                } catch {");
        script.AppendLine("                    Write-Warning \"Could not load optional module $_: $($_.Exception.Message)\"");
        script.AppendLine("                }");
        script.AppendLine("            }");
        script.AppendLine("        }");
        script.AppendLine("    }");
        script.AppendLine("} catch {");
        script.AppendLine("    Write-Error \"Failed to load PowerCLI modules: $($_.Exception.Message)\"");
        script.AppendLine("    throw");
        script.AppendLine("}");
        script.AppendLine("");
        
        // Add the actual command
        script.AppendLine("# Execute the requested command");
        script.AppendLine(command);

        return await ExecuteScriptAsync(script.ToString(), parameters, timeoutSeconds, cancellationToken);
    }

    /// <summary>
    /// Loads PowerCLI modules with version compatibility handling
    /// </summary>
    private async Task<PowerShellResult> LoadPowerCLIWithVersionCompatibilityAsync()
    {
        var loadScript = @"
            try {
                $result = [PSCustomObject]@{
                    Success = $false
                    LoadedModules = @()
                    Errors = @()
                    Message = ''
                    RecommendedAction = ''
                }

                # Check if VMware.PowerCLI meta-module is available (preferred method)
                $powerCLIModule = Get-Module -ListAvailable -Name 'VMware.PowerCLI' -ErrorAction SilentlyContinue | Select-Object -First 1
                
                if ($powerCLIModule) {
                    # Try to load the meta-module first (simplest approach)
                    try {
                        Import-Module VMware.PowerCLI -Force -ErrorAction Stop
                        $result.Success = $true
                        $result.Message = 'PowerCLI loaded successfully via meta-module'
                        $result.LoadedModules = @(Get-Module -Name '*VMware*' | Select-Object Name, Version)
                        Write-Output $result
                        return
                    } catch {
                        $result.Errors += ""Meta-module load failed: $($_.Exception.Message)""
                        # Continue to try manual loading
                    }
                }

                # Manual loading approach with version compatibility
                Write-Verbose 'Attempting manual PowerCLI module loading...'
                
                # Get all available VMware modules and sort by version
                $availableModules = Get-Module -ListAvailable -Name '*VMware*' | 
                    Group-Object Name | 
                    ForEach-Object { $_.Group | Sort-Object Version -Descending | Select-Object -First 1 }
                
                # Find core modules first
                $coreModules = @(
                    'VMware.VimAutomation.Sdk',
                    'VMware.VimAutomation.Common', 
                    'VMware.VimAutomation.Core'
                )
                
                # Check for version conflicts (specifically VMware.Vim issue)
                $vimModule = $availableModules | Where-Object { $_.Name -eq 'VMware.Vim' }
                $commonModule = $availableModules | Where-Object { $_.Name -eq 'VMware.VimAutomation.Common' }
                
                if ($vimModule -and $commonModule) {
                    # Check for known incompatible combinations
                    $vimMajorMinor = ""$($vimModule.Version.Major).$($vimModule.Version.Minor)""
                    $commonMajorMinor = ""$($commonModule.Version.Major).$($commonModule.Version.Minor)""
                    
                    if ($vimMajorMinor -eq '9.0' -and $commonMajorMinor -ne '9.0') {
                        $result.Errors += ""Version conflict detected: VMware.Vim v$($vimModule.Version) is incompatible with VMware.VimAutomation.Common v$($commonModule.Version)""
                        $result.RecommendedAction = 'Run PowerCLI cleanup script to resolve version conflicts'
                        $result.Success = $false
                        Write-Output $result
                        return
                    }
                }
                
                # Try to load core modules in order
                $loadedCount = 0
                foreach ($moduleName in $coreModules) {
                    $module = $availableModules | Where-Object { $_.Name -eq $moduleName }
                    if ($module) {
                        try {
                            Import-Module $module.Path -Force -ErrorAction Stop
                            $result.LoadedModules += @{ Name = $module.Name; Version = $module.Version.ToString() }
                            $loadedCount++
                            Write-Verbose ""Loaded $($module.Name) v$($module.Version)""
                        } catch {
                            $result.Errors += ""Failed to load $($module.Name): $($_.Exception.Message)""
                        }
                    } else {
                        $result.Errors += ""Module $moduleName not found""
                    }
                }
                
                # Check if we have minimum required modules
                if ($loadedCount -ge 2) { # At least Common and Core
                    $result.Success = $true
                    $result.Message = ""PowerCLI core modules loaded successfully ($loadedCount modules)""
                    
                    # Try to load additional modules (non-critical)
                    $additionalModules = @('VMware.VimAutomation.Vds', 'VMware.VimAutomation.Storage')
                    foreach ($moduleName in $additionalModules) {
                        $module = $availableModules | Where-Object { $_.Name -eq $moduleName }
                        if ($module) {
                            try {
                                Import-Module $module.Path -Force -ErrorAction SilentlyContinue
                                $result.LoadedModules += @{ Name = $module.Name; Version = $module.Version.ToString() }
                            } catch {
                                # Non-critical, just log
                                Write-Verbose ""Optional module $moduleName failed to load: $($_.Exception.Message)""
                            }
                        }
                    }
                } else {
                    $result.Success = $false
                    $result.Message = 'Failed to load minimum required PowerCLI modules'
                    $result.RecommendedAction = 'Install or reinstall VMware PowerCLI'
                }

                Write-Output $result
            } catch {
                Write-Output ([PSCustomObject]@{
                    Success = $false
                    LoadedModules = @()
                    Errors = @(""Critical error during PowerCLI loading: $($_.Exception.Message)"")
                    Message = ""Exception during module loading""
                    RecommendedAction = 'Check PowerCLI installation and version compatibility'
                })
            }
        ";

        var result = await ExecuteScriptAsync(loadScript, timeoutSeconds: 60);
        
        if (result.IsSuccess && result.Objects.Count > 0 && result.Objects[0] is PSObject psObj)
        {
            var success = bool.Parse(psObj.Properties["Success"]?.Value?.ToString() ?? "false");
            var message = psObj.Properties["Message"]?.Value?.ToString() ?? "";
            var errors = psObj.Properties["Errors"]?.Value as object[] ?? Array.Empty<object>();
            var loadedModules = psObj.Properties["LoadedModules"]?.Value as object[] ?? Array.Empty<object>();

            if (success)
            {
                _logger.LogInformation("PowerCLI modules loaded successfully: {Message}", message);
                foreach (var module in loadedModules)
                {
                    _logger.LogDebug("Loaded module: {Module}", module.ToString());
                }
                
                return new PowerShellResult
                {
                    IsSuccess = true,
                    Output = message,
                    ExecutionTime = result.ExecutionTime
                };
            }
            else
            {
                var errorMessage = string.Join(Environment.NewLine, errors.Select(e => e.ToString()));
                _logger.LogError("Failed to load PowerCLI modules: {Message}. Errors: {Errors}", message, errorMessage);
                
                return new PowerShellResult
                {
                    IsSuccess = false,
                    ErrorOutput = $"PowerCLI modules are not available or incompatible.\n\nDetails: {message}\n\nErrors:\n{errorMessage}\n\nSolutions:\n1. Reinstall PowerCLI: Uninstall-Module VMware.PowerCLI -AllVersions -Force; Install-Module VMware.PowerCLI\n2. Or load specific version: Import-Module VMware.VimAutomation.Core -RequiredVersion <version>",
                    ExecutionTime = result.ExecutionTime
                };
            }
        }
        
        return new PowerShellResult
        {
            IsSuccess = false,
            ErrorOutput = "Failed to check PowerCLI module compatibility",
            ExecutionTime = result.ExecutionTime
        };
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
            
            // Set execution policy for the session to most permissive
            initialSessionState.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
            
            // Add additional PowerShell commands to handle execution policy
            initialSessionState.Commands.Add(new SessionStateCmdletEntry("Set-ExecutionPolicy", typeof(Microsoft.PowerShell.Commands.SetExecutionPolicyCommand), null));

            // Import modules explicitly in the session state if they exist
            try
            {
                var coreModule = "VMware.VimAutomation.Core";
                if (System.Management.Automation.PowerShell.Create().AddCommand("Get-Module").AddParameter("ListAvailable").AddParameter("Name", coreModule).Invoke().Any())
                {
                    initialSessionState.ImportPSModule(new[] { coreModule, "VMware.VimAutomation.Common", "VMware.VimAutomation.Vds", "VMware.VimAutomation.Storage" });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not pre-import PowerCLI modules in session state, will try runtime import");
            }

            // Create runspace pool
            _runspacePool = RunspaceFactory.CreateRunspacePool(1, _options.MaxConcurrentSessions, initialSessionState, null);
            _runspacePool.Open();

            // Enhanced initialization script with better error handling
            var initScript = @"
                # Comprehensive execution policy and module setup
                try {
                    # Set execution policy for this session at multiple scopes
                    Set-ExecutionPolicy -ExecutionPolicy Bypass -Scope Process -Force -ErrorAction SilentlyContinue
                    Write-Output ""Process execution policy set to Bypass""
                    
                    # Check if we're running as admin and can set user policy
                    $isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole] ""Administrator"")
                    if ($isAdmin) {
                        try {
                            Set-ExecutionPolicy -ExecutionPolicy RemoteSigned -Scope CurrentUser -Force -ErrorAction SilentlyContinue
                            Write-Output ""CurrentUser execution policy set to RemoteSigned""
                        } catch {
                            Write-Warning ""Could not set CurrentUser execution policy: $($_.Exception.Message)""
                        }
                    }
                    
                    # Report current policies
                    $policies = @{}
                    @('Process', 'CurrentUser', 'LocalMachine', 'MachinePolicy', 'UserPolicy') | ForEach-Object {
                        try {
                            $policies[$_] = Get-ExecutionPolicy -Scope $_ -ErrorAction SilentlyContinue
                        } catch {
                            $policies[$_] = ""Unable to read""
                        }
                    }
                    
                    Write-Output ""Current Execution Policies:""
                    $policies.GetEnumerator() | ForEach-Object { Write-Output ""  $($_.Key): $($_.Value)"" }
                    
                    # Force reload PSModulePath to ensure all module paths are available
                    $env:PSModulePath = [System.Environment]::GetEnvironmentVariable('PSModulePath', 'Machine') + ';' + [System.Environment]::GetEnvironmentVariable('PSModulePath', 'User')
                    
                    # Check PowerCLI installation location
                    $powerCLIPaths = @()
                    
                    # Check common installation paths
                    $commonPaths = @(
                        ""${env:ProgramFiles}\WindowsPowerShell\Modules"",
                        ""${env:ProgramFiles(x86)}\WindowsPowerShell\Modules"",
                        ""${env:USERPROFILE}\Documents\WindowsPowerShell\Modules"",
                        ""${env:ProgramFiles}\PowerShell\Modules"",
                        ""${env:USERPROFILE}\Documents\PowerShell\Modules""
                    )
                    
                    foreach ($path in $commonPaths) {
                        if (Test-Path $path) {
                            $coreModulePath = Join-Path $path ""VMware.VimAutomation.Core""
                            if (Test-Path $coreModulePath) {
                                $powerCLIPaths += $coreModulePath
                            }
                        }
                    }
                    
                    Write-Output ""PowerCLI module paths found: $($powerCLIPaths -join ', ')""
                    
                    # Test basic functionality
                    Get-Date | Out-Null
                    Write-Output ""Basic PowerShell functionality confirmed""
                    
                    return [PSCustomObject]@{
                        Success = $true
                        ExecutionPolicies = $policies
                        PowerCLIPaths = $powerCLIPaths
                        IsAdmin = $isAdmin
                    }
                } catch {
                    return [PSCustomObject]@{
                        Success = $false
                        Error = $_.Exception.Message
                        ExecutionPolicies = @{}
                        PowerCLIPaths = @()
                        IsAdmin = $false
                    }
                }
            ";
            
            var result = await ExecuteScriptAsync(initScript, timeoutSeconds: 30, cancellationToken: cancellationToken);

            if (result.IsSuccess && result.Objects.Count > 0 && result.Objects[0] is PSObject psObj)
            {
                var success = bool.Parse(psObj.Properties["Success"]?.Value?.ToString() ?? "false");
                var isAdmin = bool.Parse(psObj.Properties["IsAdmin"]?.Value?.ToString() ?? "false");
                
                _logger.LogInformation("PowerShell initialization success: {Success}, Running as admin: {IsAdmin}", success, isAdmin);
                
                if (!success)
                {
                    var error = psObj.Properties["Error"]?.Value?.ToString();
                    _logger.LogError("PowerShell initialization failed: {Error}", error);
                }
                
                // Now test PowerCLI module availability with enhanced loading
                var moduleTestScript = @"
                    try {
                        # Force module path refresh
                        $env:PSModulePath = [System.Environment]::GetEnvironmentVariable('PSModulePath', 'Machine') + ';' + [System.Environment]::GetEnvironmentVariable('PSModulePath', 'User')
                        
                        # Get all available modules to help debug
                        $allModules = Get-Module -ListAvailable | Where-Object { $_.Name -like '*VMware*' } | Select-Object Name, Version, Path
                        
                        # Try to import VMware modules with various approaches
                        $modules = @('VMware.VimAutomation.Core', 'VMware.VimAutomation.Common', 'VMware.VimAutomation.Vds', 'VMware.VimAutomation.Storage')
                        $moduleResults = @()
                        
                        foreach ($moduleName in $modules) {
                            try {
                                # First, try to find the module
                                $availableModule = Get-Module -ListAvailable -Name $moduleName -ErrorAction SilentlyContinue | Select-Object -First 1
                                if ($availableModule) {
                                    try {
                                        # Try to import with force
                                        Import-Module $moduleName -Force -ErrorAction Stop
                                        $loadedModule = Get-Module -Name $moduleName
                                        if ($loadedModule) {
                                            $moduleResults += ""$moduleName - Successfully loaded (Version: $($availableModule.Version), Path: $($availableModule.Path))""
                                        } else {
                                            $moduleResults += ""$moduleName - Import succeeded but module not found in session""
                                        }
                                    } catch {
                                        # Try alternative import method
                                        try {
                                            Import-Module $availableModule.Path -Force -ErrorAction Stop
                                            $moduleResults += ""$moduleName - Successfully loaded via path (Version: $($availableModule.Version))""
                                        } catch {
                                            $moduleResults += ""$moduleName - Failed to import: $($_.Exception.Message)""
                                        }
                                    }
                                } else {
                                    $moduleResults += ""$moduleName - Module not found in any module path""
                                }
                            } catch {
                                $moduleResults += ""$moduleName - Error during module discovery: $($_.Exception.Message)""
                            }
                        }
                        
                        # Test a basic PowerCLI command if core module loaded
                        $coreLoaded = Get-Module -Name 'VMware.VimAutomation.Core' -ErrorAction SilentlyContinue
                        $testResult = $null
                        if ($coreLoaded) {
                            try {
                                # Test with a simple command
                                $testResult = Get-PowerCLIVersion -ErrorAction Stop
                                $moduleResults += ""PowerCLI Version Test: Success - $($testResult.ProductLine)""
                            } catch {
                                $moduleResults += ""PowerCLI Version Test: Failed - $($_.Exception.Message)""
                            }
                        }
                        
                        return [PSCustomObject]@{
                            Success = ($coreLoaded -ne $null)
                            ModuleResults = $moduleResults
                            AllVMwareModules = $allModules
                            PowerCLIVersion = $testResult
                            LoadedModules = (Get-Module | Where-Object { $_.Name -like '*VMware*' } | Select-Object Name, Version)
                        }
                    } catch {
                        return [PSCustomObject]@{
                            Success = $false
                            Error = $_.Exception.Message
                            ModuleResults = @(""Critical error during module testing: $($_.Exception.Message)"")
                            AllVMwareModules = @()
                            PowerCLIVersion = $null
                            LoadedModules = @()
                        }
                    }
                ";
                
                var moduleResult = await ExecuteScriptAsync(moduleTestScript, timeoutSeconds: 60, cancellationToken: cancellationToken);
                
                if (moduleResult.IsSuccess && moduleResult.Objects.Count > 0 && moduleResult.Objects[0] is PSObject moduleObj)
                {
                    var moduleSuccess = bool.Parse(moduleObj.Properties["Success"]?.Value?.ToString() ?? "false");
                    var moduleResultsArray = moduleObj.Properties["ModuleResults"]?.Value as object[];
                    var moduleStatuses = moduleResultsArray?.Cast<string>().ToArray() ?? Array.Empty<string>();
                    
                    _logger.LogInformation("PowerCLI module test success: {Success}", moduleSuccess);
                    foreach (var moduleStatus in moduleStatuses)
                    {
                        _logger.LogInformation("PowerCLI Module Status: {ModuleStatus}", moduleStatus);
                    }
                    
                    if (!moduleSuccess)
                    {
                        var error = moduleObj.Properties["Error"]?.Value?.ToString();
                        _logger.LogError("PowerCLI module testing failed: {Error}", error);
                        _logger.LogError("Please ensure PowerCLI is installed. Run: Install-Module -Name VMware.PowerCLI -AllowClobber");
                        _logger.LogError("If execution policy issues persist, run as administrator: Set-ExecutionPolicy -ExecutionPolicy RemoteSigned -Scope LocalMachine");
                        return false;
                    }
                }

                _isInitialized = true;
                _logger.LogInformation("PowerCLI session initialized successfully");
                return true;
            }
            else
            {
                _logger.LogError("Failed to initialize PowerCLI session: {Error}", result.ErrorOutput);
                _logger.LogError("Common solutions:");
                _logger.LogError("1. Run as administrator: Set-ExecutionPolicy -ExecutionPolicy RemoteSigned -Scope LocalMachine");
                _logger.LogError("2. For current user only: Set-ExecutionPolicy -ExecutionPolicy RemoteSigned -Scope CurrentUser");
                _logger.LogError("3. Install PowerCLI: Install-Module -Name VMware.PowerCLI -AllowClobber");
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize PowerCLI session");
            _logger.LogError("Common solutions:");
            _logger.LogError("1. Run as administrator: Set-ExecutionPolicy -ExecutionPolicy RemoteSigned -Scope LocalMachine");
            _logger.LogError("2. For current user only: Set-ExecutionPolicy -ExecutionPolicy RemoteSigned -Scope CurrentUser");
            _logger.LogError("3. Install PowerCLI: Install-Module -Name VMware.PowerCLI -AllowClobber");
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