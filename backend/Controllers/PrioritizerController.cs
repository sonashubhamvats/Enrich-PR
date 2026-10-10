using Microsoft.AspNetCore.Mvc;
using System.Threading.Tasks;
using PrPrioritizerService.Models;
using PrPrioritizerService.Services;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace PrPrioritizerService.Controllers;

[ApiController]
[Route("api/prioritizer")]
[Authorize]
public class PrioritizerController : ControllerBase
{
    private readonly IJobStateRepository _stateRepository;
    private readonly IBackgroundTaskQueue _backgroundTaskQueue;
    private readonly IGitHubService _gitHubService;
    private readonly ILogger<PrioritizerController> _logger;

    public PrioritizerController(
        IJobStateRepository stateRepository,
        IBackgroundTaskQueue backgroundTaskQueue,
        IGitHubService gitHubService,
        ILogger<PrioritizerController> logger)
    {
        _stateRepository = stateRepository;
        _backgroundTaskQueue = backgroundTaskQueue;
        _gitHubService = gitHubService;
        _logger = logger;
    }

    /// <summary>
    /// SubmitRequestApiPrioritizer: Enqueues a GitHub repository PR prioritization job on the stateful service.
    /// </summary>
    [HttpPost("submit")]
    [ProducesResponseType(typeof(SubmitJobResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SubmitRequestApiPrioritizer([FromBody] SubmitJobRequest request)
    {
        // Extract Firebase User Information from JWT Claims
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var userEmail = User.FindFirst(ClaimTypes.Email)?.Value;

        _logger.LogInformation("[SubmitRequest] Received submission request. Raw URL: '{RepoUrl}', MaxPrCount: {MaxPrCount}",
            request?.RepoUrl, request?.MaxPrCount);

        if (request == null || string.IsNullOrWhiteSpace(request.RepoUrl))
        {
            _logger.LogWarning("[SubmitRequest] Validation rejected: RepoUrl was empty or null.");
            return BadRequest(new { error = "RepoUrl is required. Example: 'octocat/Hello-World' or 'https://github.com/owner/repo'" });
        }

        string owner, repo;
        try
        {
            (owner, repo) = _gitHubService.ParseRepoUrl(request.RepoUrl);
            _logger.LogInformation("[SubmitRequest] Successfully parsed target -> Owner: '{Owner}', Repo: '{Repo}'", owner, repo);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "[SubmitRequest] Failed parsing repo URL '{RepoUrl}': {Message}", request.RepoUrl, ex.Message);
            return BadRequest(new { error = ex.Message });
        }

        int maxPrs = request.MaxPrCount.GetValueOrDefault(10);
        if (maxPrs <= 0) maxPrs = 1;
        if (maxPrs > 50) maxPrs = 50;

        string jobId = Guid.NewGuid().ToString("N");

        // 1. Persist initial details to WAL repository (Status: NotSubmitted)
        var job = await _stateRepository.CreateJobWithId(jobId, request.RepoUrl, owner, repo, maxPrs);
        _logger.LogInformation("[SubmitRequest] Created Job {JobId} in local WAL repository with Status {Status}", jobId, job.Status);

        // 2. Enqueue to background task queue for background creation in Firebase
        await _backgroundTaskQueue.QueueBackgroundWorkItemAsync(jobId);
        _logger.LogInformation("[SubmitRequest] Job {JobId} successfully enqueued into background task queue.", jobId);

        // 3. Persist the job id to user
        await _stateRepository.AddOrUpdateJobIdToUser(userId, userEmail, jobId);
        _logger.LogInformation("[SubmitRequest] Added Job {JobId} to user {UserId}", jobId, userId);

        var pollUrl = $"{Request.Scheme}://{Request.Host}/api/prioritizer/poll/{jobId}";

        return Accepted(pollUrl, new SubmitJobResponse
        {
            JobId = jobId,
            Status = JobStatus.Queued.ToString(),
            Repo = $"{owner}/{repo}",
            SubmittedAt = job.StartedAt,
            PollUrl = pollUrl
        });
    }

    /// <summary>
    /// PollRequestApiPrioritizer: Polls the state and progress of the job.
    /// </summary>
    [HttpGet("poll/{jobId}")]
    [ProducesResponseType(typeof(PollJobResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> PollRequestApiPrioritizer([FromRoute] string jobId)
    {
        var job = await _stateRepository.GetJob(jobId);
        if (job == null)
        {
            _logger.LogWarning("[PollRequest] Job {JobId} was not found in state store", jobId);
            return NotFound(new { error = $"Job with ID '{jobId}' not found." });
        }

        _logger.LogInformation("[PollRequest] Polling Job {JobId} -> Status: {Status} (Processed: {Processed})",
            job.JobId, job.Status, job.Results?.Count);

        var response = new PollJobResponse
        {
            JobId = job.JobId,
            Status = job.Status.ToString(),
            Repo = $"{job.Owner}/{job.Repo}",
            StartedAt = job.StartedAt,
            CompletedAt = job.CompletedAt,
            Results = job.Results.Values.ToList()
        };

        return Ok(response);
    }

    /// <summary>
    /// Helper endpoint to view all active and historical jobs in the stateful service
    /// </summary>
    [HttpGet("jobs")]
    public async Task<IActionResult> ListAllJobs()
    {
        var jobs = await _stateRepository.GetAllJobs();
        _logger.LogInformation("[ListAllJobs] Returning {Count} tracked jobs.", jobs.Count());
        return Ok(jobs);
    }

    /// <summary>
    /// Returns all jobs associated with a given user ID.
    /// </summary>
    [HttpGet("jobs/user/{userId}")]
    public async Task<IActionResult> GetJobsByUser([FromRoute] string userId)
    {
        var user = await _stateRepository.GetUserWithIdAsync(userId);
        if (user == null)
        {
            _logger.LogWarning("[GetJobsByUser] User {UserId} not found.", userId);
            return NotFound(new { error = "User not found" });
        }

        var jobs = new List<JobState>();
        foreach (var jobId in user.JobIds ?? new List<string>())
        {
            var job = await _stateRepository.GetJob(jobId);
            if (job != null)
            {
                jobs.Add(job);
            }
        }

        // Keep only the most recent job per repository URL
        var latestJobs = jobs
            .GroupBy(j => j.RepoUrl)
            .Select(g => g.OrderByDescending(j => j.StartedAt).First())
            .ToList();

        _logger.LogInformation("[GetJobsByUser] Returning {Count} latest jobs for user {UserId}.", latestJobs.Count, userId);
        return Ok(latestJobs);
    }

    // New endpoint to list active jobs (status not Queued or Processing)
    [HttpGet("jobs/active")]
    public async Task<IActionResult> ListActiveJobs()
    {
        var jobs = await _stateRepository.GetActiveJobsAsync();
        var count = jobs?.Count() ?? 0;
        _logger.LogInformation("[ListActiveJobs] Returning {Count} active jobs.", count);
        return Ok(jobs);
    }
}