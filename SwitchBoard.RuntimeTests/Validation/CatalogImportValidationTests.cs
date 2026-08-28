using SwitchBoard.RuntimeTests.TestInfrastructure;

namespace SwitchBoard.RuntimeTests.Validation;

public sealed class CatalogImportValidationTests : RuntimeTestBase
{
    [Fact]
    [Trait("Category", "Regression")]
    public void CatalogImport_RejectsMalformedNestedActionBeforeItCanBeSilentlyDiscarded()
    {
        var condition = Action(ActionTypeIds.ConditionIf, new JsonObject
        {
            [ActionParameterNames.ConditionType] = ConditionTypeIds.FileExists,
            [ActionParameterNames.ConditionValue] = "C:\\Temp",
            [ActionParameterNames.ThenActions] = new JsonArray("not an action")
        });
        var catalog = new SwitchBoardCatalog
        {
            Profiles = [new ProfileDefinition { Name = "Imported", Actions = [condition] }]
        };

        var exception = Assert.Throws<InvalidDataException>(() => ProfileCatalogService.ValidateForImport(catalog));

        Assert.Contains("nested action branch", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
