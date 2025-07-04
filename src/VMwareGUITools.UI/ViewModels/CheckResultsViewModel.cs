using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VMwareGUITools.Core.Models;
using VMwareGUITools.Data;
using VMwareGUITools.Infrastructure.Services;

namespace VMwareGUITools.UI.ViewModels;

/// <summary>
/// View model for managing check results
/// </summary>
public partial class CheckResultsViewModel : ObservableObject
{
    private readonly ILogger<CheckResultsViewModel> _logger;
    private readonly VMwareDbContext _dbContext;
    private readonly IServiceConfigurationManager _serviceConfigurationManager;

    [ObservableProperty]
    private ObservableCollection<CheckResult> _checkResults = new();

    [ObservableProperty]
    private ObservableCollection<CheckResultGroup> _groupedResults = new();

    [ObservableProperty]
    private CheckResult? _selectedCheckResult;

    [ObservableProperty]
    private string _filterText = string.Empty;

    [ObservableProperty]
    private CheckStatus? _filterStatus;

    [ObservableProperty]
    private bool _isLoading = false;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    [ObservableProperty]
    private DateTime _lastRefresh = DateTime.MinValue;

    [ObservableProperty]
    private VCenter? _selectedVCenter;

    [ObservableProperty]
    private bool _autoRefreshEnabled = false;

    [ObservableProperty]
    private string _selectedHostMoId = string.Empty;

    [ObservableProperty]
    private string _selectedClusterName = string.Empty;

    [ObservableProperty]
    private int _filteredResultsCount = 0;

    [ObservableProperty]
    private bool _hasFilteredResults = false;

    public ICollectionView CheckResultsView { get; }

    public CheckResultsViewModel(
        ILogger<CheckResultsViewModel> logger,
        VMwareDbContext dbContext,
        IServiceConfigurationManager serviceConfigurationManager)
    {
        _logger = logger;
        _dbContext = dbContext;
        _serviceConfigurationManager = serviceConfigurationManager;

        CheckResultsView = CollectionViewSource.GetDefaultView(CheckResults);
        CheckResultsView.Filter = FilterCheckResults;
        CheckResultsView.SortDescriptions.Add(new SortDescription(nameof(CheckResult.ExecutedAt), ListSortDirection.Descending));

        // Watch for collection and filter changes
        CheckResults.CollectionChanged += (s, e) => UpdateFilteredResultsCount();
        
        // Watch for property changes to update filters
        PropertyChanged += OnPropertyChanged;
        
        // Initialize filtered count
        UpdateFilteredResultsCount();
    }

    /// <summary>
    /// Loads check results for the specified parameters
    /// </summary>
    public async Task LoadCheckResultsAsync(VCenter? vCenter = null, string? hostMoId = null, string? clusterName = null)
    {
        try
        {
            IsLoading = true;
            StatusMessage = "Loading check results...";

            SelectedVCenter = vCenter;
            SelectedHostMoId = hostMoId ?? string.Empty;
            SelectedClusterName = clusterName ?? string.Empty;

            // Ensure database is initialized with basic data
            await EnsureDatabaseInitializedAsync();

            var query = _dbContext.CheckResults
                .Include(cr => cr.CheckDefinition)
                .ThenInclude(cd => cd.Category)
                .Include(cr => cr.Host)
                .AsQueryable();

            // Filter by vCenter if specified
            if (vCenter != null)
            {
                query = query.Where(cr => cr.Host.VCenterId == vCenter.Id);
            }

            // Filter by host if specified
            if (!string.IsNullOrEmpty(hostMoId))
            {
                query = query.Where(cr => cr.Host.MoId == hostMoId);
            }

            // Filter by cluster if specified
            if (!string.IsNullOrEmpty(clusterName))
            {
                query = query.Where(cr => cr.Host.ClusterName == clusterName);
            }

            // Get recent results (last 30 days)
            var thirtyDaysAgo = DateTime.UtcNow.AddDays(-30);
            query = query.Where(cr => cr.ExecutedAt >= thirtyDaysAgo);

            var results = await query
                .OrderByDescending(cr => cr.ExecutedAt)
                .Take(1000) // Limit to prevent performance issues
                .ToListAsync();

            // Filter out any results with null navigation properties to prevent crashes
            var validResults = results.Where(r => r.Host != null && r.CheckDefinition != null).ToList();

            // Update UI on the main thread
            Application.Current.Dispatcher.Invoke(() =>
            {
                CheckResults.Clear();
                foreach (var result in validResults)
                {
                    CheckResults.Add(result);
                }
            });

            UpdateFilteredResultsCount();
            await GroupCheckResultsAsync();

            StatusMessage = $"Loaded {CheckResults.Count} check results";
            LastRefresh = DateTime.Now;

            _logger.LogInformation("Loaded {Count} check results for vCenter: {VCenter}, Host: {Host}, Cluster: {Cluster}",
                CheckResults.Count, vCenter?.Name ?? "All", hostMoId ?? "All", clusterName ?? "All");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load check results");
            StatusMessage = $"Failed to load check results: {ex.Message}";
            
            // Ensure UI is updated even on error
            Application.Current.Dispatcher.Invoke(() =>
            {
                CheckResults.Clear();
            });
            UpdateFilteredResultsCount();
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Ensures the database has basic check categories and definitions
    /// </summary>
    private async Task EnsureDatabaseInitializedAsync()
    {
        try
        {
            // Check if we have any check categories
            var categoriesExist = await _dbContext.CheckCategories.AnyAsync();
            if (!categoriesExist)
            {
                _logger.LogInformation("No check categories found, creating default categories");
                
                var defaultCategory = new CheckCategory
                {
                    Name = "Infrastructure Health",
                    Description = "Basic infrastructure health checks",
                    Type = CheckCategoryType.Health,
                    Enabled = true,
                    SortOrder = 1,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                
                _dbContext.CheckCategories.Add(defaultCategory);
                await _dbContext.SaveChangesAsync();
                
                _logger.LogInformation("Created default check category: {CategoryName}", defaultCategory.Name);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to ensure database initialization");
        }
    }

    /// <summary>
    /// Executes a single check via Windows Service
    /// </summary>
    [RelayCommand]
    private async Task ExecuteCheckAsync(object? parameter)
    {
        if (parameter is not CheckExecutionRequest request || SelectedVCenter == null)
        {
            return;
        }

        try
        {
            // Check if service is running
            var serviceStatus = await _serviceConfigurationManager.GetServiceStatusAsync();
            if (serviceStatus == null || serviceStatus.LastHeartbeat < DateTime.UtcNow.AddSeconds(-30))
            {
                StatusMessage = "Windows Service is not running. Cannot execute checks.";
                return;
            }

            IsLoading = true;
            StatusMessage = $"Sending check '{request.CheckDefinition.Name}' command to Windows Service...";

            // Send command to service instead of executing directly
            var parameters = new
            {
                HostId = request.Host.Id,
                CheckDefinitionId = request.CheckDefinition.Id,
                VCenterId = SelectedVCenter.Id,
                IsManualRun = true
            };

            var commandId = await _serviceConfigurationManager.SendCommandAsync(
                ServiceCommandTypes.ExecuteCheck, 
                parameters);

            StatusMessage = $"Check command sent to service (ID: {commandId}). Monitoring for results...";

            // Monitor for completion
            await MonitorCommandCompletionAsync(commandId, $"Check '{request.CheckDefinition.Name}'");

            // Refresh results
            await LoadCheckResultsAsync(SelectedVCenter, SelectedHostMoId, SelectedClusterName);

            _logger.LogInformation("Check command sent to service: '{CheckName}' on host '{HostName}' with ID: {CommandId}",
                request.CheckDefinition.Name, request.Host.Name, commandId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send check command to service");
            StatusMessage = $"Failed to send command to service: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Executes checks for all hosts in a cluster via Windows Service
    /// </summary>
    [RelayCommand]
    private async Task ExecuteClusterChecksAsync(string? clusterName)
    {
        if (string.IsNullOrEmpty(clusterName) || SelectedVCenter == null)
        {
            return;
        }

        try
        {
            // Check if service is running
            var serviceStatus = await _serviceConfigurationManager.GetServiceStatusAsync();
            if (serviceStatus == null || serviceStatus.LastHeartbeat < DateTime.UtcNow.AddSeconds(-30))
            {
                StatusMessage = "Windows Service is not running. Cannot execute checks.";
                return;
            }

            IsLoading = true;
            StatusMessage = $"Sending cluster checks command to Windows Service...";

            // Find the cluster
            var cluster = await _dbContext.Clusters
                .FirstOrDefaultAsync(c => c.Name == clusterName && c.VCenterId == SelectedVCenter.Id);

            if (cluster == null)
            {
                StatusMessage = $"Cluster '{clusterName}' not found";
                return;
            }

            // Send command to service instead of executing directly
            var parameters = new
            {
                ClusterId = cluster.Id,
                VCenterId = SelectedVCenter.Id,
                IsManualRun = true
            };

            var commandId = await _serviceConfigurationManager.SendCommandAsync(
                ServiceCommandTypes.ExecuteCheck, 
                parameters);

            StatusMessage = $"Cluster checks command sent to service (ID: {commandId}). Monitoring for results...";

            // Monitor for completion
            await MonitorCommandCompletionAsync(commandId, $"Cluster '{clusterName}' checks");

            // Refresh results
            await LoadCheckResultsAsync(SelectedVCenter, SelectedHostMoId, SelectedClusterName);

            _logger.LogInformation("Cluster checks command sent to service for '{ClusterName}' with ID: {CommandId}",
                clusterName, commandId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send cluster checks command to service for '{ClusterName}'", clusterName);
            StatusMessage = $"Failed to send command to service: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Monitors a service command for completion
    /// </summary>
    private async Task MonitorCommandCompletionAsync(string commandId, string operationName)
    {
        var timeout = DateTime.UtcNow.AddMinutes(10); // 10 minute timeout
        
        while (DateTime.UtcNow < timeout)
        {
            await Task.Delay(2000); // Check every 2 seconds
            
            var command = await _serviceConfigurationManager.GetCommandResultAsync(commandId);
            if (command != null)
            {
                if (command.Status == "Completed")
                {
                    StatusMessage = $"{operationName} completed successfully";
                    return;
                }
                else if (command.Status == "Failed")
                {
                    StatusMessage = $"{operationName} failed: {command.ErrorMessage}";
                    return;
                }
            }
        }
        
        StatusMessage = $"{operationName} timed out";
    }

    /// <summary>
    /// Refreshes the check results
    /// </summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        await LoadCheckResultsAsync(SelectedVCenter, SelectedHostMoId, SelectedClusterName);
    }

    /// <summary>
    /// Clears all filters
    /// </summary>
    [RelayCommand]
    private void ClearFilters()
    {
        FilterText = string.Empty;
        FilterStatus = null;
        CheckResultsView.Refresh();
    }

    /// <summary>
    /// Groups check results by host and check category
    /// </summary>
    private async Task GroupCheckResultsAsync()
    {
        try
        {
            // Create a thread-safe copy of the CheckResults collection
            var checkResultsCopy = CheckResults.ToList();
            
            await Task.Run(() =>
            {
                var groups = checkResultsCopy
                    .Where(cr => cr.Host != null && cr.CheckDefinition != null) // Add null checks
                    .GroupBy(cr => new { HostName = cr.Host.Name, CategoryName = cr.CheckDefinition.Category?.Name })
                    .Select(g => new CheckResultGroup
                    {
                        HostName = g.Key.HostName ?? "Unknown Host",
                        CategoryName = g.Key.CategoryName ?? "Unknown Category",
                        Results = new ObservableCollection<CheckResult>(g.OrderByDescending(r => r.ExecutedAt)),
                        PassedCount = g.Count(r => r.Status == CheckStatus.Passed),
                        FailedCount = g.Count(r => r.Status == CheckStatus.Failed),
                        WarningCount = g.Count(r => r.Status == CheckStatus.Warning),
                        LastExecuted = g.Any() ? g.Max(r => r.ExecutedAt) : DateTime.MinValue
                    })
                    .OrderBy(g => g.HostName)
                    .ThenBy(g => g.CategoryName)
                    .ToList();

                Application.Current.Dispatcher.Invoke(() =>
                {
                    GroupedResults.Clear();
                    foreach (var group in groups)
                    {
                        GroupedResults.Add(group);
                    }
                });
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to group check results");
            
            // Ensure UI is updated even on error
            Application.Current.Dispatcher.Invoke(() =>
            {
                GroupedResults.Clear();
            });
        }
    }

    /// <summary>
    /// Filters check results based on current filter criteria
    /// </summary>
    private bool FilterCheckResults(object obj)
    {
        if (obj is not CheckResult checkResult)
            return false;

        // Skip results with null navigation properties to prevent crashes
        if (checkResult.CheckDefinition == null || checkResult.Host == null)
            return false;

        // Filter by text
        if (!string.IsNullOrEmpty(FilterText))
        {
            var searchText = FilterText.ToLower();
            var matches = (checkResult.CheckDefinition.Name?.ToLower().Contains(searchText) == true) ||
                         (checkResult.Host.Name?.ToLower().Contains(searchText) == true) ||
                         (checkResult.Output?.ToLower().Contains(searchText) == true) ||
                         (checkResult.ErrorMessage?.ToLower().Contains(searchText) == true);

            if (!matches)
                return false;
        }

        // Filter by status
        if (FilterStatus.HasValue && checkResult.Status != FilterStatus.Value)
            return false;

        return true;
    }

    /// <summary>
    /// Updates the count of filtered results
    /// </summary>
    private void UpdateFilteredResultsCount()
    {
        try
        {
            // Count items that pass the current filter
            var count = 0;
            foreach (var item in CheckResults)
            {
                if (FilterCheckResults(item))
                {
                    count++;
                }
            }
            FilteredResultsCount = count;
            HasFilteredResults = count > 0;
            _logger.LogDebug("Updated FilteredResultsCount to {Count} (total CheckResults: {Total}), HasFilteredResults: {HasResults}", 
                FilteredResultsCount, CheckResults.Count, HasFilteredResults);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update filtered results count");
            FilteredResultsCount = 0;
        }
    }

    /// <summary>
    /// Handles property changes to update filters
    /// </summary>
    private void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FilterText) || e.PropertyName == nameof(FilterStatus))
        {
            CheckResultsView.Refresh();
            UpdateFilteredResultsCount();
        }
    }
}

/// <summary>
/// Represents a group of check results
/// </summary>
public partial class CheckResultGroup : ObservableObject
{
    [ObservableProperty]
    private string _hostName = string.Empty;

    [ObservableProperty]
    private string _categoryName = string.Empty;

    [ObservableProperty]
    private ObservableCollection<CheckResult> _results = new();

    [ObservableProperty]
    private int _passedCount;

    [ObservableProperty]
    private int _failedCount;

    [ObservableProperty]
    private int _warningCount;

    [ObservableProperty]
    private DateTime _lastExecuted;

    public int TotalCount => PassedCount + FailedCount + WarningCount;

    public string StatusSummary => $"{PassedCount} passed, {FailedCount} failed, {WarningCount} warnings";
}

/// <summary>
/// Represents a request to execute a check
/// </summary>
public class CheckExecutionRequest
{
    public required Host Host { get; set; }
    public required CheckDefinition CheckDefinition { get; set; }
} 