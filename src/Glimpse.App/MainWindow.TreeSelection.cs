using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Glimpse.Core.Document;

namespace Glimpse.App;

/// <summary>
/// Several proteins, peptides from different proteins, or everything at once in the document tree.
/// WPF's TreeView selects one item, so the selection is kept here: Ctrl+click adds or removes an item,
/// Shift+click selects the visible rows from the last click to this one (Ctrl+Shift adds them), and
/// Ctrl+A selects every protein. The rules are <see cref="TreeSelectionState"/>'s; this turns clicks and
/// keys into them and keeps the selection in step with Skyline's.
/// </summary>
public partial class MainWindow
{
    private readonly TreeSelectionState _tree = new();
    private readonly SelectionSyncGate _syncGate = new();

    /// <summary>The items selected in the tree (bound by the row highlight).</summary>
    public IReadOnlySet<object> TreeSelection => _tree.Items;

    public static readonly DependencyProperty TreeSelectionVersionProperty =
        DependencyProperty.Register(nameof(TreeSelectionVersion), typeof(int), typeof(MainWindow), new PropertyMetadata(0));

    /// <summary>Bumped on every change, so the rows' highlight bindings re-evaluate.</summary>
    public int TreeSelectionVersion
    {
        get => (int)GetValue(TreeSelectionVersionProperty);
        set => SetValue(TreeSelectionVersionProperty, value);
    }

    private void InitTreeSelection()
    {
        DocumentTree.AddHandler(TreeViewItem.ExpandedEvent, new RoutedEventHandler((_, e) => TrackExpansion(e, true)));
        DocumentTree.AddHandler(TreeViewItem.CollapsedEvent, new RoutedEventHandler((_, e) => TrackExpansion(e, false)));
        DocumentTree.AddHandler(TreeViewItem.SelectedEvent, new RoutedEventHandler((_, e) =>
        {
            if (e.OriginalSource is TreeViewItem item)
                _nativeSelected = item;
        }));
    }

    private bool _bringingRowIntoView;

    /// <summary>
    /// Scroll a row into view vertically only. WPF brings the whole row into view when it is selected or
    /// focused, which for a protein name wider than the panel scrolls the tree sideways and hides the
    /// start of every name. The row is brought in from its left edge, header only, instead; the
    /// horizontal scrollbar still moves the tree when dragged.
    /// </summary>
    /// <remarks>
    /// TreeViewItem's own class handler answers the row's request by asking for its header element
    /// instead, so the request that actually scrolls comes from inside the row (TargetObject is the
    /// header), and is caught here by the row that owns it.
    /// </remarks>
    private void OnTreeItemRequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
    {
        if (_bringingRowIntoView || sender is not TreeViewItem row || e.TargetObject is not DependencyObject target
            || !ReferenceEquals(OwningRow(target), row))
            return;
        e.Handled = true;
        var header = row.Template?.FindName("PART_Header", row) as FrameworkElement;
        var height = header?.ActualHeight > 0 ? header.ActualHeight : Math.Min(row.ActualHeight, 20);
        // The left edge of what is showing now, in the row's coordinates: horizontally already in view, so
        // the scroll position stays wherever it is (the left, unless the user dragged the scrollbar).
        var viewer = FindAncestor<ScrollViewer>(row);
        var left = viewer is null ? 0 : viewer.TranslatePoint(new Point(0, 0), row).X;
        _bringingRowIntoView = true;
        try
        {
            row.BringIntoView(new Rect(left, 0, 1, height));
        }
        finally
        {
            _bringingRowIntoView = false;
        }
    }

    /// <summary>The row the TreeView itself has selected (it draws its own highlight on it).</summary>
    private TreeViewItem? _nativeSelected;

    /// <summary>
    /// Drop the TreeView's own single selection, so its highlight does not mark a row the multi-selection
    /// no longer holds, and a later plain click on that row registers as a change.
    /// </summary>
    private void ClearNativeTreeSelection()
    {
        if (_nativeSelected is { IsSelected: true } item && ReferenceEquals(item.DataContext, DocumentTree.SelectedItem))
            item.IsSelected = false;
        _nativeSelected = null;
    }

    private void TrackExpansion(RoutedEventArgs e, bool expanded)
    {
        if (e.OriginalSource is TreeViewItem { DataContext: { } item })
            _tree.SetExpanded(item, expanded);
    }

    private void OnDocumentTreePreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        var item = TreeItemUnder(e.OriginalSource as DependencyObject);
        if (item is null)
            return;
        var mods = Keyboard.Modifiers;
        if (mods.HasFlag(ModifierKeys.Shift) && _tree.Anchor is not null)
        {
            _tree.SelectRange(_document, item, add: mods.HasFlag(ModifierKeys.Control));
            ApplyTreeSelection();
            e.Handled = true;
        }
        else if (mods.HasFlag(ModifierKeys.Control))
        {
            _tree.Toggle(item);
            ApplyTreeSelection();
            e.Handled = true;
        }
        else if (ReferenceEquals(item, DocumentTree.SelectedItem))
        {
            // A plain click on the item the tree already has selected raises no SelectedItemChanged.
            SelectSingle(item);
        }
        if (e.Handled)
        {
            ClearNativeTreeSelection();
            DocumentTree.Focus();
        }
    }

    private void OnDocumentTreeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control)
        {
            _tree.SelectAll(_document);
            ApplyTreeSelection();
            e.Handled = true;
        }
    }

    private void OnDocumentTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not null)
            SelectSingle(e.NewValue);
    }

    private void SelectSingle(object item)
    {
        _tree.SelectSingle(item);
        ApplyTreeSelection();
    }

    /// <summary>
    /// Show the tree's selection and select its peptides, in document order. Ctrl+clicking the last
    /// selected item away selects nothing (and leaves Skyline's selection as it is).
    /// </summary>
    private void ApplyTreeSelection()
    {
        TreeSelectionVersion++;
        SetSelection(_tree.Peptides(_document), "Glimpse");
        if (_tree.Count > 0)
            PushSelectionToSkyline();
    }

    /// <summary>More tree items than this are not sent to Skyline (selecting thousands of nodes there is slow).</summary>
    private const int MaxPushedLocators = 1000;

    /// <summary>Select the tree's selection in Skyline's Targets view too.</summary>
    /// <remarks>
    /// Until Skyline has answered, the selection poll's answers are dropped (<see cref="SelectionSyncGate"/>):
    /// one that started earlier would bring back Skyline's old selection and undo this one.
    /// </remarks>
    private async void PushSelectionToSkyline()
    {
        if (_skyline is null || FollowSkylineCheck.IsChecked != true)
            return;
        var locators = _tree.Locators();
        if (locators.Count == 0)
            return;
        if (locators.Count > MaxPushedLocators)
        {
            App.WriteLog($"Not selecting {locators.Count:N0} elements in Skyline (more than {MaxPushedLocators:N0}).");
            return;
        }
        var skyline = _skyline;
        var first = locators[0];
        var rest = locators.Count > 1 ? string.Join("\n", locators.Skip(1)) : null;
        _syncGate.BeginPush(_tree.Items);
        try
        {
            await Task.Run(() => skyline.Call(c => c.SetSelectedElement(first, rest)));
        }
        catch (Exception ex)
        {
            App.WriteLog("Could not select in Skyline: " + ex.Message);
        }
        finally
        {
            _syncGate.EndPush();
        }
    }

    /// <summary>
    /// Skyline's selection changed: select the same proteins, peptides or precursors in the tree and bring
    /// the first into view. Returns false when none of the locators names an element of the document.
    /// </summary>
    private bool ShowSkylineSelection(IReadOnlyList<string> locators)
    {
        var items = locators.Select(_document.ElementForLocator).OfType<object>().ToList();
        if (items.Count == 0)
            return false;
        // The selection Glimpse just sent, coming back: nothing to do.
        if (!_tree.ReplaceWith(items))
            return true;
        TreeSelectionVersion++;
        ClearNativeTreeSelection();
        RevealInTree(items[0]);
        var peptides = _tree.Peptides(_document);
        if (peptides.Count > 0)
            SetSelection(peptides, "Skyline");
        return true;
    }

    /// <summary>Scroll the tree to an item, expanding its protein (and peptide) as needed.</summary>
    private void RevealInTree(object item)
    {
        var (protein, peptide) = item switch
        {
            DocProtein p => (p, (DocPeptide?)null),
            DocPeptide p => (p.Protein, p),
            DocPrecursor p => (p.Peptide.Protein, p.Peptide),
            _ => ((DocProtein?)null, (DocPeptide?)null),
        };
        if (protein is null)
            return;
        var proteinRow = BringIntoView(DocumentTree, IndexOf(_document.Proteins, protein));
        if (proteinRow is null || peptide is null)
            return;
        proteinRow.IsExpanded = true;
        var peptideRow = BringIntoView(proteinRow, protein.Peptides.IndexOf(peptide));
        if (peptideRow is not null && item is DocPrecursor precursor)
        {
            peptideRow.IsExpanded = true;
            BringIntoView(peptideRow, peptide.Precursors.IndexOf(precursor));
        }
    }

    private static int IndexOf<T>(IReadOnlyList<T> list, T item) where T : class
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (ReferenceEquals(list[i], item))
                return i;
        }
        return -1;
    }

    /// <summary>
    /// The row at <paramref name="index"/> of a virtualized items control, scrolled into view (its
    /// container exists only once it is realized).
    /// </summary>
    private static TreeViewItem? BringIntoView(ItemsControl parent, int index)
    {
        if (index < 0)
            return null;
        parent.ApplyTemplate();
        parent.UpdateLayout();
        if (FindChild<VirtualizingStackPanel>(parent) is { } panel)
        {
            panel.BringIndexIntoViewPublic(index);
            parent.UpdateLayout();
        }
        if (parent.ItemContainerGenerator.ContainerFromIndex(index) is not TreeViewItem row)
            return null;
        row.BringIntoView();
        return row;
    }

    /// <summary>The tree row an element belongs to: itself when it is a row, else its nearest row ancestor.</summary>
    private static TreeViewItem? OwningRow(DependencyObject element) =>
        element as TreeViewItem ?? (element is Visual ? FindAncestor<TreeViewItem>(element) : null);

    private static T? FindAncestor<T>(DependencyObject child) where T : DependencyObject
    {
        for (var d = VisualTreeHelper.GetParent(child); d is not null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is T match)
                return match;
        }
        return null;
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
                return match;
            if (child is not TreeViewItem && FindChild<T>(child) is { } deeper)
                return deeper;
        }
        return null;
    }

    /// <summary>Forget the tree's selection (the selection now comes from Skyline).</summary>
    private void ClearTreeSelection()
    {
        if (_tree.Clear())
            TreeSelectionVersion++;
    }

    /// <summary>The data item of the tree row under the mouse, or null over an expander or empty space.</summary>
    private static object? TreeItemUnder(DependencyObject? source)
    {
        for (var d = source; d is not null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (d is ToggleButton)
                return null;   // the expander arrow: let it expand
            if (d is TreeViewItem item)
                return item.DataContext;
        }
        return null;
    }
}

/// <summary>A tree row's background: highlighted when its item is in the window's tree selection.</summary>
public sealed class TreeSelectionBrushConverter : IMultiValueConverter
{
    private static readonly Brush Highlight = new SolidColorBrush(Color.FromArgb(0x55, 0x33, 0x99, 0xFF));

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values.Length >= 2 && values[1] is IReadOnlySet<object> selected && values[0] is { } item && selected.Contains(item)
            ? Highlight
            : Brushes.Transparent;

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
