using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;
using VMwareGUITools.Core.Models;
using VMwareGUITools.Data;
using VMwareGUITools.Infrastructure.Checks;
using CheckExecution = VMwareGUITools.Core.Models.CheckExecution;

namespace VMwareGUITools.Infrastructure.Scheduling;

/// <summary>
/// Quartz.NET job for executing scheduled infrastructure checks
/// </summary>
[DisallowConcurrentExecution]
public class CheckExecutionJob : IJob
{
    private readonly ILogger<CheckExecutionJob> _logger;
    private readonly IServiceProvider _serviceProvider;

    public CheckExecutionJob(ILogger<CheckExecutionJob> logger, IServiceProvider serviceProvider)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        var jobData = context.JobDetail.JobDataMap;
        var scheduleName = jobData.GetString("ScheduleName") ?? "Unknown";
        var executionId = Guid.NewGuid().ToString("N")[..8];

        _logger.LogInformation("Starting scheduled check execution: {ScheduleName} (ID: {ExecutionId})", scheduleName, executionId);

        try
        {
            // Create a scope for dependency injection
            using var scope = _serviceProvider.CreateScope();
            var checkExecutionService = scope.ServiceProvider.GetRequiredService<ICheckExecutionService>();
            var dbContext = scope.ServiceProvider.GetRequiredService<VMwareDbContext>();

            // Extract execution parameters from job data
            var hostIds = ParseIntArray(jobData.GetString("HostIds"));
            var clusterIds = ParseIntArray(jobData.GetString("ClusterIds"));
            var checkDefinitionIds = ParseIntArray(jobData.GetString("CheckDefinitionIds"));
            var maxConcurrency = jobData.GetInt("MaxConcurrency");
            var notifyOnFailure = jobData.GetBoolean("NotifyOnFailure");
            var notifyOnSuccess = jobData.GetBoolean("NotifyOnSuccess");

            var totalExecutions = new List<CheckExecution>();

            // Process hosts directly if specified
            if (hostIds.Any())
            {
                var hosts = await dbContext.Hosts
                    .Where(h => hostIds.Contains(h.Id))
                    .Include(h => h.VCenter)
                    .ToListAsync(context.CancellationToken);

                foreach (var host in hosts)
                {
                    if (host.VCenter == null) continue;

                    if (checkDefinitionIds.Any())
                    {
                        // Execute specific checks
                        var checkDefinitions = await dbContext.CheckDefinitions
                            .Where(cd => checkDefinitionIds.Contains(cd.Id) && cd.IsEnabled)
                            .ToListAsync(context.CancellationToken);

                        foreach (var checkDef in checkDefinitions)
                        {
                            totalExecutions.Add(new CheckExecution
                            {
                                Host = host,
                                CheckDefinition = checkDef,
                                VCenter = host.VCenter,
                                IsManualRun = false,
                                ScheduledAt = DateTime.UtcNow
                            });
                        }
                    }
                    else
                    {
                        // Execute all checks for the host based on its profile
                        var hostProfile = await dbContext.HostProfiles
                            .FirstOrDefaultAsync(hp => hp.Name == host.HostType.ToString(), context.CancellationToken);

                        if (hostProfile != null)
                        {
                            var checkDefinitions = await dbContext.CheckDefinitions
                                .Where(cd => hostProfile.EnabledCheckIds.Contains(cd.Id) && cd.IsEnabled)
                                .ToListAsync(context.CancellationToken);

                            foreach (var checkDef in checkDefinitions)
                            {
                                totalExecutions.Add(new CheckExecution
                                {
                                    Host = host,
                                    CheckDefinition = checkDef,
                                    VCenter = host.VCenter,
                                    IsManualRun = false,
                                    ScheduledAt = DateTime.UtcNow
                                });
                            }
                        }
                    }
                }
            }

            // Process clusters if specified
            if (clusterIds.Any())
            {
                var clusters = await dbContext.Clusters
                    .Where(c => clusterIds.Contains(c.Id))
                    .Include(c => c.VCenter)
                    .ToListAsync(context.CancellationToken);

                foreach (var cluster in clusters)
                {
                    if (cluster.VCenter == null) continue;

                    var clusterHosts = await dbContext.Hosts
                        .Where(h => h.VCenterId == cluster.VCenterId && h.ClusterName == cluster.Name)
                        .ToListAsync(context.CancellationToken);

                    foreach (var host in clusterHosts)
                    {
                        var hostProfile = await dbContext.HostProfiles
                            .FirstOrDefaultAsync(hp => hp.Name == host.HostType.ToString(), context.CancellationToken);

                        if (hostProfile != null)
                        {
                            var enabledCheckIds = checkDefinitionIds.Any() 
                                ? hostProfile.EnabledCheckIds.Intersect(checkDefinitionIds).ToList()
                                : hostProfile.EnabledCheckIds;

                            var checkDefinitions = await dbContext.CheckDefinitions
                                .Where(cd => enabledCheckIds.Contains(cd.Id) && cd.IsEnabled)
                                .ToListAsync(context.CancellationToken);

                            foreach (var checkDef in checkDefinitions)
                            {
                                totalExecutions.Add(new CheckExecution
                                {
                                    Host = host,
                                    CheckDefinition = checkDef,
                                    VCenter = cluster.VCenter,
                                    IsManualRun = false,
                                    ScheduledAt = DateTime.UtcNow
                                });
                            }
                        }
                    }
                }
            }

            if (!totalExecutions.Any())
            {
                _logger.LogWarning("No check executions found for scheduled job: {ScheduleName}", scheduleName);
                return;
            }

            _logger.LogInformation("Executing {CheckCount} checks for schedule: {ScheduleName}", 
                totalExecutions.Count, scheduleName);

            // Execute the checks in batches
            var results = await checkExecutionService.ExecuteBatchAsync(totalExecutions, maxConcurrency, context.CancellationToken);

            // Log execution summary
            var passedCount = results.Count(r => r.Status == Core.Models.CheckStatus.Passed);
            var failedCount = results.Count(r => r.Status == Core.Models.CheckStatus.Failed);

            _logger.LogInformation("Scheduled check execution completed: {ScheduleName} - {PassedCount} passed, {FailedCount} failed",
                scheduleName, passedCount, failedCount);

            // Handle notifications if configured
            if ((notifyOnFailure && failedCount > 0) || (notifyOnSuccess && passedCount > 0))
            {
                // TODO: Implement notification service integration
                _logger.LogInformation("Notifications would be sent for schedule: {ScheduleName}", scheduleName);
            }

            // Update job execution statistics
            context.JobDetail.JobDataMap.Put("LastExecutionTime", DateTime.UtcNow);
            context.JobDetail.JobDataMap.Put("LastExecutionResults", $"{passedCount} passed, {failedCount} failed");
            context.JobDetail.JobDataMap.Put("TotalExecutions", 
                context.JobDetail.JobDataMap.GetInt("TotalExecutions") + 1);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scheduled check execution failed: {ScheduleName} (ID: {ExecutionId})", 
                scheduleName, executionId);
            
            var jobException = new JobExecutionException(ex)
            {
                RefireImmediately = false,
                UnscheduleAllTriggers = false
            };
            throw jobException;
        }
    }

    private static List<int> ParseIntArray(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return new List<int>();

        try
        {
            return value.Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(int.Parse)
                        .ToList();
        }
        catch
        {
            return new List<int>();
        }
    }
} 