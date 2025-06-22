using VMwareGUITools.Core.Models;

namespace VMwareGUITools.Infrastructure.Notifications;

/// <summary>
/// Interface for sending notifications through various channels
/// </summary>
public interface INotificationService
{
    /// <summary>
    /// Sends a notification about check results
    /// </summary>
    /// <param name="notification">The notification to send</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task SendNotificationAsync(CheckNotification notification, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a batch notification about multiple check results
    /// </summary>
    /// <param name="notifications">List of notifications to send</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task SendBatchNotificationAsync(List<CheckNotification> notifications, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a summary report for a scheduled execution
    /// </summary>
    /// <param name="summary">The execution summary</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task SendExecutionSummaryAsync(ExecutionSummary summary, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tests a notification channel configuration
    /// </summary>
    /// <param name="channelType">The type of notification channel to test</param>
    /// <param name="configuration">The channel configuration</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Test result</returns>
    Task<NotificationTestResult> TestNotificationChannelAsync(NotificationChannelType channelType, Dictionary<string, object> configuration, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets available notification channels
    /// </summary>
    /// <returns>List of available notification channel types</returns>
    Task<List<NotificationChannelInfo>> GetAvailableChannelsAsync();

    /// <summary>
    /// Registers a notification channel
    /// </summary>
    /// <param name="channel">The notification channel to register</param>
    Task RegisterChannelAsync(NotificationChannel channel);

    /// <summary>
    /// Unregisters a notification channel
    /// </summary>
    /// <param name="channelId">The ID of the channel to unregister</param>
    Task UnregisterChannelAsync(string channelId);
}

/// <summary>
/// Represents a check notification
/// </summary>
public class CheckNotification
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public NotificationSeverity Severity { get; set; } = NotificationSeverity.Info;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public List<CheckResult> CheckResults { get; set; } = new List<CheckResult>();
    public string? HostName { get; set; }
    public string? ClusterName { get; set; }
    public string? VCenterName { get; set; }
    public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    public List<NotificationChannelType> TargetChannels { get; set; } = new List<NotificationChannelType>();
    public bool IsGrouped { get; set; } = false;
    public string? GroupKey { get; set; }
}

/// <summary>
/// Represents an execution summary for notifications
/// </summary>
public class ExecutionSummary
{
    public string ScheduleName { get; set; } = string.Empty;
    public DateTime ExecutionTime { get; set; } = DateTime.UtcNow;
    public TimeSpan Duration { get; set; }
    public int TotalChecks { get; set; }
    public int PassedChecks { get; set; }
    public int FailedChecks { get; set; }
    public int SkippedChecks { get; set; }
    public List<string> AffectedHosts { get; set; } = new List<string>();
    public List<string> AffectedClusters { get; set; } = new List<string>();
    public List<CheckResult> FailedResults { get; set; } = new List<CheckResult>();
    public Dictionary<string, object> Statistics { get; set; } = new Dictionary<string, object>();
}

/// <summary>
/// Represents a notification channel configuration
/// </summary>
public class NotificationChannel
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public NotificationChannelType Type { get; set; }
    public Dictionary<string, object> Configuration { get; set; } = new Dictionary<string, object>();
    public bool IsEnabled { get; set; } = true;
    public List<NotificationSeverity> EnabledSeverities { get; set; } = new List<NotificationSeverity> { NotificationSeverity.Warning, NotificationSeverity.Error, NotificationSeverity.Critical };
    public List<string> EnabledCategories { get; set; } = new List<string>();
    public TimeSpan? QuietPeriod { get; set; }
    public DateTime? LastNotificationSent { get; set; }
    public Dictionary<string, object> Filters { get; set; } = new Dictionary<string, object>();
}

/// <summary>
/// Information about an available notification channel
/// </summary>
public class NotificationChannelInfo
{
    public NotificationChannelType Type { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsAvailable { get; set; }
    public List<string> RequiredConfigurationKeys { get; set; } = new List<string>();
    public List<string> OptionalConfigurationKeys { get; set; } = new List<string>();
}

/// <summary>
/// Result of testing a notification channel
/// </summary>
public class NotificationTestResult
{
    public bool IsSuccessful { get; set; }
    public string? ErrorMessage { get; set; }
    public TimeSpan ResponseTime { get; set; }
    public Dictionary<string, object> Details { get; set; } = new Dictionary<string, object>();
}

/// <summary>
/// Types of notification channels
/// </summary>
public enum NotificationChannelType
{
    Email,
    Slack,
    MicrosoftTeams,
    Discord,
    Webhook,
    SMS,
    OpsGenie,
    PagerDuty,
    Zabbix,
    SNMP
}

/// <summary>
/// Notification severity levels
/// </summary>
public enum NotificationSeverity
{
    Info,
    Warning,
    Error,
    Critical
} 