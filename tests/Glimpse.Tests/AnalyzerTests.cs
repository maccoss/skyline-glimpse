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

    [Fact]
    public void AScanWithoutAConfigurationUsesTheRunsDefault()
    {
        var orbitrap = Configuration((CVID.MS_orbitrap, 2));
        var astral = Configuration((CVID.MS_Astral, 2));
        using var msd = new global::Pwiz.Data.MsData.MSData();
        Assert.Null(PwizMsDataSource.DefaultConfiguration(msd));                 // no configuration at all
        msd.InstrumentConfigurations.Add(orbitrap);
        Assert.Same(orbitrap, PwizMsDataSource.DefaultConfiguration(msd));       // the only one
        msd.InstrumentConfigurations.Add(astral);
        Assert.Null(PwizMsDataSource.DefaultConfiguration(msd));                 // two, and no default: unknown
        msd.Run.DefaultInstrumentConfiguration = astral;
        Assert.Same(astral, PwizMsDataSource.DefaultConfiguration(msd));         // the run's default wins
        msd.InstrumentConfigurations.RemoveAt(1);                                 // the Astral one: left with the Orbitrap only
        Assert.Same(astral, PwizMsDataSource.DefaultConfiguration(msd));         // even over a file's only one
    }

    [Fact]
    public void AnMzxmlScanGetsTheFilesOnlyAnalyzer()
    {
        // mzXML describes the instrument once; pwiz attaches it to no scan, so the scan falls back to it.
        const string mzxml = """
            <?xml version="1.0" encoding="ISO-8859-1"?>
            <mzXML xmlns="http://sashimi.sourceforge.net/schema_revision/mzXML_3.2">
              <msRun scanCount="1" startTime="PT90S" endTime="PT90S">
                <parentFile fileName="file://C:/run.raw" fileType="RAWData" fileSha1="0000000000000000000000000000000000000000"/>
                <msInstrument msInstrumentID="1">
                  <msManufacturer category="msManufacturer" value="Thermo Scientific"/>
                  <msModel category="msModel" value="Orbitrap Exploris 480"/>
                  <msIonisation category="msIonisation" value="nanoelectrospray"/>
                  <msMassAnalyzer category="msMassAnalyzer" value="orbitrap"/>
                  <msDetector category="msDetector" value="inductive detector"/>
                </msInstrument>
                <dataProcessing centroided="1"><software type="conversion" name="test" version="1"/></dataProcessing>
                <scan num="1" scanType="Full" centroided="1" msLevel="1" peaksCount="1" polarity="+" retentionTime="PT90S"
                      lowMz="100" highMz="100" basePeakMz="100" basePeakIntensity="10" totIonCurrent="10">
                  <peaks compressionType="none" compressedLen="0" precision="64" byteOrder="network" contentType="m/z-int">QFkAAAAAAABAJAAAAAAAAA==</peaks>
                </scan>
              </msRun>
            </mzXML>
            """;
        var dir = Directory.CreateTempSubdirectory("glimpse-mzxml");
        try
        {
            var path = Path.Combine(dir.FullName, "run.mzXML");
            File.WriteAllText(path, mzxml);
            using var source = PwizMsDataSource.Open(path);
            var spectrum = source.ReadSpectrum(0, centroid: false);
            Assert.Equal(new[] { 100.0 }, spectrum.Mz);
            Assert.Equal(MassAnalyzer.Orbitrap, spectrum.Header.Analyzer);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
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
