using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using PrPrioritizerService.Models;

namespace PrPrioritizerService.Services;

public interface IGitHubService
{
    (string owner, string repo) ParseRepoUrl(string inputUrl);
    Task<List<GitHubPullRequestDto>> GetOpenPullRequestsAsync(string owner, string repo, int maxCount, CancellationToken cancellationToken);
    Task<(string diff, List<string> changedFiles)> GetPullRequestDetailsAsync(string owner, string repo, int prNumber, CancellationToken cancellationToken);
}

public class GitHubService : IGitHubService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<GitHubService> _logger;

    public GitHubService(HttpClient httpClient, ILogger<GitHubService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;

        _httpClient.BaseAddress = new Uri("https://api.github.com/");
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "PrPrioritizerService-Agent");
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.v3+json"));

        var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN") ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(token))
        {
            _logger.LogInformation("[GitHubService] Initialized with GitHub Bearer token (ending in '...{Suffix}')",
                token.Substring(Math.Max(0, token.Length - 4)));
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        else
        {
            _logger.LogWarning("[GitHubService] No valid GITHUB_TOKEN detected; running in unauthenticated mode (rate limit: 60 req/hr).");
        }
    }

    public (string owner, string repo) ParseRepoUrl(string inputUrl)
    {
        if (string.IsNullOrWhiteSpace(inputUrl))
        {
            throw new ArgumentException("Repository URL cannot be empty.", nameof(inputUrl));
        }

        var cleaned = inputUrl.Trim().TrimEnd('/');
        var match = Regex.Match(cleaned, @"(?:https?:\/\/github\.com\/)?([a-zA-Z0-9_.-]+)\/([a-zA-Z0-9_.-]+)$");
        if (!match.Success)
        {
            throw new ArgumentException($"Invalid GitHub repository format: '{inputUrl}'. Expected 'owner/repo' or 'https://github.com/owner/repo'.");
        }

        return (match.Groups[1].Value, match.Groups[2].Value);
    }

    public async Task<List<GitHubPullRequestDto>> GetOpenPullRequestsAsync(string owner, string repo, int maxCount, CancellationToken cancellationToken)
    {
        var url = $"repos/{owner}/{repo}/pulls?state=open&per_page={Math.Clamp(maxCount, 1, 100)}";
        _logger.LogInformation("[GitHubService] Requesting open PRs: GET https://api.github.com/{Url}", url);
        
        var response = await _httpClient.GetAsync(url, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("[GitHubService] Failed to fetch PRs for {Owner}/{Repo}. Status: {StatusCode}, Error: {Error}",
                owner, repo, response.StatusCode, err);
            throw new HttpRequestException($"GitHub API returned {response.StatusCode} for repo '{owner}/{repo}': {err}");
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);

        var list = new List<GitHubPullRequestDto>();
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            list.Add(new GitHubPullRequestDto
            {
                Number = el.GetProperty("number").GetInt32(),
                Title = el.GetProperty("title").GetString() ?? string.Empty,
                Body = el.TryGetProperty("body", out var b) ? b.GetString() ?? string.Empty : string.Empty,
                HtmlUrl = el.GetProperty("html_url").GetString() ?? string.Empty,
                DiffUrl = el.GetProperty("diff_url").GetString() ?? string.Empty,
                Author = el.GetProperty("user").GetProperty("login").GetString() ?? "unknown",
                CreatedAt = el.GetProperty("created_at").GetDateTime()
            });

            if (list.Count >= maxCount) break;
        }

        _logger.LogInformation("[GitHubService] Parsed {Count} PR(s) from GitHub response for {Owner}/{Repo}", list.Count, owner, repo);
        return list;
    }

    public async Task<(string diff, List<string> changedFiles)> GetPullRequestDetailsAsync(string owner, string repo, int prNumber, CancellationToken cancellationToken)
    {
        string diff = string.Empty;
        var changedFiles = new List<string>();

        // 1. Fetch diff
        var diffEndpoint = $"repos/{owner}/{repo}/pulls/{prNumber}";
        _logger.LogInformation("[GitHubService] Fetching diff for PR #{PrNumber}: GET https://api.github.com/{Endpoint} (Accept: vnd.github.v3.diff)",
            prNumber, diffEndpoint);

        using (var diffReq = new HttpRequestMessage(HttpMethod.Get, diffEndpoint))
        {
            diffReq.Headers.Accept.Clear();
            diffReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.v3.diff"));
            var diffRes = await _httpClient.SendAsync(diffReq, cancellationToken);
            if (diffRes.IsSuccessStatusCode)
            {
                var largeDiff = await diffRes.Content.ReadAsStringAsync(cancellationToken);
                diff = largeDiff.Substring(0, Math.Min(largeDiff.Length, 4000));
                _logger.LogInformation("[GitHubService] Diff received for PR #{PrNumber}: {Length} bytes", prNumber, diff.Length);
            }
            else
            {
                _logger.LogWarning("[GitHubService] Failed to fetch diff for PR #{PrNumber}. Status: {StatusCode}", prNumber, diffRes.StatusCode);
            }
        }

        // 2. Fetch list of modified files
        try
        {
            var filesEndpoint = $"repos/{owner}/{repo}/pulls/{prNumber}/files?per_page=50";
            _logger.LogInformation("[GitHubService] Fetching modified files for PR #{PrNumber}: GET https://api.github.com/{Endpoint}",
                prNumber, filesEndpoint);

            var filesRes = await _httpClient.GetAsync(filesEndpoint, cancellationToken);
            if (filesRes.IsSuccessStatusCode)
            {
                var filesJson = await filesRes.Content.ReadAsStringAsync(cancellationToken);
                using var filesDoc = JsonDocument.Parse(filesJson);
                foreach (var f in filesDoc.RootElement.EnumerateArray())
                {
                    if (f.TryGetProperty("filename", out var fn))
                    {
                        var name = fn.GetString();
                        if (!string.IsNullOrEmpty(name)) changedFiles.Add(name);
                    }
                }
                _logger.LogInformation("[GitHubService] Retrieved {Count} changed file name(s) for PR #{PrNumber}",
                    changedFiles.Count, prNumber);
            }
            else
            {
                _logger.LogWarning("[GitHubService] Failed fetching changed files for PR #{PrNumber}. Status: {StatusCode}",
                    prNumber, filesRes.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[GitHubService] Exception loading files list for PR #{PrNumber}: {Message}", prNumber, ex.Message);
        }

        return (diff, changedFiles);
    }
}
