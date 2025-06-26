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
using VMwareGUITools.Infrastructure.Checks;

namespace VMwareGUITools.UI.ViewModels;

/// <summary>
/// View model for managing check results
/// </summary>
public partial class CheckResultsViewModel : ObservableObject
{
    private readonly ILogger<CheckResultsViewModel> _logger;
    private readonly VMwareDbContext _dbContext;
    private readonly ICheckExecutionService _checkExecutionService;

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

    public ICollectionView CheckResultsView { get; }

    public CheckResultsViewModel(
        ILogger<CheckResultsViewModel> logger,
        VMwareDbContext dbContext,
        ICheckExecutionService checkExecutionService)
    {
        _logger = logger;
        _dbContext = dbContext;
        _checkExecutionService = checkExecutionService;

        CheckResultsView = CollectionViewSource.GetDefaultView(CheckResults);
        CheckResultsView.Filter = FilterCheckResults;
        CheckResultsView.SortDescriptions.Add(new SortDescription(nameof(CheckResult.ExecutedAt), ListSortDirection.Descending));

        // Watch for property changes to update filters
        PropertyChanged += OnPropertyChanged;
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

            CheckResults.Clear();
            foreach (var result in results)
            {
                CheckResults.Add(result);
            }

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
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Executes a check against a specific host
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
            IsLoading = true;
            StatusMessage = $"Executing check '{request.CheckDefinition.Name}' on host '{request.Host.Name}'...";

            var result = await _checkExecutionService.ExecuteCheckAsync(
                request.Host, 
                request.CheckDefinition, 
                SelectedVCenter);

            // Add the new result to the collection
            CheckResults.Insert(0, result);
            
            await GroupCheckResultsAsync();

            StatusMessage = $"Check execution completed: {result.Status}";

            _logger.LogInformation("Executed check '{CheckName}' on host '{HostName}' with result: {Status}",
                request.CheckDefinition.Name, request.Host.Name, result.Status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute check");
            StatusMessage = $"Failed to execute check: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Executes checks for all hosts in a cluster
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
            IsLoading = true;
            StatusMessage = $"Executing checks for cluster '{clusterName}'...";

            // Find the cluster
            var cluster = await _dbContext.Clusters
                .FirstOrDefaultAsync(c => c.Name == clusterName && c.VCenterId == SelectedVCenter.Id);

            if (cluster == null)
            {
                StatusMessage = $"Cluster '{clusterName}' not found";
                return;
            }

            var results = await _checkExecutionService.ExecuteClusterChecksAsync(cluster, SelectedVCenter);

            // Add the new results to the collection
            foreach (var result in results.OrderByDescending(r => r.ExecutedAt))
            {
                CheckResults.Insert(0, result);
            }

            await GroupCheckResultsAsync();

            var passedCount = results.Count(r => r.Status == CheckStatus.Passed);
            var failedCount = results.Count(r => r.Status == CheckStatus.Failed);

            StatusMessage = $"Cluster checks completed: {passedCount} passed, {failedCount} failed";

            _logger.LogInformation("Executed cluster checks for '{ClusterName}': {TotalCount} checks, {PassedCount} passed, {FailedCount} failed",
                clusterName, results.Count, passedCount, failedCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute cluster checks for '{ClusterName}'", clusterName);
            StatusMessage = $"Failed to execute cluster checks: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
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
        await Task.Run(() =>
        {
            var groups = CheckResults
                .GroupBy(cr => new { HostName = cr.Host.Name, CategoryName = cr.CheckDefinition.Category?.Name })
                .Select(g => new CheckResultGroup
                {
                    HostName = g.Key.HostName ?? "Unknown Host",
                    CategoryName = g.Key.CategoryName ?? "Unknown Category",
                    Results = new ObservableCollection<CheckResult>(g.OrderByDescending(r => r.ExecutedAt)),
                    PassedCount = g.Count(r => r.Status == CheckStatus.Passed),
                    FailedCount = g.Count(r => r.Status == CheckStatus.Failed),
                    WarningCount = g.Count(r => r.Status == CheckStatus.Warning),
                    LastExecuted = g.Max(r => r.ExecutedAt)
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

    /// <summary>
    /// Filters check results based on current filter criteria
    /// </summary>
    private bool FilterCheckResults(object obj)
    {
        if (obj is not CheckResult checkResult)
            return false;

        // Filter by text
        if (!string.IsNullOrEmpty(FilterText))
        {
            var searchText = FilterText.ToLower();
            var matches = checkResult.CheckDefinition.Name.ToLower().Contains(searchText) ||
                         checkResult.Host.Name.ToLower().Contains(searchText) ||
                         checkResult.Output.ToLower().Contains(searchText) ||
                         checkResult.ErrorMessage.ToLower().Contains(searchText);

            if (!matches)
                return false;
        }

        // Filter by status
        if (FilterStatus.HasValue && checkResult.Status != FilterStatus.Value)
            return false;

        return true;
    }

    /// <summary>
    /// Handles property changes to update filters
    /// </summary>
    private void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FilterText) || e.PropertyName == nameof(FilterStatus))
        {
            CheckResultsView.Refresh();
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