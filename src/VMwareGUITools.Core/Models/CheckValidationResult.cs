namespace VMwareGUITools.Core.Models;

/// <summary>
/// Represents the result of validating a check definition
/// </summary>
public class CheckValidationResult
{
    /// <summary>
    /// Whether the check definition is valid
    /// </summary>
    public bool IsValid { get; set; }

    /// <summary>
    /// List of validation errors if any
    /// </summary>
    public List<string> ValidationErrors { get; set; } = new List<string>();

    /// <summary>
    /// List of validation warnings if any
    /// </summary>
    public List<string> ValidationWarnings { get; set; } = new List<string>();

    /// <summary>
    /// Time taken to validate the check
    /// </summary>
    public TimeSpan ValidationTime { get; set; }

    /// <summary>
    /// Time taken to execute the check
    /// </summary>
    public TimeSpan ExecutionTime { get; set; }

    /// <summary>
    /// Error message if validation failed
    /// </summary>
    public string ErrorMessage { get; set; } = string.Empty;

    /// <summary>
    /// Sample output from validation execution
    /// </summary>
    public string SampleOutput { get; set; } = string.Empty;

    /// <summary>
    /// Validation warnings
    /// </summary>
    public List<string> Warnings { get; set; } = new List<string>();

    /// <summary>
    /// Sample execution result if validation included execution test
    /// </summary>
    public CheckExecutionResult? SampleResult { get; set; }

    /// <summary>
    /// Creates a successful validation result
    /// </summary>
    public static CheckValidationResult Success(TimeSpan validationTime, List<string>? warnings = null)
    {
        return new CheckValidationResult
        {
            IsValid = true,
            ValidationTime = validationTime,
            ValidationWarnings = warnings ?? new List<string>()
        };
    }

    /// <summary>
    /// Creates a failed validation result
    /// </summary>
    public static CheckValidationResult Failure(List<string> errors, TimeSpan validationTime)
    {
        return new CheckValidationResult
        {
            IsValid = false,
            ValidationErrors = errors,
            ValidationTime = validationTime
        };
    }
} 