using Glimpse.Core.Document;
using Xunit;

namespace Glimpse.Tests;

/// <summary>The document tree's multi-selection rules, and the gate that keeps Skyline's polled selection from undoing Glimpse's.</summary>
public class TreeSelectionTests
{
    // P1: AAK (2+, 3+), CCK (2+); P2: DDK (2+); P3: EEK (2+).
    private readonly DocProtein _p1 = new("P1", null, null, "Protein:/P1");
    private readonly DocProtein _p2 = new("P2", null, null, "Protein:/P2");
    private readonly DocProtein _p3 = new("P3", null, null, null);
    private readonly DocPrecursor _aak2, _aak3, _cck2, _ddk2, _eek2;
    private readonly DocumentModel _doc;

    public TreeSelectionTests()
    {
        _aak2 = Docs.Precursor(_p1, "AAK", 2, 400, "run1.raw", 0, 1);
        _aak3 = Docs.Precursor(_p1, "AAK", 3, 267, "run1.raw", 0, 1);
        _cck2 = Docs.Precursor(_p1, "CCK", 2, 410, "run1.raw", 0, 1);
        _ddk2 = Docs.Precursor(_p2, "DDK", 2, 420, "run1.raw", 0, 1);
        _eek2 = Docs.Precursor(_p3, "EEK", 2, 430, "run1.raw", 0, 1);
        _doc = Docs.Model(_p1, _p2, _p3);
    }

    private DocPeptide Aak => _aak2.Peptide;

    private DocPeptide Cck => _cck2.Peptide;

    [Fact]
    public void APlainClickSelectsOneNodeAndCtrlClickAddsAndRemoves()
    {
        var tree = new TreeSelectionState();
        tree.SelectSingle(_p1);
        Assert.Equal(new object[] { _p1 }, tree.Items);
        Assert.Same(_p1, tree.Anchor);

        tree.Toggle(_ddk2.Peptide);
        Assert.Equal(2, tree.Count);
        Assert.Same(_ddk2.Peptide, tree.Anchor);
        Assert.Equal(new[] { Aak, Cck, _ddk2.Peptide }, tree.Peptides(_doc));

        tree.Toggle(_ddk2.Peptide);
        Assert.Equal(new object[] { _p1 }, tree.Items);

        // Deselecting the last one leaves nothing selected, and no peptides.
        tree.Toggle(_p1);
        Assert.Equal(0, tree.Count);
        Assert.Empty(tree.Peptides(_doc));
        Assert.False(tree.Clear());

        tree.SelectSingle(_p2);
        Assert.True(tree.Clear());
        Assert.Equal(0, tree.Count);
    }

    [Fact]
    public void CtrlClickingAPeptideOfASelectedProteinDeselectsJustThatPeptide()
    {
        var tree = new TreeSelectionState();
        tree.SelectSingle(_p1);
        tree.Toggle(Aak);
        Assert.Equal(new object[] { Cck }, tree.Items);
        Assert.Equal(new[] { Cck }, tree.Peptides(_doc));

        // Clicking it again adds it back.
        tree.Toggle(Aak);
        Assert.Equal(new[] { Aak, Cck }, tree.Peptides(_doc));
    }

    [Fact]
    public void CtrlClickingAPrecursorOfASelectedProteinKeepsEverythingElse()
    {
        // P1 and its peptide AAK both selected: deselecting AAK 3+ leaves CCK, and AAK through its 2+ only.
        var tree = new TreeSelectionState();
        tree.SelectSingle(_p1);
        tree.Toggle(_p2);
        tree.ReplaceWith(new object[] { _p1, Aak, _p2 });
        tree.Toggle(_aak3);
        Assert.Equal(new HashSet<object> { _p2, Cck, _aak2 }, tree.Items.ToHashSet());

        // Selected through its peptide only.
        tree.SelectSingle(Aak);
        tree.Toggle(_aak2);
        Assert.Equal(new object[] { _aak3 }, tree.Items);
    }

    [Fact]
    public void CtrlClickingANodeSelectedBothItselfAndThroughItsProteinStillTakesItOut()
    {
        var tree = new TreeSelectionState();
        tree.ReplaceWith(new object[] { _p1, Aak });
        tree.Toggle(Aak);
        Assert.Equal(new object[] { Cck }, tree.Items);
    }

    [Fact]
    public void ShiftClickSelectsTheRowsOnShowBetweenTheAnchorAndTheClick()
    {
        var tree = new TreeSelectionState();
        // No anchor yet: a plain click.
        tree.SelectRange(_doc, _p2, add: false);
        Assert.Equal(new object[] { _p2 }, tree.Items);

        tree.SelectSingle(_p3);
        tree.SelectRange(_doc, _p1, add: false);
        Assert.Equal(new HashSet<object> { _p1, _p2, _p3 }, tree.Items.ToHashSet());
        Assert.Same(_p3, tree.Anchor);   // the range starts from the same place next time

        // Expanded nodes' children are rows too; collapsed ones' are not.
        tree.SetExpanded(_p1, true);
        tree.SetExpanded(Aak, true);
        Assert.Equal(new object[] { _p1, Aak, _aak2, _aak3, Cck, _p2, _p3 }, tree.VisibleRows(_doc));
        tree.SelectSingle(_aak3);
        tree.SelectRange(_doc, _p2, add: false);
        Assert.Equal(new HashSet<object> { _aak3, Cck, _p2 }, tree.Items.ToHashSet());
        tree.SetExpanded(Aak, false);
        Assert.Equal(new object[] { _p1, Aak, Cck, _p2, _p3 }, tree.VisibleRows(_doc));

        // A plain Shift+click drops what was selected outside the range.
        tree.SelectSingle(_p3);
        tree.Toggle(Aak);
        tree.SelectRange(_doc, Cck, add: false);
        Assert.Equal(new HashSet<object> { Aak, Cck }, tree.Items.ToHashSet());

        // Ctrl+Shift adds the range to what is selected.
        tree.SelectSingle(_p3);
        tree.Toggle(Aak);
        tree.SelectRange(_doc, Cck, add: true);
        Assert.Equal(new HashSet<object> { _p3, Aak, Cck }, tree.Items.ToHashSet());
    }

    [Fact]
    public void ARangeToAHiddenRowIsJustThatRow()
    {
        var tree = new TreeSelectionState();
        Assert.Equal(new object[] { _ddk2 }, tree.VisibleRange(_doc, _p1, _ddk2));
        Assert.Equal(new object[] { _p1 }, tree.VisibleRange(_doc, _ddk2, _p1));
        Assert.Equal(new object[] { _p2 }, tree.VisibleRange(_doc, _p2, _p2));
        // From the first row.
        Assert.Equal(new object[] { _p1, _p2, _p3 }, tree.VisibleRange(_doc, _p1, _p3));
    }

    [Fact]
    public void CtrlASelectsEveryProteinAndResetForgetsEverything()
    {
        var tree = new TreeSelectionState();
        tree.SetExpanded(_p1, true);
        tree.SelectSingle(_aak2);
        tree.SelectAll(_doc);
        Assert.Equal(new object[] { _p1, _p2, _p3 }, tree.Items);
        Assert.Equal(new[] { Aak, Cck, _ddk2.Peptide, _eek2.Peptide }, tree.Peptides(_doc));

        tree.Reset();
        Assert.Equal(0, tree.Count);
        Assert.Null(tree.Anchor);
        Assert.Equal(new object[] { _p1, _p2, _p3 }, tree.VisibleRows(_doc));
    }

    [Fact]
    public void SkylinesSelectionReplacesTheTreesUnlessItIsTheSame()
    {
        var tree = new TreeSelectionState();
        Assert.True(tree.ReplaceWith(new object[] { _p2, _p1 }));
        Assert.Same(_p2, tree.Anchor);
        // The same nodes in another order: the selection Glimpse sent, coming back.
        Assert.False(tree.ReplaceWith(new object[] { _p1, _p2 }));
        Assert.Same(_p2, tree.Anchor);
        Assert.True(tree.ReplaceWith(Array.Empty<object>()));
        Assert.Null(tree.Anchor);
        Assert.Equal(0, tree.Count);
    }

    [Fact]
    public void TheAnchorIsSentToSkylineFirst()
    {
        var tree = new TreeSelectionState();
        tree.SelectSingle(_p1);
        tree.Toggle(_p3);   // no locator: not sent
        tree.Toggle(_p2);
        Assert.Equal(new[] { "Protein:/P2", "Protein:/P1" }, tree.Locators());

        tree.SelectSingle(_cck2);
        tree.Toggle(Aak);
        tree.Toggle(Aak);
        tree.Toggle(Aak);
        Assert.Equal(new[] { "Molecule:/P1/AAK", "Precursor:/P1/CCK/2" }, tree.Locators());
        // An anchor that was toggled away is not sent.
        tree.Toggle(Aak);
        Assert.Equal(new[] { "Precursor:/P1/CCK/2" }, tree.Locators());
    }

    [Fact]
    public void APollThatOverlapsAPushIsDropped()
    {
        var gate = new SelectionSyncGate();
        var quiet = gate.BeginPoll();
        Assert.True(gate.IsCurrent(quiet));
        Assert.False(gate.Pushing);

        // A poll started before the push answers after it began, or after it ended: Skyline's old selection.
        var early = gate.BeginPoll();
        gate.BeginPush(new object[] { _p1 });
        Assert.True(gate.Pushing);
        var during = gate.BeginPoll();
        Assert.False(gate.IsCurrent(early));
        Assert.False(gate.IsCurrent(during));
        gate.EndPush();
        Assert.False(gate.Pushing);
        Assert.False(gate.IsCurrent(early));
        Assert.False(gate.IsCurrent(during));

        // Two pushes at once: still pushing until both are answered.
        gate.BeginPush(new object[] { _p1 });
        gate.BeginPush(new object[] { _p2 });
        gate.EndPush();
        Assert.True(gate.Pushing);
        var meanwhile = gate.BeginPoll();
        Assert.False(gate.IsCurrent(meanwhile));
        gate.EndPush();

        var after = gate.BeginPoll();
        Assert.True(gate.IsCurrent(after));
    }

    [Fact]
    public void ThePushComingBackIsNotTakenForASkylineSelection()
    {
        // Glimpse sends P1, then selects every protein (too many to send). Skyline's answer, P1, is the
        // push coming back: ignored, so the tree keeps every protein.
        var gate = new SelectionSyncGate();
        Assert.False(gate.TakeEcho(new object[] { _p1 }));   // nothing sent yet
        gate.BeginPush(new object[] { _p1, _p2 });
        gate.EndPush();
        Assert.True(gate.TakeEcho(new object[] { _p2, _p1 }));
        // Answered: the same selection made in Skyline later is a real change.
        Assert.False(gate.TakeEcho(new object[] { _p1, _p2 }));

        // A different selection made in Skyline meanwhile is applied, and answers the push too.
        gate.BeginPush(new object[] { _p1 });
        gate.EndPush();
        Assert.False(gate.TakeEcho(new object[] { _p3 }));
        Assert.False(gate.TakeEcho(new object[] { _p1 }));
    }
}
