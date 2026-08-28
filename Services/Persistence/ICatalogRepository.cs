using SwitchBoard.Data;

namespace SwitchBoard.Services.Persistence;

public interface ICatalogRepository
{
    Task<SwitchBoardCatalog> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(SwitchBoardCatalog catalog, CancellationToken cancellationToken = default);
}
