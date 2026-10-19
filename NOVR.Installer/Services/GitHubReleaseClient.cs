using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace NOVR.Installer.Services;

public sealed class GitHubReleaseClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;

    public sealed record NovrRelease(ReleaseVersion Version, string AssetName, string DownloadUrl);

    public GitHubReleaseClient()
    {
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(InstallerConstants.UserAgent);
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    // Null when the repository has no published release yet.
    public async Task<NovrRelease?> TryGetLatestNovrReleaseAsync(IProgress<string> progress, CancellationToken cancellationToken)
    {
        progress.Report($"Checking latest NOVR release on github.com/{InstallerConstants.ForkSource}...");
        GitHubRelease release;
        try
        {
            release = await GetLatestReleaseAsync(InstallerConstants.GitHubOwner, InstallerConstants.GitHubRepo, cancellationToken);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        var asset = release.Assets.FirstOrDefault(asset =>
            asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
            !asset.Name.Contains("bepinex", StringComparison.OrdinalIgnoreCase));

        if (asset is null)
        {
            throw new InvalidOperationException($"Latest NOVR release {release.TagName} does not contain a ZIP asset.");
        }

        return new NovrRelease((ReleaseVersion)release.TagName, asset.Name, asset.BrowserDownloadUrl);
    }

    public Task<string> DownloadNovrReleaseAsync(NovrRelease release, string tempDir, IProgress<string> progress, CancellationToken cancellationToken)
    {
        return DownloadAssetAsync(release.AssetName, release.DownloadUrl, tempDir, progress, release.Version.ToString(), cancellationToken);
    }

    public async Task<string> DownloadLatestBepInEx5Async(string tempDir, IProgress<string> progress, CancellationToken cancellationToken)
    {
        progress.Report("Checking latest BepInEx 5 release...");
        var release = await GetLatestBepInEx5ReleaseAsync(cancellationToken);
        var asset = release.Assets.FirstOrDefault(asset =>
            asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
            asset.Name.Contains("BepInEx", StringComparison.OrdinalIgnoreCase) &&
            asset.Name.Contains("win_x64", StringComparison.OrdinalIgnoreCase));

        if (asset is null)
        {
            throw new InvalidOperationException("Latest BepInEx 5 release does not contain a Windows x64 ZIP asset.");
        }

        return await DownloadAssetAsync(asset.Name, asset.BrowserDownloadUrl, tempDir, progress, release.TagName, cancellationToken);
    }

    private async Task<GitHubRelease> GetLatestReleaseAsync(string owner, string repo, CancellationToken cancellationToken)
    {
        var url = $"https://api.github.com/repos/{owner}/{repo}/releases/latest";
        await using var stream = await _httpClient.GetStreamAsync(url, cancellationToken);
        var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(stream, JsonOptions, cancellationToken);
        return release ?? throw new InvalidOperationException($"Could not read latest release for {owner}/{repo}.");
    }

    private async Task<GitHubRelease> GetLatestBepInEx5ReleaseAsync(CancellationToken cancellationToken)
    {
        var url = $"https://api.github.com/repos/{InstallerConstants.BepInExOwner}/{InstallerConstants.BepInExRepo}/releases?per_page=30";
        await using var stream = await _httpClient.GetStreamAsync(url, cancellationToken);
        var releases = await JsonSerializer.DeserializeAsync<GitHubRelease[]>(stream, JsonOptions, cancellationToken);
        var release = releases?.FirstOrDefault(release => release.TagName.StartsWith("v5.", StringComparison.OrdinalIgnoreCase));
        return release ?? throw new InvalidOperationException("Could not find a BepInEx 5 release.");
    }

    private async Task<string> DownloadAssetAsync(string assetName, string downloadUrl, string tempDir, IProgress<string> progress, string releaseName, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(tempDir);
        var destination = Path.Combine(tempDir, assetName);
        progress.Report($"Downloading {assetName} from release {releaseName}...");

        await using var remote = await _httpClient.GetStreamAsync(downloadUrl, cancellationToken);
        await using var local = File.Create(destination);
        await remote.CopyToAsync(local, cancellationToken);
        return destination;
    }

    private sealed record GitHubRelease(
        [property: System.Text.Json.Serialization.JsonPropertyName("tag_name")]
        string TagName,
        GitHubAsset[] Assets);

    private sealed record GitHubAsset(
        string Name,
        [property: System.Text.Json.Serialization.JsonPropertyName("browser_download_url")]
        string BrowserDownloadUrl);
}
