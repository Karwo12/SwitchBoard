namespace SwitchBoard.Services.Discovery;

public sealed record ProgramDiscoveryProgress(
    string CurrentLocation,
    int ScannedFileCount,
    IReadOnlyList<ProgramCandidate> NewItems);
