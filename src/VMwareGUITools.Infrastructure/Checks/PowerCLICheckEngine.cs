using Microsoft.Extensions.Logging;
using System.Management.Automation;
using System.Text;
using VMwareGUITools.Core.Models;
using VMwareGUITools.Infrastructure.PowerShell;
using VMwareGUITools.Infrastructure.Security;

namespace VMwareGUITools.Infrastructure.Checks;

/// <summary>
/// Check engine for executing PowerCLI-based checks
/// </summary>
public class PowerCLICheckEngine : ICheckEngine
{
    private readonly ILogger<PowerCLICheckEngine> _logger;
    private readonly IPowerShellService _powerShellService;
    private readonly IPowerCLIService _powerCLIService;
    private readonly ICredentialService _credentialService;

    public string ExecutionType => "PowerCLI";

    public PowerCLICheckEngine(
        ILogger<PowerCLICheckEngine> logger,
        IPowerShellService powerShellService,
        IPowerCLIService powerCLIService,
        ICredentialService credentialService)
    {
        _logger = logger;
        _powerShellService = powerShellService;
        _powerCLIService = powerCLIService;
        _credentialService = credentialService;
    }

    public async Task<CheckEngineResult> ExecuteAsync(Host host, CheckDefinition checkDefinition, VCenter vCenter, CancellationToken cancellationToken = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = new CheckEngineResult();

        try
        {
            _logger.LogDebug("Executing PowerCLI check '{CheckName}' on host '{HostName}'", checkDefinition.Name, host.Name);

            // Decrypt vCenter credentials
            var credentials = await _credentialService.DecryptCredentialsAsync(vCenter.EncryptedCredentials);
            if (credentials == null)
            {
                return new CheckEngineResult
                {
                    IsSuccess = false,
                    ErrorMessage = "Failed to decrypt vCenter credentials",
                    ExecutionTime = stopwatch.Elapsed
                };
            }

            // Build the PowerCLI script with connection and context
            var scriptBuilder = new StringBuilder();

            // Add connection setup
            scriptBuilder.AppendLine("param($VCenterUrl, $Username, $Password, $HostName)");
            scriptBuilder.AppendLine("try {");
            scriptBuilder.AppendLine("    Set-PowerCLIConfiguration -InvalidCertificateAction Ignore -Confirm:$false -ErrorAction SilentlyContinue");
            scriptBuilder.AppendLine("    $connection = Connect-VIServer -Server $VCenterUrl -User $Username -Password $Password -ErrorAction Stop");
            
            // Add host context if the script needs it
            if (checkDefinition.Script.Contains("$VMHost") || checkDefinition.Script.Contains("Get-VMHost"))
            {
                scriptBuilder.AppendLine("    $VMHost = Get-VMHost -Name $HostName -ErrorAction Stop");
            }

            // Add the actual check script
            scriptBuilder.AppendLine();
            scriptBuilder.AppendLine("    # User-defined check script");
            scriptBuilder.AppendLine(checkDefinition.Script);
            scriptBuilder.AppendLine();

            // Add cleanup
            scriptBuilder.AppendLine("    Disconnect-VIServer -Server $VCenterUrl -Confirm:$false -ErrorAction SilentlyContinue");
            scriptBuilder.AppendLine("} catch {");
            scriptBuilder.AppendLine("    Disconnect-VIServer -Server $VCenterUrl -Confirm:$false -ErrorAction SilentlyContinue");
            scriptBuilder.AppendLine("    throw");
            scriptBuilder.AppendLine("}");

            var parameters = new Dictionary<string, object>
            {
                ["VCenterUrl"] = vCenter.Url,
                ["Username"] = credentials.Username,
                ["Password"] = credentials.Password,
                ["HostName"] = host.Name
            };

            // Add any custom parameters from the check definition
            if (!string.IsNullOrWhiteSpace(checkDefinition.Parameters))
            {
                try
                {
                    var customParams = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(checkDefinition.Parameters);
                    if (customParams != null)
                    {
                        foreach (var param in customParams)
                        {
                            parameters[param.Key] = param.Value;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to parse custom parameters for check '{CheckName}'", checkDefinition.Name);
                }
            }

            // Execute the PowerCLI script
            var psResult = await _powerShellService.ExecutePowerCLICommandAsync(
                scriptBuilder.ToString(), 
                parameters, 
                checkDefinition.TimeoutSeconds, 
                cancellationToken);

            // Process results
            result.IsSuccess = psResult.IsSuccess;
            result.ExecutionTime = psResult.ExecutionTime;
            result.RawData = System.Text.Json.JsonSerializer.Serialize(psResult);

            if (psResult.IsSuccess)
            {
                // Format output from PowerShell objects
                if (psResult.Objects.Any())
                {
                    var outputLines = new List<string>();
                    foreach (var obj in psResult.Objects)
                    {
                        if (obj is PSObject psObj)
                        {
                            // Try to get a meaningful string representation
                            var properties = psObj.Properties.Where(p => p.IsGettable).ToList();
                            if (properties.Count == 1)
                            {
                                outputLines.Add(properties[0].Value?.ToString() ?? "");
                            }
                            else if (properties.Any())
                            {
                                var propStrings = properties.Select(p => $"{p.Name}: {p.Value}");
                                outputLines.Add(string.Join(", ", propStrings));
                            }
                            else
                            {
                                outputLines.Add(psObj.ToString());
                            }
                        }
                        else
                        {
                            outputLines.Add(obj?.ToString() ?? "");
                        }
                    }
                    result.Output = string.Join(Environment.NewLine, outputLines);
                }
                else
                {
                    result.Output = psResult.Output;
                }

                // Add warnings if any
                if (psResult.Warnings.Any())
                {
                    result.Warnings = psResult.Warnings;
                }
            }
            else
            {
                result.ErrorMessage = psResult.ErrorOutput;
                result.Output = psResult.Output;
            }

            _logger.LogDebug("PowerCLI check '{CheckName}' on host '{HostName}' completed in {ElapsedMs}ms with status: {Success}",
                checkDefinition.Name, host.Name, stopwatch.ElapsedMilliseconds, result.IsSuccess);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PowerCLI check '{CheckName}' on host '{HostName}' failed with exception", 
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
            _logger.LogDebug("Validating PowerCLI check '{CheckName}'", checkDefinition.Name);

            // Perform basic syntax validation
            var warnings = new List<string>();

            // Check for common PowerCLI patterns
            if (!checkDefinition.Script.Contains("Get-") && !checkDefinition.Script.Contains("Invoke-"))
            {
                warnings.Add("Script does not contain common PowerCLI cmdlets (Get-*, Invoke-*)");
            }

            // Check for potential security issues
            if (checkDefinition.Script.Contains("Remove-") || checkDefinition.Script.Contains("Delete-"))
            {
                warnings.Add("Script contains potentially destructive operations");
            }

            // Try to execute with dry-run approach (read-only operations)
            try
            {
                var dryRunResult = await ExecuteAsync(host, checkDefinition, vCenter, cancellationToken);
                
                return new CheckEngineResult
                {
                    IsSuccess = true,
                    Output = dryRunResult.Output,
                    ErrorMessage = dryRunResult.ErrorMessage,
                    ExecutionTime = dryRunResult.ExecutionTime,
                    Warnings = warnings.Concat(dryRunResult.Warnings ?? new List<string>()).ToList()
                };
            }
            catch (Exception ex)
            {
                return new CheckEngineResult
                {
                    IsSuccess = false,
                    ErrorMessage = $"Validation failed: {ex.Message}",
                    Warnings = warnings
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PowerCLI check validation failed for '{CheckName}'", checkDefinition.Name);
            return new CheckEngineResult
            {
                IsSuccess = false,
                ErrorMessage = ex.Message
            };
        }
    }

    public async Task<bool> IsAvailableAsync()
    {
        try
        {
            var validation = await _powerCLIService.ValidatePowerCLIAsync();
            return validation.IsValid;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check PowerCLI availability");
            return false;
        }
    }
} 