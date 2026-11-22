using System.Runtime.CompilerServices;
using Glimpse.Core.Assignment;
using Glimpse.Core.Data;
using Glimpse.Core.Document;
using Glimpse.Core.Scans;

namespace Glimpse.Core.Chromatograms;

/// <summary>
/// The XICs one chromatogram needs: which run, which peptides, how they are drawn. Two requests are the
/// same when they name the same run, signals and selection (by reference) with the same options.
/// </summary>
public sealed record XicRequest(
    ScanIndex Index, DocumentSignals Signals, IReadOnlyList<DocPeptide> Peptides, XicMode Mode, bool Ms1, IntensityUnits Units,
    bool IntegratedOnly, IonCalibration? Calibration = null)
{
    public List<XicTrace> Build() => XicTraces.Build(Index, Signals, Peptides, Mode, Ms1, Units, IntegratedOnly, Calibration);

    public bool Equals(XicRequest? other) =>
        other is not null
        && ReferenceEquals(Index, other.Index)
        && ReferenceEquals(Signals, other.Signals)
        && ReferenceEquals(Peptides, other.Peptides)
        && Mode == other.Mode
        && Ms1 == other.Ms1
        && Units == other.Units
        && IntegratedOnly == other.IntegratedOnly
        && Equals(Calibration, other.Calibration);

    public override int GetHashCode() => HashCode.Combine(
        RuntimeHelpers.GetHashCode(Index), RuntimeHelpers.GetHashCode(Signals), RuntimeHelpers.GetHashCode(Peptides),
        Mode, Ms1, Units, IntegratedOnly, Calibration);
}

/// <summary>
/// The last XICs built, and the one large build running in the background. Builds finish out of order
/// (a large selection, then a small one built at once); a result is kept only while its request is
/// still the one wanted.
/// </summary>
/// <remarks>Called on one thread (the window's); only <see cref="XicRequest.Build"/> runs elsewhere.</remarks>
public sealed class XicBuildCache
{
    private XicRequest? _built;
    private List<XicTrace>? _traces;

    /// <summary>The request being built in the background, or null.</summary>
    public XicRequest? Building { get; private set; }

    /// <summary>The traces for <paramref name="request"/> when they are built already.</summary>
    public bool TryGet(XicRequest request, out List<XicTrace> traces)
    {
        if (request.Equals(_built))
        {
            traces = _traces!;
            return true;
        }
        traces = new List<XicTrace>();
        return false;
    }

    /// <summary>
    /// Traces built at once (or no XICs wanted, with a null request): kept, and any background build
    /// for an earlier request is no longer wanted.
    /// </summary>
    public void Store(XicRequest? request, List<XicTrace> traces)
    {
        Building = null;
        _built = request;
        _traces = traces;
    }

    /// <summary>Returns true when a background build of <paramref name="request"/> must start (it is not running yet).</summary>
    public bool Start(XicRequest request)
    {
        if (request.Equals(Building))
            return false;
        Building = request;
        return true;
    }

    /// <summary>
    /// A background build finished. Returns true, keeping the traces, when its request is still the one
    /// wanted; false when something else was asked for meanwhile.
    /// </summary>
    public bool Complete(XicRequest request, List<XicTrace> traces)
    {
        if (!ReferenceEquals(request, Building))
            return false;
        Store(request, traces);
        return true;
    }

    /// <summary>A background build failed: forget it, so asking again starts another.</summary>
    public void Fail(XicRequest request)
    {
        if (ReferenceEquals(request, Building))
            Building = null;
    }
}
