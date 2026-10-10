using System.Collections.Concurrent;
using PrPrioritizerService.Models;

namespace PrPrioritizerService.Services;

public interface IJobStateRepository
{
    Task<JobState> CreateJob(string repoUrl, string owner, string repo, int maxPrCount);
    Task<JobState> CreateJobWithId(string jobId, string repoUrl, string owner, string repo, int maxPrCount);
    Task<JobState> GetJob(string jobId);
    Task UpdateStatus(string jobId, JobStatus status);
    Task SetRetrievedPrs(string jobId, List<GitHubPullRequestDto> prs);
    Task AddPrResult(string jobId, PrPrioritizerResult result);
    Task CompleteJob(string jobId);
    Task FailJob(string jobId);
    Task<IEnumerable<JobState>> GetAllJobs();
    Task<IEnumerable<JobState>> GetActiveJobsAsync();
    void LoadJobToLocalDictionary(string jobId, JobState jobState);
    Task<User> GetUserWithIdAsync(string userId);
    Task AddUserAsync(User user);
    Task AddJobIdToUser(string userId, string jobId);
    Task AddOrUpdateJobIdToUser(string userId, string emailId, string jobId);
    Task PurgeJob(string jobId);
    Task<JobPrData> GetJobPrData(string jobId);
    Task<Dictionary<string, JobPrData>> GetJobPrDatas(List<string> jobIds);
    void LoadJobPrDataToLocalDictionary(string jobId, JobPrData jobPrData);
}