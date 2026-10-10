using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PrPrioritizerService.Models;
using PrPrioritizerService.Services;

namespace PrPrioritizerService.Services
{
    public class FirebaseJobStateRepository : IJobStateRepository
    {
        private readonly ConcurrentDictionary<string, JobState> _jobs = new();
        private readonly ConcurrentDictionary<string, JobPrData> _jobsPrData = new();
        private readonly HttpClient _httpClient;
        private readonly ILogger<FirebaseJobStateRepository> _logger;
        private readonly string _databaseUrl;
        private GoogleCredential _credential;
        private static readonly object _tokenLock = new object();
        
        public FirebaseJobStateRepository(IConfiguration configuration, ILogger<FirebaseJobStateRepository> logger)
        {
            _logger = logger;
            _httpClient = new HttpClient();
            
            var firebaseSection = configuration.GetSection("Firebase");
            _databaseUrl = firebaseSection["DatabaseUrl"]?.TrimEnd('/') ?? throw new Exception("DatabaseUrl missing in configuration.");
            
            // Load credentials **only** from environment variable
            var credentialsJson = Environment.GetEnvironmentVariable("FIREBASE_CREDENTIALS_JSON");
            if (string.IsNullOrEmpty(_databaseUrl) || string.IsNullOrEmpty(credentialsJson))
            {
                throw new Exception("Firebase configuration is missing in environment variables.");
            }
            
            var credentialFactory = CredentialFactory.FromJson<ServiceAccountCredential>(credentialsJson);
            _credential = credentialFactory.ToGoogleCredential()
                .CreateScoped(
                    "https://www.googleapis.com/auth/userinfo.email",
                    "https://www.googleapis.com/auth/firebase.database");
        }

        private async Task<string> GetAccessTokenAsync()
        {
            var token = await ((ITokenAccess)_credential).GetAccessTokenForRequestAsync();
            return token;
        }

        public async Task<T?> GetValueAsync<T>(string relativePath)
        {
            try
            {
                var token = await GetAccessTokenAsync();
                var url   = $"{_databaseUrl}/{relativePath}.json";
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                var response = await _httpClient.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    var err = await response.Content.ReadAsStringAsync();
                    throw new Exception(
                        $"Failed to fetch '{relativePath}' (status {response.StatusCode}): {err}");
                }
                var payload = await response.Content.ReadAsStringAsync();
                if (string.IsNullOrWhiteSpace(payload) || payload.Trim() == "null")
                    return default;
                var options = new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    PropertyNameCaseInsensitive = true
                };
                return JsonSerializer.Deserialize<T>(payload, options);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Error fetching value from Firebase (path: {Path})", relativePath);
                return default;
            }
        }


        public async Task<Dictionary<string, T>> GetDictionaryAsync<T>(string relativePath)
        {
            var result = await GetValueAsync<Dictionary<string, T>>(relativePath);
            return result ?? new Dictionary<string, T>();
        }


        public async Task<Dictionary<string, T>> TryGetDictionaryAsync<T>(string relativePath)
        {
            try
            {
                return await GetDictionaryAsync<T>(relativePath);
            }
            catch
            {
                return new Dictionary<string, T>();
            }
        }

        private async Task SendToFirebaseAsync(HttpMethod method, string path, object data)
        {
            var token = await GetAccessTokenAsync();
            var url = $"{_databaseUrl}/{path}.json";
            
            var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            
            if (data != null)
            {
                var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }

            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                _logger.LogError("Firebase error ({StatusCode}): {Error}", response.StatusCode, error);
                throw new Exception($"Failed to save to Firebase: {error}");
            }
        }

        public void LoadJobToLocalDictionary(string jobId, JobState jobState)
        {
            _jobs[jobId] = jobState;
        }

        public void LoadJobPrDataToLocalDictionary(string jobId, JobPrData jobPrData)
        {
            _jobsPrData[jobId] = jobPrData;
        }

        public async Task<JobState> CreateJob(string repoUrl, string repoOwner, string repoName, int maxPrsToProcess)
        {
            return await CreateJobWithId(Guid.NewGuid().ToString("N"), repoUrl, repoOwner, repoName, maxPrsToProcess);
        }

        public async Task<JobState> CreateJobWithId(string jobId, string repoUrl, string repoOwner, string repoName, int maxPrsToProcess)
        {
            var job = new JobState
            {
                JobId = jobId,
                RepoUrl = repoUrl,
                Owner = repoOwner,
                Repo = repoName,
                MaxPrCount = maxPrsToProcess,
                Status = JobStatus.Queued,
                StartedAt = DateTime.UtcNow
            };
                                
            await SendToFirebaseAsync(HttpMethod.Put, $"jobs/{job.JobId}", job);
            _jobs[job.JobId] = job;
            return job;
        }

        public async Task<JobState> GetJob(string jobId)
        {
            _jobs.TryGetValue(jobId, out var job);

            if (job == null)
            {
                job = await this.GetValueAsync<JobState>($"jobs/{jobId}");
            }

            return job;
        }

        public async Task<JobPrData> GetJobPrData(string jobId)
        {
            _jobsPrData.TryGetValue(jobId, out var jobPrData);

            if (jobPrData == null)
            {
                jobPrData = await this.GetValueAsync<JobPrData>($"jobsPrData/{jobId}");
            }

            return jobPrData;
        }

        public async Task<Dictionary<string, JobPrData>> GetJobPrDatas(List<string> jobIds)
        {
            var jobPrDatas = new Dictionary<string, JobPrData>();

            foreach (var jobId in jobIds)
            {
                var jobPrData = await GetJobPrData(jobId);

                if (jobPrData != null)
                {
                    jobPrDatas.Add(jobId, jobPrData);
                }
            }

            return jobPrDatas;
        }

        public async Task<IEnumerable<JobState>> GetAllJobs()
        {
            var allJobs = await this.GetValueAsync<Dictionary<string, JobState>>("jobs");
            return allJobs.Values;
        }

        public async Task<IEnumerable<JobState>> GetActiveJobsAsync()
        {
            var relativePath = "jobs";
            var dict = await this.TryGetDictionaryAsync<JobState>(relativePath);
            if (dict == null) return Enumerable.Empty<JobState>();

            // Exclude Completed and Failed statuses
            return dict.Values.Where(j => j.Status != JobStatus.Completed && j.Status != JobStatus.Failed);
        }

        public async Task<User> GetUserWithIdAsync(string userId)
        {
            var relativePath = $"users/{userId}";
            return await GetValueAsync<User>(relativePath);
        }

        public async Task AddUserAsync(User user)
        {
            var relativePath = "users";
            await SendToFirebaseAsync(HttpMethod.Put, $"{relativePath}/{user.Uid}", user);
        }

        public async Task AddJobIdToUser(string userId, string jobId)
        {
            var existing = await GetValueAsync<List<string>>($"users/{userId}/jobIds");
            var jobIds = existing ?? new List<string>();

            if (!jobIds.Contains(jobId))
                jobIds.Add(jobId);

            await SendToFirebaseAsync(HttpMethod.Put, $"users/{userId}/jobIds", jobIds);
        }

        public async Task AddOrUpdateJobIdToUser(string userId, string emailId, string jobId)
        {
            var user = await GetUserWithIdAsync(userId);
            if (user == null)
            {
                var newUser = new User
                {
                    Uid = userId,
                    EmailId = emailId,
                    JobIds = new List<string>()
                    {
                        jobId
                    }
                };

                await AddUserAsync(newUser);
            }
            else
            {
                await this.AddJobIdToUser(userId, jobId);
            }
        }

        public async Task UpdateStatus(string jobId, JobStatus status)
        {
            if (_jobs.TryGetValue(jobId, out var job))
            {
                var update = new { status = status.ToString() };
                await SendToFirebaseAsync(HttpMethod.Patch, $"jobs/{jobId}", update);
                
                job.Status = status;
            }
        }

        public async Task SetRetrievedPrs(string jobId, List<GitHubPullRequestDto> prs)
        {
            if (_jobs.TryGetValue(jobId, out var job))
            {
                var prsHere = prs ?? new List<GitHubPullRequestDto>();

                var jobPrData = new JobPrData
                {
                    JobId = jobId,
                    RepoUrl = job.RepoUrl,
                    PrsFound = prsHere.Count,
                    RetrievedPrs = prsHere
                };

                await SendToFirebaseAsync(HttpMethod.Put, $"jobsPrData/{jobId}", jobPrData);
                
                _jobsPrData[jobId] = jobPrData;
            }
        }


        

        public async Task AddPrResult(string jobId, PrPrioritizerResult result)
        {
            if (_jobs.TryGetValue(jobId, out var job))
            {
                string resultId = Guid.NewGuid().ToString("N");
                await SendToFirebaseAsync(HttpMethod.Put, $"jobs/{jobId}/results/{resultId}", result);

                lock(job._resultsLock)
                {
                    job.Results.Add(resultId, result);
                }
            }
        }

        public async Task CompleteJob(string jobId)
        {
            if (_jobs.TryGetValue(jobId, out var job))
            {
                var now = DateTime.UtcNow;
                var update = new { status = JobStatus.Completed.ToString(), completedAt = now };
                await SendToFirebaseAsync(HttpMethod.Patch, $"jobs/{jobId}", update);
                
                job.Status = JobStatus.Completed;
                job.CompletedAt = now;
            }
        }

        public async Task FailJob(string jobId)
        {
            if (_jobs.TryGetValue(jobId, out var job))
            {
                var now = DateTime.UtcNow;
                var update = new { status = JobStatus.Failed.ToString(), completedAt = now };
                await SendToFirebaseAsync(HttpMethod.Patch, $"jobs/{jobId}", update);
                
                job.Status = JobStatus.Failed;
                job.CompletedAt = now;
            }
        }

        public async Task PurgeJob(string jobId)
        {
            // Delete JobPrData node
            await SendToFirebaseAsync(HttpMethod.Delete, $"jobsPrData/{jobId}", null);

            // Delete all results under the job
            await SendToFirebaseAsync(HttpMethod.Delete, $"jobs/{jobId}/results", null);

            // Remove from local dictionary of JobPrData
            _jobsPrData.TryRemove(jobId, out _);

            // Remove results from in‑memory job object if present
            if (_jobs.TryGetValue(jobId, out var job))
            {
                lock (job._resultsLock)
                {
                    job.Results.Clear();
                }
            }
        }
    }
}