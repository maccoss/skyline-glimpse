using Glimpse.Core.Data;
using Glimpse.Core.Scans;
using Glimpse.Pwiz;
using Pwiz.Data.Common.Cv;
using Pwiz.Data.MsData.Instruments;
using Xunit;
using Xunit.Abstractions;

namespace Glimpse.Tests;

/// <summary>Which analyzer recorded each scan, read from the scan's instrument configuration.</summary>
public class AnalyzerTests
{
    private readonly ITestOutputHelper _out;

    public AnalyzerTests(ITestOutputHelper output) => _out = output;

    private static InstrumentConfiguration Configuration(params (CVID Cvid, int Order)[] components)
    {
        var configuration = new InstrumentConfiguration("IC1");
        configuration.ComponentList.Add(new Component(CVID.MS_nanoelectrospray, 1));
        foreach (var (cvid, order) in components)
            configuration.ComponentList.Add(new Component(cvid, order));
        configuration.ComponentList.Add(new Component(CVID.MS_inductive_detector, 99));
        return configuration;
    }

    [Fact]
    public void TheLastAnalyzerInTheBeamPathCounts()
    {
        Assert.Equal(MassAnalyzer.Orbitrap, PwizMsDataSource.AnalyzerOf(Configuration((CVID.MS_quadrupole, 2), (CVID.MS_orbitrap, 3))));
        // Listed out of order: Order decides, not the list position.
        Assert.Equal(MassAnalyzer.Astral, PwizMsDataSource.AnalyzerOf(Configuration((CVID.MS_Astral, 4), (CVID.MS_quadrupole, 2))));
        Assert.Equal(MassAnalyzer.IonTrap, PwizMsDataSource.AnalyzerOf(Configuration((CVID.MS_radial_ejection_linear_ion_trap, 2))));
        Assert.Equal(MassAnalyzer.Other, PwizMsDataSource.AnalyzerOf(Configuration((CVID.MS_quadrupole, 2), (CVID.MS_time_of_flight, 3))));
        Assert.Equal(MassAnalyzer.Unknown, PwizMsDataSource.AnalyzerOf(Configuration()));
        Assert.Equal(MassAnalyzer.Unknown, PwizMsDataSource.AnalyzerOf(null));
    }

    [Theory]
    [InlineData(CVID.MS_orbitrap, MassAnalyzer.Orbitrap)]
    [InlineData(CVID.MS_Astral, MassAnalyzer.Astral)]
    [InlineData(CVID.MS_ion_trap, MassAnalyzer.IonTrap)]
    [InlineData(CVID.MS_radial_ejection_linear_ion_trap, MassAnalyzer.IonTrap)]
    [InlineData(CVID.MS_quadrupole_ion_trap, MassAnalyzer.IonTrap)]
    [InlineData(CVID.MS_FT_ICR, MassAnalyzer.Other)]
    [InlineData(CVID.MS_quadrupole, MassAnalyzer.Other)]
    [InlineData(CVID.MS_mass_analyzer_type, MassAnalyzer.Other)]
    [InlineData(CVID.MS_nanoelectrospray, MassAnalyzer.Unknown)]
    [InlineData(CVID.CVID_Unknown, MassAnalyzer.Unknown)]
    public void AnalyzerTermsAreClassified(CVID cvid, MassAnalyzer expected) =>
        Assert.Equal(expected, PwizMsDataSource.Classify(cvid));

    [Fact]
    public void ThermoScansCarryTheirAnalyzer()
    {
        // small.RAW is an LTQ FT: full scans alternate between the FT cell (not an Orbitrap) and the ion trap,
        // fragments are read in the ion trap. MS1 mixes analyzers, so it has no single one.
        using (var source = PwizMsDataSource.Open(TestData.SmallRaw))
        {
            var index = ScanIndexBuilder.Build(source, centroid: true, Array.Empty<IScanObserver>());
            var counts = index.Headers.GroupBy(h => (h.MsLevel, h.Analyzer)).OrderBy(g => g.Key).Select(g => ((int)g.Key.MsLevel, g.Key.Analyzer, g.Count()));
            Assert.Equal(new[] { (1, MassAnalyzer.IonTrap, 7), (1, MassAnalyzer.Other, 7), (2, MassAnalyzer.IonTrap, 34) }, counts);
            Assert.Equal(MassAnalyzer.Unknown, index.AnalyzerOf(1));
            Assert.Equal(MassAnalyzer.IonTrap, index.AnalyzerOf(2));
            var ms1 = index.IndicesOfLevel(1).Select(i => source.ReadSpectrum(i, centroid: true)).ToArray();
            Assert.All(ms1, s => Assert.Equal(s.Details.FilterString!.StartsWith("FTMS", StringComparison.Ordinal) ? MassAnalyzer.Other : MassAnalyzer.IonTrap, s.Header.Analyzer));
        }

        // BSA-FT-HCD: FTMS fragments from a model the reader does not name, which pwiz reports as FT-ICR.
        // Without a model there is no α anyway; a named Orbitrap model reports the orbitrap.
        using (var source = PwizMsDataSource.Open(TestData.BsaHcdRaw))
        {
            var index = ScanIndexBuilder.Build(source, centroid: true, Array.Empty<IScanObserver>());
            Assert.Equal("Thermo Electron instrument model", index.Run.InstrumentModel);
            Assert.Equal(MassAnalyzer.Other, index.AnalyzerOf(2));
            Assert.Equal(MassAnalyzer.Other, source.ReadSpectrum(index.IndicesOfLevel(2)[0], centroid: true).Header.Analyzer);
        }
    }
}
