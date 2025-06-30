using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using VMwareGUITools.Core.Models;
using VMwareGUITools.Data;

namespace VMwareGUITools.Infrastructure.Services;

/// <summary>
/// Manages service configuration and communication between WPF application and service
/// </summary>
public class ServiceConfigurationManager : IServiceConfigurationManager
{
    private readonly ILogger<ServiceConfigurationManager> _logger;
    private readonly VMwareDbContext _dbContext;
    private readonly Timer _commandProcessingTimer;
    private readonly Timer _heartbeatTimer;

    public ServiceConfigurationManager(
        ILogger<ServiceConfigurationManager> logger,
        VMwareDbContext dbContext)
    {
        _logger = logger;
        _dbContext = dbContext;

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
                ServiceCommandTypes.ValidatePowerCLI => await ValidatePowerCLICommand(),
                ServiceCommandTypes.GetServiceStatus => await GetServiceStatusCommand(),
                ServiceCommandTypes.ReloadConfiguration => await ReloadConfigurationCommand(),
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