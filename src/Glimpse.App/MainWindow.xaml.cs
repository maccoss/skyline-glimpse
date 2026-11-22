using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Glimpse.App.Plotting;
using Glimpse.Core.Data;
using Glimpse.Core.Heatmaps;
using Glimpse.Core.Scans;
using Glimpse.Pwiz;
using Glimpse.Skyline;
using Microsoft.Win32;

namespace Glimpse.App;

/// <summary>
/// The Glimpse window: chromatograms and heatmaps on top, the selected spectrum below, the Skyline
/// document on the left and the properties of what is selected on the right.
/// </summary>
public partial class MainWindow : Window
{
    private readonly List<string> _files = new();
    private LoadedRun? _run;
    private CancellationTokenSource? _loadCts;
    private SkylineConnection? _skyline;
    private bool _suppressFileCombo;

    /// <summary>The file being opened (waiting for peaks or being read), or null.</summary>
    private string? _openingPath;

    public MainWindow()
    {
        InitializeComponent();
        Title = App.ProductTitle;
        DataContext = this;
        InitChromatogram();
        InitHeatmap();
        InitSpectrum();
    }

    /// <summary>Rows shown in the properties panel.</summary>
    public ObservableCollection<PropertyRow> Properties { get; } = new();

    private IntensityUnits Units => UnitsCombo.SelectedIndex == 1 ? IntensityUnits.Ions : IntensityUnits.AsRecorded;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        App.WriteLog("MainWindow loaded");
        foreach (var plot in new[] { ChromPlot, HeatPlot, SpectrumPlot })
            PlotClicks.MatchDisplayScale(plot);
        DpiChanged += (_, _) =>
        {
            foreach (var plot in new[] { ChromPlot, HeatPlot, SpectrumPlot })
                PlotClicks.MatchDisplayScale(plot);
        };
        _skyline = SkylineConnection.FromArguments(App.LaunchArgs);
        if (_skyline is not null)
            await ConnectToSkylineAsync(_skyline);

        // A data file passed on the command line (standalone use) opens straight away.
        var file = App.LaunchArgs.FirstOrDefault(File.Exists);
        if (file is not null)
            await OpenFileAsync(file);
    }

    private async Task ConnectToSkylineAsync(SkylineConnection skyline)
    {
        string? documentPath = null;
        // Skyline's tool service answers one request at a time, so a Skyline busy with a long report,
        // an import or another tool times a connection out. Keep trying for a while, and say so.
        var deadline = DateTime.UtcNow.AddMinutes(10);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var (version, path) = await Task.Run(() => (skyline.GetVersion(), skyline.GetDocumentPath()));
                SkylineText.Text = $"Connected to {version}";
                DocumentText.Text = string.IsNullOrEmpty(path) ? "Unsaved document" : path;
                App.WriteLog($"Connected to {version}: {path}");
                documentPath = string.IsNullOrWhiteSpace(path) ? null : path;
                break;
            }
            catch (Exception ex) when (DateTime.UtcNow < deadline && IsLoaded)
            {
                App.WriteLog($"Skyline connection attempt {attempt} failed: {ex.Message}");
                SkylineText.Text = "Waiting for Skyline (it is busy)...";
                DocumentText.Text = "Skyline is busy; Glimpse will connect when it is free. You can open a data file meanwhile.";
                await Task.Delay(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                _skyline = null;
                SkylineText.Text = "Not connected to Skyline";
                DocumentText.Text = "Could not connect to Skyline. Open a data file to browse it on its own.";
                App.WriteLog("Skyline connection failed: " + ex.Message);
                return;
            }
        }
        await LoadDocumentAsync(skyline, documentPath);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _loadCts?.Cancel();
        _run?.Dispose();
    }

    private async void OnOpenFile(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Mass spectrometry data|*.raw;*.mzML;*.mzXML;*.mz5|All files|*.*",
            Title = "Open a data file",
        };
        if (dialog.ShowDialog(this) == true)
            await OpenFileAsync(dialog.FileName);
    }

    private async void OnFileComboChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressFileCombo || FileCombo.SelectedIndex < 0 || FileCombo.SelectedIndex >= _files.Count)
            return;
        var path = _files[FileCombo.SelectedIndex];
        if (_run is null || !string.Equals(_run.Index.FilePath, path, StringComparison.OrdinalIgnoreCase))
            await OpenFileAsync(path);
    }

    private void OnCancel(object sender, RoutedEventArgs e) => _loadCts?.Cancel();

    /// <summary>Open a file and run the scan-index pass over it in the background.</summary>
    private async Task OpenFileAsync(string path)
    {
        _loadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _loadCts = cts;
        _openingPath = path;
        AddToFileList(path);
        ReleaseRun();

        // Skyline's peaks for this file take seconds; reading the file takes a minute or more. Waiting for
        // the peaks lets the document's signals be measured in the same pass instead of a second one.
        Progress.IsIndeterminate = true;
        Progress.Visibility = Visibility.Visible;
        CancelButton.Visibility = Visibility.Visible;
        if (!_documentLoaded.IsCompleted)
        {
            StatusText.Text = $"Waiting for the Skyline document before reading {Path.GetFileName(path)}...";
            await _documentLoaded;
        }
        // A file given up while it waited must not ask for its peaks: the report would hold Skyline, and
        // the file that replaced it would wait behind it.
        var peaks = cts.IsCancellationRequested ? Task.CompletedTask : EnsurePeaksAsync(path);
        if (!peaks.IsCompleted)
        {
            StatusText.Text = $"Fetching Skyline's peaks for {Path.GetFileName(path)}...";
            try
            {
                await peaks;
            }
            catch (Exception)
            {
                // Logged by EnsurePeaksAsync; the file is still worth reading without them.
            }
        }
        Progress.IsIndeterminate = false;
        if (cts.IsCancellationRequested)
        {
            // Superseded by another file (which now owns the status bar), or cancelled by the user.
            if (_loadCts == cts)
            {
                _openingPath = null;
                StatusText.Text = "Cancelled.";
                Progress.Visibility = Visibility.Collapsed;
                CancelButton.Visibility = Visibility.Collapsed;
            }
            return;
        }

        var centroid = CentroidCheck.IsChecked == true;
        StatusText.Text = $"Reading {Path.GetFileName(path)}...";
        Progress.Value = 0;
        Progress.Visibility = Visibility.Visible;
        CancelButton.Visibility = Visibility.Visible;
        var progress = new Progress<double>(f => Progress.Value = f);
        var clock = Stopwatch.StartNew();
        // The chromatogram is drawn as the file is read, so the first look does not wait for the whole pass.
        var preview = new Progress<ScanPassPreview>(p =>
        {
            if (_loadCts == cts)
                ShowPreview(p, Path.GetFileName(path), clock.Elapsed);
        });
        var documentObserver = CreateDocumentObserver(path);
        try
        {
            var run = await Task.Run(() => LoadedRun.Load(path, centroid, documentObserver, progress, preview, cts.Token), cts.Token);
            if (cts.IsCancellationRequested)
            {
                run.Dispose();
                return;
            }
            _run?.Dispose();
            _run = run;
            App.WriteLog($"Indexed {path}: {run.Index.Count} spectra in {clock.Elapsed.TotalSeconds:0.0} s");
            StatusText.Text = $"{Path.GetFileName(path)}: {run.Index.Count:N0} spectra "
                + $"({run.Index.IndicesOfLevel(1).Count:N0} MS1, {run.Index.IndicesOfLevel(2).Count:N0} MS2), "
                + $"RT {run.Index.RtFirst:0.00}-{run.Index.RtLast:0.00} min, read in {clock.Elapsed.TotalSeconds:0.0} s";
            Title = $"{App.ProductTitle} - {Path.GetFileName(path)}";
            OnRunLoaded();
        }
        catch (OperationCanceledException)
        {
            // Superseded by another file, which now owns the status bar, or cancelled by the user.
            if (_loadCts == cts)
                StatusText.Text = "Cancelled.";
        }
        catch (Exception ex)
        {
            App.WriteLog($"Could not open {path}: {ex}");
            StatusText.Text = $"Could not open {Path.GetFileName(path)}: {ex.Message}";
            MessageBox.Show(this, ex.Message, "Could not open file", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (_loadCts == cts)
            {
                _openingPath = null;
                Progress.Visibility = Visibility.Collapsed;
                CancelButton.Visibility = Visibility.Collapsed;
            }
        }
    }

    /// <summary>Let go of the open run: a new file is being read and its preview replaces it on screen.</summary>
    private void ReleaseRun()
    {
        _documentPassCts?.Cancel();
        _run?.Dispose();
        _run = null;
        _currentScan = -1;
        ClearSpectrum();
    }

    private void AddToFileList(string path)
    {
        var i = _files.FindIndex(f => string.Equals(f, path, StringComparison.OrdinalIgnoreCase));
        _suppressFileCombo = true;
        try
        {
            if (i < 0)
            {
                _files.Add(path);
                FileCombo.Items.Add(new ComboBoxItem { Content = Path.GetFileName(path), ToolTip = path });
                i = _files.Count - 1;
            }
            FileCombo.SelectedIndex = i;
        }
        finally
        {
            _suppressFileCombo = false;
        }
    }

    private void OnRunLoaded()
    {
        PopulateScanFilters();
        _currentScan = -1;
        RefreshChromatogram(autoscale: true);
        RefreshHeatmap(autoscale: true);
        ClearSpectrum();
        ShowRunProperties();
        if (_run!.Signals is null)
            _ = EnsureSignalsAsync(_run.Index.FilePath);
        else
            ReportSignals(_run);
    }

    private void OnViewOptionChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _run is null)
            return;
        RefreshChromatogram(autoscale: true);
        RefreshHeatmap(autoscale: ReferenceEquals(sender, HeatLevelCombo));
        if (_currentScan >= 0)
            _ = ShowSpectrumAsync(_currentScan);
    }

    private void OnTopTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource is TabControl)
        {
            ChromPlot.Refresh();
            HeatPlot.Refresh();
        }
    }

    private void OnPropertiesToggle(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
            return;
        var show = PropertiesToggle.IsChecked == true;
        PropertiesPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        PropertiesColumn.Width = show ? new GridLength(320) : new GridLength(0);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox or ComboBox)
            return;
        if (e.Key == Key.Left)
        {
            StepScan(-1);
            e.Handled = true;
        }
        else if (e.Key == Key.Right)
        {
            StepScan(+1);
            e.Handled = true;
        }
    }

    /// <summary>One opened run: its scan index, its heatmaps, and the reader kept open for spectra.</summary>
    private sealed class LoadedRun : IDisposable
    {
        private readonly object _readLock = new();

        private LoadedRun(ScanIndex index, IntensityHeatmaps heatmaps, PwizMsDataSource reader, bool centroid)
        {
            Index = index;
            Heatmaps = heatmaps;
            Reader = reader;
            Centroid = centroid;
        }

        public ScanIndex Index { get; }

        public IntensityHeatmaps Heatmaps { get; }

        public bool Centroid { get; }

        /// <summary>The document's signals in this run, once a document pass has measured them.</summary>
        public Core.Assignment.DocumentSignals? Signals { get; set; }

        private PwizMsDataSource Reader { get; }

        public static LoadedRun Load(string path, bool centroid, Core.Assignment.DocumentSignalObserver? document,
            IProgress<double> progress, IProgress<ScanPassPreview>? preview, CancellationToken ct)
        {
            var reader = PwizMsDataSource.Open(path);
            try
            {
                var heatmap = new IntensityHeatmapAccumulator();
                var observers = new List<IScanObserver> { heatmap };
                if (document is not null)
                    observers.Add(document);
                var index = ScanIndexBuilder.Build(reader, centroid, observers, progress, ct, preview);
                return new LoadedRun(index, heatmap.Result!, reader, centroid) { Signals = document?.Result };
            }
            catch
            {
                reader.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Measure the document's signals with a second pass over the file, on its own reader so spectrum
        /// reads are not blocked while it runs.
        /// </summary>
        public Core.Assignment.DocumentSignals RunDocumentPass(Core.Assignment.DocumentSignalObserver observer, IProgress<double> progress, CancellationToken ct)
        {
            using var reader = PwizMsDataSource.Open(Index.FilePath);
            ScanIndexBuilder.Build(reader, Centroid, new IScanObserver[] { observer }, progress, ct);
            return observer.Result!;
        }

        /// <summary>Read one spectrum. Serialized: the reader is not thread-safe.</summary>
        public SpectrumData ReadSpectrum(int index)
        {
            lock (_readLock)
                return Reader.ReadSpectrum(index, Centroid);
        }

        public void Dispose()
        {
            lock (_readLock)
                Reader.Dispose();
        }
    }
}
