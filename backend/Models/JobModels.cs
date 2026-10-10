using System.Text.Json.Serialization;

namespace PrPrioritizerService.Models;

public class GitHubPullRequestDto
{
    public int Number { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string HtmlUrl { get; set; } = string.Empty;
    public string DiffUrl { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public string Diff { get; set; } = string.Empty;
    public List<string> ChangedFiles { get; set; } = new List<string>();
}

public enum JobStatus
{
    Queued,
    PrsRetrieved,
    Completed,
    Failed
}

public class PrPrioritizerResult
{
    public int Number { get; set; }
    public string Title { get; set; } = string.Empty;
    public string HtmlUrl { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    
    // Evaluation results from Jev
    public string? Category { get; set; }
    public string? Severity { get; set; }
    public int? PriorityScore { get; set; }
    public bool? RequiresImmediateReview { get; set; }
}

public class SubmitJobRequest
{
    public string? RepoUrl { get; set; }
    public int? MaxPrCount { get; set; }
}

public class SubmitJobResponse
{
    public string JobId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Repo { get; set; } = string.Empty;
    public DateTime SubmittedAt { get; set; }
    public string PollUrl { get; set; } = string.Empty;
}

public class User
{
    public string Uid { get; set; } = string.Empty;
    public string EmailId { get; set; } = string.Empty;
    public List<string> JobIds { get; set; } = new List<string>();
}

public class JobState
{
    public string JobId { get; set; } = string.Empty;
    public string RepoUrl { get; set; } = string.Empty;
    public string Owner { get; set; } = string.Empty;
    public string Repo { get; set; } = string.Empty;
    public int MaxPrCount { get; set; } = 10;
    
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public JobStatus Status { get; set; } = JobStatus.Queued;
    
    public DateTime StartedAt { get; set; }
    public DateTime CompletedAt { get; set; }

    public readonly object _resultsLock = new();
    public Dictionary<string, PrPrioritizerResult> Results { get; set; } = new();
}

public class JobPrData
{
    public string JobId { get; set; } = string.Empty;
    public string RepoUrl { get; set; } = string.Empty;

    public int PrsFound { get; set; }
    public List<GitHubPullRequestDto> RetrievedPrs { get; set; } = new();
}

public class PollJobResponse
{
    public string JobId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Repo { get; set; } = string.Empty;

    public DateTime StartedAt { get; set; }
    public DateTime CompletedAt { get; set; }

    public int PrsFound { get; set; }
    public List<PrPrioritizerResult>? Results { get; set; }
}