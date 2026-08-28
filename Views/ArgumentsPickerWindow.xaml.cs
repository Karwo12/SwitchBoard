using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using SwitchBoard.Localization;

namespace SwitchBoard.Views;

public partial class ArgumentsPickerWindow : Window, INotifyPropertyChanged
{
    private string _search = string.Empty;
    private string _selectedApplicationFilter = string.Empty;
    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<ArgumentPresetItem> Presets { get; } = [];
    public ObservableCollection<ArgumentPresetItem> FilteredPresets { get; } = [];
    public ObservableCollection<string> ApplicationFilters { get; } = [];
    public string Result { get; private set; } = string.Empty;
    public string Search { get => _search; set { if (_search == value) return; _search = value; PropertyChanged?.Invoke(this, new(nameof(Search))); RefreshFilter(); } }
    public string SelectedApplicationFilter
    {
        get => _selectedApplicationFilter;
        set
        {
            if (_selectedApplicationFilter == value) return;
            _selectedApplicationFilter = value;
            PropertyChanged?.Invoke(this, new(nameof(SelectedApplicationFilter)));
            RefreshFilter();
        }
    }
    private readonly string _initialArguments;

    public ArgumentsPickerWindow(string initialArguments, ILocalizationService localizationService)
        : this(initialArguments, null, localizationService) { }

    public ArgumentsPickerWindow(string initialArguments, string? target, ILocalizationService localizationService)
    {
        _initialArguments = initialArguments ?? string.Empty;
        InitializeComponent();
        foreach (var preset in ArgumentPresetCatalog.ForTarget(target)) Presets.Add(new ArgumentPresetItem(preset, localizationService));
        ApplicationFilters.Add(localizationService.GetString("ArgumentPicker.AllApplications"));
        foreach (var compatibility in Presets.Select(item => item.Compatibility).Distinct(StringComparer.CurrentCultureIgnoreCase))
            ApplicationFilters.Add(compatibility);
        SelectedApplicationFilter = ApplicationFilters[0];
        RefreshFilter();
        DataContext = this;
    }

    private void RefreshFilter()
    {
        var filter = Search.Trim();
        var applicationFilter = SelectedApplicationFilter;
        var allApplications = ApplicationFilters.FirstOrDefault();
        FilteredPresets.Clear();
        foreach (var item in Presets.Where(item => ArgumentPresetFilter.Matches(item, filter, applicationFilter,
                                                                                  allApplications ?? string.Empty)))
            FilteredPresets.Add(item);
        PropertyChanged?.Invoke(this, new(nameof(FilteredPresets)));
    }

    private void Select_OnClick(object sender, RoutedEventArgs e)
    {
        Result = ArgumentComposer.Merge(_initialArguments, Presets);
        DialogResult = true;
    }
}
