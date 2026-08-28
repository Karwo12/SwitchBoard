using SwitchBoard.RuntimeTests.TestInfrastructure;

namespace SwitchBoard.RuntimeTests.Unit;

public sealed class ProcessSettingsMappingTests
{
    [Theory]
    [InlineData(ProcessMemoryPriorityIds.VeryLow, 1u)]
    [InlineData(ProcessMemoryPriorityIds.Low, 2u)]
    [InlineData(ProcessMemoryPriorityIds.Medium, 3u)]
    [InlineData(ProcessMemoryPriorityIds.BelowNormal, 4u)]
    [InlineData(ProcessMemoryPriorityIds.Normal, 5u)]
    [Trait("Category", "Unit")]
    public void Memory_priority_maps_to_the_Windows_values(string value, uint expected)
    {
        Assert.Equal(expected, ProcessSettingsService.ParseMemoryPriorityValue(value));
    }

    [Theory]
    [InlineData(ProcessPerformanceModeIds.NoChange, false, 0u, 0u)]
    [InlineData(ProcessPerformanceModeIds.WindowsDefault, true, 0u, 0u)]
    [InlineData(ProcessPerformanceModeIds.HighPerformance, true, 1u, 0u)]
    [InlineData(ProcessPerformanceModeIds.Efficiency, true, 1u, 1u)]
    [Trait("Category", "Unit")]
    public void Performance_mode_mapping_matches_the_execution_throttling_contract(
        string value, bool isConcrete, uint expectedControlMask, uint expectedStateMask)
    {
        Assert.Equal(isConcrete, ProcessSettingsService.IsConcretePerformanceMode(value));
        if (!isConcrete) return;

        Assert.Equal((expectedControlMask, expectedStateMask),
            ProcessSettingsService.PerformanceMasksFor(value));
    }
}
