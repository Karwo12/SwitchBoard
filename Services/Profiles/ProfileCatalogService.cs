using SwitchBoard.Data;
using SwitchBoard.Services.Persistence;

namespace SwitchBoard.Services.Profiles;

public sealed class ProfileCatalogService(ICatalogRepository repository) : IProfileCatalogService
{
    public Task<SwitchBoardCatalog> LoadAsync(CancellationToken cancellationToken = default) =>
        repository.LoadAsync(cancellationToken);

    public Task SaveAsync(SwitchBoardCatalog catalog, CancellationToken cancellationToken = default)
    {
        Validate(catalog);
        catalog.SchemaVersion = CatalogSchema.CurrentVersion;
        return repository.SaveAsync(catalog, cancellationToken);
    }

    private static void Validate(SwitchBoardCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        if (catalog.Categories.Any(category => category.Id == Guid.Empty))
        {
            throw new InvalidOperationException("Every category must have a valid identifier.");
        }

        if (catalog.Profiles.Any(profile => profile.Id == Guid.Empty))
        {
            throw new InvalidOperationException("Every profile must have a valid identifier.");
        }

        if (catalog.Categories.Any(category => string.IsNullOrWhiteSpace(category.Name)))
        {
            throw new InvalidOperationException("Category names cannot be empty.");
        }

        if (catalog.Profiles.Any(profile => string.IsNullOrWhiteSpace(profile.Name)))
        {
            throw new InvalidOperationException("Profile names cannot be empty.");
        }

        if (catalog.Categories.Select(category => category.Id).Distinct().Count() != catalog.Categories.Count)
        {
            throw new InvalidOperationException("Category identifiers must be unique.");
        }

        if (catalog.Profiles.Select(profile => profile.Id).Distinct().Count() != catalog.Profiles.Count)
        {
            throw new InvalidOperationException("Profile identifiers must be unique.");
        }

        var categoryIds = catalog.Categories.Select(category => category.Id).ToHashSet();
        if (catalog.Profiles.Any(profile => !categoryIds.Contains(profile.CategoryId)))
        {
            throw new InvalidOperationException("Every profile must belong to an existing category.");
        }

        if (catalog.Profiles.SelectMany(profile => profile.Actions).Any(action =>
                action.Id == Guid.Empty || string.IsNullOrWhiteSpace(action.Type)))
        {
            throw new InvalidOperationException("Every action must have an identifier and a type.");
        }
    }
}
