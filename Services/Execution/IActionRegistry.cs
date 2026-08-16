namespace SwitchBoard.Services.Execution;

public interface IActionRegistry
{
    IReadOnlyCollection<string> RegisteredActionTypes { get; }

    bool TryGetHandler(string actionType, out IActionHandler? handler);
}
