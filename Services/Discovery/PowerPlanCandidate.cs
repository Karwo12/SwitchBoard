namespace SwitchBoard.Services.Discovery;

public sealed record PowerPlanCandidate(Guid Id, string DisplayName, bool IsActive)
{
    public string GuidText => Id.ToString("D");

    public override string ToString() => DisplayName;
}
