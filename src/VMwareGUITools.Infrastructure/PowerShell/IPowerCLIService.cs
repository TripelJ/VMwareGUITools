namespace VMwareGUITools.Infrastructure.PowerShell;

/// <summary>
/// Dedicated interface for PowerCLI operations with improved session management
/// </summary>
public interface IPowerCLIService
{
    /// <summary>
    /// Validates PowerCLI installation and configuration
    /// </summary>
    Task<PowerCLIValidationResult> ValidatePowerCLIAsync();

    /// <summary>
    /// Tests connection to a vCenter server
    /// </summary>
    Task<PowerCLIConnectionResult> TestConnectionAsync(string serverUrl, string username, string password, int timeoutSeconds = 60);

    /// <summary>
    /// Establishes a persistent connection to vCenter
    /// </summary>
    Task<PowerCLISession> ConnectAsync(string serverUrl, string username, string password, int timeoutSeconds = 60);

    /// <summary>
    /// Disconnects from vCenter
    /// </summary>
    Task DisconnectAsync(PowerCLISession session);

    /// <summary>
    /// Executes a PowerCLI command within an existing session
    /// </summary>
    Task<PowerCLIResult> ExecuteCommandAsync(PowerCLISession session, string command, Dictionary<string, object>? parameters = null, int timeoutSeconds = 300);

    /// <summary>
    /// Gets PowerCLI version and module information
    /// </summary>
    Task<PowerCLIVersionInfo> GetVersionInfoAsync();

    /// <summary>
    /// Repairs common PowerCLI issues (execution policy, module conflicts)
    /// </summary>
    Task<PowerCLIRepairResult> RepairPowerCLIAsync();
}

/// <summary>
/// Represents the result of PowerCLI validation
/// </summary>
public class PowerCLIValidationResult
{
    public bool IsValid { get; set; }
    public string? ErrorMessage { get; set; }
    public List<string> Issues { get; set; } = new();
    public List<string> Suggestions { get; set; } = new();
    public PowerCLIVersionInfo? VersionInfo { get; set; }
    public bool RequiresRepair { get; set; }
}

/// <summary>
/// Represents the result of a PowerCLI connection attempt
/// </summary>
public class PowerCLIConnectionResult
{
    public bool IsSuccessful { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ErrorCode { get; set; }
    public TimeSpan ResponseTime { get; set; }
    public string? ServerVersion { get; set; }
    public string? ServerBuild { get; set; }
    public string? ApiVersion { get; set; }
    public bool IsSecure { get; set; }
}

/// <summary>
/// Represents an active PowerCLI session
/// </summary>
public class PowerCLISession : IDisposable
{
    public string SessionId { get; set; } = Guid.NewGuid().ToString();
    public string ServerUrl { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public DateTime ConnectedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastActivity { get; set; } = DateTime.UtcNow;
    public bool IsConnected { get; set; }
    public string? ServerVersion { get; set; }
    public string? ApiVersion { get; set; }
    
    internal object? InternalSession { get; set; } // For storing PowerShell runspace
    
    public void Dispose()
    {
        IsConnected = false;
        if (InternalSession is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}

/// <summary>
/// Represents the result of a PowerCLI command execution
/// </summary>
public class PowerCLIResult
{
    public bool IsSuccess { get; set; }
    public string Output { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
    public List<object> Objects { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public TimeSpan ExecutionTime { get; set; }
    public Exception? Exception { get; set; }
}

/// <summary>
/// Represents the result of PowerCLI repair operations
/// </summary>
public class PowerCLIRepairResult
{
    public bool IsSuccessful { get; set; }
    public List<string> ActionsPerformed { get; set; } = new();
    public List<string> Issues { get; set; } = new();
    public string? ErrorMessage { get; set; }
    public bool RequiresRestart { get; set; }
} 