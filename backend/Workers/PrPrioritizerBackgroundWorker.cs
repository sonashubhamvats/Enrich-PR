using System.Text.Json;
using PrPrioritizerService.Models;
using PrPrioritizerService.Services;

namespace PrPrioritizerService.Workers;

public class PrPrioritizerBackgroundWorker : BackgroundService
{
    private readonly IBackgroundTaskQueue _taskQueue;
    private readonly IJobStateRepository _stateRepository;
    private readonly IGitHubService _gitHubService;
    private readonly IJevAnalysisService _jevService;
    private readonly IBackgroundJevQueryTaskQueue _jevQueue;
    private readonly ILogger<PrPrioritizerBackgroundWorker> _logger;

    public PrPrioritizerBackgroundWorker(
        IBackgroundTaskQueue taskQueue,
        IJobStateRepository stateRepository,
        IBackgroundJevQueryTaskQueue jevQueue,
        IGitHubService gitHubService,
        IJevAnalysisService jevService,
        ILogger<PrPrioritizerBackgroundWorker> logger)
    {
        _taskQueue = taskQueue;
        _stateRepository = stateRepository;
        _gitHubService = gitHubService;
        _jevService = jevService;
        _jevQueue = jevQueue;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[BackgroundWorker] PrPrioritizerBackgroundWorker loop started, awaiting jobs...");

        await this.RestoreQueue(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var jobId = await _taskQueue.DequeueAsync(stoppingToken);
                _logger.LogInformation("[BackgroundWorker] Dequeued Job {JobId} from channel queue.", jobId);
                await ProcessJobAsync(jobId, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("[BackgroundWorker] Cancellation requested. Shuttindg own worker loop.");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[BackgroundWorker] Unexpected exception in worker dispatch loop.");
            }
        }

        _logger.LogInformation("[BackgroundWorker] PrPrioritizerBackgroundWorker loop terminated.");
    }

    private async Task ProcessJobAsync(string jobId, CancellationToken cancellationToken)
    {
        var job = await _stateRepository.GetJob(jobId);
        if (job == null)
        {
            _logger.LogWarning("[BackgroundWorker] Job {JobId} was not found in state store. Skipping.", jobId);
            return;
        }

        if (job.Status == JobStatus.Completed)
        {
            _logger.LogWarning($"[BackgroundWorker] Job {jobId} is already completed or failed. Skipping.");
            return;
        }

        if (job.Status == JobStatus.Failed)
        {
            _logger.LogWarning($"[BackgroundWorker] Job {jobId} is failed, retrying");

            await _stateRepository.PurgeJob(jobId);
            await _stateRepository.UpdateStatus(jobId, JobStatus.Queued);
        }

        _logger.LogInformation("[BackgroundWorker] Starting execution of Job {JobId} for repo '{Owner}/{Repo}'...",
            jobId, job.Owner, job.Repo);

        try
        {
            var jobPrData = await _stateRepository.GetJobPrData(jobId);

            if (jobPrData?.RetrievedPrs != null && jobPrData.RetrievedPrs.Any())
            {
                _logger.LogInformation("[BackgroundWorker] PRs already retrieved passing the job to the other queue");
                if (job.Status != JobStatus.PrsRetrieved)
                {
                    await _stateRepository.UpdateStatus(jobId, JobStatus.PrsRetrieved);
                }
                
                await _jevQueue.QueueBackgroundWorkItemAsync(jobId, cancellationToken);
                return;
            }

            _logger.LogInformation("[BackgroundWorker] Contacting GitHub to fetch open PRs for '{Owner}/{Repo}' (Max: {MaxPrCount})...",
                job.Owner, job.Repo, job.MaxPrCount);
            
            var pullRequests = await _gitHubService.GetOpenPullRequestsAsync(job.Owner, job.Repo, job.MaxPrCount, cancellationToken);

            if (pullRequests.Count == 0)
            {   
                _logger.LogInformation("[BackgroundWorker] No open pull requests found for '{Owner}/{Repo}'. Completing job immediately.",
                    job.Owner, job.Repo);
                await _stateRepository.CompleteJob(jobId);
                return;
            }

            _logger.LogInformation("[BackgroundWorker] GitHub returned {Count} open PR(s) for '{Owner}/{Repo}' [Job: {JobId}]. PR Numbers: [{PrList}]",
                pullRequests.Count, job.Owner, job.Repo, jobId, string.Join(", ", pullRequests.Select(p => p.Number)));

            int currentIndex = 0;
            var parallelOptions = new ParallelOptions 
            { 
                MaxDegreeOfParallelism = 10,
                CancellationToken = cancellationToken 
            };

            await Parallel.ForEachAsync(pullRequests, parallelOptions, async (pr, ct) =>
            {
                var current = System.Threading.Interlocked.Increment(ref currentIndex);

                _logger.LogInformation("[BackgroundWorker] [{Current}/{Total}] Fetching details for PR #{PrNumber}: '{Title}'...",
                    current, pullRequests.Count, pr.Number, pr.Title);

                var (diff, changedFiles) = await _gitHubService.GetPullRequestDetailsAsync(job.Owner, job.Repo, pr.Number, ct);

                _logger.LogInformation("[BackgroundWorker] [{Current}/{Total}] PR #{PrNumber} fetched. Files: {FileCount}, Diff length: {DiffLength} chars. Invoking Jev AI...",
                    current, pullRequests.Count, pr.Number, changedFiles.Count, diff.Length);

                pr.Diff = diff;
                pr.ChangedFiles = changedFiles;                
            });

            await _stateRepository.SetRetrievedPrs(jobId, pullRequests);
            await _stateRepository.UpdateStatus(jobId, JobStatus.PrsRetrieved);

            _logger.LogInformation($"[BackgroundWorker] Sending the job {jobId} to the JEB Queue");
            await _jevQueue.QueueBackgroundWorkItemAsync(jobId, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[BackgroundWorker] FATAL: Job {JobId} failed with exception: {Message}", jobId, ex.Message);
            await _stateRepository.FailJob(jobId);
            await _taskQueue.QueueBackgroundWorkItemAsync(job.JobId, cancellationToken);
        }
    }

    private async Task RestoreQueue(CancellationToken token)
    {
        var allActiveJobs = await _stateRepository.GetActiveJobsAsync();

        allActiveJobs = allActiveJobs.Where(job => job.Status == JobStatus.Queued);

        var jobIds = allActiveJobs.Select(job => job.JobId).ToList();
        var allJobPrData = await _stateRepository.GetJobPrDatas(jobIds);

        foreach (var job in allActiveJobs)
        {
            _logger.LogInformation($"[BackgroundWorker] Restoring {job.JobId}");
            _stateRepository.LoadJobToLocalDictionary(job.JobId, job);

            if (allJobPrData.ContainsKey(job.JobId))
                _stateRepository.LoadJobPrDataToLocalDictionary(job.JobId, allJobPrData[job.JobId]);

            await _taskQueue.QueueBackgroundWorkItemAsync(job.JobId, token);
        }
    }
}
