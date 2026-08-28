namespace SwitchBoard.Services.Updates;

public interface IUpdateService
{
    Task<UpdateCheckResult> CheckAsync(Version currentVersion, CancellationToken cancellationToken = default);
}

public enum UpdateCheckStatus { UpToDate, UpdateAvailable, Failed }

public sealed record UpdateCheckResult(UpdateCheckStatus Status, Version CurrentVersion,
    Version? LatestVersion = null, Uri? ReleaseUrl = null, string? Message = null);
