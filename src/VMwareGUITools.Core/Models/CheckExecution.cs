using System.ComponentModel.DataAnnotations;

namespace VMwareGUITools.Core.Models;

/// <summary>
/// Represents a check execution request containing the host, check definition, and vCenter context
/// </summary>
public class CheckExecution
{
    /// <summary>
    /// The host on which to execute the check
    /// </summary>
    [Required]
    public Host Host { get; set; } = null!;

    /// <summary>
    /// The check definition to execute
    /// </summary>
    [Required]
    public CheckDefinition CheckDefinition { get; set; } = null!;

    /// <summary>
    /// The vCenter server context for the execution
    /// </summary>
    [Required]
    public VCenter VCenter { get; set; } = null!;

    /// <summary>
    /// Whether this execution was triggered manually or automatically
    /// </summary>
    public bool IsManualRun { get; set; } = false;

    /// <summary>
    /// When this execution was scheduled/requested
    /// </summary>
    public DateTime ScheduledAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Priority of this execution (higher values = higher priority)
    /// </summary>
    public int Priority { get; set; } = 0;

    /// <summary>
    /// Maximum execution timeout in seconds
    /// </summary>
    public int TimeoutSeconds { get; set; } = 300;
} 