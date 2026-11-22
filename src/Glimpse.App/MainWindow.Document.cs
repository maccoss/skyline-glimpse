using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Glimpse.Core.Assignment;
using Glimpse.Core.Document;
using Glimpse.Core.Library;
using Glimpse.Library;
using Glimpse.Skyline;

namespace Glimpse.App;

public partial class MainWindow
{
    private DocumentModel _document = DocumentModel.Empty;
    private DocumentSettings _settings = DocumentSettings.Default;
    private ISpectralLibrary? _library;
    private SpectrumCandidates? _candidates;
    private DispatcherTimer? _selectionTimer;
    private string _lastSkylineSelection = "";
    private bool _selectionPollBusy;
    private CancellationTokenSource? _documentPassCts;

    /// <summary>The peptides currently selected (from Skyline or the tree), in document order.</summary>
    private IReadOnlyList<DocPeptide> _selectedPeptides = Array.Empty<DocPeptide>();

    /// <summary>Colors of the selected peptides, distinct within the selection; used by the XICs and the spectrum alike.</summary>
    private Dictionary<DocPeptide, ScottPlot.Color> _selectionColors = new();

    private bool _lastPollFailed;

    /// <summary>A peptide's color everywhere in the window: its selection color when selected, else its usual one.</summary>
    private ScottPlot.Color PeptideColor(DocPeptide peptide) =>
        _selectionColors.TryGetValue(peptide, out var c) ? c : Glimpse.Plotting.PeptideColors.For(peptide);

    /// <summary>Completes when the document's targets have been read (or could not be).</summary>
    private Task _documentLoaded = Task.CompletedTask;

    /// <summary>
    /// Read the document in steps, each shown as it arrives: the result files (so a file can be opened at
    /// once), then the targets and transitions, then the replicate Skyline is showing is opened.
    /// </summary>
    private async Task LoadDocumentAsync(SkylineConnection skyline, string? documentPath)
    {
        var loaded = new TaskCompletionSource();
        _documentLoaded = loaded.Task;
        var clock = Stopwatch.StartNew();
        DocumentProgress.Visibility = Visibility.Visible;
        try
        {
            DocumentText.Text = "Step 1 of 3: listing the document's result files...";
            var files = await Task.Run(() => SkylineDocumentLoader.LoadResultFiles(skyline, documentPath, App.WriteLog));
            _document = new DocumentModel(documentPath, Array.Empty<DocProtein>(), files);
            AddResultFiles(_document);

            DocumentText.Text = $"Step 2 of 3: reading targets and transitions ({files.Count:N0} result files listed)...";
            var (model, settings, library) = await Task.Run(() =>
            {
                var m = SkylineDocumentLoader.LoadTargets(skyline, documentPath, files, App.WriteLog);
                var s = SkylineDocumentLoader.LoadSettings(skyline, m.DocumentPath, App.WriteLog);
                ISpectralLibrary? lib = null;
                var libPath = SkylineDocumentLoader.FindLibrary(s, m.DocumentPath);
                if (libPath is not null)
                {
                    try
                    {
                        lib = BlibLibrary.Open(libPath);
                    }
                    catch (Exception ex)
                    {
                        App.WriteLog($"Could not open library {libPath}: {ex.Message}");
                    }
                }
                return (m, s, lib);
            });
            _document = model;
            _settings = settings;
            _library = library;
            _candidates = new SpectrumCandidates(model.Precursors);
            _peakLoads.Clear();
            DocumentTree.ItemsSource = model.Proteins;
            DocumentText.Text = $"{(model.DocumentPath is null ? "Unsaved document" : Path.GetFileName(model.DocumentPath))}: "
                + $"{model.Proteins.Count:N0} proteins, {model.Peptides.Count:N0} peptides, {model.Precursors.Count:N0} precursors, "
                + $"{model.TransitionCount:N0} transitions, {model.ResultFiles.Count:N0} result files"
                + (library is null ? "; no spectral library found" : $"; library {library.Name} ({library.Count:N0} spectra)")
                + $". Extraction: MS1 {settings.PrecursorTolerance.Describe()}, MS2 {settings.ProductTolerance.Describe()}.";
            DocumentText.ToolTip = model.DocumentPath;
            App.WriteLog($"Document read in {clock.Elapsed.TotalSeconds:0.0} s: {DocumentText.Text}");
            StartSelectionPolling(skyline);
        }
        catch (Exception ex)
        {
            App.WriteLog("Could not read the document: " + ex);
            DocumentText.Text = "Could not read the Skyline document: " + ex.Message;
            return;
        }
        finally
        {
            DocumentProgress.Visibility = Visibility.Collapsed;
            loaded.TrySetResult();
        }

        if (_run is not null)
            _ = EnsureSignalsAsync(_run.Index.FilePath);
        else if (_openingPath is null)
            await OpenActiveReplicateAsync(skyline);
    }

    /// <summary>Step 3: open the data file of the replicate Skyline is showing, so there is something to look at.</summary>
    private async Task OpenActiveReplicateAsync(SkylineConnection skyline)
    {
        string? replicate = null;
        try
        {
            replicate = await Task.Run(() => skyline.Call(c => c.GetReplicateName()));
        }
        catch (Exception ex)
        {
            App.WriteLog("Active replicate unavailable: " + ex.Message);
        }
        var file = SkylineDocumentLoader.FirstFileToOpen(_document.ResultFiles, replicate);
        if (file?.FilePath is { } path && _run is null && _openingPath is null)
        {
            App.WriteLog($"Opening the active replicate {replicate}: {path}");
            await OpenFileAsync(path);
        }
    }


    /// <summary>A document observer for a file about to be read, or null when the document has no peaks in it.</summary>
    private DocumentSignalObserver? CreateDocumentObserver(string path)
    {
        if (_document.Precursors.Count == 0)
            return null;
        var fileName = ResultFileNameFor(path);
        if (fileName is null || !_document.HasPeaksFor(fileName))
            return null;
        return new DocumentSignalObserver(_document, fileName, _settings);
    }

    private readonly Dictionary<string, Task> _peakLoads = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Fetch Skyline's integrated peaks for a data file (once per file, shared by every caller). Safe to
    /// start before the file's scan pass so the two run side by side.
    /// </summary>
    private Task EnsurePeaksAsync(string path)
    {
        var fileName = ResultFileNameFor(path);
        var file = _document.ResultFiles.FirstOrDefault(f => f.FileName == fileName);
        if (_skyline is null || fileName is null || file is null || _document.HasPeaksFor(fileName))
            return Task.CompletedTask;
        if (_peakLoads.TryGetValue(fileName, out var running))
            return running;
        var skyline = _skyline;
        var document = _document;
        var task = Task.Run(() => SkylineDocumentLoader.LoadPeaks(skyline, document, file, App.WriteLog));
        _peakLoads[fileName] = task;
        task.ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                App.WriteLog($"Peaks for {fileName} unavailable: {t.Exception?.GetBaseException().Message}");
                _peakLoads.Remove(fileName);   // let a later attempt retry
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());
        return task;
    }

    /// <summary>Peaks for the file, then the document pass over it, then refresh what is on screen.</summary>
    private async Task EnsureSignalsAsync(string path)
    {
        var fileName = ResultFileNameFor(path);
        if (fileName is null)
            return;
        if (!_document.HasPeaksFor(fileName))
        {
            DocumentText.ToolTip = null;
            StatusText.Text = $"Fetching Skyline's peaks for {fileName}...";
            try
            {
                await EnsurePeaksAsync(path);
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Could not fetch Skyline's peaks for {fileName}: {ex.GetBaseException().Message}";
                return;
            }
        }
        if (_run is not null && string.Equals(_run.Index.FilePath, path, StringComparison.OrdinalIgnoreCase) && _run.Signals is null)
            await RunDocumentPassAsync();
        if (_currentScan >= 0)
            _ = ShowSpectrumAsync(_currentScan);
    }

    /// <summary>
    /// The second pass over the open file that measures the document's signals, for a file opened
    /// before the document finished loading (or after the document changed).
    /// </summary>
    private async Task RunDocumentPassAsync()
    {
        var run = _run;
        if (run is null)
            return;
        var observer = CreateDocumentObserver(run.Index.FilePath);
        if (observer is null)
            return;
        _documentPassCts?.Cancel();
        var cts = new CancellationTokenSource();
        _documentPassCts = cts;
        StatusText.Text = $"Measuring document signals in {Path.GetFileName(run.Index.FilePath)}...";
        Progress.Value = 0;
        Progress.Visibility = Visibility.Visible;
        var progress = new Progress<double>(f => Progress.Value = f);
        var clock = Stopwatch.StartNew();
        try
        {
            var signals = await Task.Run(() => run.RunDocumentPass(observer, progress, cts.Token), cts.Token);
            run.Signals = signals;
            App.WriteLog($"Document pass over {run.Index.FilePath} in {clock.Elapsed.TotalSeconds:0.0} s");
            if (!ReferenceEquals(run, _run))
                return;
            ReportSignals(run);
            RefreshChromatogram(autoscale: false);
            RefreshHeatmap(autoscale: false);
            if (_currentScan >= 0)
                _ = ShowSpectrumAsync(_currentScan);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            App.WriteLog("Document pass failed: " + ex);
            StatusText.Text = "Could not measure document signals: " + ex.Message;
        }
        finally
        {
            if (_documentPassCts == cts)
                Progress.Visibility = Visibility.Collapsed;
        }
    }

    private void ReportSignals(LoadedRun run)
    {
        if (run.Signals is not { } s)
            return;
        double ms1Total = 0, ms1Assigned = 0, ms2Total = 0, ms2Assigned = 0;
        foreach (var h in run.Index.Headers)
        {
            if (h.MsLevel == 1)
            {
                ms1Total += h.TotalIonCurrent;
                ms1Assigned += s.AssignedIntensity[h.Index];
            }
            else if (h.MsLevel == 2)
            {
                ms2Total += h.TotalIonCurrent;
                ms2Assigned += s.AssignedIntensity[h.Index];
            }
        }
        StatusText.Text = $"{Path.GetFileName(run.Index.FilePath)}: {s.PrecursorsInFile:N0} document precursors with peaks; "
            + $"assigned signal MS1 {Percent(ms1Assigned, ms1Total)}, MS2 {Percent(ms2Assigned, ms2Total)}";
    }

    private static string Percent(double part, double whole) => whole > 0 ? $"{100 * part / whole:0.0}%" : "n/a";

    private void AddResultFiles(DocumentModel model)
    {
        foreach (var file in model.ResultFiles)
        {
            if (file.FilePath is null || _files.Any(f => string.Equals(f, file.FilePath, StringComparison.OrdinalIgnoreCase)))
                continue;
            _files.Add(file.FilePath);
            var label = file.ReplicateName is { } rep && !string.Equals(rep, Path.GetFileNameWithoutExtension(file.FileName), StringComparison.OrdinalIgnoreCase)
                ? $"{rep}  ({file.FileName})"
                : file.FileName;
            _suppressFileCombo = true;
            FileCombo.Items.Add(new ComboBoxItem
            {
                Content = label,
                ToolTip = file.FilePath,
                Foreground = File.Exists(file.FilePath) || Directory.Exists(file.FilePath) ? null : System.Windows.Media.Brushes.Gray,
            });
            _suppressFileCombo = false;
        }
    }

    /// <summary>The document's name for a data file (how its peaks are keyed), or null when the document has no such file.</summary>
    private string? ResultFileNameFor(string path)
    {
        var name = Path.GetFileName(path);
        var match = _document.ResultFiles.FirstOrDefault(f =>
            string.Equals(f.FileName, name, StringComparison.OrdinalIgnoreCase)
            || (f.FilePath is not null && string.Equals(Path.GetFileName(f.FilePath), name, StringComparison.OrdinalIgnoreCase)));
        return match?.FileName;
    }

    /// <summary>The document's name for the currently loaded file.</summary>
    private string? CurrentResultFileName() => _run is null ? null : ResultFileNameFor(_run.Index.FilePath);

    private void StartSelectionPolling(SkylineConnection skyline)
    {
        _selectionTimer?.Stop();
        _selectionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _selectionTimer.Tick += async (_, _) =>
        {
            if (_selectionPollBusy || FollowSkylineCheck.IsChecked != true || WindowState == WindowState.Minimized || !IsVisible)
                return;
            _selectionPollBusy = true;
            try
            {
                // Skip this beat while one of Glimpse's own reports holds Skyline: it would only wait.
                var (asked, selection) = await Task.Run(() => skyline.TryCall(c => c.GetSelection(), out var s) ? (true, s) : (false, null));
                if (!asked)
                    return;
                var locators = selection?.Locators ?? Array.Empty<string>();
                if (_lastPollFailed)
                    App.WriteLog("Selection polling resumed.");
                _lastPollFailed = false;
                var key = string.Join("\n", locators);
                if (key == _lastSkylineSelection)
                    return;
                _lastSkylineSelection = key;
                var peptides = locators.SelectMany(_document.PeptidesForLocator).Distinct().ToList();
                if (peptides.Count > 0)
                    SetSelection(peptides, "Skyline");
            }
            catch (Exception ex)
            {
                // Skyline answers one request at a time; while it is busy every poll fails. Say so once.
                if (!_lastPollFailed)
                    App.WriteLog("Selection poll failed (Skyline busy?): " + ex.Message);
                _lastPollFailed = true;
            }
            finally
            {
                _selectionPollBusy = false;
            }
        };
        _selectionTimer.Start();
    }

    private void SetSelection(IReadOnlyList<DocPeptide> peptides, string source)
    {
        _selectedPeptides = peptides;
        _selectionColors = peptides.Count <= Glimpse.Plotting.PeptideColors.PaletteSize
            ? Glimpse.Plotting.PeptideColors.ForSelection(peptides)
            : new Dictionary<DocPeptide, ScottPlot.Color>();
        SelectionList.ItemsSource = peptides;
        SelectionHeader.Text = $"Selection: {peptides.Count:N0} peptide{(peptides.Count == 1 ? "" : "s")} ({source})";
        if (peptides.Count > 0 && XicModeCombo.SelectedIndex == 0)
            XicModeCombo.SelectedIndex = peptides.Count == 1 ? 3 : 1; // show something as soon as there is a selection
        else
            RefreshChromatogram(autoscale: false);
    }

    private void OnDocumentTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        var peptides = e.NewValue switch
        {
            DocProtein protein => protein.Peptides.ToList(),
            DocPeptide peptide => new List<DocPeptide> { peptide },
            DocPrecursor precursor => new List<DocPeptide> { precursor.Peptide },
            _ => null,
        };
        if (peptides is { Count: > 0 })
            SetSelection(peptides, "Glimpse");
    }

    private void OnDocumentTreeDoubleClick(object sender, MouseButtonEventArgs e) =>
        NavigateSkyline(DocumentTree.SelectedItem switch
        {
            DocProtein p => p.Locator,
            DocPeptide p => p.Locator,
            DocPrecursor p => p.Locator,
            _ => null,
        });

    private void OnSelectionListDoubleClick(object sender, MouseButtonEventArgs e) =>
        NavigateSkyline((SelectionList.SelectedItem as DocPeptide)?.Locator);

    /// <summary>Select an element in Skyline's Targets view.</summary>
    private void NavigateSkyline(string? locator)
    {
        if (_skyline is null || string.IsNullOrEmpty(locator))
            return;
        var skyline = _skyline;
        // Remember it so the poller does not bounce the same selection straight back.
        _lastSkylineSelection = locator;
        _ = Task.Run(() =>
        {
            try
            {
                skyline.Call(c => c.SetSelectedElement(locator));
            }
            catch (Exception ex)
            {
                App.WriteLog("Navigate failed: " + ex.Message);
            }
        });
    }

    /// <summary>The document precursors in the given spectrum, or empty when there is no document or no match.</summary>
    private IReadOnlyList<DocPrecursor> CandidatesFor(Core.Data.SpectrumData spectrum, bool ignoreRt)
    {
        var file = CurrentResultFileName();
        if (_candidates is null || (file is null && !ignoreRt))
            return Array.Empty<DocPrecursor>();
        return _candidates.Find(spectrum.Header, file ?? "", new CandidateOptions { IgnoreRetentionTime = ignoreRt });
    }
}
