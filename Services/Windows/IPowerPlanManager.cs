using SwitchBoard.Services.Discovery;

namespace SwitchBoard.Services.Windows;

public interface IPowerPlanManager
{
    Task<IReadOnlyList<PowerPlanCandidate>> GetPlansAsync(CancellationToken cancellationToken = default);

    Task<Guid> GetActivePlanAsync(CancellationToken cancellationToken = default);

    Task SetActivePlanAsync(Guid planId, CancellationToken cancellationToken = default);
}
