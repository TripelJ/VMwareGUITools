using System.ComponentModel.DataAnnotations;

namespace VMwareGUITools.Core.Models;

/// <summary>
/// Represents the result of executing a check against a host
/// </summary>
public class CheckResult
{
    public int Id { get; set; }

    [Required]
    public int HostId { get; set; }

    [Required]
    public int CheckId { get; set; }

    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    public CheckStatus Status { get; set; } = CheckStatus.Unknown;

    public string Output { get; set; } = string.Empty;

    public string Details { get; set; } = string.Empty;

    public string ErrorMessage { get; set; } = string.Empty;

    public int ExecutionTimeMs { get; set; } = 0;

    public bool IsManualRun { get; set; } = false;

    public string? RunBy { get; set; }

    // Navigation properties
    public virtual Host Host { get; set; } = null!;
    public virtual CheckDefinition CheckDefinition { get; set; } = null!;

    /// <summary>
    /// Gets a summary of the check result
    /// </summary>
    public string Summary
    {
        get
        {
            var summary = $"{CheckDefinition?.Name ?? "Unknown Check"}: {Status}";
            if (!string.IsNullOrEmpty(Output) && Output.Length > 100)
            {
                summary += $" - {Output.Substring(0, 97)}...";
            }
            else if (!string.IsNullOrEmpty(Output))
            {
                summary += $" - {Output}";
            }
            return summary;
        }
    }

    /// <summary>
    /// Gets whether this result indicates a problem that requires attention
    /// </summary>
    public bool RequiresAttention => Status == CheckStatus.Critical || Status == CheckStatus.Warning;

    /// <summary>
    /// Gets a friendly display of the execution time
    /// </summary>
    public string ExecutionTimeDisplay
    {
        get
        {
            if (ExecutionTimeMs < 1000) return $"{ExecutionTimeMs}ms";
            if (ExecutionTimeMs < 60000) return $"{ExecutionTimeMs / 1000.0:F1}s";
            return $"{ExecutionTimeMs / 60000.0:F1}min";
        }
    }
}

/// <summary>
/// Represents the status of a check execution
/// </summary>
public enum CheckStatus
{
    Unknown,
    Success,
    Warning,
    Critical,
    Error,
    Timeout,
    Skipped
} 