using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;
using VMwareGUITools.Core.Models;
using VMwareGUITools.Data;
using VMwareGUITools.Infrastructure.VMware;

namespace VMwareGUITools.Infrastructure.Scheduling;

/// <summary>
/// Quartz.NET job for synchronizing VMware infrastructure data from vCenter to local database
/// </summary>
[DisallowConcurrentExecution]
public class DataSynchronizationJob : IJob
{
    private readonly ILogger<DataSynchronizationJob> _logger;
    private readonly IServiceProvider _serviceProvider;

    public DataSynchronizationJob(ILogger<DataSynchronizationJob> logger, IServiceProvider serviceProvider)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        var jobData = context.JobDetail.JobDataMap;
        var jobName = jobData.GetString("JobName") ?? "Data Synchronization";
        var executionId = Guid.NewGuid().ToString("N")[..8];

        _logger.LogInformation("Starting data synchronization job: {JobName} (ID: {ExecutionId})", jobName, executionId);

        try
        {
            // Create a scope for dependency injection
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<VMwareDbContext>();
            var vmwareService = scope.ServiceProvider.GetRequiredService<IVMwareConnectionService>();

            // Get all enabled vCenters
            var vCenters = await dbContext.VCenters
                .Where(v => v.Enabled)
                .ToListAsync(context.CancellationToken);

            if (!vCenters.Any())
            {
                _logger.LogInformation("No enabled vCenters found for synchronization");
                return;
            }

            var totalClusters = 0;
            var totalHosts = 0;
            var successfulVCenters = 0;

            foreach (var vCenter in vCenters)
            {
                try
                {
                    await SynchronizeVCenterDataAsync(vmwareService, dbContext, vCenter, context.CancellationToken);
                    successfulVCenters++;
                    
                    // Get counts for logging
                    var clusterCount = await dbContext.Clusters.CountAsync(c => c.VCenterId == vCenter.Id, context.CancellationToken);
                    var hostCount = await dbContext.Hosts.CountAsync(h => h.VCenterId == vCenter.Id, context.CancellationToken);
                    
                    totalClusters += clusterCount;
                    totalHosts += hostCount;
                    
                    _logger.LogInformation("Successfully synchronized vCenter {VCenterName}: {ClusterCount} clusters, {HostCount} hosts", 
                        vCenter.Name, clusterCount, hostCount);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to synchronize vCenter {VCenterName}", vCenter.Name);
                    // Continue with other vCenters even if one fails
                }
            }

            _logger.LogInformation("Data synchronization completed: {SuccessfulVCenters}/{TotalVCenters} vCenters, {TotalClusters} clusters, {TotalHosts} hosts", 
                successfulVCenters, vCenters.Count, totalClusters, totalHosts);

            // Update job execution statistics
            context.JobDetail.JobDataMap.Put("LastExecutionTime", DateTime.UtcNow);
            context.JobDetail.JobDataMap.Put("LastExecutionResults", $"{successfulVCenters}/{vCenters.Count} vCenters synchronized");
            context.JobDetail.JobDataMap.Put("TotalExecutions", 
                context.JobDetail.JobDataMap.GetInt("TotalExecutions") + 1);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Data synchronization job failed: {JobName} (ID: {ExecutionId})", jobName, executionId);
            
            var jobException = new JobExecutionException(ex)
            {
                RefireImmediately = false,
                UnscheduleAllTriggers = false
            };
            throw jobException;
        }
    }

    /// <summary>
    /// Synchronize data for a single vCenter
    /// </summary>
    private async Task SynchronizeVCenterDataAsync(IVMwareConnectionService vmwareService, VMwareDbContext dbContext, VCenter vCenter, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Synchronizing data for vCenter: {VCenterName}", vCenter.Name);

        // Connect to vCenter
        var session = await vmwareService.ConnectAsync(vCenter);
        
        try
        {
            // Update vCenter last scan time
            vCenter.LastScan = DateTime.UtcNow;

            // Discover and synchronize clusters
            var clusters = await vmwareService.DiscoverClustersAsync(session, cancellationToken);
            await SynchronizeClustersAsync(dbContext, vCenter, clusters, cancellationToken);

            // Discover and synchronize hosts for each cluster
            foreach (var clusterInfo in clusters)
            {
                try
                {
                    var hosts = await vmwareService.DiscoverHostsAsync(session, clusterInfo.MoId, cancellationToken);
                    await SynchronizeHostsAsync(dbContext, vCenter, clusterInfo, hosts, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to synchronize hosts for cluster {ClusterName} in vCenter {VCenterName}", 
                        clusterInfo.Name, vCenter.Name);
                }
            }

            // Discover and synchronize datastores
            try
            {
                var datastores = await vmwareService.DiscoverDatastoresAsync(session, cancellationToken);
                await SynchronizeDatastoresAsync(dbContext, vCenter, datastores, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to synchronize datastores for vCenter {VCenterName}", vCenter.Name);
            }

            // Save all changes
            await dbContext.SaveChangesAsync(cancellationToken);
            
            _logger.LogDebug("Successfully synchronized data for vCenter: {VCenterName}", vCenter.Name);
        }
        finally
        {
            // Disconnect from vCenter
            try
            {
                await vmwareService.DisconnectAsync(session, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to disconnect from vCenter {VCenterName}", vCenter.Name);
            }
        }
    }

    /// <summary>
    /// Synchronize clusters in the database
    /// </summary>
    private async Task SynchronizeClustersAsync(VMwareDbContext dbContext, VCenter vCenter, List<VMwareGUITools.Infrastructure.VMware.ClusterInfo> discoveredClusters, CancellationToken cancellationToken)
    {
        // Get existing clusters for this vCenter
        var existingClusters = await dbContext.Clusters
            .Where(c => c.VCenterId == vCenter.Id)
            .ToListAsync(cancellationToken);

        // Add or update clusters
        foreach (var clusterInfo in discoveredClusters)
        {
            var existingCluster = existingClusters.FirstOrDefault(c => c.MoId == clusterInfo.MoId);
            
            if (existingCluster == null)
            {
                // Create new cluster
                var newCluster = new Cluster
                {
                    Name = clusterInfo.Name,
                    MoId = clusterInfo.MoId,
                    VCenterId = vCenter.Id,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                
                dbContext.Clusters.Add(newCluster);
                _logger.LogDebug("Added new cluster: {ClusterName} (MoId: {MoId})", clusterInfo.Name, clusterInfo.MoId);
            }
            else
            {
                // Update existing cluster
                existingCluster.Name = clusterInfo.Name;
                existingCluster.UpdatedAt = DateTime.UtcNow;
                
                _logger.LogDebug("Updated cluster: {ClusterName} (MoId: {MoId})", clusterInfo.Name, clusterInfo.MoId);
            }
        }

        // Mark clusters that no longer exist as disabled
        var discoveredMoIds = discoveredClusters.Select(c => c.MoId).ToHashSet();
        var clustersToDisable = existingClusters.Where(c => !discoveredMoIds.Contains(c.MoId));
        
        foreach (var cluster in clustersToDisable)
        {
            cluster.Enabled = false;
            cluster.UpdatedAt = DateTime.UtcNow;
            _logger.LogDebug("Disabled cluster that no longer exists: {ClusterName} (MoId: {MoId})", cluster.Name, cluster.MoId);
        }
    }

    /// <summary>
    /// Synchronize hosts in the database
    /// </summary>
    private async Task SynchronizeHostsAsync(VMwareDbContext dbContext, VCenter vCenter, VMwareGUITools.Infrastructure.VMware.ClusterInfo clusterInfo, List<VMwareGUITools.Infrastructure.VMware.HostInfo> discoveredHosts, CancellationToken cancellationToken)
    {
        // Get the cluster from the database
        var cluster = await dbContext.Clusters
            .FirstOrDefaultAsync(c => c.VCenterId == vCenter.Id && c.MoId == clusterInfo.MoId, cancellationToken);
        
        if (cluster == null)
        {
            _logger.LogWarning("Cluster not found in database: {ClusterName} (MoId: {MoId})", clusterInfo.Name, clusterInfo.MoId);
            return;
        }

        // Get existing hosts for this cluster
        var existingHosts = await dbContext.Hosts
            .Where(h => h.VCenterId == vCenter.Id && h.ClusterId == cluster.Id)
            .ToListAsync(cancellationToken);

        // Add or update hosts
        foreach (var hostInfo in discoveredHosts)
        {
            var existingHost = existingHosts.FirstOrDefault(h => h.MoId == hostInfo.MoId);
            
            if (existingHost == null)
            {
                // Create new host
                var newHost = new Host
                {
                    Name = hostInfo.Name,
                    MoId = hostInfo.MoId,
                    IpAddress = hostInfo.IpAddress,
                    HostType = hostInfo.Type,
                    VCenterId = vCenter.Id,
                    ClusterId = cluster.Id,
                    ClusterName = cluster.Name,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                
                dbContext.Hosts.Add(newHost);
                _logger.LogDebug("Added new host: {HostName} (MoId: {MoId}) to cluster {ClusterName}", 
                    hostInfo.Name, hostInfo.MoId, cluster.Name);
            }
            else
            {
                // Update existing host
                existingHost.Name = hostInfo.Name;
                existingHost.IpAddress = hostInfo.IpAddress;
                existingHost.HostType = hostInfo.Type;
                existingHost.ClusterName = cluster.Name;
                existingHost.UpdatedAt = DateTime.UtcNow;
                
                _logger.LogDebug("Updated host: {HostName} (MoId: {MoId}) in cluster {ClusterName}", 
                    hostInfo.Name, hostInfo.MoId, cluster.Name);
            }
        }

        // Mark hosts that no longer exist as disabled
        var discoveredMoIds = discoveredHosts.Select(h => h.MoId).ToHashSet();
        var hostsToDisable = existingHosts.Where(h => !discoveredMoIds.Contains(h.MoId));
        
        foreach (var host in hostsToDisable)
        {
            host.Enabled = false;
            host.UpdatedAt = DateTime.UtcNow;
            _logger.LogDebug("Disabled host that no longer exists: {HostName} (MoId: {MoId})", host.Name, host.MoId);
        }
    }

    /// <summary>
    /// Synchronize datastores in the database
    /// </summary>
    private async Task SynchronizeDatastoresAsync(VMwareDbContext dbContext, VCenter vCenter, List<VMwareGUITools.Infrastructure.VMware.DatastoreInfo> discoveredDatastores, CancellationToken cancellationToken)
    {
        // Get existing datastores for this vCenter
        var existingDatastores = await dbContext.Datastores
            .Where(d => d.VCenterId == vCenter.Id)
            .ToListAsync(cancellationToken);

        // Add or update datastores
        foreach (var datastoreInfo in discoveredDatastores)
        {
            var existingDatastore = existingDatastores.FirstOrDefault(d => d.MoId == datastoreInfo.MoId);
            
            if (existingDatastore == null)
            {
                // Create new datastore
                var newDatastore = new Datastore
                {
                    Name = datastoreInfo.Name,
                    MoId = datastoreInfo.MoId,
                    Type = datastoreInfo.Type,
                    CapacityMB = datastoreInfo.CapacityMB,
                    FreeMB = datastoreInfo.FreeMB,
                    Accessible = datastoreInfo.Accessible,
                    VCenterId = vCenter.Id,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                
                dbContext.Datastores.Add(newDatastore);
                _logger.LogDebug("Added new datastore: {DatastoreName} (Type: {Type}, Capacity: {CapacityMB}MB)", 
                    datastoreInfo.Name, datastoreInfo.Type, datastoreInfo.CapacityMB);
            }
            else
            {
                // Update existing datastore
                existingDatastore.Name = datastoreInfo.Name;
                existingDatastore.Type = datastoreInfo.Type;
                existingDatastore.CapacityMB = datastoreInfo.CapacityMB;
                existingDatastore.FreeMB = datastoreInfo.FreeMB;
                existingDatastore.Accessible = datastoreInfo.Accessible;
                existingDatastore.UpdatedAt = DateTime.UtcNow;
                
                _logger.LogDebug("Updated datastore: {DatastoreName} (Type: {Type}, Capacity: {CapacityMB}MB)", 
                    datastoreInfo.Name, datastoreInfo.Type, datastoreInfo.CapacityMB);
            }
        }

        // Mark datastores that no longer exist as disabled (if we add an Enabled field to Datastore in the future)
        var discoveredMoIds = discoveredDatastores.Select(d => d.MoId).ToHashSet();
        var datastoresToRemove = existingDatastores.Where(d => !discoveredMoIds.Contains(d.MoId));
        
        foreach (var datastore in datastoresToRemove)
        {
            // For now, we'll keep the datastore record but could add an Enabled field later
            _logger.LogDebug("Datastore no longer exists: {DatastoreName} (MoId: {MoId})", datastore.Name, datastore.MoId);
        }
    }
} 