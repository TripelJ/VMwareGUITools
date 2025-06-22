namespace VMwareGUITools.Infrastructure.PowerShell;

/// <summary>
/// Interface for PowerShell script execution and PowerCLI integration
/// </summary>
public interface IPowerShellService
{
    /// <summary>
    /// Executes a PowerShell script and returns the result
    /// </summary>
    /// <param name="script">The PowerShell script to execute</param>
    /// <param name="parameters">Parameters to pass to the script</param>
    /// <param name="timeoutSeconds">Execution timeout in seconds</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>PowerShell execution result</returns>
    Task<PowerShellResult> ExecuteScriptAsync(string script, Dictionary<string, object>? parameters = null, int timeoutSeconds = 300, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a PowerCLI command and returns the result
    /// </summary>
    /// <param name="command">The PowerCLI command to execute</param>
    /// <param name="parameters">Parameters to pass to the command</param>
    /// <param name="timeoutSeconds">Execution timeout in seconds</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>PowerShell execution result</returns>
    Task<PowerShellResult> ExecutePowerCLICommandAsync(string command, Dictionary<string, object>? parameters = null, int timeoutSeconds = 300, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if PowerCLI modules are available
    /// </summary>
    /// <returns>True if PowerCLI is available</returns>
    Task<bool> IsPowerCLIAvailableAsync();

    /// <summary>
    /// Gets the version of installed PowerCLI
    /// </summary>
    /// <returns>PowerCLI version information</returns>
    Task<PowerCLIVersionInfo> GetPowerCLIVersionAsync();

    /// <summary>
    /// Initializes PowerCLI session with required modules
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if initialization was successful</returns>
    Task<bool> InitializePowerCLIAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Represents the result of a PowerShell script execution
/// </summary>
public class PowerShellResult
{
    public bool IsSuccess { get; set; }
    public string Output { get; set; } = string.Empty;
    public string ErrorOutput { get; set; } = string.Empty;
    public List<string> Warnings { get; set; } = new List<string>();
    public List<string> Verbose { get; set; } = new List<string>();
    public TimeSpan ExecutionTime { get; set; }
    public Exception? Exception { get; set; }
    public List<object> Objects { get; set; } = new List<object>();
}

/// <summary>
/// Contains PowerCLI version and module information
/// </summary>
public class PowerCLIVersionInfo
{
    public string Version { get; set; } = string.Empty;
    public List<PowerCLIModuleInfo> Modules { get; set; } = new List<PowerCLIModuleInfo>();
    public bool IsCompatible { get; set; }
    public string? CompatibilityMessage { get; set; }
}

/// <summary>
/// Information about a PowerCLI module
/// </summary>
public class PowerCLIModuleInfo
{
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public bool IsLoaded { get; set; }
    public bool IsAvailable { get; set; }
} 