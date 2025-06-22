using VMwareGUITools.Core.Models;

namespace VMwareGUITools.Infrastructure.Scheduling;

/// <summary>
/// Interface for managing scheduled check executions
/// </summary>
public interface ISchedulingService
{
    /// <summary>
    /// Schedules checks for a specific host based on its host profile
    /// </summary>
    /// <param name="host">The host to schedule checks for</param>
    /// <param name="vCenter">The vCenter the host belongs to</param>
    /// <returns>List of created schedule IDs</returns>
    Task<List<string>> ScheduleHostChecksAsync(Host host, VCenter vCenter);

    /// <summary>
    /// Schedules checks for all hosts in a cluster
    /// </summary>
    /// <param name="cluster">The cluster to schedule checks for</param>
    /// <param name="vCenter">The vCenter the cluster belongs to</param>
    /// <returns>List of created schedule IDs</returns>
    Task<List<string>> ScheduleClusterChecksAsync(Cluster cluster, VCenter vCenter);

    /// <summary>
    /// Schedules checks for all hosts across all vCenters
    /// </summary>
    /// <returns>List of created schedule IDs</returns>
    Task<List<string>> ScheduleGlobalChecksAsync();

    /// <summary>
    /// Creates a custom schedule for specific checks
    /// </summary>
    /// <param name="schedule">The schedule definition</param>
    /// <returns>The created schedule ID</returns>
    Task<string> CreateScheduleAsync(CheckSchedule schedule);

    /// <summary>
    /// Updates an existing schedule
    /// </summary>
    /// <param name="scheduleId">The schedule ID to update</param>
    /// <param name="schedule">The updated schedule definition</param>
    Task UpdateScheduleAsync(string scheduleId, CheckSchedule schedule);

    /// <summary>
    /// Deletes a schedule
    /// </summary>
    /// <param name="scheduleId">The schedule ID to delete</param>
    Task DeleteScheduleAsync(string scheduleId);

    /// <summary>
    /// Pauses a schedule
    /// </summary>
    /// <param name="scheduleId">The schedule ID to pause</param>
    Task PauseScheduleAsync(string scheduleId);

    /// <summary>
    /// Resumes a paused schedule
    /// </summary>
    /// <param name="scheduleId">The schedule ID to resume</param>
    Task ResumeScheduleAsync(string scheduleId);

    /// <summary>
    /// Gets all active schedules
    /// </summary>
    /// <returns>List of active schedules</returns>
    Task<List<ScheduleInfo>> GetActiveSchedulesAsync();

    /// <summary>
    /// Triggers an immediate execution of a scheduled job
    /// </summary>
    /// <param name="scheduleId">The schedule ID to trigger</param>
    Task TriggerScheduleAsync(string scheduleId);

    /// <summary>
    /// Gets the next execution times for a schedule
    /// </summary>
    /// <param name="scheduleId">The schedule ID</param>
    /// <param name="count">Number of future executions to return</param>
    /// <returns>List of next execution times</returns>
    Task<List<DateTime>> GetNextExecutionTimesAsync(string scheduleId, int count = 5);
}

/// <summary>
/// Represents a check schedule definition
/// </summary>
public class CheckSchedule
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string CronExpression { get; set; } = string.Empty;
    public List<int> HostIds { get; set; } = new List<int>();
    public List<int> ClusterIds { get; set; } = new List<int>();
    public List<int> CheckDefinitionIds { get; set; } = new List<int>();
    public bool IsEnabled { get; set; } = true;
    public DateTime StartDate { get; set; } = DateTime.UtcNow;
    public DateTime? EndDate { get; set; }
    public int Priority { get; set; } = 1;
    public int MaxConcurrency { get; set; } = 5;
    public bool NotifyOnFailure { get; set; } = true;
    public bool NotifyOnSuccess { get; set; } = false;
    public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
}

/// <summary>
/// Information about an active schedule
/// </summary>
public class ScheduleInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string CronExpression { get; set; } = string.Empty;
    public DateTime? NextFireTime { get; set; }
    public DateTime? PreviousFireTime { get; set; }
    public ScheduleState State { get; set; }
    public int TotalFireCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastModified { get; set; }
}

/// <summary>
/// Enumeration of schedule states
/// </summary>
public enum ScheduleState
{
    Normal,
    Paused,
    Complete,
    Error,
    Blocked
} 