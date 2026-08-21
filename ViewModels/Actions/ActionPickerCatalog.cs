using System.Text.Json.Nodes;
using System.Collections.ObjectModel;
using SwitchBoard.Localization;
using SwitchBoard.Services.Actions;

namespace SwitchBoard.ViewModels.Actions;

/// <summary>
/// Presentation-facing adapter for the shared action descriptor registry.
/// MainWindowViewModel keeps the existing forwarding properties and commands,
/// while this class owns picker option creation and searching.
/// </summary>
internal sealed class ActionPickerCatalog(ILocalizationService localization)
{
    public ObservableCollection<ActionTypeOption> CreateOptions() =>
        new(ActionDescriptorRegistry.PickerDescriptors.Select(descriptor => new ActionTypeOption(
            descriptor.TypeId,
            descriptor.DisplayNameResourceKey,
            localization,
            descriptor.CategoryResourceKey,
            descriptor.Keywords.ToArray())));

    public IReadOnlyList<ActionTypeOption> Filter(IEnumerable<ActionTypeOption> options, string query) =>
        options.Where(option => option.Matches(query)).ToList();

    public JsonObject CreateDefaultParameters(string actionType, bool nested) =>
        ActionDescriptorRegistry.Get(actionType)?.CreateDefaultParameters(nested) ?? [];
}
