using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace SwitchBoard.Services.Updates;

/// <summary>Checks only the latest stable GitHub Release; it never downloads or installs anything.</summary>
public sealed class GitHubReleaseUpdateService : IUpdateService
{
    public static readonly Uri LatestReleaseUri =
        new("https://api.github.com/repos/Karwo12/SwitchBoard/releases/latest");
    private readonly HttpClient _client;

    public GitHubReleaseUpdateService(HttpClient client) => _client = client;

    public async Task<UpdateCheckResult> CheckAsync(Version currentVersion,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUri);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("SwitchBoard", currentVersion.ToString(3)));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var message = response.StatusCode == HttpStatusCode.Forbidden
                    ? "GitHub API rate limit or access denied."
                    : $"GitHub API returned {(int)response.StatusCode}.";
                return new(UpdateCheckStatus.Failed, currentVersion, Message: message);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean() ||
                root.TryGetProperty("prerelease", out var prerelease) && prerelease.GetBoolean())
                return new(UpdateCheckStatus.Failed, currentVersion, Message: "No stable release is available.");
            var tag = root.TryGetProperty("tag_name", out var tagValue) ? tagValue.GetString() : null;
            if (!TryParseVersion(tag, out var latest))
                return new(UpdateCheckStatus.Failed, currentVersion, Message: "GitHub Release has an unsupported version tag.");
            Uri? url = null;
            if (root.TryGetProperty("html_url", out var urlValue)) Uri.TryCreate(urlValue.GetString(), UriKind.Absolute, out url);
            return latest > currentVersion
                ? new(UpdateCheckStatus.UpdateAvailable, currentVersion, latest, url)
                : new(UpdateCheckStatus.UpToDate, currentVersion, latest, url);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(UpdateCheckStatus.Failed, currentVersion, Message: "Update check timed out.");
        }
        catch (HttpRequestException exception)
        {
            return new(UpdateCheckStatus.Failed, currentVersion, Message: exception.Message);
        }
        catch (JsonException exception)
        {
            return new(UpdateCheckStatus.Failed, currentVersion, Message: exception.Message);
        }
    }

    public static bool TryParseVersion(string? tag, out Version version) =>
        Version.TryParse(tag?.Trim().TrimStart('v', 'V'), out version!);
}
