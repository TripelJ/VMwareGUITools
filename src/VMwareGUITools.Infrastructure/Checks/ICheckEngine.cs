using VMwareGUITools.Core.Models;

namespace VMwareGUITools.Infrastructure.Checks;

/// <summary>
/// Interface for check execution engines that handle different execution types
/// </summary>
public interface ICheckEngine
{
    /// <summary>
    /// The execution type this engine handles (e.g., "PowerCLI", "SSH", "API")
    /// </summary>
    string ExecutionType { get; }

    /// <summary>
    /// Executes a check against a host
    /// </summary>
    /// <param name="host">The target host</param>
    /// <param name="checkDefinition">The check to execute</param>
    /// <param name="vCenter">The vCenter the host belongs to</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Check execution result</returns>
    Task<CheckEngineResult> ExecuteAsync(Host host, CheckDefinition checkDefinition, VCenter vCenter, CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates a check definition by performing a dry run
    /// </summary>
    /// <param name="host">The target host</param>
    /// <param name="checkDefinition">The check to validate</param>
    /// <param name="vCenter">The vCenter the host belongs to</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Validation result</returns>
    Task<CheckEngineResult> ValidateAsync(Host host, CheckDefinition checkDefinition, VCenter vCenter, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if this engine is available and properly configured
    /// </summary>
    /// <returns>True if the engine is available</returns>
    Task<bool> IsAvailableAsync();
}

/// <summary>
/// Represents the result of a check engine execution
/// </summary>
public class CheckEngineResult
{
    public bool IsSuccess { get; set; }
    public string Output { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
    public TimeSpan ExecutionTime { get; set; }
    public string? RawData { get; set; }
    public List<string>? Warnings { get; set; }
    public Dictionary<string, object>? Metadata { get; set; }
} 