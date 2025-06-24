using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using VMwareGUITools.Core.Models;

namespace VMwareGUITools.UI.ViewModels
{
    /// <summary>
    /// ViewModel for managing the list of VMware clusters
    /// </summary>
    public class ClusterListViewModel : INotifyPropertyChanged
    {
        private ObservableCollection<Cluster> _clusters;
        private Cluster? _selectedCluster;
        private bool _isLoading;

        public ClusterListViewModel()
        {
            _clusters = new ObservableCollection<Cluster>();
        }

        /// <summary>
        /// Collection of clusters
        /// </summary>
        public ObservableCollection<Cluster> Clusters
        {
            get => _clusters;
            set
            {
                _clusters = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// Currently selected cluster
        /// </summary>
        public Cluster? SelectedCluster
        {
            get => _selectedCluster;
            set
            {
                _selectedCluster = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// Indicates if data is being loaded
        /// </summary>
        public bool IsLoading
        {
            get => _isLoading;
            set
            {
                _isLoading = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// Refreshes the cluster list
        /// </summary>
        public async Task RefreshAsync()
        {
            // TODO: Implement cluster refresh logic
            IsLoading = true;
            try
            {
                // Implementation will be added when cluster data access is available
                await Task.Delay(100); // Placeholder
            }
            finally
            {
                IsLoading = false;
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
} 