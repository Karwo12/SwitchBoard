using System.Text.Json.Nodes;

namespace SwitchBoard.Models.Actions;

public sealed class ActionDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Type { get; set; } = string.Empty;

    public int ActionSchemaVersion { get; set; } = 1;

    public int SortOrder { get; set; }

    public string? Name { get; set; }

    public bool IsEnabled { get; set; } = true;

    public ActionFailurePolicy FailurePolicy { get; set; } = ActionFailurePolicy.Stop;

    public TimeSpan? Timeout { get; set; }

    public JsonObject Parameters { get; set; } = [];
}
