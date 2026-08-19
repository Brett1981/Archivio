using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Archivio.App;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        _viewModel.PropertyChanged += HandleViewModelPropertyChanged;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await _viewModel.LoadCommand.ExecuteAsync(null);
    }

    private void HandleViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.IsReviewFocusMode))
        {
            ApplyReviewWorkspaceLayout();
        }
    }

    private void ApplyReviewWorkspaceLayout()
    {
        if (_viewModel.IsReviewFocusMode)
        {
            ReviewCandidateColumn.Width = new GridLength(0.9, GridUnitType.Star);
            ReviewColumnGap.Width = new GridLength(18);
            ReviewDetailsColumn.Width = new GridLength(1.1, GridUnitType.Star);
            ReviewCandidateRow.Height = new GridLength(1, GridUnitType.Star);
            ReviewRowGap.Height = new GridLength(0);
            ReviewDetailsRow.Height = new GridLength(0);
            Grid.SetRow(CandidateDetailsPanel, 6);
            Grid.SetColumn(CandidateDetailsPanel, 2);
            return;
        }

        ReviewCandidateColumn.Width = new GridLength(1, GridUnitType.Star);
        ReviewColumnGap.Width = new GridLength(0);
        ReviewDetailsColumn.Width = new GridLength(0);
        ReviewCandidateRow.Height = new GridLength(0.8, GridUnitType.Star);
        ReviewRowGap.Height = new GridLength(14);
        ReviewDetailsRow.Height = new GridLength(1.2, GridUnitType.Star);
        Grid.SetRow(CandidateDetailsPanel, 8);
        Grid.SetColumn(CandidateDetailsPanel, 0);
    }

    private void HandleReviewPanelPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.IsReviewFocusMode)
        {
            return;
        }

        Dispatcher.BeginInvoke(
            () => _viewModel.EnterReviewFocusCommand.Execute(null),
            DispatcherPriority.ContextIdle);
    }

    private void HandleWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !_viewModel.IsReviewFocusMode)
        {
            return;
        }

        _viewModel.ExitReviewFocusCommand.Execute(null);
        e.Handled = true;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Closed -= OnClosed;
        _viewModel.PropertyChanged -= HandleViewModelPropertyChanged;
    }
}
