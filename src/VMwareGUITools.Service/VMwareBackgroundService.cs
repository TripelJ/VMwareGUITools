using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VMwareGUITools.Infrastructure.Services;
using VMwareGUITools.Infrastructure.Scheduling;
using VMwareGUITools.Data;
using Microsoft.EntityFrameworkCore;
using VMwareGUITools.Core.Models;
using Quartz;
using Quartz.Impl.Triggers;
using System.Diagnostics;

namespace VMwareGUITools.Service;

/// <summary>
/// Background service that manages the VMware GUI Tools service operations
/// </summary>
public class VMwareBackgroundService : BackgroundService
{
    private readonly ILogger<VMwareBackgroundService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly Timer _heartbeatTimer;
    private readonly Timer _jobMonitorTimer;
    private bool _jobsInitialized = false;

    public VMwareBackgroundService(
        ILogger<VMwareBackgroundService> logger,
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        
        // Initialize timers - these will start when the service starts
        _heartbeatTimer = new Timer(UpdateHeartbeat, null, Timeout.Infinite, Timeout.Infinite);
        _jobMonitorTimer = new Timer(MonitorAndMaintainJobs, null, Timeout.Infinite, Timeout.Infinite);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("=== VMware GUI Tools Service ExecuteAsync Starting ===");
        _logger.LogInformation("Process ID: {ProcessId}", Environment.ProcessId);
        _logger.LogInformation("Machine Name: {MachineName}", Environment.MachineName);
        _logger.LogInformation("Service Start Time: {StartTime}", DateTime.UtcNow);

        try
        {
            // Initialize the service configuration manager
            using var scope = _serviceProvider.CreateScope();
            var serviceConfigManager = scope.ServiceProvider.GetRequiredService<IServiceConfigurationManager>();
            
            // Set initial status to starting
            await serviceConfigManager.UpdateServiceStatusAsync(
                status: "Starting",
                activeExecutions: 0,
                nextExecution: null,
                statistics: new
                {
                    ServiceStartTime = DateTime.UtcNow,
                    ProcessId = Environment.ProcessId,
                    MachineName = Environment.MachineName,
                    Phase = "Initializing"
                });
            
            _logger.LogInformation("ServiceConfigurationManager initialized - Initial status set to 'Starting'");

            // Initialize scheduled jobs for data refresh and health checks
            await InitializeScheduledJobsAsync();

            // Now set status to running since initialization is complete
            await serviceConfigManager.UpdateServiceStatusAsync(
                status: "Running",
                activeExecutions: 0,
                nextExecution: null,
                statistics: new
                {
                    ServiceStartTime = DateTime.UtcNow,
                    ProcessId = Environment.ProcessId,
                    MachineName = Environment.MachineName,
                    Phase = "Running",
                    JobsInitialized = _jobsInitialized
                });

            _logger.LogInformation("=== Service Status Changed to 'Running' ===");

            // Start heartbeat timer (every 10 seconds)
            _heartbeatTimer.Change(TimeSpan.Zero, TimeSpan.FromSeconds(10));
            
            // Start job monitoring timer (every 5 minutes)
            _jobMonitorTimer.Change(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5));

            _logger.LogInformation("=== VMware GUI Tools Service Fully Operational ===");

            // Keep the service running
            while (!stoppingToken.IsCancellationRequested)
            {
                // Main service loop - just wait and let timers handle the work
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("VMware GUI Tools Service is stopping");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "VMware GUI Tools Service encountered an error");
            
            // Set error status
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var serviceConfigManager = scope.ServiceProvider.GetRequiredService<IServiceConfigurationManager>();
                await serviceConfigManager.UpdateServiceStatusAsync(
                    status: "Error",
                    activeExecutions: 0,
                    nextExecution: null,
                    statistics: new { Error = ex.Message, ErrorTime = DateTime.UtcNow });
            }
            catch { /* Ignore errors during error reporting */ }
            
            throw;
        }
    }

    /// <summary>
    /// Initialize scheduled jobs for data refresh and health checks
    /// </summary>
    private async Task InitializeScheduledJobsAsync()
    {
        try
        {
            _logger.LogInformation("Initializing scheduled jobs...");

            using var scope = _serviceProvider.CreateScope();
            var schedulingService = scope.ServiceProvider.GetRequiredService<ISchedulingService>();
            var dbContext = scope.ServiceProvider.GetRequiredService<VMwareDbContext>();

            // Get all vCenters from the database
            var vCenters = await dbContext.VCenters
                .Where(v => v.Enabled)
                .Include(v => v.Clusters)
                .Include(v => v.Hosts)
                .ToListAsync();

            _logger.LogInformation("Found {VCenterCount} enabled vCenters", vCenters.Count);

            if (vCenters.Any())
            {
                // 1. Schedule global data refresh jobs
                await ScheduleDataRefreshJobsAsync(schedulingService);
                
                // 2. Schedule health check jobs for each vCenter
                foreach (var vCenter in vCenters)
                {
                    await ScheduleVCenterJobsAsync(schedulingService, vCenter, dbContext);
                }

                // 3. Schedule global compliance checks
                await ScheduleGlobalComplianceJobsAsync(schedulingService);
                
                _jobsInitialized = true;
                _logger.LogInformation("Successfully initialized scheduled jobs for {VCenterCount} vCenters", vCenters.Count);
            }
            else
            {
                _logger.LogInformation("No vCenters found - will initialize jobs when vCenters are added");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize scheduled jobs");
            throw;
        }
    }

    /// <summary>
    /// Schedule data refresh jobs to populate Overview and Infrastructure tabs
    /// </summary>
    private async Task ScheduleDataRefreshJobsAsync(ISchedulingService schedulingService)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var scheduler = scope.ServiceProvider.GetRequiredService<ISchedulerFactory>().GetScheduler().Result;

            // Schedule frequent data refresh (every 10 minutes) using DataSynchronizationJob
            var frequentRefreshJobKey = new JobKey("DataSync-Frequent", "DataSynchronization");
            var frequentRefreshJob = JobBuilder.Create<DataSynchronizationJob>()
                .WithIdentity(frequentRefreshJobKey)
                .WithDescription("Frequent data refresh to populate Overview and Infrastructure views")
                .UsingJobData("JobName", "DataSync-Frequent")
                .UsingJobData("TotalExecutions", 0)
                .Build();

            var frequentRefreshTrigger = TriggerBuilder.Create()
                .WithIdentity("DataSync-Frequent-Trigger", "DataSynchronization")
                .WithDescription("Trigger for frequent data synchronization")
                .WithCronSchedule("0 */10 * * * ?") // Every 10 minutes
                .StartNow()
                .Build();

            await scheduler.ScheduleJob(frequentRefreshJob, frequentRefreshTrigger);
            _logger.LogInformation("Created frequent data refresh schedule: runs every 10 minutes");

            // Schedule detailed data refresh (every hour) using DataSynchronizationJob
            var detailedRefreshJobKey = new JobKey("DataSync-Detailed", "DataSynchronization");
            var detailedRefreshJob = JobBuilder.Create<DataSynchronizationJob>()
                .WithIdentity(detailedRefreshJobKey)
                .WithDescription("Detailed data refresh including performance metrics")
                .UsingJobData("JobName", "DataSync-Detailed")
                .UsingJobData("TotalExecutions", 0)
                .Build();

            var detailedRefreshTrigger = TriggerBuilder.Create()
                .WithIdentity("DataSync-Detailed-Trigger", "DataSynchronization")
                .WithDescription("Trigger for detailed data synchronization")
                .WithCronSchedule("0 0 * * * ?") // Every hour
                .StartNow()
                .Build();

            await scheduler.ScheduleJob(detailedRefreshJob, detailedRefreshTrigger);
            _logger.LogInformation("Created detailed data refresh schedule: runs every hour");

            // Schedule immediate data refresh to populate data right away
            var immediateRefreshJobKey = new JobKey("DataSync-Immediate", "DataSynchronization");
            var immediateRefreshJob = JobBuilder.Create<DataSynchronizationJob>()
                .WithIdentity(immediateRefreshJobKey)
                .WithDescription("Immediate data refresh on service startup")
                .UsingJobData("JobName", "DataSync-Immediate")
                .UsingJobData("TotalExecutions", 0)
                .Build();

            var immediateRefreshTrigger = TriggerBuilder.Create()
                .WithIdentity("DataSync-Immediate-Trigger", "DataSynchronization")
                .WithDescription("Trigger for immediate data synchronization")
                .StartAt(DateTimeOffset.Now.AddSeconds(30)) // Start in 30 seconds
                .Build();

            await scheduler.ScheduleJob(immediateRefreshJob, immediateRefreshTrigger);
            _logger.LogInformation("Created immediate data refresh schedule: will run in 30 seconds");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create data refresh schedules");
            throw;
        }
    }

    /// <summary>
    /// Schedule jobs for a specific vCenter
    /// </summary>
    private async Task ScheduleVCenterJobsAsync(ISchedulingService schedulingService, VCenter vCenter, VMwareDbContext dbContext)
    {
        try
        {
            _logger.LogInformation("Scheduling jobs for vCenter: {VCenterName}", vCenter.Name);

            // Schedule cluster checks
            foreach (var cluster in vCenter.Clusters)
            {
                await schedulingService.ScheduleClusterChecksAsync(cluster, vCenter);
            }

            // Schedule host checks
            foreach (var host in vCenter.Hosts)
            {
                await schedulingService.ScheduleHostChecksAsync(host, vCenter);
            }

            _logger.LogInformation("Scheduled jobs for vCenter: {VCenterName} ({ClusterCount} clusters, {HostCount} hosts)", 
                vCenter.Name, vCenter.Clusters.Count, vCenter.Hosts.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to schedule jobs for vCenter: {VCenterName}", vCenter.Name);
        }
    }

    /// <summary>
    /// Schedule global compliance and health check jobs
    /// </summary>
    private async Task ScheduleGlobalComplianceJobsAsync(ISchedulingService schedulingService)
    {
        try
        {
            var scheduleIds = await schedulingService.ScheduleGlobalChecksAsync();
            _logger.LogInformation("Created {Count} global compliance schedules", scheduleIds.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create global compliance schedules");
            throw;
        }
    }

    /// <summary>
    /// Monitor and maintain scheduled jobs
    /// </summary>
    private async void MonitorAndMaintainJobs(object? state)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var schedulingService = scope.ServiceProvider.GetRequiredService<ISchedulingService>();
            var dbContext = scope.ServiceProvider.GetRequiredService<VMwareDbContext>();

            // Get active schedules
            var activeSchedules = await schedulingService.GetActiveSchedulesAsync();
            _logger.LogDebug("Monitoring {ScheduleCount} active schedules", activeSchedules.Count);

            // Check if we need to add schedules for new vCenters
            var vCenters = await dbContext.VCenters
                .Where(v => v.Enabled)
                .Include(v => v.Clusters)
                .Include(v => v.Hosts)
                .ToListAsync();

            // If we have vCenters but no jobs initialized, initialize them
            if (vCenters.Any() && !_jobsInitialized)
            {
                _logger.LogInformation("Found vCenters but jobs not initialized - initializing now");
                await InitializeScheduledJobsAsync();
            }

            // Update service status with job information
            var serviceConfigManager = scope.ServiceProvider.GetRequiredService<IServiceConfigurationManager>();
            var nextExecution = activeSchedules
                .Where(s => s.NextFireTime.HasValue)
                .OrderBy(s => s.NextFireTime)
                .FirstOrDefault()?.NextFireTime;

            var statistics = new
            {
                ActiveSchedules = activeSchedules.Count,
                VCenters = vCenters.Count,
                TotalHosts = vCenters.Sum(v => v.Hosts.Count),
                TotalClusters = vCenters.Sum(v => v.Clusters.Count),
                LastJobMonitor = DateTime.UtcNow
            };

            await serviceConfigManager.UpdateServiceStatusAsync(
                "Running", 
                activeExecutions: 0, 
                nextExecution: nextExecution, 
                statistics: statistics);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during job monitoring");
        }
    }

    /// <summary>
    /// Update service heartbeat
    /// </summary>
    private async void UpdateHeartbeat(object? state)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var serviceConfigManager = scope.ServiceProvider.GetRequiredService<IServiceConfigurationManager>();
            
            // Update heartbeat with more detailed status
            var heartbeatTime = DateTime.UtcNow;
            await serviceConfigManager.UpdateServiceStatusAsync(
                status: "Running",
                activeExecutions: 0,
                nextExecution: null,
                statistics: new
                {
                    HeartbeatTime = heartbeatTime,
                    ServiceUptime = DateTime.UtcNow.Subtract(Process.GetCurrentProcess().StartTime).ToString(@"hh\:mm\:ss"),
                    ProcessId = Environment.ProcessId,
                    MachineName = Environment.MachineName,
                    JobsInitialized = _jobsInitialized
                });
            
            _logger.LogDebug("Service heartbeat updated at {HeartbeatTime} - Status: Running", heartbeatTime);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update service heartbeat");
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("VMware GUI Tools Service is stopping...");
        
        try
        {
            // Stop timers
            _heartbeatTimer.Change(Timeout.Infinite, Timeout.Infinite);
            _jobMonitorTimer.Change(Timeout.Infinite, Timeout.Infinite);

            using var scope = _serviceProvider.CreateScope();
            var serviceConfigManager = scope.ServiceProvider.GetRequiredService<IServiceConfigurationManager>();
            await serviceConfigManager.UpdateServiceStatusAsync("Stopped");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update service status on stop");
        }

        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _heartbeatTimer?.Dispose();
        _jobMonitorTimer?.Dispose();
        base.Dispose();
    }
} 