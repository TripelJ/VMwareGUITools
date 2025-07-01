using System.ComponentModel.DataAnnotations;
using System.IO;

namespace VMwareGUITools.Core.Models;

/// <summary>
/// Represents service configuration that can be updated by the WPF application
/// </summary>
public class ServiceConfiguration
{
    [Key]
    public int Id { get; set; }
    
    /// <summary>
    /// Configuration key identifier
    /// </summary>
    [Required]
    [MaxLength(100)]
    public string Key { get; set; } = string.Empty;
    
    /// <summary>
    /// Configuration value as JSON
    /// </summary>
    public string Value { get; set; } = string.Empty;
    
    /// <summary>
    /// Configuration category (e.g., "PowerCLI", "CheckExecution", "Scheduling")
    /// </summary>
    [MaxLength(50)]
    public string Category { get; set; } = string.Empty;
    
    /// <summary>
    /// Configuration description for UI display
    /// </summary>
    [MaxLength(500)]
    public string Description { get; set; } = string.Empty;
    
    /// <summary>
    /// Whether this configuration requires service restart
    /// </summary>
    public bool RequiresRestart { get; set; } = false;
    
    /// <summary>
    /// When this configuration was last updated
    /// </summary>
    public DateTime LastModified { get; set; } = DateTime.UtcNow;
    
    /// <summary>
    /// Who last modified this configuration
    /// </summary>
    [MaxLength(100)]
    public string ModifiedBy { get; set; } = "System";
}

/// <summary>
/// Service status information
/// </summary>
public class ServiceStatus
{
    [Key]
    public int Id { get; set; }
    
    /// <summary>
    /// Service status (Running, Stopped, Starting, Stopping)
    /// </summary>
    [MaxLength(20)]
    public string Status { get; set; } = "Unknown";
    
    /// <summary>
    /// Last heartbeat from the service
    /// </summary>
    public DateTime LastHeartbeat { get; set; } = DateTime.UtcNow;
    
    /// <summary>
    /// Service version
    /// </summary>
    [MaxLength(20)]
    public string Version { get; set; } = string.Empty;
    
    /// <summary>
    /// Number of active check executions
    /// </summary>
    public int ActiveExecutions { get; set; } = 0;
    
    /// <summary>
    /// Next scheduled execution time
    /// </summary>
    public DateTime? NextExecution { get; set; }
    
    /// <summary>
    /// Service statistics as JSON
    /// </summary>
    public string Statistics { get; set; } = "{}";
}

/// <summary>
/// Service command for WPF to service communication
/// </summary>
public class ServiceCommand
{
    [Key]
    public int Id { get; set; }
    
    /// <summary>
    /// Command type identifier
    /// </summary>
    [Required]
    [MaxLength(50)]
    public string CommandType { get; set; } = string.Empty;
    
    /// <summary>
    /// Command parameters as JSON
    /// </summary>
    public string Parameters { get; set; } = "{}";
    
    /// <summary>
    /// Command result as JSON
    /// </summary>
    public string Result { get; set; } = string.Empty;
    
    /// <summary>
    /// Command status (Pending, Processing, Completed, Failed)
    /// </summary>
    [MaxLength(20)]
    public string Status { get; set; } = "Pending";
    
    /// <summary>
    /// Error message if command failed
    /// </summary>
    public string ErrorMessage { get; set; } = string.Empty;
    
    /// <summary>
    /// When this command was created
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    
    /// <summary>
    /// When this command was processed by the service
    /// </summary>
    public DateTime? ProcessedAt { get; set; }
    
    /// <summary>
    /// Command priority (1 = highest, 10 = lowest)
    /// </summary>
    public int Priority { get; set; } = 5;
}

/// <summary>
/// Service command types
/// </summary>
public static class ServiceCommandTypes
{
    public const string ExecuteCheck = "ExecuteCheck";
    public const string ValidatePowerCLI = "ValidatePowerCLI";
    public const string GetServiceStatus = "GetServiceStatus";
    public const string ReloadConfiguration = "ReloadConfiguration";
    public const string GetOverviewData = "GetOverviewData";
    public const string GetInfrastructureData = "GetInfrastructureData";
    public const string ConnectVCenter = "ConnectVCenter";
    public const string TestVCenterConnection = "TestVCenterConnection";
}

/// <summary>
/// Configuration options for database path resolution
/// </summary>
public class DatabaseOptions
{
    public const string SectionName = "Database";
    
    /// <summary>
    /// Relative path from the executable to the database file
    /// </summary>
    public string RelativePathFromExecutable { get; set; } = "vmware-gui-tools.db";
    
    /// <summary>
    /// Database file name
    /// </summary>
    public string FileName { get; set; } = "vmware-gui-tools.db";
    
    /// <summary>
    /// Resolves the actual database path based on the executable location
    /// </summary>
    /// <returns>Full path to the database file</returns>
    public string ResolveDatabasePath()
    {
        var executableDirectory = AppDomain.CurrentDomain.BaseDirectory;
        var databasePath = Path.Combine(executableDirectory, RelativePathFromExecutable);
        return Path.GetFullPath(databasePath);
    }
} 