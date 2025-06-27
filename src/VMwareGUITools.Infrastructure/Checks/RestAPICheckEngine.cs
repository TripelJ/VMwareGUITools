using Microsoft.Extensions.Logging;
using System.Text.Json;
using VMwareGUITools.Core.Models;
using VMwareGUITools.Infrastructure.Security;
using VMwareGUITools.Infrastructure.VMware;

namespace VMwareGUITools.Infrastructure.Checks;

/// <summary>
/// Check engine for executing REST API-based checks against vSphere with SDK integration for specialized checks
/// </summary>
public class RestAPICheckEngine : ICheckEngine
{
    private readonly ILogger<RestAPICheckEngine> _logger;
    private readonly IVSphereRestAPIService _vsphereService;
    private readonly IVSphereSDKService _vsphereSDKService;
    private readonly ICredentialService _credentialService;

    public string ExecutionType => "vSphereRestAPI";

    public RestAPICheckEngine(
        ILogger<RestAPICheckEngine> logger,
        IVSphereRestAPIService vsphereService,
        IVSphereSDKService vsphereSDKService,
        ICredentialService credentialService)
    {
        _logger = logger;
        _vsphereService = vsphereService;
        _vsphereSDKService = vsphereSDKService;
        _credentialService = credentialService;
    }

    public async Task<CheckEngineResult> ExecuteAsync(Host host, CheckDefinition checkDefinition, VCenter vCenter, CancellationToken cancellationToken = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = new CheckEngineResult();

        try
        {
            _logger.LogDebug("Executing REST API check '{CheckName}' on host '{HostName}'", checkDefinition.Name, host.Name);

            // Establish vSphere session
            VSphereSession session;
            try
            {
                session = await _vsphereService.ConnectAsync(vCenter, cancellationToken);
            }
            catch (Exception ex)
            {
                return new CheckEngineResult
                {
                    IsSuccess = false,
                    ErrorMessage = $"Failed to connect to vCenter: {ex.Message}",
                    ExecutionTime = stopwatch.Elapsed
                };
            }

            try
            {
                // Parse check parameters
                var parameters = ParseCheckParameters(checkDefinition);

                // Execute the check based on the check type or script content
                var checkType = DetermineCheckType(checkDefinition);
                
                // Use vSphere SDK for iSCSI path checks for better accuracy
                if (IsISCSIPathCheck(checkDefinition))
                {
                    var sdkResult = await ExecuteISCSICheckWithSDK(vCenter, host.MoId, checkDefinition, parameters, cancellationToken);
                    
                    result.IsSuccess = sdkResult.IsSuccess;
                    result.ExecutionTime = stopwatch.Elapsed;
                    result.Output = sdkResult.Output;
                    result.RawData = sdkResult.RawData;
                    
                    if (!sdkResult.IsSuccess)
                    {
                        result.ErrorMessage = sdkResult.ErrorMessage;
                    }
                }
                else
                {
                    var apiResult = await _vsphereService.ExecuteCheckAsync(session, host.MoId, checkType, parameters, cancellationToken);

                    // Process results
                    result.IsSuccess = apiResult.IsSuccess;
                    result.ExecutionTime = stopwatch.Elapsed;
                    result.Output = apiResult.Data;
                    result.RawData = JsonSerializer.Serialize(apiResult);

                    if (!apiResult.IsSuccess)
                    {
                        result.ErrorMessage = apiResult.ErrorMessage ?? "Check execution failed";
                    }

                    // Evaluate thresholds if defined
                    if (checkDefinition.HasThreshold && result.IsSuccess)
                    {
                        result = EvaluateThresholds(result, checkDefinition, apiResult);
                    }
                }

                _logger.LogDebug("REST API check '{CheckName}' on host '{HostName}' completed in {ElapsedMs}ms with status: {Success}",
                    checkDefinition.Name, host.Name, stopwatch.ElapsedMilliseconds, result.IsSuccess);

                return result;
            }
            finally
            {
                // Always disconnect the session
                try
                {
                    await _vsphereService.DisconnectAsync(session, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error disconnecting vSphere session after check execution");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "REST API check '{CheckName}' on host '{HostName}' failed with exception", 
                checkDefinition.Name, host.Name);

            return new CheckEngineResult
            {
                IsSuccess = false,
                ErrorMessage = ex.Message,
                ExecutionTime = stopwatch.Elapsed
            };
        }
    }

    public async Task<CheckEngineResult> ValidateAsync(Host host, CheckDefinition checkDefinition, VCenter vCenter, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Validating REST API check '{CheckName}'", checkDefinition.Name);

            var warnings = new List<string>();

            // Validate check definition
            if (string.IsNullOrWhiteSpace(checkDefinition.Name))
            {
                warnings.Add("Check name is required");
            }

            if (checkDefinition.TimeoutSeconds <= 0)
            {
                warnings.Add("Check timeout must be greater than 0");
            }

            // Validate check type
            var checkType = DetermineCheckType(checkDefinition);
            if (string.IsNullOrWhiteSpace(checkType))
            {
                warnings.Add("Unable to determine check type from definition");
            }

            // Validate parameters
            try
            {
                var parameters = ParseCheckParameters(checkDefinition);
            }
            catch (Exception ex)
            {
                warnings.Add($"Invalid parameters: {ex.Message}");
            }

            // Validate thresholds
            if (checkDefinition.HasThreshold)
            {
                try
                {
                    var thresholds = checkDefinition.GetThresholds();
                    if (thresholds.Count == 0 && string.IsNullOrWhiteSpace(checkDefinition.ThresholdCriteria))
                    {
                        warnings.Add("Threshold configuration is incomplete");
                    }
                }
                catch (Exception ex)
                {
                    warnings.Add($"Invalid threshold configuration: {ex.Message}");
                }
            }

            // Test connectivity
            try
            {
                var credentials = await _credentialService.DecryptCredentialsAsync(vCenter.EncryptedCredentials);
                if (credentials == null)
                {
                    warnings.Add("Unable to decrypt vCenter credentials");
                }
                else
                {
                    var connectionResult = await _vsphereService.TestConnectionAsync(
                        vCenter.Url, 
                        credentials?.Username ?? string.Empty, 
                        credentials?.Password ?? string.Empty, 
                        cancellationToken);

                    if (!connectionResult.IsSuccessful)
                    {
                        warnings.Add($"vCenter connection test failed: {connectionResult.ErrorMessage}");
                    }
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"Connection validation failed: {ex.Message}");
            }

            return new CheckEngineResult
            {
                IsSuccess = warnings.Count == 0,
                Output = warnings.Count == 0 ? "Validation successful" : "Validation completed with warnings",
                Warnings = warnings,
                ExecutionTime = TimeSpan.FromSeconds(1) // Minimal time for validation
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "REST API check validation failed for '{CheckName}'", checkDefinition.Name);

            return new CheckEngineResult
            {
                IsSuccess = false,
                ErrorMessage = ex.Message,
                ExecutionTime = TimeSpan.FromSeconds(1)
            };
        }
    }

    public async Task<bool> IsAvailableAsync()
    {
        try
        {
            // REST API is always available as long as we can make HTTP requests
            // No external dependencies like PowerShell modules
            return await Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check REST API availability");
            return false;
        }
    }

    private Dictionary<string, object> ParseCheckParameters(CheckDefinition checkDefinition)
    {
        var parameters = new Dictionary<string, object>();

        // Parse JSON parameters
        if (!string.IsNullOrWhiteSpace(checkDefinition.Parameters))
        {
            try
            {
                var jsonParams = JsonSerializer.Deserialize<Dictionary<string, object>>(checkDefinition.Parameters);
                if (jsonParams != null)
                {
                    foreach (var param in jsonParams)
                    {
                        parameters[param.Key] = param.Value;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse check parameters for '{CheckName}'", checkDefinition.Name);
            }
        }

        // Add default parameters
        parameters["timeout"] = checkDefinition.TimeoutSeconds;
        parameters["category"] = checkDefinition.Category?.Name ?? "Unknown";
        parameters["severity"] = checkDefinition.DefaultSeverity.ToString();

        return parameters;
    }

    private string DetermineCheckType(CheckDefinition checkDefinition)
    {
        // Map check definitions to REST API check types
        // This could be enhanced to parse the script content or use a mapping table

        // Check by name first for specific well-known checks
        if (!string.IsNullOrWhiteSpace(checkDefinition.Name))
        {
            var name = checkDefinition.Name.ToLower();
            
            if (name.Contains("iscsi") && name.Contains("dead") && name.Contains("path"))
                return "host-storage";
            
            if (name.Contains("multipath") || name.Contains("storage path"))
                return "host-storage";
            
            if (name.Contains("ssh") && name.Contains("service"))
                return "host-security";
        }

        if (!string.IsNullOrWhiteSpace(checkDefinition.Script))
        {
            var script = checkDefinition.Script.ToLower();

            if (script.Contains("cpu") || script.Contains("memory") || script.Contains("performance"))
                return "host-performance";
            
            if (script.Contains("hardware") || script.Contains("bios") || script.Contains("firmware"))
                return "host-hardware";
            
            if (script.Contains("network") || script.Contains("vnic") || script.Contains("vmkernel"))
                return "host-networking";
            
            if (script.Contains("iscsi") || script.Contains("storage") || script.Contains("datastore") || script.Contains("vmfs"))
                return "host-storage";
            
            if (script.Contains("security") || script.Contains("firewall") || script.Contains("certificate") || script.Contains("ssh"))
                return "host-security";
            
            if (script.Contains("configuration") || script.Contains("settings") || script.Contains("policy"))
                return "host-configuration";
        }

        // Default to configuration check
        return "host-configuration";
    }

    private CheckEngineResult EvaluateThresholds(CheckEngineResult result, CheckDefinition checkDefinition, VSphereApiResult apiResult)
    {
        try
        {
            var thresholds = checkDefinition.GetThresholds();
            
            // Simple threshold evaluation - can be enhanced based on needs
            foreach (var threshold in thresholds)
            {
                var thresholdKey = threshold.Key?.ToLower() ?? string.Empty;
                var thresholdValue = threshold.Value;

                if (!string.IsNullOrEmpty(thresholdKey) && apiResult.Properties != null && apiResult.Properties.TryGetValue(thresholdKey, out var actualValue))
                {
                    // Compare values based on type
                    if (actualValue is int intValue && thresholdValue is JsonElement jsonElement && jsonElement.ValueKind == JsonValueKind.Number)
                    {
                        var thresholdInt = jsonElement.GetInt32();
                        if (intValue > thresholdInt)
                        {
                            result.Warnings ??= new List<string>();
                            result.Warnings.Add($"{thresholdKey} value {intValue} exceeds threshold {thresholdInt}");
                        }
                    }
                    // Add more comparison logic as needed
                }
            }

            // Evaluate custom threshold criteria if present
            if (!string.IsNullOrWhiteSpace(checkDefinition.ThresholdCriteria))
            {
                // Simple criteria evaluation - can be enhanced with expression parsing
                var criteria = checkDefinition.ThresholdCriteria.ToLower();
                if (criteria.Contains("error") && !string.IsNullOrEmpty(result.Output) && result.Output.ToLower().Contains("error"))
                {
                    result.IsSuccess = false;
                    result.ErrorMessage = "Threshold criteria matched: error condition detected";
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to evaluate thresholds for check '{CheckName}'", checkDefinition.Name);
            result.Warnings ??= new List<string>();
            result.Warnings.Add($"Threshold evaluation failed: {ex.Message}");
            return result;
        }
    }

    /// <summary>
    /// Determines if the check definition is for iSCSI path monitoring
    /// </summary>
    private static bool IsISCSIPathCheck(CheckDefinition checkDefinition)
    {
        if (string.IsNullOrWhiteSpace(checkDefinition.Name))
            return false;

        var name = checkDefinition.Name.ToLower();
        return name.Contains("iscsi") && name.Contains("dead") && name.Contains("path");
    }

    /// <summary>
    /// Executes iSCSI path check using the vSphere SDK for accurate results
    /// </summary>
    private async Task<CheckEngineResult> ExecuteISCSICheckWithSDK(VCenter vCenter, string hostMoId, CheckDefinition checkDefinition, Dictionary<string, object> parameters, CancellationToken cancellationToken)
    {
        VimClientConnection? sdkConnection = null;
        try
        {
            _logger.LogInformation("Executing iSCSI dead path check using vSphere SDK for host {HostMoId}", hostMoId);

            // Connect using vSphere SDK
            sdkConnection = await _vsphereSDKService.ConnectAsync(vCenter, cancellationToken);

            // Get iSCSI path status
            var iscsiResult = await _vsphereSDKService.GetISCSIPathStatusAsync(sdkConnection, hostMoId, cancellationToken);

            // Parse threshold parameter
            var maxDeadPaths = 0;
            if (parameters.TryGetValue("maxDeadPaths", out var maxDeadValue))
            {
                int.TryParse(maxDeadValue?.ToString(), out maxDeadPaths);
            }

            // Determine success based on dead path count vs threshold
            var isSuccess = iscsiResult.IsSuccess && (iscsiResult.DeadPaths <= maxDeadPaths);
            var errorMessage = string.Empty;

            if (!iscsiResult.IsSuccess)
            {
                errorMessage = iscsiResult.ErrorMessage ?? "iSCSI path check failed";
            }
            else if (iscsiResult.DeadPaths > maxDeadPaths)
            {
                errorMessage = $"Found {iscsiResult.DeadPaths} dead paths, exceeding threshold of {maxDeadPaths}";
                isSuccess = false;
            }

            // Build detailed output
            var outputBuilder = new System.Text.StringBuilder();
            outputBuilder.AppendLine($"iSCSI Path Check Results for Host: {iscsiResult.HostName}");
            outputBuilder.AppendLine($"Check Time: {iscsiResult.CheckedAt:yyyy-MM-dd HH:mm:ss} UTC");
            outputBuilder.AppendLine($"Host MORef ID: {iscsiResult.HostMoId}");
            outputBuilder.AppendLine();
            
            outputBuilder.AppendLine("=== Path Summary ===");
            outputBuilder.AppendLine($"Total iSCSI Adapters: {iscsiResult.ISCSIAdapters.Count}");
            outputBuilder.AppendLine($"Total Paths: {iscsiResult.TotalPaths}");
            outputBuilder.AppendLine($"Active Paths: {iscsiResult.ActivePaths}");
            outputBuilder.AppendLine($"Dead Paths: {iscsiResult.DeadPaths}");
            outputBuilder.AppendLine($"Standby Paths: {iscsiResult.StandbyPaths}");
            outputBuilder.AppendLine($"Threshold (Max Dead Paths): {maxDeadPaths}");
            outputBuilder.AppendLine($"Status: {(isSuccess ? "PASS" : "FAIL")}");
            outputBuilder.AppendLine();

            if (iscsiResult.ISCSIAdapters.Any())
            {
                outputBuilder.AppendLine("=== iSCSI Adapter Details ===");
                foreach (var adapter in iscsiResult.ISCSIAdapters)
                {
                    outputBuilder.AppendLine($"Adapter: {adapter.AdapterName}");
                    outputBuilder.AppendLine($"  Type: {adapter.HbaType}");
                    outputBuilder.AppendLine($"  Online: {adapter.IsOnline}");
                    outputBuilder.AppendLine($"  Portal Addresses: {string.Join(", ", adapter.PortalAddresses)}");
                    outputBuilder.AppendLine($"  Path Count: {adapter.Paths.Count}");
                    outputBuilder.AppendLine();
                }

                outputBuilder.AppendLine("=== Path Details ===");
                foreach (var path in iscsiResult.PathDetails.OrderBy(p => p.Adapter).ThenBy(p => p.PathName))
                {
                    var status = path.IsActive ? "ACTIVE" : path.PathState.ToUpper();
                    outputBuilder.AppendLine($"{path.PathName} -> {status} ({path.Transport})");
                    if (!string.IsNullOrEmpty(path.Target))
                    {
                        outputBuilder.AppendLine($"  Target: {path.Target}, LUN: {path.Lun}");
                    }
                }
            }
            else
            {
                outputBuilder.AppendLine("No iSCSI adapters found on this host.");
            }

            if (!string.IsNullOrEmpty(errorMessage))
            {
                outputBuilder.AppendLine();
                outputBuilder.AppendLine($"Error: {errorMessage}");
            }

            // Build raw data for detailed analysis
            var rawData = JsonSerializer.Serialize(iscsiResult, new JsonSerializerOptions 
            { 
                WriteIndented = true 
            });

            return new CheckEngineResult
            {
                IsSuccess = isSuccess,
                Output = outputBuilder.ToString(),
                ErrorMessage = errorMessage,
                RawData = rawData,
                ExecutionTime = TimeSpan.FromMilliseconds(100) // Will be overwritten by caller
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute iSCSI check with vSphere SDK for host {HostMoId}", hostMoId);
            return new CheckEngineResult
            {
                IsSuccess = false,
                ErrorMessage = $"iSCSI SDK check failed: {ex.Message}",
                Output = $"Failed to execute iSCSI dead path check using vSphere SDK.\nError: {ex.Message}\n\nThis indicates a problem with the vSphere SDK connection or host access.",
                ExecutionTime = TimeSpan.FromMilliseconds(100)
            };
        }
        finally
        {
            // Clean up SDK connection
            if (sdkConnection != null)
            {
                try
                {
                    await _vsphereSDKService.DisconnectAsync(sdkConnection, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error disconnecting vSphere SDK session");
                }
            }
        }
    }
} 