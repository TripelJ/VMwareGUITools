using System.ComponentModel.DataAnnotations;

namespace VMwareGUITools.Core.Models;

/// <summary>
/// Represents a scheduled check execution configuration
/// </summary>
public class CheckSchedule
{
    /// <summary>
    /// Unique name for this schedule
    /// </summary>
    [Required]
    [StringLength(100)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable description of this schedule
    /// </summary>
    [StringLength(500)]
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Cron expression defining when to run this schedule
    /// </summary>
    [Required]
    public string CronExpression { get; set; } = string.Empty;

    /// <summary>
    /// When this schedule should start (defaults to now)
    /// </summary>
    public DateTimeOffset StartDate { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// When this schedule should end (optional)
    /// </summary>
    public DateTimeOffset? EndDate { get; set; }

    /// <summary>
    /// Whether this schedule is enabled
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// List of host IDs to include in this schedule
    /// </summary>
    public List<int> HostIds { get; set; } = new List<int>();

    /// <summary>
    /// List of cluster IDs to include in this schedule
    /// </summary>
    public List<int> ClusterIds { get; set; } = new List<int>();

    /// <summary>
    /// List of specific check definition IDs to run (if empty, runs all applicable checks)
    /// </summary>
    public List<int> CheckDefinitionIds { get; set; } = new List<int>();

    /// <summary>
    /// Maximum number of concurrent check executions
    /// </summary>
    public int MaxConcurrency { get; set; } = 5;

    /// <summary>
    /// Whether to send notifications on check failures
    /// </summary>
    public bool NotifyOnFailure { get; set; } = true;

    /// <summary>
    /// Whether to send notifications on successful completion
    /// </summary>
    public bool NotifyOnSuccess { get; set; } = false;

    /// <summary>
    /// When this schedule was created
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When this schedule was last updated
    /// </summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Information about a schedule's current state
/// </summary>
public class ScheduleInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTime? NextFireTime { get; set; }
    public DateTime? PreviousFireTime { get; set; }
    public ScheduleState State { get; set; }
    public int TotalFireCount { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Possible states for a schedule
/// </summary>
public enum ScheduleState
{
    Normal,
    Paused,
    Complete,
    Error,
    Blocked
} 