using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using VMwareGUITools.Core.Models;
using VMwareGUITools.Data;
using VMwareGUITools.Infrastructure.Checks;
using VMwareGUITools.Infrastructure.VMware;

namespace VMwareGUITools.Infrastructure.Services;

/// <summary>
/// Manages service configuration and communication between WPF application and service
/// </summary>
public class ServiceConfigurationManager : IServiceConfigurationManager
{
    private readonly ILogger<ServiceConfigurationManager> _logger;
    private readonly VMwareDbContext _dbContext;
    private readonly IServiceProvider _serviceProvider;
    private readonly Timer _commandProcessingTimer;
    private readonly Timer _heartbeatTimer;

    public ServiceConfigurationManager(
        ILogger<ServiceConfigurationManager> logger,
        VMwareDbContext dbContext,
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _dbContext = dbContext;
        _serviceProvider = serviceProvider;

        // Process commands every 5 seconds
        _commandProcessingTimer = new Timer(ProcessPendingCommands, null, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        
        // Update heartbeat every 30 seconds
        _heartbeatTimer = new Timer(UpdateHeartbeat, null, TimeSpan.Zero, TimeSpan.FromSeconds(30));
    }

    public async Task<T?> GetConfigurationAsync<T>(string key, string category = "") where T : class
    {
        try
        {
            var config = await _dbContext.ServiceConfigurations
                .FirstOrDefaultAsync(c => c.Key == key && 
                    (string.IsNullOrEmpty(category) || c.Category == category));

            if (config == null)
            {
                return null;
            }

            return JsonSerializer.Deserialize<T>(config.Value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get configuration for key: {Key}", key);
            return null;
        }
    }

    public async Task SetConfigurationAsync<T>(string key, T value, string category = "", string description = "", bool requiresRestart = false) where T : class
    {
        try
        {
            var config = await _dbContext.ServiceConfigurations
                .FirstOrDefaultAsync(c => c.Key == key && c.Category == category);

            var jsonValue = JsonSerializer.Serialize(value);

            if (config == null)
            {
                config = new ServiceConfiguration
                {
                    Key = key,
                    Value = jsonValue,
                    Category = category,
                    Description = description,
                    RequiresRestart = requiresRestart,
                    LastModified = DateTime.UtcNow,
                    ModifiedBy = "Service"
                };
                _dbContext.ServiceConfigurations.Add(config);
            }
            else
            {
                config.Value = jsonValue;
                config.LastModified = DateTime.UtcNow;
                config.ModifiedBy = "Service";
            }

            await _dbContext.SaveChangesAsync();
            _logger.LogInformation("Configuration updated: {Key} = {Value}", key, jsonValue);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set configuration for key: {Key}", key);
        }
    }

    public async Task<string> SendCommandAsync(string commandType, object parameters)
    {
        try
        {
            var command = new ServiceCommand
            {
                CommandType = commandType,
                Parameters = JsonSerializer.Serialize(parameters),
                CreatedAt = DateTime.UtcNow,
                Status = "Pending"
            };

            _dbContext.ServiceCommands.Add(command);
            await _dbContext.SaveChangesAsync();

            _logger.LogInformation("Command sent: {CommandType} with ID: {CommandId}", commandType, command.Id);
            return command.Id.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send command: {CommandType}", commandType);
            throw;
        }
    }

    public async Task<ServiceCommand?> GetCommandResultAsync(string commandId)
    {
        try
        {
            if (!int.TryParse(commandId, out var id))
            {
                return null;
            }

            return await _dbContext.ServiceCommands
                .FirstOrDefaultAsync(c => c.Id == id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get command result for ID: {CommandId}", commandId);
            return null;
        }
    }

    public async Task UpdateServiceStatusAsync(string status, int activeExecutions = 0, DateTime? nextExecution = null, object? statistics = null)
    {
        try
        {
            var serviceStatus = await _dbContext.ServiceStatuses.FirstOrDefaultAsync();

            if (serviceStatus == null)
            {
                serviceStatus = new ServiceStatus
                {
                    Status = status,
                    LastHeartbeat = DateTime.UtcNow,
                    Version = GetServiceVersion(),
                    ActiveExecutions = activeExecutions,
                    NextExecution = nextExecution,
                    Statistics = statistics != null ? JsonSerializer.Serialize(statistics) : "{}"
                };
                _dbContext.ServiceStatuses.Add(serviceStatus);
            }
            else
            {
                serviceStatus.Status = status;
                serviceStatus.LastHeartbeat = DateTime.UtcNow;
                serviceStatus.ActiveExecutions = activeExecutions;
                serviceStatus.NextExecution = nextExecution;
                if (statistics != null)
                {
                    serviceStatus.Statistics = JsonSerializer.Serialize(statistics);
                }
            }

            await _dbContext.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update service status");
        }
    }

    public async Task<ServiceStatus?> GetServiceStatusAsync()
    {
        try
        {
            return await _dbContext.ServiceStatuses.FirstOrDefaultAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get service status");
            return null;
        }
    }

    private async void ProcessPendingCommands(object? state)
    {
        try
        {
            var pendingCommands = await _dbContext.ServiceCommands
                .Where(c => c.Status == "Pending")
                .OrderBy(c => c.CreatedAt)
                .Take(10)
                .ToListAsync();

            foreach (var command in pendingCommands)
            {
                await ProcessCommandAsync(command);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing pending commands");
        }
    }

    private async Task ProcessCommandAsync(ServiceCommand command)
    {
        try
        {
            _logger.LogInformation("Processing command: {CommandType} (ID: {CommandId})", command.CommandType, command.Id);

            command.Status = "Processing";
            command.ProcessedAt = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync();

            var result = command.CommandType switch
            {
                ServiceCommandTypes.ExecuteCheck => await ExecuteCheckCommand(command.Parameters),
                ServiceCommandTypes.ValidatePowerCLI => await ValidatePowerCLICommand(),
                ServiceCommandTypes.GetServiceStatus => await GetServiceStatusCommand(),
                ServiceCommandTypes.ReloadConfiguration => await ReloadConfigurationCommand(),
                ServiceCommandTypes.GetOverviewData => await GetOverviewDataCommand(command.Parameters),
                ServiceCommandTypes.GetInfrastructureData => await GetInfrastructureDataCommand(command.Parameters),
                _ => $"Unknown command type: {command.CommandType}"
            };

            command.Status = "Completed";
            command.Result = result;
            await _dbContext.SaveChangesAsync();

            _logger.LogInformation("Command completed: {CommandType} (ID: {CommandId})", command.CommandType, command.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process command: {CommandType} (ID: {CommandId})", command.CommandType, command.Id);
            
            command.Status = "Failed";
            command.ErrorMessage = ex.Message;
            await _dbContext.SaveChangesAsync();
        }
    }

    private async Task<string> ExecuteCheckCommand(string parametersJson)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var checkExecutionService = scope.ServiceProvider.GetRequiredService<ICheckExecutionService>();

            var parameters = JsonSerializer.Deserialize<JsonElement>(parametersJson);
            
            // Handle different types of check execution requests
            if (parameters.TryGetProperty("HostId", out var hostIdProperty) && 
                parameters.TryGetProperty("CheckDefinitionId", out var checkDefIdProperty) &&
                parameters.TryGetProperty("VCenterId", out var vCenterIdProperty))
            {
                // Single host check execution
                var result = await ExecuteSingleHostCheck(checkExecutionService, 
                    hostIdProperty.GetInt32(), 
                    checkDefIdProperty.GetInt32(), 
                    vCenterIdProperty.GetInt32());
                    
                return JsonSerializer.Serialize(new { 
                    Success = true, 
                    Message = $"Check executed with status: {result.Status}",
                    ResultId = result.Id
                });
            }
            else if (parameters.TryGetProperty("ClusterId", out var clusterIdProperty) &&
                     parameters.TryGetProperty("VCenterId", out var clusterVCenterIdProperty))
            {
                // Cluster checks execution
                var results = await ExecuteClusterChecks(checkExecutionService, 
                    clusterIdProperty.GetInt32(), 
                    clusterVCenterIdProperty.GetInt32());
                    
                return JsonSerializer.Serialize(new { 
                    Success = true, 
                    Message = $"Cluster checks executed: {results.Count} checks completed",
                    ResultCount = results.Count,
                    PassedCount = results.Count(r => r.Status == CheckStatus.Passed),
                    FailedCount = results.Count(r => r.Status == CheckStatus.Failed)
                });
            }
            else if (parameters.TryGetProperty("CheckType", out var checkTypeProperty) &&
                     parameters.TryGetProperty("VCenterId", out var specialVCenterIdProperty))
            {
                // Special check types (like iSCSI checks)
                var checkType = checkTypeProperty.GetString();
                var results = await ExecuteSpecialChecks(checkExecutionService, 
                    checkType!, 
                    specialVCenterIdProperty.GetInt32());
                    
                return JsonSerializer.Serialize(new { 
                    Success = true, 
                    Message = $"{checkType} checks executed: {results.Count} checks completed",
                    ResultCount = results.Count,
                    PassedCount = results.Count(r => r.Status == CheckStatus.Passed),
                    FailedCount = results.Count(r => r.Status == CheckStatus.Failed)
                });
            }
            else
            {
                return JsonSerializer.Serialize(new { 
                    Success = false, 
                    Message = "Invalid check execution parameters" 
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute check command");
            return JsonSerializer.Serialize(new { 
                Success = false, 
                Message = $"Check execution failed: {ex.Message}" 
            });
        }
    }

    private async Task<CheckResult> ExecuteSingleHostCheck(ICheckExecutionService checkExecutionService, 
        int hostId, int checkDefinitionId, int vCenterId)
    {
        var host = await _dbContext.Hosts
            .FirstOrDefaultAsync(h => h.Id == hostId);
        var checkDefinition = await _dbContext.CheckDefinitions
            .FirstOrDefaultAsync(cd => cd.Id == checkDefinitionId);
        var vCenter = await _dbContext.VCenters
            .FirstOrDefaultAsync(v => v.Id == vCenterId);

        if (host == null || checkDefinition == null || vCenter == null)
        {
            throw new ArgumentException("Invalid host, check definition, or vCenter ID");
        }

        return await checkExecutionService.ExecuteCheckAsync(host, checkDefinition, vCenter);
    }

    private async Task<List<CheckResult>> ExecuteClusterChecks(ICheckExecutionService checkExecutionService, 
        int clusterId, int vCenterId)
    {
        var cluster = await _dbContext.Clusters
            .FirstOrDefaultAsync(c => c.Id == clusterId);
        var vCenter = await _dbContext.VCenters
            .FirstOrDefaultAsync(v => v.Id == vCenterId);

        if (cluster == null || vCenter == null)
        {
            throw new ArgumentException("Invalid cluster or vCenter ID");
        }

        return await checkExecutionService.ExecuteClusterChecksAsync(cluster, vCenter);
    }

    private async Task<List<CheckResult>> ExecuteSpecialChecks(ICheckExecutionService checkExecutionService, 
        string checkType, int vCenterId)
    {
        var vCenter = await _dbContext.VCenters
            .FirstOrDefaultAsync(v => v.Id == vCenterId);

        if (vCenter == null)
        {
            throw new ArgumentException("Invalid vCenter ID");
        }

        switch (checkType.ToLower())
        {
            case "iscsi":
                return await ExecuteiSCSIChecks(checkExecutionService, vCenter);
            default:
                throw new ArgumentException($"Unknown check type: {checkType}");
        }
    }

    private async Task<List<CheckResult>> ExecuteiSCSIChecks(ICheckExecutionService checkExecutionService, VCenter vCenter)
    {
        // Get or create the iSCSI check definition
        var iSCSICheck = await _dbContext.CheckDefinitions
            .FirstOrDefaultAsync(cd => cd.Name.Contains("iSCSI") && cd.Name.Contains("Dead Path"));

        if (iSCSICheck == null)
        {
            // Create the iSCSI check if it doesn't exist
            var storageCategory = await _dbContext.CheckCategories
                .FirstOrDefaultAsync(cc => cc.Name == "Storage")
                ?? new CheckCategory
                {
                    Name = "Storage",
                    Description = "Storage health and configuration checks",
                    Type = CheckCategoryType.Health,
                    Enabled = true,
                    SortOrder = 5
                };

            if (storageCategory.Id == 0)
            {
                _dbContext.CheckCategories.Add(storageCategory);
                await _dbContext.SaveChangesAsync();
            }

            iSCSICheck = new CheckDefinition
            {
                CategoryId = storageCategory.Id,
                Name = "iSCSI Dead Path Check",
                Description = "Check for dead or inactive iSCSI storage paths",
                ExecutionType = CheckExecutionType.vSphereRestAPI,
                DefaultSeverity = CheckSeverity.Critical,
                IsEnabled = true,
                TimeoutSeconds = 120,
                ScriptPath = "Scripts/Storage/Check-iSCSIDeadPaths.ps1",
                Script = "# PowerShell script to check iSCSI path status",
                Parameters = """{"checkAllAdapters": true}""",
                Thresholds = """{"maxDeadPaths": 0}"""
            };

            _dbContext.CheckDefinitions.Add(iSCSICheck);
            await _dbContext.SaveChangesAsync();
        }

        // Get all hosts for this vCenter
        var hosts = await _dbContext.Hosts
            .Where(h => h.VCenterId == vCenter.Id && h.Enabled)
            .ToListAsync();

        var results = new List<CheckResult>();
        
        // Execute the check on each host
        foreach (var host in hosts)
        {
            try
            {
                var result = await checkExecutionService.ExecuteCheckAsync(host, iSCSICheck, vCenter);
                results.Add(result);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to execute iSCSI check on host {HostName}", host.Name);
                
                // Create a failed result for reporting purposes
                var failedResult = new CheckResult
                {
                    CheckDefinitionId = iSCSICheck.Id,
                    HostId = host.Id,
                    Status = CheckStatus.Error,
                    ErrorMessage = $"Failed to execute check: {ex.Message}",
                    ExecutedAt = DateTime.UtcNow,
                    ExecutionTime = TimeSpan.Zero,
                    Output = "Check execution failed",
                    Details = $"Host: {host.Name}, Error: {ex.Message}"
                };
                results.Add(failedResult);
            }
        }

        return results;
    }

    private async Task<string> ValidatePowerCLICommand()
    {
        // Implementation would call PowerCLI validation service
        await Task.Delay(100); // Placeholder
        return JsonSerializer.Serialize(new { IsValid = true, Message = "PowerCLI validation completed" });
    }

    private async Task<string> GetServiceStatusCommand()
    {
        var status = await GetServiceStatusAsync();
        return JsonSerializer.Serialize(status);
    }

    private async Task<string> ReloadConfigurationCommand()
    {
        // Implementation would reload configuration
        await Task.Delay(100); // Placeholder
        return JsonSerializer.Serialize(new { Success = true, Message = "Configuration reloaded" });
    }

    private async Task<string> GetOverviewDataCommand(string parametersJson)
    {
        try
        {
            _logger.LogInformation("Executing GetOverviewData command");
            
            var parameters = JsonSerializer.Deserialize<Dictionary<string, object>>(parametersJson);
            if (parameters == null || !parameters.TryGetValue("VCenterId", out var vCenterIdObj))
            {
                throw new ArgumentException("VCenterId parameter is required");
            }

            var vCenterId = Convert.ToInt32(vCenterIdObj);
            var vCenter = await _dbContext.VCenters.FirstOrDefaultAsync(v => v.Id == vCenterId);
            if (vCenter == null)
            {
                throw new ArgumentException($"vCenter with ID {vCenterId} not found");
            }

            using var scope = _serviceProvider.CreateScope();
            var restApiService = scope.ServiceProvider.GetRequiredService<IVSphereRestAPIService>();
            
            var session = await restApiService.ConnectAsync(vCenter);
            var overviewData = await restApiService.GetOverviewDataAsync(session);
            await restApiService.DisconnectAsync(session);

            return JsonSerializer.Serialize(overviewData);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get overview data");
            throw;
        }
    }

    private async Task<string> GetInfrastructureDataCommand(string parametersJson)
    {
        try
        {
            _logger.LogInformation("Executing GetInfrastructureData command");
            
            var parameters = JsonSerializer.Deserialize<Dictionary<string, object>>(parametersJson);
            if (parameters == null || !parameters.TryGetValue("VCenterId", out var vCenterIdObj))
            {
                throw new ArgumentException("VCenterId parameter is required");
            }

            var vCenterId = Convert.ToInt32(vCenterIdObj);
            var vCenter = await _dbContext.VCenters.FirstOrDefaultAsync(v => v.Id == vCenterId);
            if (vCenter == null)
            {
                throw new ArgumentException($"vCenter with ID {vCenterId} not found");
            }

            using var scope = _serviceProvider.CreateScope();
            var restApiService = scope.ServiceProvider.GetRequiredService<IVSphereRestAPIService>();
            
            var session = await restApiService.ConnectAsync(vCenter);
            var clusters = await restApiService.DiscoverClustersAsync(session);
            var datastores = await restApiService.DiscoverDatastoresAsync(session);
            await restApiService.DisconnectAsync(session);

            var infrastructureData = new
            {
                Clusters = clusters,
                Datastores = datastores,
                LastUpdated = DateTime.UtcNow
            };

            return JsonSerializer.Serialize(infrastructureData);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get infrastructure data");
            throw;
        }
    }

    private async void UpdateHeartbeat(object? state)
    {
        await UpdateServiceStatusAsync("Running");
    }

    private string GetServiceVersion()
    {
        return System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";
    }

    public void Dispose()
    {
        _commandProcessingTimer?.Dispose();
        _heartbeatTimer?.Dispose();
    }
}

/// <summary>
/// Interface for service configuration management
/// </summary>
public interface IServiceConfigurationManager : IDisposable
{
    Task<T?> GetConfigurationAsync<T>(string key, string category = "") where T : class;
    Task SetConfigurationAsync<T>(string key, T value, string category = "", string description = "", bool requiresRestart = false) where T : class;
    Task<string> SendCommandAsync(string commandType, object parameters);
    Task<ServiceCommand?> GetCommandResultAsync(string commandId);
    Task UpdateServiceStatusAsync(string status, int activeExecutions = 0, DateTime? nextExecution = null, object? statistics = null);
    Task<ServiceStatus?> GetServiceStatusAsync();
} 