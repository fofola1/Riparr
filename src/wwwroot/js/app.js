// Global state
let currentActiveTab = 'queue-tab';
let pollInterval = null;
let parsedPayloadData = null; // Stores parsed payload from textarea
let isAuthModalOpen = false;

// API Key configuration
const getApiKey = () => {
    const urlParams = new URLSearchParams(window.location.search);
    let key = urlParams.get('apikey');
    if (key) {
        localStorage.setItem('riparr_api_key', key);
        return key;
    }
    return localStorage.getItem('riparr_api_key') || '';
};

// Fetch wrapper with API key appending
async function apiCall(params = {}) {
    const urlParams = new URLSearchParams();
    urlParams.append('output', 'json');
    
    const key = getApiKey();
    if (key) {
        urlParams.append('apikey', key);
    }
    
    for (const [k, v] of Object.entries(params)) {
        urlParams.append(k, v);
    }
    
    try {
        const response = await fetch(`/api/sabnzbd?${urlParams.toString()}`);
        if (response.status === 401) {
            showAuthModal();
            throw new Error("401 Unauthorized");
        }
        if (!response.ok) {
            throw new Error(`HTTP Error: ${response.status}`);
        }
        return await response.json();
    } catch (error) {
        console.error("API Call failed:", error);
        if (error.message !== "401 Unauthorized") {
            document.getElementById('conn-dot').className = 'status-dot red';
            document.getElementById('conn-text').innerText = 'API Offline';
        }
        throw error;
    }
}

function showAuthModal() {
    if (isAuthModalOpen) return;
    isAuthModalOpen = true;
    
    // Stop polling
    if (pollInterval) {
        clearInterval(pollInterval);
        pollInterval = null;
    }
    
    document.getElementById('api-key-modal').style.display = 'flex';
}

function hideAuthModal() {
    document.getElementById('api-key-modal').style.display = 'none';
    isAuthModalOpen = false;
    
    // Restart polling
    if (!pollInterval) {
        pollInterval = setInterval(pollData, 2000);
    }
    pollData();
}


// Initialise Application
document.addEventListener('DOMContentLoaded', () => {
    initTabs();
    initEventListeners();
    fetchConfig();
    
    // Initial data load
    pollData();
    
    // Start polling loop every 2 seconds
    pollInterval = setInterval(pollData, 2000);
});

// Tab navigation handler
function initTabs() {
    const navButtons = document.querySelectorAll('.nav-btn');
    const tabPanels = document.querySelectorAll('.tab-panel');

    navButtons.forEach(btn => {
        btn.addEventListener('click', () => {
            const targetTab = btn.getAttribute('data-tab');
            
            navButtons.forEach(b => b.classList.remove('active'));
            tabPanels.forEach(p => p.classList.remove('active'));
            
            btn.classList.add('active');
            const panel = document.getElementById(targetTab);
            if (panel) panel.classList.add('active');
            
            currentActiveTab = targetTab;
            
            // Instantly poll on tab switch
            pollData();
        });
    });
}

// Global button actions mapping
function initEventListeners() {
    // Global pause/resume queue
    document.getElementById('btn-pause-all').addEventListener('click', async () => {
        try {
            await apiCall({ mode: 'pause' });
            pollData();
        } catch(e) {}
    });

    document.getElementById('btn-resume-all').addEventListener('click', async () => {
        try {
            await apiCall({ mode: 'resume' });
            pollData();
        } catch(e) {}
    });

    // Purge queue
    document.getElementById('btn-purge-queue').addEventListener('click', async () => {
        if (confirm("Are you sure you want to cancel and delete ALL downloads in the queue?")) {
            try {
                await apiCall({ mode: 'queue', name: 'purge' });
                pollData();
            } catch(e) {}
        }
    });

    // Purge history
    document.getElementById('btn-purge-history').addEventListener('click', async () => {
        if (confirm("Are you sure you want to clear all completion history?")) {
            try {
                await apiCall({ mode: 'history', name: 'purge' });
                pollData();
            } catch(e) {}
        }
    });

    // Manual Add Form submission
    document.getElementById('manual-download-form').addEventListener('submit', async (e) => {
        e.preventDefault();
        
        const title = document.getElementById('m-title').value;
        const seasonVal = document.getElementById('m-season').value;
        const episode = document.getElementById('m-episode').value;
        const streamUrl = document.getElementById('m-url').value;
        const resolution = document.getElementById('m-res').value;
        const source = document.getElementById('m-source').value;

        const payload = {
            site: streamUrl ? "yt_dlp" : "ani_cli",
            id: "manual-" + Date.now(),
            title: title,
            season: seasonVal ? parseInt(seasonVal) : null,
            ep: parseInt(episode),
            stream_url: streamUrl || null,
            resolution: resolution || null,
            source: source || null
        };

        const jsonStr = JSON.stringify(payload);
        const b64 = btoa(unescape(encodeURIComponent(jsonStr)));
        const mockUrl = `http://localhost/download?payload=${b64}`;

        try {
            const res = await apiCall({ mode: 'addurl', name: mockUrl });
            if (res.status) {
                alert("Download queued successfully!");
                document.getElementById('manual-download-form').reset();
                // Switch to queue tab
                document.querySelector('[data-tab="queue-tab"]').click();
            } else {
                alert("Failed to queue download: " + (res.error || "Unknown error"));
            }
        } catch (error) {
            alert("Error sending request: " + error.message);
        }
    });

    // Decode base64 payload helper
    document.getElementById('btn-decode-payload').addEventListener('click', () => {
        const input = document.getElementById('payload-input').value.trim();
        if (!input) return;

        let base64 = input;
        
        // Extract parameter if URL is pasted
        if (input.includes('payload=')) {
            try {
                const urlObj = new URL(input);
                base64 = urlObj.searchParams.get('payload') || '';
            } catch (e) {
                // Try manual split if invalid URL format
                const parts = input.split('payload=');
                base64 = parts[1].split('&')[0];
            }
        }

        try {
            // Standardize base64 and decode
            base64 = base64.replace(/-/g, '+').replace(/_/g, '/');
            const decoded = decodeURIComponent(escape(atob(base64)));
            const parsed = JSON.parse(decoded);
            
            parsedPayloadData = input; // Keep the original string to send
            
            document.getElementById('payload-preview-code').innerText = JSON.stringify(parsed, null, 2);
            document.getElementById('payload-preview-box').style.display = 'block';
        } catch (err) {
            alert("Failed to parse base64 payload. Make sure it is valid Base64 JSON.");
            document.getElementById('payload-preview-box').style.display = 'none';
            parsedPayloadData = null;
        }
    });

    // Submit decoded payload download
    document.getElementById('btn-submit-payload').addEventListener('click', async () => {
        if (!parsedPayloadData) return;
        
        try {
            const res = await apiCall({ mode: 'addurl', name: parsedPayloadData });
            if (res.status) {
                alert("Download queued successfully!");
                document.getElementById('payload-input').value = '';
                document.getElementById('payload-preview-box').style.display = 'none';
                parsedPayloadData = null;
                // Switch to queue tab
                document.querySelector('[data-tab="queue-tab"]').click();
            } else {
                alert("Failed to queue: " + (res.error || "Unknown error"));
            }
        } catch (error) {
            alert("Error sending request: " + error.message);
        }
    });

    // Submit API Key from modal
    document.getElementById('btn-submit-modal-key').addEventListener('click', () => {
        const key = document.getElementById('modal-api-key-input').value.trim();
        if (key) {
            localStorage.setItem('riparr_api_key', key);
            hideAuthModal();
        } else {
            alert("API Key cannot be empty.");
        }
    });

    // Support enter key in modal input field
    document.getElementById('modal-api-key-input').addEventListener('keypress', (e) => {
        if (e.key === 'Enter') {
            document.getElementById('btn-submit-modal-key').click();
        }
    });
}

// Fetch configs for settings tab
async function fetchConfig() {
    try {
        const configData = await apiCall({ mode: 'get_config' });
        const path = configData.config?.misc?.complete_dir || 'Default (/downloads/completed)';
        document.getElementById('cfg-completed-dir').innerText = path;
        document.getElementById('cfg-incomplete-dir').innerText = path.replace('completed', 'incomplete');
    } catch (error) {
        console.error("Could not fetch config settings:", error);
    }
    
    // Display saved API key details
    const key = getApiKey();
    if (key) {
        document.getElementById('cfg-api-key').innerHTML = `
            <div style="display: flex; gap: 10px; align-items: center;">
                <span style="font-family: monospace; background: rgba(0,0,0,0.2); padding: 4px 8px; border-radius: 6px;">••••••••</span>
                <button class="btn btn-danger" style="padding: 4px 8px; font-size: 12px; height: auto;" onclick="clearSavedKey()">
                    <i class="fa-solid fa-right-from-bracket"></i> Clear Key
                </button>
            </div>`;
    } else {
        document.getElementById('cfg-api-key').innerText = 'None (Accessing without a token)';
    }
}

// Global scope helper to clear saved token
window.clearSavedKey = () => {
    localStorage.removeItem('riparr_api_key');
    alert("API Key cleared. Refreshing page...");
    window.location.reload();
};

// Polling loop updates
async function pollData() {
    try {
        // Run parallel queries to speed up loading
        const [queueRes, historyRes] = await Promise.all([
            apiCall({ mode: 'queue' }),
            apiCall({ mode: 'history' })
        ]);
        
        document.getElementById('conn-dot').className = 'status-dot green';
        document.getElementById('conn-text').innerText = 'API Online';
        
        renderStats(queueRes.queue || {});
        renderQueue(queueRes.queue || {});
        renderHistory(historyRes.history || {});
    } catch (error) {
        console.error("Polling error:", error);
    }
}

// Render header stats card
function renderStats(queue) {
    document.getElementById('global-speed').innerText = queue.speed || '0 B/s';
    document.getElementById('global-sizeleft').innerText = queue.sizeleft || '0.0 MB';
    
    let statusText = queue.status || 'Idle';
    if (queue.paused) {
        statusText = 'Paused';
    }
    document.getElementById('global-status').innerText = statusText;
}

// Render queue list dynamically
function renderQueue(queue) {
    const queueList = document.getElementById('queue-list');
    const slots = queue.slots || [];
    
    // Toggle pause all button visibility
    const pauseAllBtn = document.getElementById('btn-pause-all');
    const resumeAllBtn = document.getElementById('btn-resume-all');
    
    if (queue.paused) {
        pauseAllBtn.style.display = 'none';
        resumeAllBtn.style.display = 'inline-flex';
    } else {
        pauseAllBtn.style.display = 'inline-flex';
        resumeAllBtn.style.display = 'none';
    }

    if (slots.length === 0) {
        queueList.innerHTML = `
            <div class="empty-state">
                <i class="fa-solid fa-circle-check"></i>
                <p>Queue is empty. No active downloads.</p>
            </div>`;
        return;
    }

    let html = '';
    slots.forEach(slot => {
        const percentage = parseFloat(slot.percentage) || 0;
        const isPaused = slot.status.toLowerCase() === 'paused';
        
        const actionBtn = isPaused 
            ? `<button class="btn btn-secondary btn-icon" onclick="resumeJob('${slot.nzo_id}')" title="Resume download"><i class="fa-solid fa-play"></i></button>`
            : `<button class="btn btn-secondary btn-icon" onclick="pauseJob('${slot.nzo_id}')" title="Pause download"><i class="fa-solid fa-pause"></i></button>`;

        html += `
            <div class="download-card">
                <div class="card-top">
                    <div class="card-info">
                        <span class="card-title">${slot.filename}</span>
                        <div class="card-meta">
                            <span class="card-meta-item"><i class="fa-solid fa-tags"></i> Category: ${slot.cat}</span>
                            <span class="card-meta-item"><i class="fa-solid fa-hard-drive"></i> Size: ${slot.size}</span>
                            <span class="card-meta-item"><i class="fa-solid fa-shield-halved"></i> ID: ${slot.nzo_id.substring(0, 16)}...</span>
                        </div>
                    </div>
                    <div class="card-actions">
                        ${actionBtn}
                        <button class="btn btn-danger btn-icon" onclick="deleteJob('${slot.nzo_id}', 'queue')" title="Cancel and delete download">
                            <i class="fa-solid fa-trash-can"></i>
                        </button>
                    </div>
                </div>
                <div class="progress-container">
                    <div class="progress-track">
                        <div class="progress-fill ${isPaused ? 'paused' : ''}" style="width: ${percentage}%"></div>
                    </div>
                    <div class="progress-text">
                        <span>${slot.status} - ${slot.speed}</span>
                        <span>${slot.percentage}% (${slot.sizeleft} left)</span>
                    </div>
                </div>
            </div>`;
    });
    
    queueList.innerHTML = html;
}

// Render history slots table dynamically
function renderHistory(history) {
    const historyRows = document.getElementById('history-rows');
    const slots = history.slots || [];

    if (slots.length === 0) {
        historyRows.innerHTML = `
            <tr>
                <td colspan="5" class="empty-table">
                    <i class="fa-solid fa-inbox"></i>
                    <p>No history entries found.</p>
                </td>
            </tr>`;
        return;
    }

    let html = '';
    slots.forEach(slot => {
        const isSuccess = slot.status.toLowerCase() === 'completed';
        const badgeClass = isSuccess ? 'badge-success' : 'badge-danger';
        
        let titleColumn = slot.name;
        if (!isSuccess && slot.fail_message) {
            titleColumn = `
                <div>
                    <strong>${slot.name}</strong>
                    <div style="font-size: 12px; color: #f87171; margin-top: 4px; font-family: monospace;">
                        Error: ${slot.fail_message}
                    </div>
                </div>`;
        }

        html += `
            <tr>
                <td>${titleColumn}</td>
                <td>${slot.size}</td>
                <td><span class="badge ${badgeClass}">${slot.status}</span></td>
                <td style="font-size: 13px; color: var(--text-secondary); max-width: 300px; word-break: break-all;">
                    ${slot.downloaded_to}
                </td>
                <td>
                    <button class="btn btn-danger btn-icon" onclick="deleteJob('${slot.nzo_id}', 'history')" title="Delete from history">
                        <i class="fa-solid fa-trash-can"></i>
                    </button>
                </td>
            </tr>`;
    });
    
    historyRows.innerHTML = html;
}

// Job-level Pause/Resume/Delete functions called from inline button handlers
async function pauseJob(jobId) {
    try {
        await apiCall({ mode: 'queue', name: 'pause', value: jobId });
        pollData();
    } catch(e) {}
}

async function resumeJob(jobId) {
    try {
        await apiCall({ mode: 'queue', name: 'resume', value: jobId });
        pollData();
    } catch(e) {}
}

async function deleteJob(jobId, listType) {
    if (confirm("Are you sure you want to remove this job?")) {
        try {
            await apiCall({ mode: listType, name: 'delete', value: jobId });
            pollData();
        } catch(e) {}
    }
}
