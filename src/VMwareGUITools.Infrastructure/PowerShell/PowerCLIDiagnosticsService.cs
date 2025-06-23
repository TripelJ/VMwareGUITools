using Microsoft.Extensions.Logging;

namespace VMwareGUITools.Infrastructure.PowerShell;

/// <summary>
/// Service for PowerCLI diagnostics and repair operations
/// </summary>
public class PowerCLIDiagnosticsService
{
    private readonly ILogger<PowerCLIDiagnosticsService> _logger;
    private readonly IPowerCLIService _powerCLIService;

    public PowerCLIDiagnosticsService(
        ILogger<PowerCLIDiagnosticsService> logger,
        IPowerCLIService powerCLIService)
    {
        _logger = logger;
        _powerCLIService = powerCLIService;
    }

    /// <summary>
    /// Runs comprehensive PowerCLI diagnostics
    /// </summary>
    public async Task<PowerCLIValidationResult> RunDiagnosticsAsync()
    {
        try
        {
            _logger.LogInformation("Running PowerCLI diagnostics");
            return await _powerCLIService.ValidatePowerCLIAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to run PowerCLI diagnostics");
            return new PowerCLIValidationResult
            {
                IsValid = false,
                ErrorMessage = $"Diagnostics failed: {ex.Message}",
                RequiresRepair = true
            };
        }
    }

    /// <summary>
    /// Attempts to repair common PowerCLI issues
    /// </summary>
    public async Task<PowerCLIRepairResult> RepairPowerCLIAsync()
    {
        try
        {
            _logger.LogInformation("Starting PowerCLI repair");
            return await _powerCLIService.RepairPowerCLIAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to repair PowerCLI");
            return new PowerCLIRepairResult
            {
                IsSuccessful = false,
                ErrorMessage = $"Repair failed: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Gets PowerCLI version information
    /// </summary>
    public async Task<PowerCLIVersionInfo> GetVersionInfoAsync()
    {
        try
        {
            _logger.LogInformation("Getting PowerCLI version info");
            return await _powerCLIService.GetVersionInfoAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get PowerCLI version info");
            return new PowerCLIVersionInfo
            {
                Version = "Unknown",
                IsCompatible = false,
                CompatibilityMessage = ex.Message
            };
        }
    }
} 