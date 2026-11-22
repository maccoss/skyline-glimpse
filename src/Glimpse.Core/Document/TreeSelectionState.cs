namespace Glimpse.Core.Document;

/// <summary>
/// The document tree's multi-selection: proteins, peptides and precursors, any mix. The window turns
/// clicks into these calls; the rules live here so they can be tested without a window.
/// </summary>
/// <remarks>
/// A selected node selects everything under it. Ctrl+clicking a node that is selected only through its
/// protein (or peptide) therefore deselects just that node: its ancestor is replaced by the ancestor's other
/// children.
/// </remarks>
public sealed class TreeSelectionState
{
    private readonly HashSet<object> _items = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<object> _expanded = new(ReferenceEqualityComparer.Instance);

    /// <summary>The selected nodes.</summary>
    public IReadOnlySet<object> Items => _items;

    public int Count => _items.Count;

    /// <summary>The last node clicked: where a Shift+click range starts, and Skyline's primary selection.</summary>
    public object? Anchor { get; private set; }

    /// <summary>Forget everything (a new document).</summary>
    public void Reset()
    {
        _items.Clear();
        _expanded.Clear();
        Anchor = null;
    }

    /// <summary>Record a node expanded or collapsed in the tree (a Shift+click range covers the rows on show).</summary>
    public void SetExpanded(object node, bool expanded)
    {
        if (expanded)
            _expanded.Add(node);
        else
            _expanded.Remove(node);
    }

    /// <summary>A plain click: this node only.</summary>
    public void SelectSingle(object node)
    {
        _items.Clear();
        _items.Add(node);
        Anchor = node;
    }

    /// <summary>
    /// Ctrl+click: add the node, or take it out. A node selected through a selected protein or peptide is
    /// taken out by selecting that ancestor's other children instead.
    /// </summary>
    public void Toggle(object node)
    {
        Anchor = node;
        var removed = _items.Remove(node);
        var path = PathTo(node);
        var top = -1;
        for (var i = 0; i < path.Count - 1; i++)
        {
            if (_items.Contains(path[i]))
            {
                top = i;
                break;
            }
        }
        if (top < 0)
        {
            if (!removed)
                _items.Add(node);
            return;
        }
        // Replace each selected ancestor, from the highest down, by its children other than the one on the
        // way to the node.
        for (var i = top; i < path.Count - 1; i++)
        {
            _items.Remove(path[i]);
            foreach (var child in Children(path[i]))
            {
                if (!ReferenceEquals(child, path[i + 1]))
                    _items.Add(child);
            }
        }
    }

    /// <summary>
    /// Shift+click: the rows on show from the anchor to <paramref name="node"/>, replacing the selection or,
    /// with <paramref name="add"/> (Ctrl+Shift), added to it. With no anchor yet it is a plain click.
    /// </summary>
    public void SelectRange(DocumentModel document, object node, bool add)
    {
        if (Anchor is null)
        {
            SelectSingle(node);
            return;
        }
        var range = VisibleRange(document, Anchor, node);
        if (!add)
            _items.Clear();
        _items.UnionWith(range);
    }

    /// <summary>Ctrl+A: every protein.</summary>
    public void SelectAll(DocumentModel document)
    {
        _items.Clear();
        _items.UnionWith(document.Proteins);
    }

    /// <summary>
    /// Skyline's selection: these nodes, the first as the anchor. Returns false, changing nothing, when the
    /// tree already holds exactly these nodes (the selection Glimpse sent, coming back).
    /// </summary>
    public bool ReplaceWith(IReadOnlyList<object> nodes)
    {
        if (_items.SetEquals(nodes))
            return false;
        _items.Clear();
        _items.UnionWith(nodes);
        Anchor = nodes.Count > 0 ? nodes[0] : null;
        return true;
    }

    /// <summary>Select nothing. Returns false when nothing was selected.</summary>
    public bool Clear()
    {
        if (_items.Count == 0)
            return false;
        _items.Clear();
        return true;
    }

    /// <summary>The peptides the selection covers, in document order.</summary>
    public List<DocPeptide> Peptides(DocumentModel document) => document.PeptidesOf(_items);

    /// <summary>The selection's Skyline locators, the anchor first (Skyline shows its primary selection).</summary>
    public List<string> Locators()
    {
        var ordered = Anchor is not null && _items.Contains(Anchor)
            ? _items.Where(i => !ReferenceEquals(i, Anchor)).Prepend(Anchor)
            : _items;
        return ordered.Select(LocatorOf).OfType<string>().ToList();
    }

    /// <summary>The rows the tree shows, in order: every protein, and the children of expanded nodes.</summary>
    public List<object> VisibleRows(DocumentModel document)
    {
        var rows = new List<object>();
        foreach (var protein in document.Proteins)
        {
            rows.Add(protein);
            if (!_expanded.Contains(protein))
                continue;
            foreach (var peptide in protein.Peptides)
            {
                rows.Add(peptide);
                if (_expanded.Contains(peptide))
                    rows.AddRange(peptide.Precursors);
            }
        }
        return rows;
    }

    /// <summary>The rows on show from one node to another, either way round; just <paramref name="to"/> when either is hidden.</summary>
    public List<object> VisibleRange(DocumentModel document, object from, object to)
    {
        var rows = VisibleRows(document);
        int a = IndexOf(rows, from), b = IndexOf(rows, to);
        if (a < 0 || b < 0)
            return new List<object> { to };
        return rows.GetRange(Math.Min(a, b), Math.Abs(a - b) + 1);
    }

    private static int IndexOf(List<object> rows, object node)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            if (ReferenceEquals(rows[i], node))
                return i;
        }
        return -1;
    }

    /// <summary>The node's ancestors, highest first, then the node.</summary>
    private static List<object> PathTo(object node) => node switch
    {
        DocPeptide p => new List<object> { p.Protein, p },
        DocPrecursor p => new List<object> { p.Peptide.Protein, p.Peptide, p },
        _ => new List<object> { node },
    };

    private static IEnumerable<object> Children(object node) => node switch
    {
        DocProtein p => p.Peptides,
        DocPeptide p => p.Precursors,
        _ => Array.Empty<object>(),
    };

    private static string? LocatorOf(object node) => node switch
    {
        DocProtein p => p.Locator,
        DocPeptide p => p.Locator,
        DocPrecursor p => p.Locator,
        _ => null,
    };
}

/// <summary>
/// Keeps Skyline's polled selection from undoing the one Glimpse sent. A poll that started before a push
/// finished may return Skyline's old selection: its answer is dropped. The first answer after a push is
/// Glimpse's own selection coming back: it is not a change made in Skyline, and must not replace whatever
/// the tree has selected since (a selection too large to send, say).
/// </summary>
/// <remarks>Called on one thread (the window's): start and finish each push and poll there.</remarks>
public sealed class SelectionSyncGate
{
    private int _pushes;
    private int _generation;
    private HashSet<object>? _sent;

    /// <summary>True while a selection Glimpse sent has not been answered yet.</summary>
    public bool Pushing => _pushes > 0;

    /// <summary>A push of these document elements starts.</summary>
    public void BeginPush(IEnumerable<object> elements)
    {
        _pushes++;
        _sent = new HashSet<object>(elements, ReferenceEqualityComparer.Instance);
    }

    public void EndPush()
    {
        _pushes--;
        _generation++;
    }

    /// <summary>Call as a poll starts; pass the result to <see cref="IsCurrent"/> when it answers.</summary>
    public int BeginPoll() => _generation;

    /// <summary>
    /// True when no push is running and none finished while the poll ran, so its answer is Skyline's
    /// current selection. (A push that started during the poll is still running, or has finished.)
    /// </summary>
    public bool IsCurrent(int poll) => _pushes == 0 && poll == _generation;

    /// <summary>
    /// A current poll found a new Skyline selection (its document elements). Returns true when it is the
    /// last push coming back, to be ignored. Either way the push is answered: a later selection of the same
    /// elements was made in Skyline.
    /// </summary>
    public bool TakeEcho(IEnumerable<object> elements)
    {
        var sent = _sent;
        _sent = null;
        return sent is not null && sent.SetEquals(elements);
    }
}
