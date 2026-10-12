# Enrich PRs

Enrich PRs is an ASP.NET web service that retrieves open pull requests from a GitHub repository and prioritizes them for review. It enriches each PR with its patch and changed-file list, sends that context to the JEV analysis service, and records a category, severity, priority score, and immediate-review recommendation. The API accepts work asynchronously: clients submit a repository, receive a job ID, and poll for progress and results.

## How a job flows through the system

1. **Submit and persist.** `POST /api/prioritizer/submit` validates a repository URL or `owner/repo` value, creates a job ID, and writes the initial job state to Firebase Realtime Database before putting the ID on the first queue.
2. **Retrieve pull requests.** `PrPrioritizerBackgroundWorker` reads the job, asks GitHub for up to the requested number of open PRs, and fetches the diff and changed-file names. Detail requests within a job run concurrently, with a maximum degree of parallelism of 10.
3. **Save and hand off.** The worker writes the retrieved PR data to Firebase and changes the job status to `PrsRetrieved`, then passes the job ID to the analysis queue.
4. **Analyze and complete.** `JevBackgroundWorker` analyzes each PR (up to 10 concurrently), stores each result, and marks the job `Completed` after processing. The JEV output is represented as a category, severity, 1–5 priority score, and immediate-review flag.
5. **Poll.** `GET /api/prioritizer/poll/{jobId}` returns the job status and its available results.

## JEV-powered classification

JEV is used as a focused pull-request classifier, not as an open-ended code-review chatbot. For each PR, Enrich PRs provides the title, description, changed-file names, and patch context, then asks JEV to return four actionable signals:

- **Category:** Security, Bug, Feature, Refactor, or Docs.
- **Severity:** Critical, High, Medium, or Low.
- **Priority score:** a numeric score on a 1–5 scale.
- **Immediate review:** whether the change should be escalated for urgent review.

The same question schema and evaluation criteria are applied to every PR. Results are mapped into `PrPrioritizerResult`, including rounding and bounding the priority score to 1–5. This structured output gives the product a consistent classification vocabulary and makes results easy to group, compare, and display in the UI—adding a deterministic *workflow* around AI-assisted judgment without claiming that a model's underlying response is guaranteed to be identical on every run.

The integration is designed to be cost-conscious: it sends bounded PR context (the patch is capped before analysis) and asks for compact labels and scores rather than lengthy general-purpose review prose. A focused classification call can be more economical than using a larger general model for broad, free-form review, while the actual savings depend on provider pricing, usage, and model behavior; this repository does not include a cost benchmark.

When `JEV_API_KEY` is not configured, the service uses a local simulated evaluator so the end-to-end workflow can still produce the same classification fields during development.

## Demo

[Watch the demo video](assets/2026-10-11%2017-28-21.mkv)

## Screenshots

### Sign in

![Enrich PRs sign-in screen](assets/Screenshot%202026-10-11%20000153.png)

### Browse prioritized pull requests by category

![Prioritized pull requests grouped by category](assets/Screenshot%202026-10-11%20000121.png)

### Submit a repository and view recent runs

![Repository submission and recent runs](assets/Screenshot%202026-10-11%20000035.png)

![Expanded prioritized pull-request results](assets/Screenshot%202026-10-10%20235947.png)

## Queues and throughput

The service uses two separate, bounded in-process queues, each implemented with a .NET `Channel<string>` and a capacity of 100 job IDs:

- **Retrieval queue** — consumed by `PrPrioritizerBackgroundWorker`; handles GitHub listing and PR detail retrieval.
- **Analysis queue** — consumed by `JevBackgroundWorker`; handles JEV evaluation and result persistence.

Separating these stages keeps repository access and AI evaluation independently managed, while bounded queues provide backpressure when work arrives faster than a stage can consume it. Each hosted worker advances jobs through its stage, with PR-level GitHub detail fetches and JEV calls parallelized up to 10 at a time. This combination supports controlled throughput and keeps the API responsive by doing the longer-running work in the background.

## Persistence and fault tolerance

Firebase Realtime Database acts as the durable job journal. Job records are stored under `jobs/{jobId}`, retrieved PR payloads under `jobsPrData/{jobId}`, and user-to-job associations under `users/{uid}`. In-memory concurrent dictionaries support active processing, while Firebase provides the persisted state that outlives an individual process.

Enrich PRs uses a WAL-style **persist-before-handoff** pattern: it records the initial job before queueing retrieval, persists the retrieved PR payload and `PrsRetrieved` stage before queueing analysis, and saves analysis results and final status as work progresses. These durable stage checkpoints let the service reconstruct pending work rather than relying solely on transient queue contents. The log is implemented as structured Firebase records, rather than a conventional local append-only WAL file.

At startup, the retrieval worker finds persisted `Queued` jobs and the analysis worker finds `PrsRetrieved` jobs, reloads their saved state, and repopulates the appropriate in-memory queue. Processing exceptions are recorded as job failures and trigger a retry path. Together, durable stage data, explicit job statuses, startup queue restoration, and stage-specific workers provide a practical recovery mechanism for background processing.

## API authentication and authorization

All routes on `PrioritizerController` require authentication. Clients send a Firebase ID token using the HTTP Bearer scheme; the custom authentication handler verifies the JWT through the Firebase Admin SDK and exposes the verified Firebase UID and email as claims. Submitted jobs are associated with the authenticated UID, making it possible to retrieve a user's job history. Swagger documents the Firebase Bearer/JWT authentication flow and is enabled in Development.

### Main endpoints

| Method | Path | Purpose |
|---|---|---|
| `POST` | `/api/prioritizer/submit` | Submit a repository for prioritization; returns `202 Accepted`, a job ID, and a polling URL. |
| `GET` | `/api/prioritizer/poll/{jobId}` | Read a job's status and results. |
| `GET` | `/api/prioritizer/jobs` | List all tracked jobs. |
| `GET` | `/api/prioritizer/jobs/user/{userId}` | List the latest job per repository associated with a user ID. |
| `GET` | `/api/prioritizer/jobs/active` | List jobs not marked `Completed` or `Failed`. |

`SubmitJobRequest` accepts `repoUrl` and optional `maxPrCount`; the controller defaults the count to 10 and clamps it to 1–50. Jobs move through the `Queued`, `PrsRetrieved`, `Completed`, and `Failed` statuses.

## Data models

- **`JobState`** — job ID, input repository URL, owner/repository names, requested PR limit, status, start/completion timestamps, and a dictionary of `PrPrioritizerResult` values.
- **`JobPrData`** — job ID, repository URL, count of retrieved PRs, and the retrieved PR records. This larger input payload is stored separately from the job record.
- **`GitHubPullRequestDto`** — GitHub PR number, title, description, HTML and diff URLs, author, creation time, patch text, and changed-file names.
- **`PrPrioritizerResult`** — PR identity/display details plus JEV-derived category, severity, priority score, and immediate-review flag.
- **`User`** — Firebase UID, email, and associated job IDs.
- **Request/response DTOs** — `SubmitJobRequest`, `SubmitJobResponse`, and `PollJobResponse` define the submission and polling API shapes.

Firebase stores these objects as JSON under the paths described above. There is no relational database or ORM in this project.

## Configuration and local run

### Frontend Firebase configuration

Create your own Firebase project and web app in the Firebase console, then add that app's configuration values to `frontend\firebase-config.js`. Use `frontend\firebase-config.example.js` as a guide for the required fields. The local config file is ignored by Git.

Enable Firebase Authentication and Realtime Database for your project, and configure its authorized domains and database rules. Firebase web app config is visible to browser users; do not put service-account credentials in the frontend.

### Backend configuration

Configure the following environment variables:

- `FIREBASE_CREDENTIALS_JSON` — Firebase service-account JSON; required.
- `Firebase__DatabaseUrl` — Firebase Realtime Database URL, supplied through .NET's environment-variable configuration mapping; required.
- `GITHUB_TOKEN` — GitHub API token. Optional, but unauthenticated requests have a lower GitHub rate limit.
- `JEV_API_KEY` — JEV API key. When missing or set to a recognized placeholder/demo value, the service uses a simulated evaluation instead of a live JEV request.

From the repository root:

```powershell
cd backend
dotnet run --urls "http://localhost:5000"
```

The project is an ASP.NET application. In Development, Swagger UI is available at `/swagger`.
"# Enrich-PR" 
