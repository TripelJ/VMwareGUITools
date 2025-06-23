using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace VMwareGUITools.Infrastructure.PowerShell;

/// <summary>
/// Robust PowerCLI service implementation with improved session management and error handling
/// </summary>
public class PowerCLIService : IPowerCLIService, IDisposable
{
    private readonly ILogger<PowerCLIService> _logger;
    private readonly PowerCLIOptions _options;
    private readonly SemaphoreSlim _initializationSemaphore = new(1, 1);
    private readonly Dictionary<string, PowerCLISession> _activeSessions = new();
    private InitialSessionState? _initialSessionState;
    private bool _isInitialized = false;

    public PowerCLIService(ILogger<PowerCLIService> logger, IOptions<PowerCLIOptions> options)
    {
        _logger = logger;
        _options = options.Value;
    }

    public async Task<PowerCLIValidationResult> ValidatePowerCLIAsync()
    {
        var result = new PowerCLIValidationResult();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            _logger.LogInformation("Validating PowerCLI installation and configuration");

            // Check execution policy
            var executionPolicyCheck = await CheckExecutionPolicyAsync();
            if (!executionPolicyCheck.IsValid)
            {
                result.Issues.AddRange(executionPolicyCheck.Issues);
                result.Suggestions.AddRange(executionPolicyCheck.Suggestions);
                result.RequiresRepair = true;
            }

            // Check PowerCLI modules
            var moduleCheck = await CheckPowerCLIModulesAsync();
            if (!moduleCheck.IsValid)
            {
                result.Issues.AddRange(moduleCheck.Issues);
                result.Suggestions.AddRange(moduleCheck.Suggestions);
                result.RequiresRepair = true;
            }
            else
            {
                result.VersionInfo = moduleCheck.VersionInfo;
            }

            // Test basic PowerCLI functionality
            var functionalityCheck = await TestBasicFunctionalityAsync();
            if (!functionalityCheck.IsValid)
            {
                result.Issues.AddRange(functionalityCheck.Issues);
                result.Suggestions.AddRange(functionalityCheck.Suggestions);
                result.RequiresRepair = true;
            }

            result.IsValid = result.Issues.Count == 0;
            
            _logger.LogInformation("PowerCLI validation completed in {ElapsedMs}ms. Valid: {IsValid}, Issues: {IssueCount}", 
                stopwatch.ElapsedMilliseconds, result.IsValid, result.Issues.Count);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PowerCLI validation failed");
            result.IsValid = false;
            result.ErrorMessage = $"Validation failed: {ex.Message}";
            result.RequiresRepair = true;
            return result;
        }
    }

    public async Task<PowerCLIConnectionResult> TestConnectionAsync(string serverUrl, string username, string password, int timeoutSeconds = 60)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = new PowerCLIConnectionResult();

        try
        {
            _logger.LogInformation("Testing PowerCLI connection to {ServerUrl}", serverUrl);

            // Validate PowerCLI first
            var validation = await ValidatePowerCLIAsync();
            if (!validation.IsValid)
            {
                result.ErrorMessage = $"PowerCLI validation failed: {string.Join(", ", validation.Issues)}";
                result.ErrorCode = "POWERCLI_INVALID";
                return result;
            }

            await EnsureInitializedAsync();

            using var runspace = RunspaceFactory.CreateRunspace(_initialSessionState);
            runspace.Open();

            using var powerShell = System.Management.Automation.PowerShell.Create();
            powerShell.Runspace = runspace;

            // Build connection test script
            var script = BuildConnectionTestScript();
            powerShell.AddScript(script);
            powerShell.AddParameter("ServerUrl", serverUrl);
            powerShell.AddParameter("Username", username);
            powerShell.AddParameter("Password", password);
            powerShell.AddParameter("TimeoutSeconds", timeoutSeconds);

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds + 10));
            
            var task = Task.Run(() => powerShell.Invoke(), timeoutCts.Token);
            var psResults = await task;

            result.ResponseTime = stopwatch.Elapsed;

            if (powerShell.Streams.Error.Count > 0)
            {
                var error = powerShell.Streams.Error[0];
                result.ErrorMessage = ExtractMeaningfulError(error);
                result.ErrorCode = ClassifyError(error);
                _logger.LogWarning("PowerCLI connection test failed: {Error}", result.ErrorMessage);
                return result;
            }

            if (psResults != null && psResults.Count > 0)
            {
                var connectionResult = psResults[0];
                if (connectionResult is PSObject psObj)
                {
                    result.IsSuccessful = GetPropertyValue<bool>(psObj, "Success");
                    result.ServerVersion = GetPropertyValue<string>(psObj, "Version");
                    result.ServerBuild = GetPropertyValue<string>(psObj, "Build");
                    result.ApiVersion = GetPropertyValue<string>(psObj, "ApiVersion");
                    result.IsSecure = serverUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

                    if (!result.IsSuccessful)
                    {
                        result.ErrorMessage = GetPropertyValue<string>(psObj, "ErrorMessage");
                        result.ErrorCode = GetPropertyValue<string>(psObj, "ErrorCode") ?? "UNKNOWN";
                    }
                }
            }

            _logger.LogInformation("PowerCLI connection test completed. Success: {Success}, Time: {ElapsedMs}ms", 
                result.IsSuccessful, stopwatch.ElapsedMilliseconds);

            return result;
        }
        catch (OperationCanceledException)
        {
            result.ErrorMessage = $"Connection test timed out after {timeoutSeconds} seconds";
            result.ErrorCode = "TIMEOUT";
            result.ResponseTime = stopwatch.Elapsed;
            _logger.LogWarning("PowerCLI connection test timed out for {ServerUrl}", serverUrl);
            return result;
        }
        catch (Exception ex)
        {
            result.ErrorMessage = $"Connection test failed: {ex.Message}";
            result.ErrorCode = "EXCEPTION";
            result.ResponseTime = stopwatch.Elapsed;
            _logger.LogError(ex, "PowerCLI connection test failed for {ServerUrl}", serverUrl);
            return result;
        }
    }

    public async Task<PowerCLISession> ConnectAsync(string serverUrl, string username, string password, int timeoutSeconds = 60)
    {
        var session = new PowerCLISession
        {
            ServerUrl = serverUrl,
            Username = username
        };

        try
        {
            _logger.LogInformation("Establishing PowerCLI session to {ServerUrl} for user {Username}", serverUrl, username);

            await EnsureInitializedAsync();

            var runspace = RunspaceFactory.CreateRunspace(_initialSessionState);
            runspace.Open();
            session.InternalSession = runspace;

            using var powerShell = System.Management.Automation.PowerShell.Create();
            powerShell.Runspace = runspace;

            // Build connection script
            var script = BuildConnectionScript();
            powerShell.AddScript(script);
            powerShell.AddParameter("ServerUrl", serverUrl);
            powerShell.AddParameter("Username", username);
            powerShell.AddParameter("Password", password);
            powerShell.AddParameter("TimeoutSeconds", timeoutSeconds);

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds + 10));
            
            var task = Task.Run(() => powerShell.Invoke(), timeoutCts.Token);
            var psResults = await task;

            if (powerShell.Streams.Error.Count > 0)
            {
                var error = powerShell.Streams.Error[0];
                var errorMessage = ExtractMeaningfulError(error);
                session.Dispose();
                throw new InvalidOperationException($"Failed to connect to vCenter: {errorMessage}");
            }

            if (psResults != null && psResults.Count > 0 && psResults[0] is PSObject psObj)
            {
                var success = GetPropertyValue<bool>(psObj, "Success");
                if (success)
                {
                    session.IsConnected = true;
                    session.ServerVersion = GetPropertyValue<string>(psObj, "Version");
                    session.ApiVersion = GetPropertyValue<string>(psObj, "ApiVersion");
                    session.LastActivity = DateTime.UtcNow;

                    _activeSessions[session.SessionId] = session;
                    _logger.LogInformation("PowerCLI session established successfully. SessionId: {SessionId}", session.SessionId);
                }
                else
                {
                    var errorMessage = GetPropertyValue<string>(psObj, "ErrorMessage") ?? "Unknown connection error";
                    session.Dispose();
                    throw new InvalidOperationException($"Failed to connect to vCenter: {errorMessage}");
                }
            }
            else
            {
                session.Dispose();
                throw new InvalidOperationException("No valid response received from PowerCLI connection attempt");
            }

            return session;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to establish PowerCLI session to {ServerUrl}", serverUrl);
            session.Dispose();
            throw;
        }
    }

    public async Task DisconnectAsync(PowerCLISession session)
    {
        try
        {
            _logger.LogInformation("Disconnecting PowerCLI session {SessionId}", session.SessionId);

            if (session.InternalSession is Runspace runspace && session.IsConnected)
            {
                using var powerShell = System.Management.Automation.PowerShell.Create();
                powerShell.Runspace = runspace;

                var disconnectScript = $"Disconnect-VIServer -Server '{session.ServerUrl}' -Confirm:$false -ErrorAction SilentlyContinue";
                powerShell.AddScript(disconnectScript);

                await Task.Run(() => powerShell.Invoke());
            }

            _activeSessions.Remove(session.SessionId);
            session.Dispose();

            _logger.LogInformation("PowerCLI session {SessionId} disconnected successfully", session.SessionId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error while disconnecting PowerCLI session {SessionId}", session.SessionId);
            // Always ensure cleanup
            _activeSessions.Remove(session.SessionId);
            session.Dispose();
        }
    }

    public async Task<PowerCLIResult> ExecuteCommandAsync(PowerCLISession session, string command, Dictionary<string, object>? parameters = null, int timeoutSeconds = 300)
    {
        var result = new PowerCLIResult();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            if (!session.IsConnected || session.InternalSession is not Runspace runspace)
            {
                throw new InvalidOperationException("Session is not connected or invalid");
            }

            session.LastActivity = DateTime.UtcNow;

            using var powerShell = System.Management.Automation.PowerShell.Create();
            powerShell.Runspace = runspace;

            powerShell.AddScript(command);

            if (parameters != null)
            {
                foreach (var param in parameters)
                {
                    powerShell.AddParameter(param.Key, param.Value);
                }
            }

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            
            var task = Task.Run(() => powerShell.Invoke(), timeoutCts.Token);
            var psResults = await task;

            result.Objects = psResults?.Cast<object>().ToList() ?? new List<object>();
            result.Output = string.Join(Environment.NewLine, result.Objects.Select(o => o?.ToString() ?? ""));
            result.Warnings = powerShell.Streams.Warning.Select(w => w.Message).ToList();
            result.ExecutionTime = stopwatch.Elapsed;

            if (powerShell.Streams.Error.Count > 0)
            {
                var error = powerShell.Streams.Error[0];
                result.ErrorMessage = ExtractMeaningfulError(error);
                result.IsSuccess = false;
            }
            else
            {
                result.IsSuccess = true;
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            result.ErrorMessage = $"Command execution timed out after {timeoutSeconds} seconds";
            result.ExecutionTime = stopwatch.Elapsed;
            return result;
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
            result.Exception = ex;
            result.ExecutionTime = stopwatch.Elapsed;
            return result;
        }
    }

    public async Task<PowerCLIVersionInfo> GetVersionInfoAsync()
    {
        try
        {
            await EnsureInitializedAsync();

            using var runspace = RunspaceFactory.CreateRunspace(_initialSessionState);
            runspace.Open();

            using var powerShell = System.Management.Automation.PowerShell.Create();
            powerShell.Runspace = runspace;

            powerShell.AddScript("Get-PowerCLIVersion | Select-Object PowerCLIVersion, UserPowerShellVersion, Build");
            var psResults = powerShell.Invoke();

            var versionInfo = new PowerCLIVersionInfo();

            if (psResults != null && psResults.Count > 0 && psResults[0] is PSObject psObj)
            {
                versionInfo.Version = GetPropertyValue<string>(psObj, "PowerCLIVersion") ?? "";
                versionInfo.IsCompatible = true;
            }

            // Get module information
            powerShell.Commands.Clear();
            powerShell.AddScript("Get-Module -ListAvailable VMware.* | Select-Object Name, Version");
            var moduleResults = powerShell.Invoke();

            foreach (var module in moduleResults.OfType<PSObject>())
            {
                versionInfo.Modules.Add(new PowerCLIModuleInfo
                {
                    Name = GetPropertyValue<string>(module, "Name") ?? "",
                    Version = GetPropertyValue<string>(module, "Version") ?? "",
                    IsAvailable = true
                });
            }

            return versionInfo;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get PowerCLI version information");
            return new PowerCLIVersionInfo
            {
                Version = "Unknown",
                IsCompatible = false,
                CompatibilityMessage = ex.Message
            };
        }
    }

    public async Task<PowerCLIRepairResult> RepairPowerCLIAsync()
    {
        var result = new PowerCLIRepairResult();
        
        try
        {
            _logger.LogInformation("Starting PowerCLI repair process");

            // Step 1: Fix execution policy
            var executionPolicyResult = await RepairExecutionPolicyAsync();
            result.ActionsPerformed.AddRange(executionPolicyResult.ActionsPerformed);
            
            if (!executionPolicyResult.IsSuccessful)
            {
                result.Issues.AddRange(executionPolicyResult.Issues);
            }

            // Step 2: Check and repair module installation
            var moduleResult = await RepairModulesAsync();
            result.ActionsPerformed.AddRange(moduleResult.ActionsPerformed);
            
            if (!moduleResult.IsSuccessful)
            {
                result.Issues.AddRange(moduleResult.Issues);
            }

            result.IsSuccessful = result.Issues.Count == 0;
            result.RequiresRestart = executionPolicyResult.RequiresRestart || moduleResult.RequiresRestart;

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

    #region Private Methods

    private async Task EnsureInitializedAsync()
    {
        if (_isInitialized) return;

        await _initializationSemaphore.WaitAsync();
        try
        {
            if (_isInitialized) return;

            _logger.LogInformation("Initializing PowerCLI session state");

            _initialSessionState = InitialSessionState.CreateDefault();
            
            // Import essential modules
            var modulesToImport = new[]
            {
                "VMware.VimAutomation.Core",
                "VMware.VimAutomation.Common",
                "VMware.VimAutomation.Vds",
                "VMware.VimAutomation.Storage"
            };

            foreach (var module in modulesToImport)
            {
                try
                {
                    _initialSessionState.ImportPSModule(module);
                    _logger.LogDebug("Imported PowerCLI module: {Module}", module);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to import PowerCLI module {Module}", module);
                }
            }

            // Set PowerCLI configuration
            _initialSessionState.Variables.Add(new SessionStateVariableEntry(
                "PowerCLIConfiguration", 
                "Set-PowerCLIConfiguration -InvalidCertificateAction Ignore -Confirm:$false -DefaultVIServerMode Multiple", 
                "PowerCLI Configuration"));

            _isInitialized = true;
            _logger.LogInformation("PowerCLI session state initialized successfully");
        }
        finally
        {
            _initializationSemaphore.Release();
        }
    }

    private string BuildConnectionTestScript()
    {
        return @"
param($ServerUrl, $Username, $Password, $TimeoutSeconds)

try {
    # Configure PowerCLI
    Set-PowerCLIConfiguration -InvalidCertificateAction Ignore -Confirm:$false -DefaultVIServerMode Multiple -ErrorAction SilentlyContinue

    # Test connection
    $connection = Connect-VIServer -Server $ServerUrl -User $Username -Password $Password -ErrorAction Stop
    
    if ($connection) {
        $vcInfo = Get-View -ViewType ServiceInstance -ErrorAction Stop
        
        # Disconnect immediately after test
        Disconnect-VIServer -Server $ServerUrl -Confirm:$false -ErrorAction SilentlyContinue
        
        return [PSCustomObject]@{
            Success = $true
            Version = $vcInfo.Content.About.Version
            Build = $vcInfo.Content.About.Build
            ApiVersion = $vcInfo.Content.About.ApiVersion
            ErrorMessage = $null
            ErrorCode = $null
        }
    } else {
        return [PSCustomObject]@{
            Success = $false
            ErrorMessage = 'Failed to establish connection'
            ErrorCode = 'CONNECTION_FAILED'
        }
    }
} catch {
    $errorCode = 'UNKNOWN'
    if ($_.Exception.Message -like '*credentials*' -or $_.Exception.Message -like '*authentication*') {
        $errorCode = 'AUTHENTICATION_FAILED'
    } elseif ($_.Exception.Message -like '*network*' -or $_.Exception.Message -like '*timeout*') {
        $errorCode = 'NETWORK_ERROR'
    } elseif ($_.Exception.Message -like '*certificate*' -or $_.Exception.Message -like '*SSL*') {
        $errorCode = 'CERTIFICATE_ERROR'
    }
    
    return [PSCustomObject]@{
        Success = $false
        ErrorMessage = $_.Exception.Message
        ErrorCode = $errorCode
    }
}";
    }

    private string BuildConnectionScript()
    {
        return @"
param($ServerUrl, $Username, $Password, $TimeoutSeconds)

try {
    # Configure PowerCLI
    Set-PowerCLIConfiguration -InvalidCertificateAction Ignore -Confirm:$false -DefaultVIServerMode Multiple -ErrorAction SilentlyContinue

    # Connect to vCenter
    $connection = Connect-VIServer -Server $ServerUrl -User $Username -Password $Password -ErrorAction Stop
    
    if ($connection) {
        $vcInfo = Get-View -ViewType ServiceInstance -ErrorAction Stop
        
        return [PSCustomObject]@{
            Success = $true
            Version = $vcInfo.Content.About.Version
            Build = $vcInfo.Content.About.Build
            ApiVersion = $vcInfo.Content.About.ApiVersion
            ErrorMessage = $null
        }
    } else {
        return [PSCustomObject]@{
            Success = $false
            ErrorMessage = 'Failed to establish connection'
        }
    }
} catch {
    return [PSCustomObject]@{
        Success = $false
        ErrorMessage = $_.Exception.Message
    }
}";
    }

    private Task<PowerCLIValidationResult> CheckExecutionPolicyAsync()
    {
        var result = new PowerCLIValidationResult();
        
        try
        {
            using var powerShell = System.Management.Automation.PowerShell.Create();
            powerShell.AddScript("Get-ExecutionPolicy -Scope CurrentUser");
            var psResults = powerShell.Invoke();

            if (psResults != null && psResults.Count > 0)
            {
                var policy = psResults[0].ToString();
                if (policy == "Restricted" || policy == "Undefined")
                {
                    result.Issues.Add($"PowerShell execution policy is set to '{policy}' which prevents PowerCLI modules from loading");
                    result.Suggestions.Add("Run 'Set-ExecutionPolicy -ExecutionPolicy RemoteSigned -Scope CurrentUser -Force' to fix this issue");
                }
                else
                {
                    result.IsValid = true;
                }
            }
        }
        catch (Exception ex)
        {
            result.Issues.Add($"Failed to check execution policy: {ex.Message}");
        }

        return Task.FromResult(result);
    }

    private async Task<PowerCLIValidationResult> CheckPowerCLIModulesAsync()
    {
        var result = new PowerCLIValidationResult();
        
        try
        {
            using var powerShell = System.Management.Automation.PowerShell.Create();
            powerShell.AddScript("Get-Module -ListAvailable VMware.VimAutomation.Core | Select-Object -First 1");
            var psResults = powerShell.Invoke();

            if (psResults == null || psResults.Count == 0)
            {
                result.Issues.Add("PowerCLI Core module is not installed");
                result.Suggestions.Add("Install PowerCLI with 'Install-Module VMware.PowerCLI -Scope CurrentUser'");
            }
            else
            {
                result.IsValid = true;
                result.VersionInfo = await GetVersionInfoAsync();
            }
        }
        catch (Exception ex)
        {
            result.Issues.Add($"Failed to check PowerCLI modules: {ex.Message}");
        }

        return result;
    }

    private Task<PowerCLIValidationResult> TestBasicFunctionalityAsync()
    {
        var result = new PowerCLIValidationResult();
        
        try
        {
            using var powerShell = System.Management.Automation.PowerShell.Create();
            powerShell.AddScript("Import-Module VMware.VimAutomation.Core -ErrorAction Stop; Get-PowerCLIVersion");
            var psResults = powerShell.Invoke();

            if (powerShell.Streams.Error.Count > 0)
            {
                result.Issues.Add($"PowerCLI basic functionality test failed: {powerShell.Streams.Error[0].Exception.Message}");
            }
            else
            {
                result.IsValid = true;
            }
        }
        catch (Exception ex)
        {
            result.Issues.Add($"PowerCLI basic functionality test failed: {ex.Message}");
        }

        return Task.FromResult(result);
    }

    private Task<PowerCLIRepairResult> RepairExecutionPolicyAsync()
    {
        var result = new PowerCLIRepairResult();
        
        try
        {
            using var powerShell = System.Management.Automation.PowerShell.Create();
            powerShell.AddScript("Set-ExecutionPolicy -ExecutionPolicy RemoteSigned -Scope CurrentUser -Force");
            powerShell.Invoke();

            if (powerShell.Streams.Error.Count == 0)
            {
                result.IsSuccessful = true;
                result.ActionsPerformed.Add("Set execution policy to RemoteSigned for CurrentUser scope");
            }
            else
            {
                result.Issues.Add($"Failed to set execution policy: {powerShell.Streams.Error[0].Exception.Message}");
            }
        }
        catch (Exception ex)
        {
            result.Issues.Add($"Failed to repair execution policy: {ex.Message}");
        }

        return Task.FromResult(result);
    }

    private Task<PowerCLIRepairResult> RepairModulesAsync()
    {
        var result = new PowerCLIRepairResult();
        
        try
        {
            // Check if PowerCLI is installed
            using var powerShell = System.Management.Automation.PowerShell.Create();
            powerShell.AddScript("Get-Module -ListAvailable VMware.PowerCLI | Select-Object -First 1");
            var psResults = powerShell.Invoke();

            if (psResults == null || psResults.Count == 0)
            {
                // Install PowerCLI
                powerShell.Commands.Clear();
                powerShell.AddScript("Install-Module VMware.PowerCLI -Scope CurrentUser -AllowClobber -Force");
                powerShell.Invoke();

                if (powerShell.Streams.Error.Count == 0)
                {
                    result.ActionsPerformed.Add("Installed VMware.PowerCLI module");
                    result.IsSuccessful = true;
                }
                else
                {
                    result.Issues.Add($"Failed to install PowerCLI: {powerShell.Streams.Error[0].Exception.Message}");
                }
            }
            else
            {
                result.IsSuccessful = true;
                result.ActionsPerformed.Add("PowerCLI modules are already installed");
            }
        }
        catch (Exception ex)
        {
            result.Issues.Add($"Failed to repair PowerCLI modules: {ex.Message}");
        }

        return Task.FromResult(result);
    }

    private string ExtractMeaningfulError(ErrorRecord error)
    {
        var message = error.Exception.Message;
        
        // Improve common error messages
        if (message.Contains("Connect-VIServer"))
        {
            if (message.Contains("authentication") || message.Contains("credentials"))
                return "Invalid username or password";
            if (message.Contains("certificate") || message.Contains("SSL"))
                return "SSL certificate validation failed";
            if (message.Contains("network") || message.Contains("timeout"))
                return "Network connection failed or timed out";
        }

        return message;
    }

    private string ClassifyError(ErrorRecord error)
    {
        var message = error.Exception.Message.ToLowerInvariant();
        
        if (message.Contains("authentication") || message.Contains("credentials") || message.Contains("password"))
            return "AUTHENTICATION_FAILED";
        if (message.Contains("certificate") || message.Contains("ssl"))
            return "CERTIFICATE_ERROR";
        if (message.Contains("network") || message.Contains("timeout"))
            return "NETWORK_ERROR";
        if (message.Contains("powercli") || message.Contains("module"))
            return "POWERCLI_ERROR";
        
        return "UNKNOWN";
    }

    private T? GetPropertyValue<T>(PSObject psObject, string propertyName)
    {
        try
        {
            var property = psObject.Properties[propertyName];
            if (property?.Value != null)
            {
                if (typeof(T) == typeof(bool) && property.Value is string strValue)
                {
                    return (T)(object)bool.Parse(strValue);
                }
                return (T)property.Value;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get property {PropertyName} from PSObject", propertyName);
        }
        return default(T);
    }

    #endregion

    public void Dispose()
    {
        foreach (var session in _activeSessions.Values.ToList())
        {
            try
            {
                DisconnectAsync(session).Wait(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing session {SessionId}", session.SessionId);
            }
        }
        
        _activeSessions.Clear();
        _initializationSemaphore.Dispose();
    }
}

/// <summary>
/// Configuration options for PowerCLI service
/// </summary>
public class PowerCLIOptions
{
    public const string SectionName = "PowerCLI";

    public int ConnectionTimeoutSeconds { get; set; } = 60;
    public int CommandTimeoutSeconds { get; set; } = 300;
    public bool IgnoreInvalidCertificates { get; set; } = true;
    public bool EnableVerboseLogging { get; set; } = false;
    public string PreferredPowerCLIVersion { get; set; } = string.Empty;
} 