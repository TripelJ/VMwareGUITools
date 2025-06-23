using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace VMwareGUITools.Infrastructure.PowerShell;

/// <summary>
/// PowerShell service that executes commands via external PowerShell process
/// This bypasses execution policy issues by launching powershell.exe directly
/// </summary>
public class ExternalPowerShellService : IExternalPowerShellService
{
    private readonly ILogger<ExternalPowerShellService> _logger;
    private readonly ExternalPowerShellOptions _options;

    public ExternalPowerShellService(
        ILogger<ExternalPowerShellService> logger,
        IOptions<ExternalPowerShellOptions> options)
    {
        _logger = logger;
        _options = options.Value;
    }

    public async Task<ExternalPowerShellResult> ExecuteScriptAsync(
        string script, 
        Dictionary<string, object>? parameters = null, 
        int timeoutSeconds = 300, 
        CancellationToken cancellationToken = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = new ExternalPowerShellResult();

        try
        {
            _logger.LogDebug("Executing PowerShell script via external process");

            // Create a temporary script file to avoid command line length limits
            var tempScriptPath = Path.GetTempFileName() + ".ps1";
            
            try
            {
                // Build the complete script with parameters
                var completeScript = BuildCompleteScript(script, parameters);
                await File.WriteAllTextAsync(tempScriptPath, completeScript, cancellationToken);

                // Configure process start info
                var startInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-ExecutionPolicy Bypass -NoProfile -WindowStyle Hidden -File \"{tempScriptPath}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = Environment.CurrentDirectory
                };

                // Add environment variables if needed
                if (_options.InheritEnvironmentVariables)
                {
                    foreach (DictionaryEntry envVar in Environment.GetEnvironmentVariables())
                    {
                        startInfo.Environment[envVar.Key.ToString()!] = envVar.Value?.ToString() ?? "";
                    }
                }

                using var process = new Process { StartInfo = startInfo };
                
                var outputBuilder = new StringBuilder();
                var errorBuilder = new StringBuilder();
                
                process.OutputDataReceived += (sender, e) => {
                    if (e.Data != null) outputBuilder.AppendLine(e.Data);
                };
                
                process.ErrorDataReceived += (sender, e) => {
                    if (e.Data != null) errorBuilder.AppendLine(e.Data);
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                // Wait for completion with timeout
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
                using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

                try
                {
                    await process.WaitForExitAsync(combinedCts.Token);
                    
                    result.IsSuccess = process.ExitCode == 0;
                    result.ExitCode = process.ExitCode;
                    result.StandardOutput = outputBuilder.ToString();
                    result.StandardError = errorBuilder.ToString();
                    result.ExecutionTime = stopwatch.Elapsed;

                    if (!result.IsSuccess)
                    {
                        result.ErrorMessage = $"PowerShell process exited with code {process.ExitCode}. Error: {result.StandardError}";
                        _logger.LogWarning("PowerShell process failed with exit code {ExitCode}: {Error}", 
                            process.ExitCode, result.StandardError);
                    }
                    else
                    {
                        _logger.LogDebug("PowerShell script executed successfully in {ElapsedMs}ms", 
                            stopwatch.ElapsedMilliseconds);
                    }
                }
                catch (OperationCanceledException)
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            process.Kill(entireProcessTree: true);
                            await process.WaitForExitAsync(CancellationToken.None);
                        }
                    }
                    catch (Exception killEx)
                    {
                        _logger.LogWarning(killEx, "Failed to kill PowerShell process");
                    }

                    result.IsSuccess = false;
                    result.ErrorMessage = cancellationToken.IsCancellationRequested 
                        ? "PowerShell execution was cancelled"
                        : $"PowerShell execution timed out after {timeoutSeconds} seconds";
                    result.ExecutionTime = stopwatch.Elapsed;
                }
            }
            finally
            {
                // Clean up temporary script file
                try
                {
                    if (File.Exists(tempScriptPath))
                    {
                        File.Delete(tempScriptPath);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete temporary script file: {TempScriptPath}", tempScriptPath);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute PowerShell script via external process");
            result.IsSuccess = false;
            result.ErrorMessage = $"Failed to execute PowerShell script: {ex.Message}";
            result.ExecutionTime = stopwatch.Elapsed;
        }

        return result;
    }

    public async Task<ExternalPowerShellResult> ExecutePowerCLICommandAsync(
        string command, 
        Dictionary<string, object>? parameters = null, 
        int timeoutSeconds = 300, 
        CancellationToken cancellationToken = default)
    {
        var script = $@"
# Import PowerCLI modules
try {{
    Import-Module VMware.VimAutomation.Core -ErrorAction Stop
    Import-Module VMware.VimAutomation.Common -ErrorAction SilentlyContinue
    Import-Module VMware.VimAutomation.Vds -ErrorAction SilentlyContinue
    Import-Module VMware.VimAutomation.Storage -ErrorAction SilentlyContinue
    
    # Configure PowerCLI settings
    Set-PowerCLIConfiguration -InvalidCertificateAction Ignore -Confirm:$false -DefaultVIServerMode Multiple -ErrorAction SilentlyContinue
}} catch {{
    Write-Error ""Failed to import PowerCLI modules: $($_.Exception.Message)""
    exit 1
}}

# Execute the command
try {{
    {command}
}} catch {{
    Write-Error ""PowerCLI command failed: $($_.Exception.Message)""
    exit 1
}}
";

        return await ExecuteScriptAsync(script, parameters, timeoutSeconds, cancellationToken);
    }

    public async Task<bool> TestPowerCLIAvailabilityAsync()
    {
        var testScript = @"
try {
    $modules = @('VMware.VimAutomation.Core', 'VMware.VimAutomation.Common')
    foreach ($module in $modules) {
        if (-not (Get-Module -ListAvailable -Name $module)) {
            Write-Output ""MISSING: $module""
            exit 1
        }
    }
    Write-Output ""PowerCLI modules available""
    exit 0
} catch {
    Write-Error $_.Exception.Message
    exit 1
}";

        var result = await ExecuteScriptAsync(testScript, timeoutSeconds: 30);
        return result.IsSuccess;
    }

    private string BuildCompleteScript(string script, Dictionary<string, object>? parameters)
    {
        var scriptBuilder = new StringBuilder();

        // Add error action preference
        scriptBuilder.AppendLine("$ErrorActionPreference = 'Stop'");
        scriptBuilder.AppendLine();

        // Add parameters if provided
        if (parameters != null && parameters.Count > 0)
        {
            foreach (var param in parameters)
            {
                var value = ConvertParameterValue(param.Value);
                scriptBuilder.AppendLine($"${param.Key} = {value}");
            }
            scriptBuilder.AppendLine();
        }

        // Add the main script
        scriptBuilder.AppendLine(script);

        return scriptBuilder.ToString();
    }

    private string ConvertParameterValue(object value)
    {
        return value switch
        {
            string str => $"'{str.Replace("'", "''")}'",
            bool b => b ? "$true" : "$false",
            null => "$null",
            _ => value.ToString() ?? "$null"
        };
    }
}

/// <summary>
/// Interface for external PowerShell execution service
/// </summary>
public interface IExternalPowerShellService
{
    Task<ExternalPowerShellResult> ExecuteScriptAsync(string script, Dictionary<string, object>? parameters = null, int timeoutSeconds = 300, CancellationToken cancellationToken = default);
    Task<ExternalPowerShellResult> ExecutePowerCLICommandAsync(string command, Dictionary<string, object>? parameters = null, int timeoutSeconds = 300, CancellationToken cancellationToken = default);
    Task<bool> TestPowerCLIAvailabilityAsync();
}

/// <summary>
/// Result of external PowerShell execution
/// </summary>
public class ExternalPowerShellResult
{
    public bool IsSuccess { get; set; }
    public int ExitCode { get; set; }
    public string StandardOutput { get; set; } = string.Empty;
    public string StandardError { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
    public TimeSpan ExecutionTime { get; set; }
}

/// <summary>
/// Configuration options for external PowerShell execution
/// </summary>
public class ExternalPowerShellOptions
{
    public const string SectionName = "ExternalPowerShell";
    
    public bool InheritEnvironmentVariables { get; set; } = true;
    public int DefaultTimeoutSeconds { get; set; } = 300;
    public bool UseWindowsPowerShell { get; set; } = true;
    public string? CustomPowerShellPath { get; set; }
} 