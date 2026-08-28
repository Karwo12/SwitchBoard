namespace SwitchBoard.Data;

/// <summary>
/// A top-level entry in the Profile navigation. Only categories and profiles
/// assigned to the root can occur here; profiles inside categories keep the
/// ordering of their owning category.
/// </summary>
public sealed class RootNavigationItemDefinition
{
    public RootNavigationItemKind Kind { get; set; }

    public Guid Id { get; set; }
}

public enum RootNavigationItemKind
{
    Category,
    Profile
}
