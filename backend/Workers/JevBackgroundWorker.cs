using System.Text.Json;
using PrPrioritizerService.Models;
using PrPrioritizerService.Services;

namespace PrPrioritizerService.Workers;

public class JevBackgroundWorker : BackgroundService
{
    private readonly IBackgroundTaskQueue _taskQueue;
    private readonly IJobStateRepository _stateRepository;
    private readonly IJevAnalysisService _jevService;
    private readonly IBackgroundJevQueryTaskQueue _jevQueue;
    private readonly ILogger<PrPrioritizerBackgroundWorker> _logger;

    public JevBackgroundWorker(
        IJobStateRepository stateRepository,
        IBackgroundTaskQueue taskQueue,
        IBackgroundJevQueryTaskQueue jevQueue,
        IJevAnalysisService jevService,
        ILogger<PrPrioritizerBackgroundWorker> logger)
    {
        _stateRepository = stateRepository;
        _jevService = jevService;
        _jevQueue = jevQueue;
        _logger = logger;
        _taskQueue = taskQueue;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[JevBackgroundWorker] loop started, awaiting jobs...");

        await this.RestoreQueue(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var jobId = await _jevQueue.DequeueAsync(stoppingToken);
                _logger.LogInformation("[JevBackgroundWorker] Dequeued Job {JobId} from channel queue.", jobId);
                await ProcessJobAsync(jobId, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("[JevBackgroundWorker] Cancellation requested. Shuttindg own worker loop.");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[JevBackgroundWorker] Unexpected exception in worker dispatch loop.");
            }
        }

        _logger.LogInformation("[JevBackgroundWorker] loop terminated.");
    }

    private async Task ProcessJobAsync(string jobId, CancellationToken cancellationToken)
    {
        var job = await _stateRepository.GetJob(jobId);
        if (job == null)
        {
            _logger.LogWarning("[JevBackgroundWorker] Job {JobId} was not found in state store. Skipping.", jobId);
            return;
        }

        if (job.Status == JobStatus.Completed)
        {
            _logger.LogWarning($"[JevBackgroundWorker] Job {jobId} is already completed. Skipping.");
            return;
        }

        if (job.Status == JobStatus.Failed)
        {
            _logger.LogWarning($"[JevBackgroundWorker] Job {jobId} is failed, retrying");
            await _taskQueue.QueueBackgroundWorkItemAsync(job.JobId, cancellationToken);
            return;
        }

        _logger.LogInformation("[JevBackgroundWorker] Starting execution of Job {JobId} for repo '{Owner}/{Repo}'...",
            jobId, job.Owner, job.Repo);

        try
        {
            var jobPrData = await _stateRepository.GetJobPrData(jobId);

            var pullRequests = jobPrData?.RetrievedPrs;

            if (pullRequests == null || !pullRequests.Any())
            {
                _logger.LogInformation("[JevBackgroundWorker] No PRs detected returning without analysing");
                await _stateRepository.CompleteJob(jobId);

                return;
            }
            
            // Analyze each PR concurrently
            var parallelOptions = new ParallelOptions 
            { 
                MaxDegreeOfParallelism = 10,
                CancellationToken = cancellationToken 
            };

            await Parallel.ForEachAsync(pullRequests, parallelOptions, async (pr, ct) =>
            {
                // Call Jev AI analysis
                _logger.LogInformation($"[JevBackgroundWorker] Analyzing the PR - {pr.Title}");
                var rawResult = await _jevService.AnalyzeWithJevAsync(pr.Title, pr.Body, pr.Diff, pr.ChangedFiles);

                var prResult = new PrPrioritizerResult
                {
                    Number = pr.Number,
                    Title = pr.Title,
                    HtmlUrl = pr.HtmlUrl,
                    Author = pr.Author,
                    CreatedAt = pr.CreatedAt
                };

                ExtractEvaluationFields(rawResult, prResult);

                await _stateRepository.AddPrResult(jobId, prResult);

                _logger.LogInformation("[JevBackgroundWorker] Completed PR #{PrNumber} -> Category: '{Category}', Severity: '{Severity}', Score: {Score}/5, ImmediateReview: {Immediate}",
                    pr.Number,
                    prResult.Category ?? "N/A",
                    prResult.Severity ?? "N/A",
                    prResult.PriorityScore?.ToString() ?? "N/A",
                    prResult.RequiresImmediateReview?.ToString() ?? "N/A");
            });

            await _stateRepository.CompleteJob(jobId);
            _logger.LogInformation("[JevBackgroundWorker] Job {JobId} for '{Owner}/{Repo}' completed successfully. All PRs evaluated.",
                jobId, job.Owner, job.Repo);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[JevBackgroundWorker] FATAL: Job {JobId} failed with exception: {Message}", jobId, ex.Message);
            await _stateRepository.FailJob(jobId);
            await _taskQueue.QueueBackgroundWorkItemAsync(job.JobId, cancellationToken);
        }
    }

    private void ExtractEvaluationFields(object rawResult, PrPrioritizerResult target)
    {
        try
        {
            var json = JsonSerializer.Serialize(rawResult);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Single extraction path: parse from 'answers'
            if (root.TryGetProperty("answers", out var answers))
            {
                // Format A: answers as dictionary { "category": { ... }, "severity": { ... } }
                if (answers.ValueKind == JsonValueKind.Object)
                {
                    if (answers.TryGetProperty("category", out var cat))
                    {
                        if (cat.TryGetProperty("choice", out var c)) target.Category = c.GetString();
                    }

                    if (answers.TryGetProperty("severity", out var sev))
                    {
                        if (sev.TryGetProperty("choice", out var c)) target.Severity = c.GetString();
                    }

                    if (answers.TryGetProperty("priority_score", out var score))
                    {
                        if (score.TryGetProperty("score", out var sc) && sc.ValueKind == JsonValueKind.Number)
                        {
                            var rounded = Math.Round(sc.GetDouble());
                            target.PriorityScore = Math.Clamp((int)rounded, 1, 5);
                        }
                    }

                    if (answers.TryGetProperty("requires_immediate_review", out var noul))
                    {
                        if (noul.TryGetProperty("noul", out var n) && n.ValueKind == JsonValueKind.Number)
                            target.RequiresImmediateReview = n.GetDouble() >= 0.5;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[JevBackgroundWorker][ExtractEvaluation] Could not parse answers; falling back to null/default: {Message}", ex.Message);
        }
    }

    private async Task RestoreQueue(CancellationToken token)
    {
        var allActiveJobs = await _stateRepository.GetActiveJobsAsync();

        allActiveJobs = allActiveJobs.Where(job => job.Status == JobStatus.PrsRetrieved);

        var jobIds = allActiveJobs.Select(job => job.JobId).ToList();
        var allJobPrData = await _stateRepository.GetJobPrDatas(jobIds);

        foreach (var job in allActiveJobs)
        {
            _logger.LogInformation($"[JevBackgroundWorker] Restoring {job.JobId}");

            _stateRepository.LoadJobToLocalDictionary(job.JobId, job);

            _stateRepository.LoadJobPrDataToLocalDictionary(job.JobId, allJobPrData[job.JobId]);

            await _jevQueue.QueueBackgroundWorkItemAsync(job.JobId, token);
        }
    }
}
