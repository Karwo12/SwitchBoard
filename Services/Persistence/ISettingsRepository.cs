using SwitchBoard.Data;

namespace SwitchBoard.Services.Persistence;

public interface ISettingsRepository
{
    Task<UserSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(UserSettings settings, CancellationToken cancellationToken = default);
}
