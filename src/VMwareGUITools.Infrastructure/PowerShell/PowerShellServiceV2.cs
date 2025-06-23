using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Management.Automation;
using System.Text;
using VMwareGUITools.Infrastructure.PowerShell;

namespace VMwareGUITools.Infrastructure.PowerShell;

/// <summary>
/// Enhanced PowerShell service that uses external process execution to bypass execution policy issues
/// Falls back to embedded runspace if external execution fails
/// </summary>
public class PowerShellServiceV2 : IPowerShellService
{
    private readonly ILogger<PowerShellServiceV2> _logger;
    private readonly IExternalPowerShellService _externalPowerShellService;
    private readonly PowerShellService _fallbackService;
    private readonly PowerShellV2Options _options;
    private bool _useExternalByDefault = true;

    public PowerShellServiceV2(
        ILogger<PowerShellServiceV2> logger,
        IExternalPowerShellService externalPowerShellService,
        PowerShellService fallbackService,
        IOptions<PowerShellV2Options> options)
    {
        _logger = logger;
        _externalPowerShellService = externalPowerShellService;
        _fallbackService = fallbackService;
        _options = options.Value;
    }

    public async Task<PowerShellResult> ExecuteScriptAsync(
        string script, 
        Dictionary<string, object>? parameters = null, 
        int timeoutSeconds = 300, 
        CancellationToken cancellationToken = default)
    {
        if (_useExternalByDefault && _options.UseExternalExecution)
        {
            try
            {
                _logger.LogDebug("Attempting external PowerShell execution");
                var externalResult = await _externalPowerShellService.ExecuteScriptAsync(script, parameters, timeoutSeconds, cancellationToken);
                
                // Convert external result to PowerShellResult
                var result = new PowerShellResult
                {
                    IsSuccess = externalResult.IsSuccess,
                    Output = externalResult.StandardOutput,
                    ErrorMessage = externalResult.ErrorMessage,
                    ExecutionTime = externalResult.ExecutionTime,
                    Objects = new List<object>()
                };

                // Try to parse output as objects if it looks like structured data
                if (result.IsSuccess && !string.IsNullOrWhiteSpace(result.Output))
                {
                    result.Objects.Add(result.Output);
                }

                if (result.IsSuccess)
                {
                    _logger.LogDebug("External PowerShell execution succeeded");
                    return result;
                }
                else if (_options.FallbackToEmbedded)
                {
                    _logger.LogWarning("External PowerShell execution failed, falling back to embedded: {Error}", externalResult.ErrorMessage);
                }
                else
                {
                    return result;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "External PowerShell execution threw exception, falling back to embedded");
                
                if (!_options.FallbackToEmbedded)
                {
                    return new PowerShellResult
                    {
                        IsSuccess = false,
                        ErrorMessage = $"External PowerShell execution failed: {ex.Message}",
                        ExecutionTime = TimeSpan.Zero
                    };
                }
            }
        }

        // Fallback to embedded execution
        _logger.LogDebug("Using embedded PowerShell execution");
        return await _fallbackService.ExecuteScriptAsync(script, parameters, timeoutSeconds, cancellationToken);
    }

    public async Task<PowerShellResult> ExecutePowerCLICommandAsync(
        string command, 
        Dictionary<string, object>? parameters = null, 
        int timeoutSeconds = 300, 
        CancellationToken cancellationToken = default)
    {
        if (_useExternalByDefault && _options.UseExternalExecution)
        {
            try
            {
                _logger.LogDebug("Attempting external PowerCLI execution");
                var externalResult = await _externalPowerShellService.ExecutePowerCLICommandAsync(command, parameters, timeoutSeconds, cancellationToken);
                
                // Convert external result to PowerShellResult
                var result = new PowerShellResult
                {
                    IsSuccess = externalResult.IsSuccess,
                    Output = externalResult.StandardOutput,
                    ErrorMessage = externalResult.ErrorMessage,
                    ExecutionTime = externalResult.ExecutionTime,
                    Objects = new List<object>()
                };

                // Try to parse output as objects if it looks like structured data
                if (result.IsSuccess && !string.IsNullOrWhiteSpace(result.Output))
                {
                    result.Objects.Add(result.Output);
                }

                if (result.IsSuccess)
                {
                    _logger.LogDebug("External PowerCLI execution succeeded");
                    return result;
                }
                else if (_options.FallbackToEmbedded)
                {
                    _logger.LogWarning("External PowerCLI execution failed, falling back to embedded: {Error}", externalResult.ErrorMessage);
                }
                else
                {
                    return result;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "External PowerCLI execution threw exception, falling back to embedded");
                
                if (!_options.FallbackToEmbedded)
                {
                    return new PowerShellResult
                    {
                        IsSuccess = false,
                        ErrorMessage = $"External PowerCLI execution failed: {ex.Message}",
                        ExecutionTime = TimeSpan.Zero
                    };
                }
            }
        }

        // Fallback to embedded execution
        _logger.LogDebug("Using embedded PowerCLI execution");
        return await _fallbackService.ExecutePowerCLICommandAsync(command, parameters, timeoutSeconds, cancellationToken);
    }

    public async Task<bool> IsPowerCLIAvailableAsync()
    {
        if (_useExternalByDefault && _options.UseExternalExecution)
        {
            try
            {
                return await _externalPowerShellService.TestPowerCLIAvailabilityAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "External PowerCLI availability check failed, falling back to embedded");
                
                if (!_options.FallbackToEmbedded)
                {
                    return false;
                }
            }
        }

        return await _fallbackService.IsPowerCLIAvailableAsync();
    }

    public async Task<PowerCLIVersionInfo> GetPowerCLIVersionAsync()
    {
        return await _fallbackService.GetPowerCLIVersionAsync();
    }

    public async Task<bool> InitializePowerCLIAsync(CancellationToken cancellationToken = default)
    {
        if (_useExternalByDefault && _options.UseExternalExecution)
        {
            // External execution doesn't need initialization
            try
            {
                var isAvailable = await _externalPowerShellService.TestPowerCLIAvailabilityAsync();
                if (isAvailable)
                {
                    _logger.LogInformation("External PowerCLI execution is available and ready");
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "External PowerCLI availability check failed during initialization");
            }
        }

        // Fallback to embedded initialization
        return await _fallbackService.InitializePowerCLIAsync(cancellationToken);
    }

    /// <summary>
    /// Force the service to use embedded execution for debugging or troubleshooting
    /// </summary>
    public void ForceEmbeddedExecution()
    {
        _useExternalByDefault = false;
        _logger.LogInformation("Forced to use embedded PowerShell execution");
    }

    /// <summary>
    /// Reset to use external execution by default
    /// </summary>
    public void ResetToExternalExecution()
    {
        _useExternalByDefault = true;
        _logger.LogInformation("Reset to use external PowerShell execution by default");
    }

    public void Dispose()
    {
        _fallbackService?.Dispose();
    }
}

/// <summary>
/// Configuration options for enhanced PowerShell service
/// </summary>
public class PowerShellV2Options
{
    public const string SectionName = "PowerShellV2";
    
    /// <summary>
    /// Whether to use external PowerShell process execution by default
    /// </summary>
    public bool UseExternalExecution { get; set; } = true;
    
    /// <summary>
    /// Whether to fallback to embedded execution if external fails
    /// </summary>
    public bool FallbackToEmbedded { get; set; } = true;
    
    /// <summary>
    /// Timeout for external process execution in seconds
    /// </summary>
    public int ExternalTimeoutSeconds { get; set; } = 300;
    
    /// <summary>
    /// Whether to log external PowerShell output for debugging
    /// </summary>
    public bool LogExternalOutput { get; set; } = false;
} 