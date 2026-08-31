namespace SwitchBoard.RuntimeTests.Monitoring;

public sealed class PerformancePanelLifecycleTests
{
    [Fact]
    [Trait("Category", "Regression")]
    public void PerformancePanel_OwnsOneTimerAndDetachesItDuringDisposal()
    {
        var source = File.ReadAllText(FindSourceFile("ViewModels", "Panels", "PerformancePanelViewModel.cs"));

        Assert.Equal(1, source.Split("new DispatcherTimer", StringSplitOptions.None).Length - 1);
        Assert.Contains("if (_disposed || !_isRunning || _isRefreshing || (IsLiveViewPaused && !IsMeasuring)) return;", source,
            StringComparison.Ordinal);
        Assert.Contains("_timer.Stop();", source, StringComparison.Ordinal);
        Assert.Contains("_timer.Tick -= PerformanceTimerOnTick;", source, StringComparison.Ordinal);
        Assert.Contains("Cancel(ref _refreshCancellation);", source, StringComparison.Ordinal);
        Assert.Contains("Cancel(ref _detailsCancellation);", source, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Regression")]
    public void PerformancePanel_ReconcilesRowsInPlaceAndPausesOnlyTheLiveView()
    {
        var source = File.ReadAllText(FindSourceFile("ViewModels", "Panels", "PerformancePanelViewModel.cs"));

        Assert.DoesNotContain("PerformanceProcesses.Clear();", source, StringComparison.Ordinal);
        Assert.Contains("SynchronizeRows(rows);", source, StringComparison.Ordinal);
        Assert.Contains("PerformanceProcesses.Move(currentIndex, index);", source, StringComparison.Ordinal);
        Assert.Contains("if (!IsLiveViewPaused || IsMeasuring) _ = RefreshAsync();", source, StringComparison.Ordinal);
        Assert.Contains("if (IsMeasuring) AddMeasurement(snapshot);", source, StringComparison.Ordinal);
        Assert.Contains("if (IsLiveViewPaused) return;", source, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Regression")]
    public void PerformancePanel_SortsAggregatedGroupsWithoutReplacingTheCollection()
    {
        var source = File.ReadAllText(FindSourceFile("ViewModels", "Panels", "PerformancePanelViewModel.cs"));

        Assert.Contains("BuildDisplaySnapshots(children)", source, StringComparison.Ordinal);
        Assert.Contains("BuildGroupProcessCounts(children)", source, StringComparison.Ordinal);
        Assert.Contains("CountGroupProcesses", source, StringComparison.Ordinal);
        Assert.Contains("DisplayName => IsGroup && _groupProcessCount > 1", source, StringComparison.Ordinal);
        Assert.Contains("CompareMetric", source, StringComparison.Ordinal);
        Assert.Contains("ResolveIconPath", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Take(MaximumVisibleRows)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("PerformanceProcesses.Clear();", source, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Regression")]
    public void PerformancePanel_MeasurementUsesTheCurrentSortAndTracksVram()
    {
        var source = File.ReadAllText(FindSourceFile("ViewModels", "Panels", "PerformancePanelViewModel.cs"));

        Assert.Contains("results.Sort(CompareMeasurement);", source, StringComparison.Ordinal);
        Assert.Contains("PeakVramBytes", source, StringComparison.Ordinal);
        Assert.Contains("VramBytes", source, StringComparison.Ordinal);
        Assert.Contains("MeasurementSortText", source, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Regression")]
    public void PerformancePanel_UsesDeclarativeProcessVisualsWithoutVisualTreeMutation()
    {
        var source = File.ReadAllText(FindSourceFile("Views", "Panels", "PerformancePanel.xaml.cs"));

        Assert.DoesNotContain("VisualTreeHelper", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ItemContainerGenerator", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ProcessIconTag", source, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ProcessGroup_DisplayNameIncludesItsRecursiveProcessCountOnlyForGroups()
    {
        var snapshot = new SwitchBoard.Services.Monitoring.PerformanceProcessSnapshot(42, null, "chrome", null,
            null, null, null, null, false);

        var group = new SwitchBoard.ViewModels.Panels.PerformanceProcessRowViewModel(snapshot, 0, 3, 15, true, false);
        var single = new SwitchBoard.ViewModels.Panels.PerformanceProcessRowViewModel(snapshot, 0, 0, 1, false, false);

        Assert.Equal("chrome (15)", group.DisplayName);
        Assert.Equal("chrome", single.DisplayName);
    }

    private static string FindSourceFile(params string[] relativePath)
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                var candidate = Path.Combine([directory.FullName, .. relativePath]);
                if (File.Exists(candidate)) return candidate;
            }
        }

        throw new FileNotFoundException("Could not find the performance panel source.", Path.Combine(relativePath));
    }
}
