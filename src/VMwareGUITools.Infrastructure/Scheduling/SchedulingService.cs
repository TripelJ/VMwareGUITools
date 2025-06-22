using Microsoft.Extensions.Logging;
using Quartz;
using VMwareGUITools.Core.Models;
using VMwareGUITools.Data;

namespace VMwareGUITools.Infrastructure.Scheduling;

/// <summary>
/// Implementation of scheduling service using Quartz.NET
/// </summary>
public class SchedulingService : ISchedulingService
{
    private readonly ILogger<SchedulingService> _logger;
    private readonly ISchedulerFactory _schedulerFactory;
    private readonly VMwareDbContext _dbContext;

    public SchedulingService(
        ILogger<SchedulingService> logger,
        ISchedulerFactory schedulerFactory,
        VMwareDbContext dbContext)
    {
        _logger = logger;
        _schedulerFactory = schedulerFactory;
        _dbContext = dbContext;
    }

    public async Task<List<string>> ScheduleHostChecksAsync(Host host, VCenter vCenter)
    {
        try
        {
            _logger.LogInformation("Scheduling checks for host: {HostName}", host.Name);

            var schedule = new CheckSchedule
            {
                Name = $"Host-{host.Name}-DailyChecks",
                Description = $"Daily checks for host {host.Name}",
                CronExpression = "0 0 2 * * ?", // Daily at 2 AM
                HostIds = new List<int> { host.Id },
                IsEnabled = true,
                NotifyOnFailure = true
            };

            var scheduleId = await CreateScheduleAsync(schedule);
            return new List<string> { scheduleId };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to schedule checks for host: {HostName}", host.Name);
            throw;
        }
    }

    public async Task<List<string>> ScheduleClusterChecksAsync(Cluster cluster, VCenter vCenter)
    {
        try
        {
            _logger.LogInformation("Scheduling checks for cluster: {ClusterName}", cluster.Name);

            var schedule = new CheckSchedule
            {
                Name = $"Cluster-{cluster.Name}-DailyChecks",
                Description = $"Daily checks for cluster {cluster.Name}",
                CronExpression = "0 0 3 * * ?", // Daily at 3 AM
                ClusterIds = new List<int> { cluster.Id },
                IsEnabled = true,
                NotifyOnFailure = true
            };

            var scheduleId = await CreateScheduleAsync(schedule);
            return new List<string> { scheduleId };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to schedule checks for cluster: {ClusterName}", cluster.Name);
            throw;
        }
    }

    public async Task<List<string>> ScheduleGlobalChecksAsync()
    {
        try
        {
            _logger.LogInformation("Scheduling global checks for all infrastructure");

            var schedules = new List<CheckSchedule>
            {
                new CheckSchedule
                {
                    Name = "Global-DailyHealthChecks",
                    Description = "Daily health checks for all hosts",
                    CronExpression = "0 0 1 * * ?", // Daily at 1 AM
                    IsEnabled = true,
                    NotifyOnFailure = true
                },
                new CheckSchedule
                {
                    Name = "Global-WeeklyComplianceChecks",
                    Description = "Weekly compliance checks for all hosts",
                    CronExpression = "0 0 4 ? * SUN", // Weekly on Sunday at 4 AM
                    IsEnabled = true,
                    NotifyOnFailure = true,
                    NotifyOnSuccess = true
                }
            };

            var scheduleIds = new List<string>();
            foreach (var schedule in schedules)
            {
                var scheduleId = await CreateScheduleAsync(schedule);
                scheduleIds.Add(scheduleId);
            }

            return scheduleIds;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to schedule global checks");
            throw;
        }
    }

    public async Task<string> CreateScheduleAsync(CheckSchedule schedule)
    {
        try
        {
            _logger.LogInformation("Creating schedule: {ScheduleName}", schedule.Name);

            var scheduler = await _schedulerFactory.GetScheduler();

            // Create job
            var jobKey = new JobKey(schedule.Name, "CheckExecution");
            var job = JobBuilder.Create<CheckExecutionJob>()
                .WithIdentity(jobKey)
                .WithDescription(schedule.Description)
                .UsingJobData("ScheduleName", schedule.Name)
                .UsingJobData("HostIds", string.Join(",", schedule.HostIds))
                .UsingJobData("ClusterIds", string.Join(",", schedule.ClusterIds))
                .UsingJobData("CheckDefinitionIds", string.Join(",", schedule.CheckDefinitionIds))
                .UsingJobData("MaxConcurrency", schedule.MaxConcurrency)
                .UsingJobData("NotifyOnFailure", schedule.NotifyOnFailure)
                .UsingJobData("NotifyOnSuccess", schedule.NotifyOnSuccess)
                .UsingJobData("TotalExecutions", 0)
                .Build();

            // Create trigger
            var triggerKey = new TriggerKey($"{schedule.Name}-Trigger", "CheckExecution");
            var trigger = TriggerBuilder.Create()
                .WithIdentity(triggerKey)
                .WithDescription($"Trigger for {schedule.Name}")
                .WithCronSchedule(schedule.CronExpression)
                .StartAt(schedule.StartDate)
                .Build();

            if (schedule.EndDate.HasValue)
            {
                trigger = trigger.GetTriggerBuilder()
                    .EndAt(schedule.EndDate.Value)
                    .Build();
            }

            // Schedule the job
            await scheduler.ScheduleJob(job, trigger);

            _logger.LogInformation("Successfully created schedule: {ScheduleName} with key: {JobKey}", 
                schedule.Name, jobKey);

            return jobKey.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create schedule: {ScheduleName}", schedule.Name);
            throw;
        }
    }

    public async Task UpdateScheduleAsync(string scheduleId, CheckSchedule schedule)
    {
        try
        {
            _logger.LogInformation("Updating schedule: {ScheduleId}", scheduleId);

            var scheduler = await _schedulerFactory.GetScheduler();
            var jobKey = JobKey.Create(scheduleId);

            // Delete existing job and create new one
            await scheduler.DeleteJob(jobKey);
            await CreateScheduleAsync(schedule);

            _logger.LogInformation("Successfully updated schedule: {ScheduleId}", scheduleId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update schedule: {ScheduleId}", scheduleId);
            throw;
        }
    }

    public async Task DeleteScheduleAsync(string scheduleId)
    {
        try
        {
            _logger.LogInformation("Deleting schedule: {ScheduleId}", scheduleId);

            var scheduler = await _schedulerFactory.GetScheduler();
            var jobKey = JobKey.Create(scheduleId);

            var deleted = await scheduler.DeleteJob(jobKey);
            
            if (deleted)
            {
                _logger.LogInformation("Successfully deleted schedule: {ScheduleId}", scheduleId);
            }
            else
            {
                _logger.LogWarning("Schedule not found for deletion: {ScheduleId}", scheduleId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete schedule: {ScheduleId}", scheduleId);
            throw;
        }
    }

    public async Task PauseScheduleAsync(string scheduleId)
    {
        try
        {
            _logger.LogInformation("Pausing schedule: {ScheduleId}", scheduleId);

            var scheduler = await _schedulerFactory.GetScheduler();
            var jobKey = JobKey.Create(scheduleId);

            await scheduler.PauseJob(jobKey);

            _logger.LogInformation("Successfully paused schedule: {ScheduleId}", scheduleId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to pause schedule: {ScheduleId}", scheduleId);
            throw;
        }
    }

    public async Task ResumeScheduleAsync(string scheduleId)
    {
        try
        {
            _logger.LogInformation("Resuming schedule: {ScheduleId}", scheduleId);

            var scheduler = await _schedulerFactory.GetScheduler();
            var jobKey = JobKey.Create(scheduleId);

            await scheduler.ResumeJob(jobKey);

            _logger.LogInformation("Successfully resumed schedule: {ScheduleId}", scheduleId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resume schedule: {ScheduleId}", scheduleId);
            throw;
        }
    }

    public async Task<List<ScheduleInfo>> GetActiveSchedulesAsync()
    {
        try
        {
            var scheduler = await _schedulerFactory.GetScheduler();
            var jobKeys = await scheduler.GetJobKeys(GroupMatcher<JobKey>.AnyGroup());

            var schedules = new List<ScheduleInfo>();

            foreach (var jobKey in jobKeys)
            {
                var jobDetail = await scheduler.GetJobDetail(jobKey);
                var triggers = await scheduler.GetTriggersOfJob(jobKey);

                if (jobDetail != null)
                {
                    var trigger = triggers.FirstOrDefault();
                    schedules.Add(new ScheduleInfo
                    {
                        Id = jobKey.ToString(),
                        Name = jobDetail.Key.Name,
                        Description = jobDetail.Description ?? "",
                        NextFireTime = trigger?.GetNextFireTimeUtc()?.DateTime,
                        PreviousFireTime = trigger?.GetPreviousFireTimeUtc()?.DateTime,
                        State = await GetScheduleStateAsync(scheduler, jobKey),
                        TotalFireCount = jobDetail.JobDataMap.GetInt("TotalExecutions"),
                        CreatedAt = DateTime.UtcNow // This would need to be stored separately
                    });
                }
            }

            return schedules;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get active schedules");
            throw;
        }
    }

    public async Task TriggerScheduleAsync(string scheduleId)
    {
        try
        {
            _logger.LogInformation("Triggering immediate execution of schedule: {ScheduleId}", scheduleId);

            var scheduler = await _schedulerFactory.GetScheduler();
            var jobKey = JobKey.Create(scheduleId);

            await scheduler.TriggerJob(jobKey);

            _logger.LogInformation("Successfully triggered schedule: {ScheduleId}", scheduleId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to trigger schedule: {ScheduleId}", scheduleId);
            throw;
        }
    }

    public async Task<List<DateTime>> GetNextExecutionTimesAsync(string scheduleId, int count = 5)
    {
        try
        {
            var scheduler = await _schedulerFactory.GetScheduler();
            var jobKey = JobKey.Create(scheduleId);
            var triggers = await scheduler.GetTriggersOfJob(jobKey);

            var nextTimes = new List<DateTime>();
            var trigger = triggers.FirstOrDefault();

            if (trigger != null)
            {
                var nextFireTime = trigger.GetNextFireTimeUtc();
                for (int i = 0; i < count && nextFireTime.HasValue; i++)
                {
                    nextTimes.Add(nextFireTime.Value.DateTime);
                    nextFireTime = trigger.GetFireTimeAfter(nextFireTime);
                }
            }

            return nextTimes;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get next execution times for schedule: {ScheduleId}", scheduleId);
            throw;
        }
    }

    private async Task<ScheduleState> GetScheduleStateAsync(IScheduler scheduler, JobKey jobKey)
    {
        try
        {
            var triggers = await scheduler.GetTriggersOfJob(jobKey);
            var trigger = triggers.FirstOrDefault();

            if (trigger == null)
                return ScheduleState.Complete;

            var state = await scheduler.GetTriggerState(trigger.Key);

            return state switch
            {
                TriggerState.Normal => ScheduleState.Normal,
                TriggerState.Paused => ScheduleState.Paused,
                TriggerState.Complete => ScheduleState.Complete,
                TriggerState.Error => ScheduleState.Error,
                TriggerState.Blocked => ScheduleState.Blocked,
                _ => ScheduleState.Normal
            };
        }
        catch
        {
            return ScheduleState.Error;
        }
    }
} 