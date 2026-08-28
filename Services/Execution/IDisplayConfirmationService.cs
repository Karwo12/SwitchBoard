namespace SwitchBoard.Services.Execution;

public interface IDisplayConfirmationService
{
    Task<bool> ConfirmAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
}
