using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using VMwareGUITools.Infrastructure.PowerShell;
using VMwareGUITools.Infrastructure.VMware;
using VMwareGUITools.Infrastructure.Security;
using VMwareGUITools.Infrastructure.Checks;
using VMwareGUITools.Infrastructure.Notifications;
using VMwareGUITools.Infrastructure.Scheduling;

namespace VMwareGUITools.Infrastructure.Extensions;

/// <summary>
/// Extension methods for configuring VMware GUI Tools services
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds VMware GUI Tools infrastructure services with the new external PowerShell execution
    /// This replaces the old problematic embedded PowerShell services
    /// </summary>
    public static IServiceCollection AddVMwareGUIToolsInfrastructure(
        this IServiceCollection services, 
        IConfiguration configuration)
    {
        // Configure options
        services.Configure<ExternalPowerShellOptions>(configuration.GetSection(ExternalPowerShellOptions.SectionName));
        services.Configure<PowerShellV2Options>(configuration.GetSection(PowerShellV2Options.SectionName));
        
        // Register HTTP client for REST API services
        services.AddHttpClient();
        
        // NEW: External PowerShell Services (bypasses execution policy issues)
        services.AddScoped<IExternalPowerShellService, ExternalPowerShellService>();
        
        // NEW: Hybrid PowerShell service (tries external first, falls back to embedded)
        services.AddScoped<PowerShellService>(); // Keep as fallback
        services.AddScoped<PowerShellServiceV2>();
        
        // Replace the old problematic IPowerShellService with the new external one
        services.AddScoped<IPowerShellService>(provider => 
        {
            var logger = provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<PowerShellServiceV2>>();
            var externalService = provider.GetRequiredService<IExternalPowerShellService>();
            var fallbackService = provider.GetRequiredService<PowerShellService>();
            var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<PowerShellV2Options>>();
            
            return new PowerShellServiceV2(logger, externalService, fallbackService, options);
        });
        
        // Keep the PowerCLI service but it will now use the external PowerShell service
        services.Configure<PowerCLIOptions>(configuration.GetSection(PowerCLIOptions.SectionName));
        services.AddSingleton<IPowerCLIService, PowerCLIService>();
        services.AddScoped<PowerCLIDiagnosticsService>();
        
        // VMware Connection Services
        services.AddScoped<ICredentialService, CredentialService>();
        
        // Option 1: Use Enhanced service with external PowerShell (recommended)
        services.AddScoped<IVMwareConnectionService, EnhancedVMwareConnectionService>();
        
        // Option 2: Alternative REST API service (completely bypasses PowerShell)
        services.AddScoped<RestVMwareConnectionService>();
        
        // Check and execution services
        services.AddScoped<ICheckEngine, PowerCLICheckEngine>();
        services.AddScoped<ICheckExecutionService, CheckExecutionService>();
        services.Configure<CheckExecutionOptions>(configuration.GetSection(CheckExecutionOptions.SectionName));
        
        // Notification services
        services.AddScoped<INotificationService, NotificationService>();
        
        // Scheduling services
        services.AddScoped<ISchedulingService, SchedulingService>();
        
        return services;
    }
    
    /// <summary>
    /// Adds VMware GUI Tools infrastructure services using REST API instead of PowerCLI
    /// This completely eliminates PowerShell dependency
    /// </summary>
    public static IServiceCollection AddVMwareGUIToolsInfrastructureWithRestAPI(
        this IServiceCollection services, 
        IConfiguration configuration)
    {
        // Configure HTTP client for REST API services
        services.AddHttpClient();
        
        // Security services
        services.AddScoped<ICredentialService, CredentialService>();
        
        // Use REST API VMware service (no PowerShell at all)
        services.AddScoped<IVMwareConnectionService, RestVMwareConnectionService>();
        
        // Notification services
        services.AddScoped<INotificationService, NotificationService>();
        
        // Scheduling services
        services.AddScoped<ISchedulingService, SchedulingService>();
        
        // Note: Check engine services omitted since they rely on PowerCLI
        // You would need to implement REST API-based checks if needed
        
        return services;
    }
    
    /// <summary>
    /// Legacy method - DO NOT USE in production
    /// This is kept only for comparison/debugging purposes
    /// </summary>
    [Obsolete("Use AddVMwareGUIToolsInfrastructure instead. This method uses problematic embedded PowerShell services.")]
    public static IServiceCollection AddVMwareGUIToolsInfrastructureLegacy(
        this IServiceCollection services, 
        IConfiguration configuration)
    {
        // OLD PROBLEMATIC SERVICES - DO NOT USE
        services.Configure<PowerShellOptions>(configuration.GetSection(PowerShellOptions.SectionName));
        services.AddScoped<IPowerShellService, PowerShellService>(); // PROBLEMATIC
        
        services.Configure<PowerCLIOptions>(configuration.GetSection(PowerCLIOptions.SectionName));
        services.AddSingleton<IPowerCLIService, PowerCLIService>(); // PROBLEMATIC
        
        services.AddScoped<ICredentialService, CredentialService>();
        services.AddScoped<IVMwareConnectionService, VMwareConnectionService>(); // PROBLEMATIC
        
        return services;
    }
} 