namespace SwitchBoard.ViewModels;

public enum ReorderItemKind
{
    Category,
    Profile,
    Action
}

public sealed record ReorderDropRequest(
    ReorderItemKind Kind,
    object Item,
    object? TargetItem,
    int TargetIndex,
    Guid? TargetParentId = null);
