import { initializeApp } from "https://www.gstatic.com/firebasejs/9.23.0/firebase-app.js";
import {
  getAuth,
  createUserWithEmailAndPassword,
  signInWithEmailAndPassword,
  signOut,
  onAuthStateChanged
} from "https://www.gstatic.com/firebasejs/9.23.0/firebase-auth.js";
import { firebaseConfig } from "./firebase-config.js";

const firebaseApp = initializeApp(firebaseConfig);
const auth = getAuth(firebaseApp);

// UI references
const authScreen = document.getElementById('auth-screen');
const dashboardScreen = document.getElementById('dashboard-screen');
const emailInput = document.getElementById('email');
const passwordInput = document.getElementById('password');
const loginBtn = document.getElementById('login-btn');
const signupBtn = document.getElementById('signup-btn');
const authError = document.getElementById('auth-error');

const userEmailSpan = document.getElementById('user-email');
const logoutBtn = document.getElementById('logout-btn');

const prLinkInput = document.getElementById('pr-link');
const maxPrCountInput = document.getElementById('max-pr-count');
const analyseBtn = document.getElementById('analyse-btn');
const analyseMsg = document.getElementById('analyse-msg');
const jobsContainer = document.getElementById('jobs-container');
const refreshBtn = document.getElementById('refresh-btn');

let currentUid = null;

function showDashboard(uid, email) {
  currentUid = uid;
  authScreen.classList.add('hidden');
  dashboardScreen.classList.remove('hidden');
  userEmailSpan.textContent = email;
  loadJobs(uid);
}

function showAuth() {
  dashboardScreen.classList.add('hidden');
  authScreen.classList.remove('hidden');
  emailInput.value = '';
  passwordInput.value = '';
  authError.textContent = '';
}

async function login(isSignup) {
  const email = emailInput.value.trim();
  const password = passwordInput.value.trim();

  if (!email || !password) {
    authError.textContent = 'Email and password are required.';
    return;
  }

  authError.textContent = '';

  try {
    let credential;
    if (isSignup) {
      credential = await createUserWithEmailAndPassword(auth, email, password);
    } else {
      credential = await signInWithEmailAndPassword(auth, email, password);
    }
    showDashboard(credential.user.uid, credential.user.email);
  } catch (e) {
    authError.textContent = e.message.replace('Firebase: ', '');
  }
}

loginBtn.addEventListener('click', () => login(false));
signupBtn.addEventListener('click', () => login(true));

logoutBtn.addEventListener('click', async () => {
  await signOut(auth);
  currentUid = null;
  showAuth();
});

if (refreshBtn) {
  refreshBtn.addEventListener('click', () => loadJobs(currentUid));
}

onAuthStateChanged(auth, (user) => {
  if (user) {
    showDashboard(user.uid, user.email);
  } else {
    showAuth();
  }
});

async function getFreshToken() {
  if (!auth.currentUser) throw new Error("User not authenticated");
  return await auth.currentUser.getIdToken(true);
}

async function loadJobs(uid) {
  if (!uid) return;
  jobsContainer.innerHTML = '<p class="loading-text">Loading runs...</p>';

  try {
    const token = await getFreshToken();
    const resp = await fetch(`http://localhost:5000/api/prioritizer/jobs/user/${encodeURIComponent(uid)}`, {
      headers: { Authorization: `Bearer ${token}` },
    });

    if (!resp.ok) throw new Error(`HTTP error ${resp.status}`);
    const jobs = await resp.json();

    if (!Array.isArray(jobs) || jobs.length === 0) {
      jobsContainer.innerHTML = '<p class="empty-state">No repository analysis runs found yet.</p>';
      return;
    }

    renderJobCards(jobs);
  } catch (e) {
    jobsContainer.innerHTML = `<p class="error">Failed to fetch jobs: ${e.message}</p>`;
  }
}

function renderJobCards(jobs) {
  jobsContainer.innerHTML = jobs.map(job => {
    const statusClass = (job.status || 'Queued').toLowerCase();
    const date = job.createdAt ? new Date(job.createdAt).toLocaleString() : 'N/A';
    const repoName = job.owner && job.repo ? `${job.owner}/${job.repo}` : (job.repoUrl || 'Repository');

    let resultsHtml = '';
    const prArray = Array.isArray(job.results) ? job.results : (job.results ? Object.values(job.results) : []);

    if (prArray.length > 0) {
      // Group PRs by Category
      const groupedPrs = {};
      prArray.forEach(pr => {
        const category = pr.category || 'Uncategorized';
        if (!groupedPrs[category]) groupedPrs[category] = [];
        groupedPrs[category].push(pr);
      });

      let groupsHtml = '';
      for (const [category, prs] of Object.entries(groupedPrs)) {
        prs.sort((a, b) => (b.priorityScore || 0) - (a.priorityScore || 0));

        const prItemsHtml = prs.map(pr => {
          const severityLower = pr.severity ? pr.severity.toLowerCase() : 'none';
          const severityText = pr.severity || 'Unrated';
          const scoreText = pr.priorityScore || 0;

          // Explicit Concrete Values
          const requiresReviewStr = pr.requiresImmediateReview ? 'Yes ⚠️' : 'No';
          const urgentClass = pr.requiresImmediateReview ? 'text-urgent' : 'text-muted';

          return `
            <div class="pr-item severity-border-${severityLower}">
              <div class="pr-info">
                <a class="pr-link" href="${pr.htmlUrl}" target="_blank">#${pr.number} ${pr.title}</a>
                <span class="pr-author">by ${pr.author}</span>
              </div>
              
              <!-- Concrete Labeled Metrics Row -->
              <div class="pr-metrics">
                <div class="metric">
                  <span class="metric-label">Severity:</span>
                  <span class="badge badge-${severityLower}">${severityText}</span>
                </div>
                <div class="metric">
                  <span class="metric-label">Score:</span>
                  <span class="metric-value font-bold">${scoreText}</span>
                </div>
                <div class="metric">
                  <span class="metric-label">Immediate Review:</span>
                  <span class="metric-value ${urgentClass}">${requiresReviewStr}</span>
                </div>
              </div>
            </div>
          `;
        }).join('');

        groupsHtml += `
          <div class="category-group">
            <h4 class="category-title">${category} <span class="count-badge">${prs.length}</span></h4>
            <div class="category-items">
              ${prItemsHtml}
            </div>
          </div>
        `;
      }

      resultsHtml = `
        <details class="results-accordion">
          <summary>View Prioritized PRs (${prArray.length})</summary>
          <div class="results-content">
            ${groupsHtml}
          </div>
        </details>
      `;
    }

    return `
      <div class="job-card">
        <div class="job-header">
          <span class="job-title">${repoName}</span>
          <span class="status-badge status-${statusClass}">${job.status || 'Queued'}</span>
        </div>
        <div class="job-meta">
          <span><strong>Submitted:</strong> ${date}</span>
          <span><strong>Job ID:</strong> <code>${job.jobId}</code></span>
        </div>
        ${resultsHtml}
      </div>
    `;
  }).join('');
}

analyseBtn.addEventListener('click', async () => {
  const link = prLinkInput.value.trim();
  const maxPrs = parseInt(maxPrCountInput.value) || 10;

  if (!link) {
    analyseMsg.className = 'msg error';
    analyseMsg.textContent = 'Please enter a valid repository URL.';
    return;
  }

  analyseMsg.className = 'msg info';
  analyseMsg.textContent = 'Submitting job...';
  analyseBtn.disabled = true;

  try {
    const token = await getFreshToken();
    const payload = { repoUrl: link, maxPrCount: maxPrs };

    const resp = await fetch('http://localhost:5000/api/prioritizer/submit', {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        Authorization: `Bearer ${token}`,
      },
      body: JSON.stringify(payload),
    });

    if (!resp.ok) {
      const err = await resp.json();
      throw new Error(err.error || 'Submission failed');
    }

    const result = await resp.json();
    analyseMsg.className = 'msg success';
    analyseMsg.textContent = `Job queued successfully! (ID: ${result.jobId})`;
    prLinkInput.value = '';

    setTimeout(() => loadJobs(currentUid), 1500);
  } catch (e) {
    analyseMsg.className = 'msg error';
    analyseMsg.textContent = `Error: ${e.message}`;
  } finally {
    analyseBtn.disabled = false;
  }
});