using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace PrPrioritizerService.Services;

public interface IJevAnalysisService
{
    Task<object> AnalyzeWithJevAsync(string title, string body, string diff, List<string> changedFiles);
}

public class JevAnalysisService : IJevAnalysisService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<JevAnalysisService> _logger;
    private string _jevApiKey;

    public JevAnalysisService(HttpClient httpClient, ILogger<JevAnalysisService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;

        _jevApiKey = Environment.GetEnvironmentVariable("JEV_API_KEY") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(_jevApiKey) || _jevApiKey.StartsWith("your-", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("[JevService] JEV_API_KEY not configured or using placeholder; falling back to simulated evaluation.");
        }
        else
        {
            _logger.LogInformation("[JevService] Loaded JEV_API_KEY (starts with '{Prefix}...')",
                _jevApiKey.Substring(0, Math.Min(10, _jevApiKey.Length)));
        }
    }

    public async Task<object> AnalyzeWithJevAsync(string title, string body, string diff, List<string> changedFiles)
    {
        string truncatedDiff = diff.Length > 20000 ? diff.Substring(0, 20000) + "\n...[diff truncated]" : diff;

        // Build a richer structured state that gives the model more context about the PR
        string structuredState =
            "[PULL REQUEST CONTEXT]\n" +
            $"Title: {title}\n" +
            $"Description: {body}\n" +
            $"Files Modified ({changedFiles.Count}): {string.Join(", ", changedFiles)}\n\n" +
            "[GIT DIFF PATCH]\n" +
            truncatedDiff +
            "\n[ADDITIONAL METADATA]\n" +
            $"Diff Length: {diff.Length} characters\n" +
            $"Truncated: {diff.Length > 20000}\n";

        var payload = new
        {
            model = "jev-latest",
            state = structuredState,
            // Enriched question set with more detailed instructions and criteria
            questions = new Dictionary<string, object>
            {
                ["category"] = new
                {
                    type = "choice",
                    instructions = "Select the primary category of this pull request. Consider the overall intent and impact.",
                    criteria = new Dictionary<string, string>
                    {
                        ["Security"] = "Authentication, authorization, input validation, encryption, dependency vulnerabilities.",
                        ["Bug"] = "Functional defect fix.",
                        ["Feature"] = "New functionality or enhancement.",
                        ["Refactor"] = "Code structure improvement without behavior change.",
                        ["Docs"] = "Documentation, comments, or markdown edits."
                    }
                },
                ["severity"] = new
                {
                    type = "choice",
                    instructions = "Assess the severity of the change. Higher severity implies greater risk to production.",
                    criteria = new Dictionary<string, string>
                    {
                        ["Critical"] = "Fixes security vulnerabilities, data corruption, severe performance bottlenecks, or production crashes.",
                        ["High"] = "Fixes broken core features or major functional bugs.",
                        ["Medium"] = "Non‑blocking bugs, minor features, UI/UX fixes.",
                        ["Low"] = "Refactoring, code cleanup, documentation updates, dependency bumps, or test additions."
                    }
                },
                ["priority_score"] = new
                {
                    type = "score",
                    instructions = "Rate the overall review priority on a scale from 1 (lowest) to 5 (highest). Consider severity, risk, and business impact.",
                    criteria = new[] { "1", "2", "3", "4", "5" }
                },
                ["requires_immediate_review"] = new
                {
                    type = "noul",
                    instructions = "Does this pull request require immediate emergency review? Answer true if any security or production‑critical issue is present."
                }
            }
        };

        if (string.IsNullOrWhiteSpace(_jevApiKey) || _jevApiKey == "mock" || _jevApiKey == "demo" || _jevApiKey.StartsWith("your-", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("[JevService] No valid JEV_API_KEY detected ('{KeyPreview}'). Using simulated evaluation for PR: '{Title}'",
                string.IsNullOrWhiteSpace(_jevApiKey) ? "EMPTY" : _jevApiKey.Substring(0, Math.Min(8, _jevApiKey.Length)), title);
            return GenerateSimulatedEvaluation(title, changedFiles, truncatedDiff);
        }

        try
        {
            _logger.LogInformation("[JevService] Dispatching POST to https://api.typesafe.ai/v1/systemone for PR: '{Title}'", title);
            
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.typesafe.ai/v1/systemone");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _jevApiKey);
            req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            var res = await _httpClient.SendAsync(req);
            var responseContent = await res.Content.ReadAsStringAsync();

            if (!res.IsSuccessStatusCode)
            {
                _logger.LogWarning("[JevService] Jev API returned HTTP {StatusCode}. Body: {Body}", res.StatusCode, responseContent);
                return new { error = "Jev evaluation failed", statusCode = (int)res.StatusCode, details = responseContent };
            }

            _logger.LogInformation("[JevService] Successfully received response from Jev SystemOne: {Body}", responseContent);

            using var doc = JsonDocument.Parse(responseContent);
            return doc.RootElement.Clone();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[JevService] Exception while invoking Jev evaluation API for PR '{Title}': {Message}", title, ex.Message);
            return new { error = "Jev evaluation exception", message = ex.Message };
        }
    }

    private static object GenerateSimulatedEvaluation(string title, List<string> files, string diff)
    {
        var lower = (title + " " + string.Join(" ", files)).ToLowerInvariant();
        
        string category = "Feature";
        string severity = "Medium";
        int score = 3;
        bool immediate = false;

        if (lower.Contains("security") || lower.Contains("cve") || lower.Contains("auth") || lower.Contains("secret") || lower.Contains("vulnerability"))
        {
            category = "Security";
            severity = "Critical";
            score = 5;
            immediate = true;
        }
        else if (lower.Contains("fix") || lower.Contains("bug") || lower.Contains("crash") || lower.Contains("error") || lower.Contains("patch"))
        {
            category = "Bug";
            severity = lower.Contains("crash") || lower.Contains("critical") ? "High" : "Medium";
            score = severity == "High" ? 4 : 3;
            immediate = severity == "High";
        }
        else if (lower.Contains("refactor") || lower.Contains("clean") || lower.Contains("rename"))
        {
            category = "Refactor";
            severity = "Low";
            score = 2;
            immediate = false;
        }
        else if (lower.Contains("doc") || lower.Contains("readme") || lower.EndsWith(".md"))
        {
            category = "Docs";
            severity = "Low";
            score = 1;
            immediate = false;
        }

        return new
        {
            choices = new Dictionary<string, object>
            {
                ["category"] = new { choice = category },
                ["severity"] = new { choice = severity }
            },
            scores = new Dictionary<string, object>
            {
                ["priority_score"] = new { score = (double)score }
            },
            nouls = new Dictionary<string, object>
            {
                ["requires_immediate_review"] = new { noul = immediate ? 1.0 : 0.0 }
            },
            mode = "simulated"
        };
    }
}
