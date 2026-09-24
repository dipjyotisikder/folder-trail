using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace FolderTrail.App.Features.Timeline;

public partial class TimelineView : UserControl
{
    private const double LoadMoreThreshold = 600;
    private ScrollViewer? _scroller;

    public TimelineView()
    {
        InitializeComponent();
        TimelineList.TemplateApplied += (_, _) => AttachScroller();
    }

    public void FocusSearch()
    {
        SearchBox.Focus(NavigationMethod.Tab);
        SearchBox.SelectAll();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        AttachScroller();
    }

    private void AttachScroller()
    {
        var scroller = TimelineList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (scroller is null || ReferenceEquals(scroller, _scroller))
        {
            return;
        }

        if (_scroller is not null)
        {
            _scroller.ScrollChanged -= OnScrollChanged;
        }

        _scroller = scroller;
        _scroller.ScrollChanged += OnScrollChanged;
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_scroller is null || DataContext is not TimelineViewModel viewModel)
        {
            return;
        }

        var offset = _scroller.Offset.Y;
        var wasAtTop = viewModel.IsScrolledToTop;
        viewModel.IsScrolledToTop = offset < 8;
        if (!wasAtTop && viewModel.IsScrolledToTop && viewModel.HasPendingNew)
        {
            _ = viewModel.ReloadAsync();
        }

        if (offset + _scroller.Viewport.Height >= _scroller.Extent.Height - LoadMoreThreshold)
        {
            _ = viewModel.LoadMoreAsync();
        }
    }
}
