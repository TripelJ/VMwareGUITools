namespace VMwareGUITools.Core.Models;

/// <summary>
/// Represents the result of executing a check through a check engine
/// </summary>
public class CheckExecutionResult
{
    /// <summary>
    /// Whether the check execution was successful (not the check result itself)
    /// </summary>
    public bool IsSuccess { get; set; }

    /// <summary>
    /// The output/result data from the check execution
    /// </summary>
    public string Output { get; set; } = string.Empty;

    /// <summary>
    /// Any error message if the execution failed
    /// </summary>
    public string ErrorMessage { get; set; } = string.Empty;

    /// <summary>
    /// Time taken to execute the check
    /// </summary>
    public TimeSpan ExecutionTime { get; set; }

    /// <summary>
    /// Raw data returned from the check (JSON, XML, etc.)
    /// </summary>
    public string? RawData { get; set; }

    /// <summary>
    /// Additional metadata about the execution
    /// </summary>
    public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();

    /// <summary>
    /// Creates a successful execution result
    /// </summary>
    public static CheckExecutionResult Success(string output, TimeSpan executionTime, string? rawData = null)
    {
        return new CheckExecutionResult
        {
            IsSuccess = true,
            Output = output,
            ExecutionTime = executionTime,
            RawData = rawData
        };
    }

    /// <summary>
    /// Creates a failed execution result
    /// </summary>
    public static CheckExecutionResult Failure(string errorMessage, TimeSpan executionTime)
    {
        return new CheckExecutionResult
        {
            IsSuccess = false,
            ErrorMessage = errorMessage,
            ExecutionTime = executionTime
        };
    }
} 