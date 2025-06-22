using VMwareGUITools.Core.Models;

namespace VMwareGUITools.Infrastructure.Checks;

/// <summary>
/// Interface for executing infrastructure checks against VMware hosts
/// </summary>
public interface ICheckExecutionService
{
    /// <summary>
    /// Executes a single check against a host
    /// </summary>
    /// <param name="host">The target host</param>
    /// <param name="checkDefinition">The check to execute</param>
    /// <param name="vCenter">The vCenter the host belongs to</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Check execution result</returns>
    Task<CheckResult> ExecuteCheckAsync(Host host, CheckDefinition checkDefinition, VCenter vCenter, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes all checks for a host based on its host profile
    /// </summary>
    /// <param name="host">The target host</param>
    /// <param name="vCenter">The vCenter the host belongs to</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of check results</returns>
    Task<List<CheckResult>> ExecuteHostProfileChecksAsync(Host host, VCenter vCenter, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes checks for all hosts in a cluster
    /// </summary>
    /// <param name="cluster">The target cluster</param>
    /// <param name="vCenter">The vCenter the cluster belongs to</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of check results for all hosts</returns>
    Task<List<CheckResult>> ExecuteClusterChecksAsync(Cluster cluster, VCenter vCenter, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a batch of checks in parallel
    /// </summary>
    /// <param name="checkExecutions">List of check executions to run</param>
    /// <param name="maxConcurrency">Maximum number of concurrent executions</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of check results</returns>
    Task<List<CheckResult>> ExecuteBatchAsync(List<CheckExecution> checkExecutions, int maxConcurrency = 5, CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates a check definition by performing a dry run
    /// </summary>
    /// <param name="checkDefinition">The check definition to validate</param>
    /// <param name="sampleHost">A sample host to test against</param>
    /// <param name="vCenter">The vCenter for testing</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Validation result</returns>
    Task<CheckValidationResult> ValidateCheckAsync(CheckDefinition checkDefinition, Host sampleHost, VCenter vCenter, CancellationToken cancellationToken = default);
}


} 