using SwitchBoard.Data;

namespace SwitchBoard.Services.Profiles;

public interface IProfileCatalogService
{
    Task<SwitchBoardCatalog> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(SwitchBoardCatalog catalog, CancellationToken cancellationToken = default);
}
