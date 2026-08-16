using SwitchBoard.Services.Discovery;

namespace SwitchBoard.Services;

public interface IUserDialogService
{
    bool Confirm(string title, string message);

    string? SelectFile(string title, string filter, string? initialPath = null);

    ProcessCandidate? SelectProcess(string title);

    ServiceCandidate? SelectService(string title);

    PowerPlanCandidate? SelectPowerPlan(string title);

    DisplayCandidate? SelectDisplay(string title);

    ProgramCandidate? FindProgram(string title);
}
