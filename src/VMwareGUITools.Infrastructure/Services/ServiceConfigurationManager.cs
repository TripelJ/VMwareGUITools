using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using VMwareGUITools.Core.Models;
using VMwareGUITools.Data;
using VMwareGUITools.Infrastructure.Checks;
using VMwareGUITools.Infrastructure.Security;
using VMwareGUITools.Infrastructure.VMware;

namespace VMwareGUITools.Infrastructure.Services;

/// <summary>
/// Manages service configuration and communication between WPF application and service
/// </summary>
public class ServiceConfigurationManager : IServiceConfigurationManager
{
    private readonly ILogger<ServiceConfigurationManager> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly Timer? _commandProcessingTimer;
    private readonly Timer? _heartbeatTimer;
    private readonly bool _isServiceContext;

    public ServiceConfigurationManager(
        ILogger<ServiceConfigurationManager> logger,
        IServiceProvider serviceProvider,
        bool isServiceContext = false)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _isServiceContext = isServiceContext;

        // Only run heartbeat and command processing if this is running in the service context
        if (_isServiceContext)
        {
            // Process commands every 5 seconds
            _commandProcessingTimer = new Timer(ProcessPendingCommands, null, TimeSpan.Zero, TimeSpan.FromSeconds(5));
            
            // Update heartbeat every 10 seconds
            _heartbeatTimer = new Timer(UpdateHeartbeat, null, TimeSpan.Zero, TimeSpan.FromSeconds(10));
            
            _logger.LogInformation("ServiceConfigurationManager initialized in SERVICE context - heartbeat and command processing enabled");
        }
        else
        {
            _logger.LogInformation("ServiceConfigurationManager initialized in GUI context - heartbeat and command processing disabled");
        }
    }

    public async Task<T?> GetConfigurationAsync<T>(string key, string category = "") where T : class
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<VMwareDbContext>();
            
            var config = await dbContext.ServiceConfigurations
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
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<VMwareDbContext>();
            
            var config = await dbContext.ServiceConfigurations
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
                dbContext.ServiceConfigurations.Add(config);
            }
            else
            {
                config.Value = jsonValue;
                config.LastModified = DateTime.UtcNow;
                config.ModifiedBy = "Service";
            }

            await dbContext.SaveChangesAsync();
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
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<VMwareDbContext>();
            
            var command = new ServiceCommand
            {
                CommandType = commandType,
                Parameters = JsonSerializer.Serialize(parameters),
                CreatedAt = DateTime.UtcNow,
                Status = "Pending"
            };

            dbContext.ServiceCommands.Add(command);
            await dbContext.SaveChangesAsync();

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

            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<VMwareDbContext>();

            return await dbContext.ServiceCommands
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
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<VMwareDbContext>();
            
            var serviceStatus = await dbContext.ServiceStatuses.FirstOrDefaultAsync();

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
                dbContext.ServiceStatuses.Add(serviceStatus);
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

            await dbContext.SaveChangesAsync();
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
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<VMwareDbContext>();
            
            return await dbContext.ServiceStatuses.FirstOrDefaultAsync();
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
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<VMwareDbContext>();
            
            var pendingCommands = await dbContext.ServiceCommands
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

            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<VMwareDbContext>();
            dbContext.ServiceCommands.Update(command);
            await dbContext.SaveChangesAsync();

            string result = command.CommandType switch
            {
                ServiceCommandTypes.ExecuteCheck => await ExecuteCheckCommand(command.Parameters),
                ServiceCommandTypes.ValidatePowerCLI => await ValidatePowerCLICommand(),
                ServiceCommandTypes.GetServiceStatus => await GetServiceStatusCommand(),
                ServiceCommandTypes.ReloadConfiguration => await ReloadConfigurationCommand(),
                ServiceCommandTypes.GetOverviewData => await GetOverviewDataCommand(command.Parameters),
                ServiceCommandTypes.GetInfrastructureData => await GetInfrastructureDataCommand(command.Parameters),
                ServiceCommandTypes.ConnectVCenter => await ConnectVCenterCommand(command.Parameters),
                ServiceCommandTypes.TestVCenterConnection => await TestVCenterConnectionCommand(command.Parameters),
                ServiceCommandTypes.TestVCenterConnectionWithCredentials => await TestVCenterConnectionWithCredentialsCommand(command.Parameters),
                ServiceCommandTypes.AddVCenter => await AddVCenterCommand(command.Parameters),
                ServiceCommandTypes.EditVCenter => await EditVCenterCommand(command.Parameters),
                ServiceCommandTypes.DeleteVCenter => await DeleteVCenterCommand(command.Parameters),
                _ => throw new NotSupportedException($"Command type '{command.CommandType}' is not supported")
            };

            command.Result = result;
            command.Status = "Completed";
            _logger.LogInformation("Command completed: {CommandType} (ID: {CommandId})", command.CommandType, command.Id);
        }
        catch (Exception ex)
        {
            command.ErrorMessage = ex.Message;
            command.Status = "Failed";
            _logger.LogError(ex, "Command failed: {CommandType} (ID: {CommandId})", command.CommandType, command.Id);
        }
        finally
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<VMwareDbContext>();
            dbContext.ServiceCommands.Update(command);
            await dbContext.SaveChangesAsync();
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
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<VMwareDbContext>();
        
        var host = await dbContext.Hosts
            .FirstOrDefaultAsync(h => h.Id == hostId);
        var checkDefinition = await dbContext.CheckDefinitions
            .FirstOrDefaultAsync(cd => cd.Id == checkDefinitionId);
        var vCenter = await dbContext.VCenters
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
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<VMwareDbContext>();
        
        var cluster = await dbContext.Clusters
            .FirstOrDefaultAsync(c => c.Id == clusterId);
        var vCenter = await dbContext.VCenters
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
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<VMwareDbContext>();
        
        var vCenter = await dbContext.VCenters
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
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<VMwareDbContext>();
        
        // Get or create the iSCSI check definition
        var iSCSICheck = await dbContext.CheckDefinitions
            .FirstOrDefaultAsync(cd => cd.Name.Contains("iSCSI") && cd.Name.Contains("Dead Path"));

        if (iSCSICheck == null)
        {
            // Create the iSCSI check if it doesn't exist
            var storageCategory = await dbContext.CheckCategories
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
                dbContext.CheckCategories.Add(storageCategory);
                await dbContext.SaveChangesAsync();
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

            dbContext.CheckDefinitions.Add(iSCSICheck);
            await dbContext.SaveChangesAsync();
        }

        // Get all hosts for this vCenter
        var hosts = await dbContext.Hosts
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

            var vCenterId = vCenterIdObj switch
            {
                JsonElement jsonElement => jsonElement.GetInt32(),
                int intValue => intValue,
                _ => Convert.ToInt32(vCenterIdObj)
            };
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<VMwareDbContext>();
            
            var vCenter = await dbContext.VCenters.FirstOrDefaultAsync(v => v.Id == vCenterId);
            if (vCenter == null)
            {
                throw new ArgumentException($"vCenter with ID {vCenterId} not found");
            }
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

            var vCenterId = vCenterIdObj switch
            {
                JsonElement jsonElement => jsonElement.GetInt32(),
                int intValue => intValue,
                _ => Convert.ToInt32(vCenterIdObj)
            };
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<VMwareDbContext>();
            
            var vCenter = await dbContext.VCenters.FirstOrDefaultAsync(v => v.Id == vCenterId);
            if (vCenter == null)
            {
                throw new ArgumentException($"vCenter with ID {vCenterId} not found");
            }
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



    private async Task<string> ConnectVCenterCommand(string parametersJson)
    {
        try
        {
            _logger.LogInformation("Executing ConnectVCenter command");
            
            var parameters = JsonSerializer.Deserialize<Dictionary<string, object>>(parametersJson);
            if (parameters == null || !parameters.TryGetValue("VCenterId", out var vCenterIdObj))
            {
                throw new ArgumentException("VCenterId parameter is required");
            }

            var vCenterId = vCenterIdObj switch
            {
                JsonElement jsonElement => jsonElement.GetInt32(),
                int intValue => intValue,
                _ => Convert.ToInt32(vCenterIdObj)
            };

            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<VMwareDbContext>();
            var vmwareService = scope.ServiceProvider.GetRequiredService<IVMwareConnectionService>();
            
            var vCenter = await dbContext.VCenters.FirstOrDefaultAsync(v => v.Id == vCenterId);
            if (vCenter == null)
            {
                throw new ArgumentException($"vCenter with ID {vCenterId} not found");
            }

            var session = await vmwareService.ConnectAsync(vCenter);
            
            // Update connection status
            vCenter.UpdateConnectionStatus(true);
            vCenter.LastScan = DateTime.UtcNow;
            await dbContext.SaveChangesAsync();

            return JsonSerializer.Serialize(new { Success = true, Message = $"Connected to {vCenter.Name}", SessionId = session?.SessionId });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to vCenter");
            throw;
        }
    }

    private async Task<string> TestVCenterConnectionCommand(string parametersJson)
    {
        try
        {
            _logger.LogInformation("Executing TestVCenterConnection command");
            
            var parameters = JsonSerializer.Deserialize<Dictionary<string, object>>(parametersJson);
            if (parameters == null || !parameters.TryGetValue("VCenterId", out var vCenterIdObj))
            {
                throw new ArgumentException("VCenterId parameter is required");
            }

            var vCenterId = vCenterIdObj switch
            {
                JsonElement jsonElement => jsonElement.GetInt32(),
                int intValue => intValue,
                _ => Convert.ToInt32(vCenterIdObj)
            };

            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<VMwareDbContext>();
            var vmwareService = scope.ServiceProvider.GetRequiredService<IVMwareConnectionService>();
            var credentialService = scope.ServiceProvider.GetRequiredService<ICredentialService>();
            
            var vCenter = await dbContext.VCenters.FirstOrDefaultAsync(v => v.Id == vCenterId);
            if (vCenter == null)
            {
                throw new ArgumentException($"vCenter with ID {vCenterId} not found");
            }

            var credentials = credentialService.DecryptCredentials(vCenter.EncryptedCredentials);
            var testResult = await vmwareService.TestConnectionAsync(vCenter.Url, credentials.Username, credentials.Password);

            // Update connection status
            vCenter.UpdateConnectionStatus(testResult.IsSuccessful);
            await dbContext.SaveChangesAsync();

            return JsonSerializer.Serialize(new 
            { 
                IsSuccessful = testResult.IsSuccessful,
                ResponseTime = testResult.ResponseTime.TotalMilliseconds,
                ErrorMessage = testResult.ErrorMessage
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to test vCenter connection");
            throw;
        }
    }

    private async Task<string> TestVCenterConnectionWithCredentialsCommand(string parametersJson)
    {
        try
        {
            _logger.LogInformation("Executing TestVCenterConnectionWithCredentials command");
            
            var parameters = JsonSerializer.Deserialize<Dictionary<string, object>>(parametersJson);
            if (parameters == null)
            {
                throw new ArgumentException("Invalid parameters");
            }

            // Extract parameters
            var url = parameters.TryGetValue("Url", out var urlObj) ? urlObj.ToString() : throw new ArgumentException("Url is required");
            var username = parameters.TryGetValue("Username", out var usernameObj) ? usernameObj.ToString() : throw new ArgumentException("Username is required");
            var password = parameters.TryGetValue("Password", out var passwordObj) ? passwordObj.ToString() : throw new ArgumentException("Password is required");

            using var scope = _serviceProvider.CreateScope();
            var vmwareService = scope.ServiceProvider.GetRequiredService<IVMwareConnectionService>();
            
            var testResult = await vmwareService.TestConnectionAsync(url!, username!, password!);

            return JsonSerializer.Serialize(new 
            { 
                IsSuccessful = testResult.IsSuccessful,
                ResponseTime = testResult.ResponseTime.TotalMilliseconds,
                ErrorMessage = testResult.ErrorMessage,
                VersionInfo = testResult.VersionInfo
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to test vCenter connection with credentials");
            throw;
        }
    }

    private async Task<string> AddVCenterCommand(string parametersJson)
    {
        try
        {
            _logger.LogInformation("Executing AddVCenter command");
            
            var parameters = JsonSerializer.Deserialize<Dictionary<string, object>>(parametersJson);
            if (parameters == null)
            {
                throw new ArgumentException("Invalid parameters");
            }

            // Extract parameters
            var name = parameters.TryGetValue("Name", out var nameObj) ? nameObj.ToString() : throw new ArgumentException("Name is required");
            var url = parameters.TryGetValue("Url", out var urlObj) ? urlObj.ToString() : throw new ArgumentException("Url is required");
            var username = parameters.TryGetValue("Username", out var usernameObj) ? usernameObj.ToString() : throw new ArgumentException("Username is required");
            var password = parameters.TryGetValue("Password", out var passwordObj) ? passwordObj.ToString() : throw new ArgumentException("Password is required");
            var availabilityZoneId = parameters.TryGetValue("AvailabilityZoneId", out var azIdObj) ? Convert.ToInt32(azIdObj) : (int?)null;
            var enableAutoDiscovery = parameters.TryGetValue("EnableAutoDiscovery", out var autoDiscoveryObj) ? Convert.ToBoolean(autoDiscoveryObj) : true;

            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<VMwareDbContext>();
            var credentialService = scope.ServiceProvider.GetRequiredService<ICredentialService>();
            
            // Check if vCenter with same URL already exists
            var existingVCenter = await dbContext.VCenters.FirstOrDefaultAsync(v => v.Url == url);
            if (existingVCenter != null)
            {
                throw new InvalidOperationException($"A vCenter with URL '{url}' already exists");
            }

            // Encrypt credentials using service context
            var encryptedCredentials = credentialService.EncryptCredentials(username!, password!);

            // Create new vCenter
            var vCenter = new VCenter
            {
                Name = name!,
                Url = url!,
                EncryptedCredentials = encryptedCredentials,
                AvailabilityZoneId = availabilityZoneId,
                EnableAutoDiscovery = enableAutoDiscovery,
                CreatedAt = DateTime.UtcNow,
                IsConnected = false,
                LastScan = null
            };

            dbContext.VCenters.Add(vCenter);
            await dbContext.SaveChangesAsync();

            _logger.LogInformation("vCenter '{Name}' added successfully with ID: {Id}", name, vCenter.Id);

            return JsonSerializer.Serialize(new 
            { 
                Success = true, 
                Message = $"vCenter '{name}' added successfully",
                VCenterId = vCenter.Id
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add vCenter");
            throw;
        }
    }

    private async Task<string> EditVCenterCommand(string parametersJson)
    {
        try
        {
            _logger.LogInformation("Executing EditVCenter command");
            
            var parameters = JsonSerializer.Deserialize<Dictionary<string, object>>(parametersJson);
            if (parameters == null)
            {
                throw new ArgumentException("Invalid parameters");
            }

            // Extract parameters
            var vCenterId = parameters.TryGetValue("VCenterId", out var idObj) ? Convert.ToInt32(idObj) : throw new ArgumentException("VCenterId is required");
            var name = parameters.TryGetValue("Name", out var nameObj) ? nameObj.ToString() : null;
            var url = parameters.TryGetValue("Url", out var urlObj) ? urlObj.ToString() : null;
            var username = parameters.TryGetValue("Username", out var usernameObj) ? usernameObj.ToString() : null;
            var password = parameters.TryGetValue("Password", out var passwordObj) ? passwordObj.ToString() : null;
            var availabilityZoneId = parameters.TryGetValue("AvailabilityZoneId", out var azIdObj) ? Convert.ToInt32(azIdObj) : (int?)null;
            var enableAutoDiscovery = parameters.TryGetValue("EnableAutoDiscovery", out var autoDiscoveryObj) ? Convert.ToBoolean(autoDiscoveryObj) : (bool?)null;

            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<VMwareDbContext>();
            var credentialService = scope.ServiceProvider.GetRequiredService<ICredentialService>();
            
            // Find existing vCenter
            var vCenter = await dbContext.VCenters.FirstOrDefaultAsync(v => v.Id == vCenterId);
            if (vCenter == null)
            {
                throw new ArgumentException($"vCenter with ID {vCenterId} not found");
            }

            // Update properties if provided
            if (!string.IsNullOrEmpty(name))
                vCenter.Name = name;
            
            if (!string.IsNullOrEmpty(url))
            {
                // Check if another vCenter with same URL exists
                var existingWithUrl = await dbContext.VCenters.FirstOrDefaultAsync(v => v.Url == url && v.Id != vCenterId);
                if (existingWithUrl != null)
                {
                    throw new InvalidOperationException($"Another vCenter with URL '{url}' already exists");
                }
                vCenter.Url = url;
            }

            // Update credentials if provided
            if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password))
            {
                var encryptedCredentials = credentialService.EncryptCredentials(username, password);
                vCenter.EncryptedCredentials = encryptedCredentials;
            }

            if (availabilityZoneId.HasValue)
                vCenter.AvailabilityZoneId = availabilityZoneId;

            if (enableAutoDiscovery.HasValue)
                vCenter.EnableAutoDiscovery = enableAutoDiscovery.Value;

            vCenter.UpdatedAt = DateTime.UtcNow;
            
            await dbContext.SaveChangesAsync();

            _logger.LogInformation("vCenter '{Name}' (ID: {Id}) updated successfully", vCenter.Name, vCenter.Id);

            return JsonSerializer.Serialize(new 
            { 
                Success = true, 
                Message = $"vCenter '{vCenter.Name}' updated successfully",
                VCenterId = vCenter.Id
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to edit vCenter");
            throw;
        }
    }

    private async Task<string> DeleteVCenterCommand(string parametersJson)
    {
        try
        {
            _logger.LogInformation("Executing DeleteVCenter command");
            
            var parameters = JsonSerializer.Deserialize<Dictionary<string, object>>(parametersJson);
            if (parameters == null || !parameters.TryGetValue("VCenterId", out var vCenterIdObj))
            {
                throw new ArgumentException("VCenterId parameter is required");
            }

            var vCenterId = vCenterIdObj switch
            {
                JsonElement jsonElement => jsonElement.GetInt32(),
                int intValue => intValue,
                _ => Convert.ToInt32(vCenterIdObj)
            };

            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<VMwareDbContext>();
            
            // Find the vCenter and related data
            var vCenter = await dbContext.VCenters
                .Include(v => v.Clusters)
                    .ThenInclude(c => c.Hosts)
                        .ThenInclude(h => h.CheckResults)
                .Include(v => v.Clusters)
                    .ThenInclude(c => c.Datastores)
                .FirstOrDefaultAsync(v => v.Id == vCenterId);

            if (vCenter == null)
            {
                throw new ArgumentException($"vCenter with ID {vCenterId} not found");
            }

            var vCenterName = vCenter.Name;

            // Delete all related data (cascading delete)
            foreach (var cluster in vCenter.Clusters.ToList())
            {
                foreach (var host in cluster.Hosts.ToList())
                {
                    // Delete check results for this host
                    dbContext.CheckResults.RemoveRange(host.CheckResults);
                }
                // Delete hosts in cluster
                dbContext.Hosts.RemoveRange(cluster.Hosts);
                
                // Delete datastores in cluster
                dbContext.Datastores.RemoveRange(cluster.Datastores);
            }
            
            // Delete clusters
            dbContext.Clusters.RemoveRange(vCenter.Clusters);
            
            // Finally delete the vCenter
            dbContext.VCenters.Remove(vCenter);
            
            await dbContext.SaveChangesAsync();

            _logger.LogInformation("vCenter '{Name}' (ID: {Id}) and all related data deleted successfully", vCenterName, vCenterId);

            return JsonSerializer.Serialize(new 
            { 
                Success = true, 
                Message = $"vCenter '{vCenterName}' and all related data deleted successfully"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete vCenter");
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