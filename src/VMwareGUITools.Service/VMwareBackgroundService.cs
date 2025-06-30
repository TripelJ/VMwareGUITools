using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VMwareGUITools.Infrastructure.Services;

namespace VMwareGUITools.Service;

/// <summary>
/// Background service that manages the VMware GUI Tools service operations
/// </summary>
public class VMwareBackgroundService : BackgroundService
{
    private readonly ILogger<VMwareBackgroundService> _logger;
    private readonly IServiceProvider _serviceProvider;

    public VMwareBackgroundService(
        ILogger<VMwareBackgroundService> logger,
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("VMware GUI Tools Service starting...");

        try
        {
            // Initialize the service configuration manager to start heartbeat
            using var scope = _serviceProvider.CreateScope();
            var serviceConfigManager = scope.ServiceProvider.GetRequiredService<IServiceConfigurationManager>();
            
            _logger.LogInformation("ServiceConfigurationManager initialized - heartbeat started");

            // Keep the service running and update status periodically
            while (!stoppingToken.IsCancellationRequested)
            {
                await serviceConfigManager.UpdateServiceStatusAsync("Running");
                _logger.LogDebug("Service heartbeat updated");
                
                // Wait 30 seconds before next heartbeat
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("VMware GUI Tools Service is stopping");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "VMware GUI Tools Service encountered an error");
            throw;
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("VMware GUI Tools Service is stopping...");
        
        try
        {
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
} 