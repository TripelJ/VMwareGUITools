using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using VMwareGUITools.Core.Models;
using VMwareGUITools.Data;
using VMwareGUITools.Infrastructure.PowerShell;
using VMwareGUITools.Infrastructure.Security;

namespace VMwareGUITools.Infrastructure.Checks;

/// <summary>
/// Service for executing infrastructure checks against VMware hosts
/// </summary>
public class CheckExecutionService : ICheckExecutionService
{
    private readonly ILogger<CheckExecutionService> _logger;
    private readonly IPowerShellService _powerShellService;
    private readonly ICredentialService _credentialService;
    private readonly VMwareDbContext _dbContext;
    private readonly CheckExecutionOptions _options;
    private readonly Dictionary<string, ICheckEngine> _checkEngines = new();

    public CheckExecutionService(
        ILogger<CheckExecutionService> logger,
        IPowerShellService powerShellService,
        ICredentialService credentialService,
        VMwareDbContext dbContext,
        IOptions<CheckExecutionOptions> options,
        IEnumerable<ICheckEngine> checkEngines)
    {
        _logger = logger;
        _powerShellService = powerShellService;
        _credentialService = credentialService;
        _dbContext = dbContext;
        _options = options.Value;

        // Register check engines by their execution type
        foreach (var engine in checkEngines)
        {
            _checkEngines[engine.ExecutionType] = engine;
        }
    }

    public async Task<CheckResult> ExecuteCheckAsync(Host host, CheckDefinition checkDefinition, VCenter vCenter, CancellationToken cancellationToken = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = new CheckResult
        {
            HostId = host.Id,
            CheckDefinitionId = checkDefinition.Id,
            ExecutedAt = DateTime.UtcNow,
            IsManualRun = true
        };

        try
        {
            _logger.LogInformation("Executing check '{CheckName}' on host '{HostName}'", checkDefinition.Name, host.Name);

            // Validate check definition
            if (!IsCheckDefinitionValid(checkDefinition))
            {
                result.Status = CheckStatus.Failed;
                result.Output = "Invalid check definition";
                result.ErrorMessage = "Check definition validation failed";
                result.ExecutionTime = stopwatch.Elapsed;
                return result;
            }

            // Get the appropriate check engine
            if (!_checkEngines.TryGetValue(checkDefinition.ExecutionType.ToString(), out var engine))
            {
                result.Status = CheckStatus.Failed;
                result.Output = $"No engine available for execution type: {checkDefinition.ExecutionType}";
                result.ErrorMessage = $"Unsupported execution type: {checkDefinition.ExecutionType}";
                result.ExecutionTime = stopwatch.Elapsed;
                return result;
            }

            // Execute the check with timeout
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(checkDefinition.TimeoutSeconds));
            using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            var executionResult = await engine.ExecuteAsync(host, checkDefinition, vCenter, combinedCts.Token);

            // Map execution result to check result
            result.Status = executionResult.IsSuccess ? CheckStatus.Passed : CheckStatus.Failed;
            result.Output = executionResult.Output;
            result.ErrorMessage = executionResult.ErrorMessage ?? string.Empty;
            result.ExecutionTime = executionResult.ExecutionTime;
            result.RawData = executionResult.RawData;

            // Evaluate thresholds if defined
            if (checkDefinition.HasThreshold && executionResult.IsSuccess)
            {
                var thresholdResult = EvaluateThreshold(checkDefinition, executionResult.Output);
                if (thresholdResult.HasValue)
                {
                    result.Status = thresholdResult.Value ? CheckStatus.Passed : CheckStatus.Failed;
                    if (!thresholdResult.Value)
                    {
                        result.ErrorMessage = $"Threshold evaluation failed: {checkDefinition.ThresholdCriteria}";
                    }
                }
            }

            _logger.LogInformation("Check '{CheckName}' on host '{HostName}' completed with status: {Status} in {ElapsedMs}ms",
                checkDefinition.Name, host.Name, result.Status, stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            result.Status = CheckStatus.Failed;
            result.ErrorMessage = "Check execution was cancelled";
            _logger.LogWarning("Check '{CheckName}' on host '{HostName}' was cancelled", checkDefinition.Name, host.Name);
        }
        catch (OperationCanceledException)
        {
            result.Status = CheckStatus.Failed;
            result.ErrorMessage = $"Check execution timed out after {checkDefinition.TimeoutSeconds} seconds";
            _logger.LogWarning("Check '{CheckName}' on host '{HostName}' timed out after {TimeoutSeconds} seconds",
                checkDefinition.Name, host.Name, checkDefinition.TimeoutSeconds);
        }
        catch (Exception ex)
        {
            result.Status = CheckStatus.Failed;
            result.ErrorMessage = ex.Message;
            _logger.LogError(ex, "Check '{CheckName}' on host '{HostName}' failed with exception",
                checkDefinition.Name, host.Name);
        }

        result.ExecutionTime = stopwatch.Elapsed;

        // Save result to database
        try
        {
            _dbContext.CheckResults.Add(result);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save check result to database");
        }

        return result;
    }

    public async Task<List<CheckResult>> ExecuteHostProfileChecksAsync(Host host, VCenter vCenter, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Executing host profile checks for host '{HostName}' (Type: {HostType})", host.Name, host.HostType);

            // Get host profile
            var hostProfile = await _dbContext.HostProfiles
                .FirstOrDefaultAsync(hp => hp.Name == host.HostType.ToString(), cancellationToken);

            if (hostProfile == null)
            {
                _logger.LogWarning("No host profile found for host type: {HostType}", host.HostType);
                return new List<CheckResult>();
            }

            // Get enabled checks for this host profile
            var checkIds = hostProfile.EnabledCheckIds;
            var checkDefinitions = await _dbContext.CheckDefinitions
                .Where(cd => checkIds.Contains(cd.Id) && cd.IsEnabled)
                .ToListAsync(cancellationToken);

            var results = new List<CheckResult>();

            // Execute checks with concurrency limit
            var semaphore = new SemaphoreSlim(_options.MaxConcurrentChecksPerHost, _options.MaxConcurrentChecksPerHost);
            var tasks = checkDefinitions.Select(async check =>
            {
                await semaphore.WaitAsync(cancellationToken);
                try
                {
                    return await ExecuteCheckAsync(host, check, vCenter, cancellationToken);
                }
                finally
                {
                    semaphore.Release();
                }
            });

            results.AddRange(await Task.WhenAll(tasks));

            _logger.LogInformation("Completed {CheckCount} checks for host '{HostName}' with {PassedCount} passed, {FailedCount} failed",
                results.Count, host.Name,
                results.Count(r => r.Status == CheckStatus.Passed),
                results.Count(r => r.Status == CheckStatus.Failed));

            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute host profile checks for host '{HostName}'", host.Name);
            throw;
        }
    }

    public async Task<List<CheckResult>> ExecuteClusterChecksAsync(Cluster cluster, VCenter vCenter, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Executing cluster checks for cluster '{ClusterName}'", cluster.Name);

            // Get all hosts in the cluster
            var hosts = await _dbContext.Hosts
                .Where(h => h.VCenterId == vCenter.Id && h.ClusterName == cluster.Name)
                .ToListAsync(cancellationToken);

            if (!hosts.Any())
            {
                _logger.LogWarning("No hosts found in cluster '{ClusterName}'", cluster.Name);
                return new List<CheckResult>();
            }

            var allResults = new List<CheckResult>();

            // Execute checks for each host with concurrency limit
            var semaphore = new SemaphoreSlim(_options.MaxConcurrentHosts, _options.MaxConcurrentHosts);
            var tasks = hosts.Select(async host =>
            {
                await semaphore.WaitAsync(cancellationToken);
                try
                {
                    return await ExecuteHostProfileChecksAsync(host, vCenter, cancellationToken);
                }
                finally
                {
                    semaphore.Release();
                }
            });

            var hostResults = await Task.WhenAll(tasks);
            foreach (var results in hostResults)
            {
                allResults.AddRange(results);
            }

            _logger.LogInformation("Completed cluster checks for '{ClusterName}' - {TotalChecks} total checks across {HostCount} hosts",
                cluster.Name, allResults.Count, hosts.Count);

            return allResults;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute cluster checks for cluster '{ClusterName}'", cluster.Name);
            throw;
        }
    }

    public async Task<List<CheckResult>> ExecuteBatchAsync(List<CheckExecution> checkExecutions, int maxConcurrency = 5, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Executing batch of {CheckCount} checks with concurrency limit of {MaxConcurrency}",
                checkExecutions.Count, maxConcurrency);

            var results = new ConcurrentBag<CheckResult>();
            var semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);

            var tasks = checkExecutions.Select(async execution =>
            {
                await semaphore.WaitAsync(cancellationToken);
                try
                {
                    var result = await ExecuteCheckAsync(execution.Host, execution.CheckDefinition, execution.VCenter, cancellationToken);
                    result.IsManualRun = execution.IsManualRun;
                    results.Add(result);
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);

            var resultList = results.ToList();
            _logger.LogInformation("Batch execution completed - {TotalChecks} checks with {PassedCount} passed, {FailedCount} failed",
                resultList.Count,
                resultList.Count(r => r.Status == CheckStatus.Passed),
                resultList.Count(r => r.Status == CheckStatus.Failed));

            return resultList;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute check batch");
            throw;
        }
    }

    public async Task<CheckValidationResult> ValidateCheckAsync(CheckDefinition checkDefinition, Host sampleHost, VCenter vCenter, CancellationToken cancellationToken = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = new CheckValidationResult();

        try
        {
            _logger.LogInformation("Validating check '{CheckName}' against host '{HostName}'", checkDefinition.Name, sampleHost.Name);

            // Basic validation
            if (!IsCheckDefinitionValid(checkDefinition))
            {
                result.IsValid = false;
                result.ErrorMessage = "Check definition is invalid";
                return result;
            }

            // Get the appropriate check engine
            if (!_checkEngines.TryGetValue(checkDefinition.ExecutionType.ToString(), out var engine))
            {
                result.IsValid = false;
                result.ErrorMessage = $"No engine available for execution type: {checkDefinition.ExecutionType}";
                return result;
            }

            // Perform validation execution
            var executionResult = await engine.ValidateAsync(sampleHost, checkDefinition, vCenter, cancellationToken);

            result.IsValid = executionResult.IsSuccess;
            result.ErrorMessage = executionResult.ErrorMessage ?? string.Empty;
            result.SampleOutput = executionResult.Output;
            result.ExecutionTime = executionResult.ExecutionTime;

            if (executionResult.Warnings != null)
            {
                result.Warnings.AddRange(executionResult.Warnings);
            }

            _logger.LogInformation("Check validation for '{CheckName}' completed - Valid: {IsValid}",
                checkDefinition.Name, result.IsValid);
        }
        catch (Exception ex)
        {
            result.IsValid = false;
            result.ErrorMessage = ex.Message;
            _logger.LogError(ex, "Check validation failed for '{CheckName}'", checkDefinition.Name);
        }

        result.ExecutionTime = stopwatch.Elapsed;
        return result;
    }

    private bool IsCheckDefinitionValid(CheckDefinition checkDefinition)
    {
        if (string.IsNullOrWhiteSpace(checkDefinition.Name) ||
            string.IsNullOrWhiteSpace(checkDefinition.Script))
        {
            return false;
        }

        if (checkDefinition.TimeoutSeconds <= 0 || checkDefinition.TimeoutSeconds > 3600)
        {
            return false;
        }

        return true;
    }

    private bool? EvaluateThreshold(CheckDefinition checkDefinition, string output)
    {
        try
        {
            // Simple threshold evaluation - this could be enhanced with more sophisticated logic
            if (string.IsNullOrWhiteSpace(checkDefinition.ThresholdCriteria))
                return null;

            // Parse numeric values from output for simple comparisons
            if (decimal.TryParse(output.Trim(), out var numericValue))
            {
                var criteria = checkDefinition.ThresholdCriteria.Trim();
                
                if (criteria.StartsWith(">="))
                {
                    return decimal.TryParse(criteria.Substring(2).Trim(), out var threshold) && numericValue >= threshold;
                }
                else if (criteria.StartsWith("<="))
                {
                    return decimal.TryParse(criteria.Substring(2).Trim(), out var threshold) && numericValue <= threshold;
                }
                else if (criteria.StartsWith(">"))
                {
                    return decimal.TryParse(criteria.Substring(1).Trim(), out var threshold) && numericValue > threshold;
                }
                else if (criteria.StartsWith("<"))
                {
                    return decimal.TryParse(criteria.Substring(1).Trim(), out var threshold) && numericValue < threshold;
                }
                else if (criteria.StartsWith("=="))
                {
                    return decimal.TryParse(criteria.Substring(2).Trim(), out var threshold) && numericValue == threshold;
                }
            }

            // String-based evaluations
            if (checkDefinition.ThresholdCriteria.Equals("not_empty", StringComparison.OrdinalIgnoreCase))
            {
                return !string.IsNullOrWhiteSpace(output);
            }
            else if (checkDefinition.ThresholdCriteria.Equals("empty", StringComparison.OrdinalIgnoreCase))
            {
                return string.IsNullOrWhiteSpace(output);
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to evaluate threshold for check definition {CheckId}", checkDefinition.Id);
            return null;
        }
    }
}

/// <summary>
/// Configuration options for check execution
/// </summary>
public class CheckExecutionOptions
{
    public const string SectionName = "CheckExecution";

    public int MaxConcurrentChecksPerHost { get; set; } = 3;
    public int MaxConcurrentHosts { get; set; } = 5;
    public int DefaultTimeoutSeconds { get; set; } = 300;
    public bool EnableDetailedLogging { get; set; } = false;
    public bool SaveFailedCheckLogs { get; set; } = true;
} 