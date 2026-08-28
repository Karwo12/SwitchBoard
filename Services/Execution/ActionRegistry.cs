namespace SwitchBoard.Services.Execution;

public sealed class ActionRegistry : IActionRegistry
{
    private readonly IReadOnlyDictionary<string, IActionHandler> _handlers;

    public ActionRegistry(IEnumerable<IActionHandler> handlers)
    {
        var registeredHandlers = new Dictionary<string, IActionHandler>(StringComparer.OrdinalIgnoreCase);
        foreach (var handler in handlers)
        {
            if (!registeredHandlers.TryAdd(handler.ActionType, handler))
            {
                throw new InvalidOperationException(
                    $"An action handler for '{handler.ActionType}' is already registered.");
            }
        }

        _handlers = registeredHandlers;
    }

    public IReadOnlyCollection<string> RegisteredActionTypes => _handlers.Keys.ToArray();

    public bool TryGetHandler(string actionType, out IActionHandler? handler) =>
        _handlers.TryGetValue(actionType, out handler);
}
