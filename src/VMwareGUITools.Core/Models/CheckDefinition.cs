using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace VMwareGUITools.Core.Models;

/// <summary>
/// Represents a check definition that can be executed against hosts
/// </summary>
public class CheckDefinition
{
    public int Id { get; set; }

    [Required]
    public int CategoryId { get; set; }

    [Required]
    [StringLength(100)]
    public string Name { get; set; } = string.Empty;

    [StringLength(500)]
    public string Description { get; set; } = string.Empty;

    public CheckExecutionType ExecutionType { get; set; } = CheckExecutionType.PowerCLI;

    [Required]
    public string ScriptPath { get; set; } = string.Empty;

    public string Script { get; set; } = string.Empty;

    public string Parameters { get; set; } = "{}"; // JSON object for parameters

    public string Thresholds { get; set; } = "{}"; // JSON object for thresholds

    public string? ThresholdCriteria { get; set; }

    public CheckSeverity DefaultSeverity { get; set; } = CheckSeverity.Warning;

    public bool IsEnabled { get; set; } = true;

    public int TimeoutSeconds { get; set; } = 300; // 5 minutes default

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties
    public virtual CheckCategory Category { get; set; } = null!;
    public virtual ICollection<CheckResult> CheckResults { get; set; } = new List<CheckResult>();

    /// <summary>
    /// Gets the parsed parameters from the JSON string
    /// </summary>
    public Dictionary<string, object> GetParameters()
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object>>(Parameters) ?? new Dictionary<string, object>();
        }
        catch
        {
            return new Dictionary<string, object>();
        }
    }

    /// <summary>
    /// Sets the parameters as JSON string
    /// </summary>
    public void SetParameters(Dictionary<string, object> parameters)
    {
        Parameters = JsonSerializer.Serialize(parameters, new JsonSerializerOptions 
        { 
            WriteIndented = true 
        });
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Gets the parsed thresholds from the JSON string
    /// </summary>
    public Dictionary<string, object> GetThresholds()
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object>>(Thresholds) ?? new Dictionary<string, object>();
        }
        catch
        {
            return new Dictionary<string, object>();
        }
    }

    /// <summary>
    /// Sets the thresholds as JSON string
    /// </summary>
    public void SetThresholds(Dictionary<string, object> thresholds)
    {
        Thresholds = JsonSerializer.Serialize(thresholds, new JsonSerializerOptions 
        { 
            WriteIndented = true 
        });
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Gets whether this check has threshold criteria defined
    /// </summary>
    public bool HasThreshold => !string.IsNullOrEmpty(ThresholdCriteria) || GetThresholds().Count > 0;
}

/// <summary>
/// Represents how a check should be executed
/// </summary>
public enum CheckExecutionType
{
    [Obsolete("PowerCLI is deprecated. Use vSphereRestAPI instead.")]
    PowerCLI,
    SSH,
    vSphereAPI,
    WinRM,
    Custom,
    vSphereRestAPI
}

/// <summary>
/// Represents the severity level of a check
/// </summary>
public enum CheckSeverity
{
    Info,
    Warning,
    Critical
} 