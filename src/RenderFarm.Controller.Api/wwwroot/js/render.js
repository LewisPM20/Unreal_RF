import { state, workers, projects, profiles, jobs, summary, activities, diagnostics } from './state.js';

export const byId = id => document.getElementById(id);

export function text(value) {
  return value == null ? '' : String(value);
}

export function escapeHtml(value) {
  return text(value).replace(/[&<>"]/g, character => ({
    '&': '&amp;',
    '<': '&lt;',
    '>': '&gt;',
    '"': '&quot;'
  })[character]);
}

export function className(value) {
  return text(value || 'unknown').replace(/\s+/g, '').toLowerCase();
}

export function badge(value) {
  const label = value || 'unknown';
  return `<span class="badge ${className(label)}">${escapeHtml(label)}</span>`;
}

export function formatDate(value) {
  return value ? new Date(value).toLocaleString() : '-';
}

export function formatRelative(value) {
  if (!value) return 'No timestamp';
  const delta = Math.max(0, Date.now() - new Date(value).getTime());
  const seconds = Math.floor(delta / 1000);
  if (seconds < 60) return `${seconds}s ago`;
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return `${minutes}m ago`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours}h ago`;
  return `${Math.floor(hours / 24)}d ago`;
}

export function toast(message, tone = 'info') {
  const element = byId('toast');
  element.textContent = message;
  element.dataset.tone = tone;
  element.classList.add('show');
  window.clearTimeout(Number(element.dataset.timer || 0));
  element.dataset.timer = String(window.setTimeout(() => element.classList.remove('show'), 4200));
}

export function setBusy(isBusy) {
  state.busy = isBusy;
  ['refreshBtn', 'rescanWorkersBtn', 'expireBtn', 'clearJobsBtn', 'resetStateBtn', 'importConfigBtn', 'previewChunksBtn', 'newRenderNextBtn', 'newRenderQueueBtn'].forEach(id => {
    const element = byId(id);
    if (element) element.disabled = isBusy;
  });
}

export function showError(message) {
  const banner = byId('errorBanner');
  banner.textContent = message;
  banner.classList.remove('hidden');
  byId('navStatus').textContent = 'Offline';
}

export function clearError() {
  byId('errorBanner').classList.add('hidden');
}

export function renderLoading() {
  byId('summaryCards').innerHTML = Array.from({ length: 4 }, () => '<div class="skeleton"></div>').join('');
  byId('farmReadiness').innerHTML = '<div class="skeleton"></div>';
  byId('healthTimeline').innerHTML = Array.from({ length: 5 }, () => '<div class="skeleton small"></div>').join('');
  byId('activityFeed').innerHTML = '<div class="skeleton"></div>';
  byId('workers').innerHTML = Array.from({ length: 4 }, () => '<div class="skeleton"></div>').join('');
  byId('jobs').innerHTML = '<tr><td colspan="9"><div class="skeleton small"></div></td></tr>';
  if (byId('recentCompletedRenders')) byId('recentCompletedRenders').innerHTML = '<div class="skeleton small"></div>';
  if (byId('diagnosticsPanel')) byId('diagnosticsPanel').innerHTML = '<div class="skeleton"></div>';
}

export function renderAll() {
  const data = summary();
  if (!data) return;

  byId('controllerVersion').textContent = `${data.service} ${data.version} | ${data.runtime}`;
  byId('navStatus').textContent = data.ok ? 'Online' : 'Degraded';
  byId('navUpdated').textContent = `Updated ${new Date().toLocaleTimeString()}`;
  renderSummaryCards();
  renderFarmReadiness();
  renderApprovalBanner();
  renderHealthRail();
  renderActivityFeed();
  renderRecentCompletedRenders();
  renderDiagnosticsPanel();
  renderWorkers();
  renderPendingWorkers();
  renderOutputs();
  renderProjects();
  renderProfiles();
  renderJobs();
  renderSelects();
  renderDatalists();
  renderNavBadges();
}

function renderSummaryCards() {
  const data = summary();
  const jobStates = data.jobStates || {};
  const queued = jobStates.Queued || 0;
  const active = (jobStates.Running || 0) + (jobStates.Reserved || 0);
  const failed = jobStates.Failed || 0;
  const pending = workers().filter(worker => className(worker.approval) === 'pending').length;
  const stale = countWorkers('stale') + countWorkers('offline');
  const cards = [
    ['Workers', data.workers, `${availableWorkers()} available`, stale ? 'warn' : 'good'],
    ['Queue', data.jobs, `${queued} queued, ${active} active`, active || queued ? 'info' : 'good'],
    ['Projects', data.projects, `${data.renderProfiles} profiles`, data.projects ? 'good' : 'warn'],
    ['Attention', pending + failed + stale, pending || failed || stale ? 'needs review' : 'clear', pending || failed || stale ? 'bad' : 'good']
  ];

  byId('summaryCards').innerHTML = cards.map(([title, value, detail, tone]) => `
    <article class="summary-card ${tone}">
      <span>${escapeHtml(title)}</span>
      <strong>${escapeHtml(value)}</strong>
      <b>${escapeHtml(detail)}</b>
    </article>
  `).join('');
}

function renderFarmReadiness() {
  const readiness = buildFarmReadiness();
  const target = byId('farmReadiness');
  const action = readiness.action;
  const actionButton = action
    ? action.newRender
      ? `<button class="button ${action.primary ? 'primary' : ''}" data-new-render type="button">${escapeHtml(action.label)}</button>`
      : `<button class="button ${action.primary ? 'primary' : ''}" data-view="${escapeHtml(action.view)}" type="button">${escapeHtml(action.label)}</button>`
    : '';

  target.className = `panel wide readiness-panel ${readiness.ready ? 'ready' : 'needs-attention'}`;
  target.innerHTML = `
    <div class="readiness-hero">
      <div>
        <p class="eyebrow">Farm readiness</p>
        <h2>${readiness.ready ? 'Farm ready' : 'Farm needs attention'}</h2>
        <p>${escapeHtml(readiness.summary)}</p>
      </div>
      <div class="readiness-action">
        <span>${escapeHtml(readiness.nextBestAction)}</span>
        ${actionButton}
      </div>
    </div>
    <div class="readiness-checklist">
      ${readiness.items.map(item => `
        <article class="readiness-check ${item.ok ? 'ok' : item.warning ? 'warn' : 'blocked'}">
          <span class="check-dot" aria-hidden="true"></span>
          <div><strong>${escapeHtml(item.label)}</strong><p>${escapeHtml(item.detail)}</p></div>
        </article>
      `).join('')}
    </div>
  `;
}

function buildFarmReadiness() {
  const data = summary() || {};
  const pending = workers().filter(worker => className(worker.approval) === 'pending');
  const failed = jobs().filter(row => className(row.job?.state) === 'failed');
  const approvedWorkers = workers().filter(worker => className(worker.approval || 'accepted') === 'accepted');
  const readyWorkers = approvedWorkers.filter(worker => {
    const status = className(worker.effectiveStatus || worker.status);
    const mode = className(worker.schedulingMode || 'active');
    return (status === 'online' || status === 'idle') && mode === 'active';
  });
  const writableRoots = workers().flatMap(worker => worker.capabilities?.sharedOutputRoots || []).filter(root => root.writable);
  const setupCount = profiles().length;
  const items = [
    { label: 'Controller online', ok: Boolean(data.ok), detail: data.ok ? 'C# controller is responding.' : 'Controller health is degraded.' },
    { label: 'Workers connected', ok: workers().length > 0, detail: workers().length ? `${workers().length} worker(s) registered.` : 'No workers connected yet. Start a worker machine to make it appear here.' },
    { label: 'Worker approvals', ok: approvedWorkers.length > 0 && pending.length === 0, warning: pending.length > 0, detail: pending.length ? `${pending.length} worker(s) waiting for approval.` : `${approvedWorkers.length} approved worker(s).` },
    { label: 'Ready worker', ok: readyWorkers.length > 0, warning: approvedWorkers.length > 0, detail: readyWorkers.length ? `${readyWorkers.length} worker(s) ready for scheduling.` : approvedWorkers.length ? 'Workers are connected, but none are ready yet.' : 'Approve or start a worker before queueing renders.' },
    { label: 'Shared output', ok: writableRoots.length > 0, detail: writableRoots.length ? `${writableRoots.length} writable output root(s) reported.` : 'No validated shared output location yet. Start a worker and confirm its output root.' },
    { label: 'Render setup', ok: setupCount > 0, detail: setupCount ? `${setupCount} render setup(s) available.` : 'No render setup available. Add a render setup before queueing production renders.' },
    { label: 'Failed jobs', ok: failed.length === 0, warning: failed.length > 0, detail: failed.length ? `${failed.length} failed job(s) need review.` : 'No failed jobs need attention.' },
    { label: 'Pending approvals', ok: pending.length === 0, warning: pending.length > 0, detail: pending.length ? `${pending.length} worker approval(s) pending.` : 'No workers are waiting for approval.' }
  ];
  const blocking = items.filter(item => !item.ok && !item.warning);
  const warnings = items.filter(item => item.warning);
  const ready = blocking.length === 0 && warnings.length === 0;
  const action = nextReadinessAction({ pending, failed, readyWorkers, writableRoots, setupCount, workerCount: workers().length });
  const summaryText = ready
    ? `${readyWorkers.length} worker(s) ready, ${writableRoots.length} output root(s) valid, ${setupCount} render setup(s) available.`
    : [...warnings, ...blocking].slice(0, 3).map(item => item.detail).join(' ');

  return {
    ready,
    items,
    summary: summaryText,
    nextBestAction: action?.reason || 'Queue a render when you are ready.',
    action
  };
}

function nextReadinessAction({ pending, failed, readyWorkers, writableRoots, setupCount, workerCount }) {
  if (pending.length) return { label: 'Review workers', view: 'workers', reason: 'Approve trusted worker machines.', primary: true };
  if (!workerCount) return { label: 'Open Workers', view: 'workers', reason: 'Start a worker machine so it can report capabilities.' };
  if (!readyWorkers.length) return { label: 'Review workers', view: 'workers', reason: 'Workers are connected, but none are ready yet.' };
  if (!writableRoots.length) return { label: 'Review outputs', view: 'workers', reason: 'Confirm at least one writable shared output root.' };
  if (!setupCount) return { label: 'Add render setup', view: 'projects', reason: 'Create a render setup for a saved project.', primary: true };
  if (failed.length) return { label: 'Review failed jobs', view: 'queue', reason: 'Review failed jobs before queueing more work.' };
  return { label: 'Queue render', newRender: true, reason: 'Farm ready. Queue the next render.', primary: true };
}

function renderApprovalBanner() {
  const pending = workers().filter(worker => className(worker.approval) === 'pending');
  const banner = byId('approvalBanner');
  if (!pending.length) {
    banner.classList.add('hidden');
    banner.innerHTML = '';
    return;
  }

  banner.classList.remove('hidden');
  banner.innerHTML = `
    <div><strong>${escapeHtml(pending.length)} worker${pending.length === 1 ? '' : 's'} waiting for approval</strong><span>New machines cannot receive renders until approved.</span></div>
    <button class="button primary" data-view="workers" type="button">Review approvals</button>
  `;
}
function renderHealthRail() {
  const jobStates = summary()?.jobStates || {};
  const pending = workers().filter(w => className(w.approval) === 'pending').length;
  const stale = countWorkers('stale') + countWorkers('offline');
  const failedJobs = jobs().filter(row => className(row.job?.state) === 'failed').length;
  const active = (jobStates.Running || 0) + (jobStates.Reserved || 0);
  const queued = jobStates.Queued || 0;
  const cells = [
    ['Controller', 'Online', 'C# runtime', 'good'],
    ['Ready workers', availableWorkers(), stale ? `${stale} unavailable` : 'Telemetry fresh', stale ? 'warn' : 'good'],
    ['Active renders', active, queued ? `${queued} queued` : 'Queue clear', active || queued ? 'info' : 'good'],
    ['Approvals', pending, pending ? 'Waiting for trust decision' : 'No pending workers', pending ? 'warn' : 'good'],
    ['Failures', failedJobs, failedJobs ? 'Review required' : 'No failed jobs', failedJobs ? 'bad' : 'good']
  ];

  byId('healthTimeline').innerHTML = cells.map(([label, value, detail, tone]) => `
    <div class="health-cell ${tone}">
      <span>${escapeHtml(label)}</span>
      <strong>${escapeHtml(value)}</strong>
      <small>${escapeHtml(detail)}</small>
    </div>
  `).join('');
}

function renderActivityFeed() {
  const entries = buildActivityItems().slice(0, 14);
  byId('activityFeed').innerHTML = entries.length ? entries.map(item => `
    <article class="activity-item ${item.tone} ${item.jobId ? 'clickable' : ''}" ${item.jobId ? `data-job-id="${escapeHtml(item.jobId)}" tabindex="0" title="Open job details"` : ''}>
      <div>
        <span class="activity-type">${escapeHtml(item.type)}</span>
        <strong>${escapeHtml(item.title)}</strong>
        <p>${escapeHtml(item.message)}</p>
      </div>
      <time>${escapeHtml(formatRelative(item.timestamp))}</time>
    </article>
  `).join('') : empty('No recent farm activity yet. Worker heartbeats, queued renders, and completion events will appear here.');
}

function buildActivityItems() {
  const controllerEvents = activities().map(activity => ({
    id: activity.id,
    tone: activityTone(activity.severity),
    type: activity.type || 'Activity',
    title: activity.title || 'Farm activity',
    message: activity.message || '',
    timestamp: activity.timestampUtc || activity.TimestampUtc,
    jobId: activity.jobId || activity.JobId || null
  }));

  const derived = buildSnapshotActivityItems();
  return [...controllerEvents, ...derived]
    .filter(item => item.timestamp || item.title)
    .sort((a, b) => new Date(b.timestamp || 0) - new Date(a.timestamp || 0));
}
function activityTone(severity) {
  const key = className(severity);
  if (key === 'success') return 'good';
  if (key === 'warning') return 'warn';
  if (key === 'error') return 'bad';
  return 'info';
}

function buildSnapshotActivityItems() {
  const entries = [];
  workers().forEach(worker => {
    const status = className(worker.effectiveStatus || worker.status);
    const approval = className(worker.approval);
    const outputs = worker.capabilities?.sharedOutputRoots || [];
    const badOutput = outputs.find(root => root.exists && !root.writable);
    if (approval === 'pending') {
      entries.push({ tone: 'warn', type: 'Approval', title: worker.name || worker.id, message: 'Worker is waiting for operator approval before scheduling.', timestamp: worker.lastHeartbeatUtc });
    } else if (status === 'offline' || status === 'stale') {
      entries.push({ tone: 'warn', type: 'Worker', title: worker.name || worker.id, message: `Worker telemetry is ${status}. Check the machine or agent service.`, timestamp: worker.lastHeartbeatUtc });
    } else if (status === 'busy') {
      entries.push({ tone: 'info', type: 'Worker', title: worker.name || worker.id, message: `Rendering ${worker.currentJobId || 'assigned work'}.`, timestamp: worker.lastHeartbeatUtc, jobId: worker.currentJobId || null });
    } else if (status === 'online' || status === 'idle') {
      entries.push({ tone: 'good', type: 'Worker', title: worker.name || worker.id, message: 'Worker is connected and ready for scheduling.', timestamp: worker.lastHeartbeatUtc });
    }
    if (badOutput) {
      entries.push({ tone: 'warn', type: 'Output', title: worker.name || worker.id, message: badOutput.message || `Output root is not writable: ${badOutput.path}`, timestamp: worker.lastHeartbeatUtc });
    }
  });

  jobs().forEach(row => {
    const job = row.job;
    const state = className(job.state);
    if (state === 'failed') {
      entries.push({ tone: 'bad', type: 'Job failed', title: job.name || job.id, message: buildJobFailureSummary(job), timestamp: job.updatedAtUtc, jobId: job.id });
    } else if (state === 'succeeded' || state === 'completed') {
      entries.push({ tone: 'good', type: 'Job complete', title: job.name || job.id, message: job.outputDirectory ? `Output: ${job.outputDirectory}` : 'Render completed successfully.', timestamp: job.finishedAtUtc || job.updatedAtUtc, jobId: job.id });
    } else if (isRunningState(state)) {
      entries.push({ tone: 'info', type: 'Job running', title: job.name || job.id, message: `Assigned to ${job.assignedWorkerId || 'worker pending start'}.`, timestamp: job.startedAtUtc || job.updatedAtUtc, jobId: job.id });
    } else if (state === 'queued' || state === 'retrywait') {
      entries.push({ tone: 'neutral', type: state === 'retrywait' ? 'Retry wait' : 'Queued', title: job.name || job.id, message: state === 'retrywait' ? 'Waiting for retry policy delay.' : 'Waiting for a suitable worker lease.', timestamp: job.queuedAtUtc || job.updatedAtUtc, jobId: job.id });
    }
  });

  return entries;
}
export function renderWorkers() {
  const list = filteredWorkers();
  byId('workers').innerHTML = list.length ? list.map(workerCard).join('') : empty('No workers connected yet. Start a worker machine to make it appear here, or adjust the current filter.');
}

function filteredWorkers() {
  const query = state.search.trim().toLowerCase();
  const filter = state.workerStatus;
  return workers().filter(worker => {
    const status = className(worker.effectiveStatus || worker.status);
    const approval = className(worker.approval);
    const blob = [worker.id, worker.name, worker.hostname, worker.ipAddress, worker.serviceUrl, status, approval]
      .map(x => text(x).toLowerCase())
      .join(' ');
    return (!query || blob.includes(query)) && (!filter || status === filter || approval === filter);
  });
}

function workerCard(worker) {
  const caps = worker.capabilities || {};
  const installs = caps.unrealInstallations || [];
  const paths = caps.projectPaths || [];
  const outputs = caps.sharedOutputRoots || [];
  const installText = installs.length ? installs.slice(0, 3).map(i => `UE ${escapeHtml(i.version)} ${i.exists ? 'ready' : 'missing'}`).join(' | ') : 'No Unreal installations reported';
  const pathText = paths.length ? paths.slice(0, 2).map(p => `${escapeHtml(p.path)} (${p.exists ? 'exists' : 'missing'})`).join('<br>') : 'No project paths reported';
  const outputText = outputs.length ? outputs.slice(0, 2).map(root => `${escapeHtml(root.path)} (${root.writable ? 'writable' : 'not writable'})`).join('<br>') : 'No output roots reported';

  return `
    <article class="worker-card ${className(worker.effectiveStatus || worker.status)}">
      <div class="worker-card-head">
        <div>
          <div class="item-title">${escapeHtml(worker.name || worker.id)}</div>
          <div class="meta">${escapeHtml(worker.hostname) || 'Unknown host'} ${escapeHtml(worker.ipAddress)}</div>
        </div>
        ${badge(worker.effectiveStatus || worker.status)}
      </div>
      <div class="metric-row">
        <span>Approval <strong>${escapeHtml(worker.approval || 'accepted')}</strong></span>
        <span>Mode <strong>${escapeHtml(worker.schedulingMode || 'Active')}</strong></span>
        <span>Heartbeat <strong>${escapeHtml(formatRelative(worker.lastHeartbeatUtc))}</strong></span>
      </div>
      <div class="meta">Current job: ${escapeHtml(worker.currentJobId) || 'none'} | Agent: ${escapeHtml(worker.agentVersion) || '-'}</div>
      <div class="capability-chips">
        <span>CPU ${escapeHtml(caps.cpuCores) || '-'}</span>
        <span>RAM ${escapeHtml(caps.ramGb) || '-'} GB</span>
        <span>GPU ${escapeHtml(caps.gpuName) || '-'}</span>
        <span>Disk ${escapeHtml(caps.freeDiskGb) || '-'} GB</span>
      </div>
      <div class="meta">${installText}</div>
      <div class="meta">${pathText}</div>
      <div class="meta">${outputText}</div>
      <div class="inline-actions">${workerActions(worker)}</div>
    </article>
  `;
}

function workerActions(worker) {
  const approval = className(worker.approval);
  let actions = `<button class="button" type="button" data-fill-project-worker="${escapeHtml(worker.id)}">Use for project</button>`;
  actions += `<button class="button" type="button" data-worker-mode="${escapeHtml(worker.id)}" data-mode="Draining">Drain</button>`;
  actions += `<button class="button" type="button" data-worker-mode="${escapeHtml(worker.id)}" data-mode="Active">Enable</button>`;
  actions += `<button class="button danger" type="button" data-worker-mode="${escapeHtml(worker.id)}" data-mode="Disabled">Disable</button>`;
  if (approval === 'pending') {
    actions += `<button class="button primary" type="button" data-accept-worker="${escapeHtml(worker.id)}">Approve</button><button class="button" type="button" data-reject-worker="${escapeHtml(worker.id)}">Reject</button>`;
  }
  if (approval === 'rejected') {
    actions += `<button class="button primary" type="button" data-accept-worker="${escapeHtml(worker.id)}">Approve again</button>`;
  }
  return actions;
}

function renderPendingWorkers() {
  const pending = workers().filter(worker => className(worker.approval) === 'pending' || className(worker.effectiveStatus || worker.status) === 'pending');
  byId('pendingWorkers').innerHTML = pending.length ? pending.map(worker => {
    const outputs = worker.capabilities?.sharedOutputRoots || [];
    const writable = outputs.filter(root => root.writable).length;
    const outputLabel = outputs.length ? `${writable}/${outputs.length} writable output roots` : 'No output roots reported';
    const outputDetail = outputs.slice(0, 2).map(root => `${root.path} (${root.writable ? 'writable' : 'check'})`).join('<br>') || 'Worker has not reported shared output roots yet.';
    return `
      <article class="approval-card attention-item">
        <div class="item-head"><div><div class="item-title">${escapeHtml(worker.name || worker.id)}</div><div class="meta">${escapeHtml(worker.id)}</div></div>${badge('pending')}</div>
        <div class="approval-grid">
          <span>Host <strong>${escapeHtml(worker.hostname) || 'Unknown'}</strong></span>
          <span>Address <strong>${escapeHtml(worker.ipAddress) || '-'}</strong></span>
          <span>Agent <strong>${escapeHtml(worker.agentVersion) || '-'}</strong></span>
          <span>Heartbeat <strong>${escapeHtml(formatRelative(worker.lastHeartbeatUtc))}</strong></span>
        </div>
        <div class="meta"><strong>${escapeHtml(outputLabel)}</strong><br>${outputDetail}</div>
        <div class="inline-actions"><button class="button primary" type="button" data-accept-worker="${escapeHtml(worker.id)}">Approve worker</button><button class="button" type="button" data-reject-worker="${escapeHtml(worker.id)}">Reject</button></div>
      </article>
    `;
  }).join('') : empty('No workers are waiting for approval. New machines will appear here after their first heartbeat.');
}
function renderOutputs() {
  const roots = [];
  workers().forEach(worker => (worker.capabilities?.sharedOutputRoots || []).forEach(root => roots.push({ worker: worker.id, ...root })));
  byId('outputs').innerHTML = roots.length ? roots.map(root => `
    <article class="item">
      <div class="item-head"><div class="item-title">${escapeHtml(root.path)}</div>${badge(root.writable ? 'Writable' : 'Check')}</div>
      <div class="meta">Worker ${escapeHtml(root.worker)} | exists ${escapeHtml(root.exists)} | writable ${escapeHtml(root.writable)} | ${escapeHtml(root.freeDiskGb) || '-'} GB free</div>
      <div class="meta">${escapeHtml(root.message)}</div>
    </article>
  `).join('') : empty('No shared output roots have been reported. Configure worker output roots and refresh telemetry.');
}

function renderProjects() {
  byId('projects').innerHTML = projects().length ? projects().map(project => `
    <article class="item">
      <div class="item-head"><div class="item-title">${escapeHtml(project.displayName)}</div><span class="meta">${escapeHtml(project.id)}</span></div>
      <div class="meta">${escapeHtml(project.uProjectPath) || 'No default project path'} | UE ${escapeHtml(project.preferredEngineVersion) || 'any'}</div>
      <div class="meta">Worker paths: ${(project.workerPaths || []).length}</div>
      <div class="inline-actions"><button class="button primary" type="button" data-open-profile-project="${escapeHtml(project.id)}">Add profile</button><button class="button" type="button" data-readiness-project="${escapeHtml(project.id)}">Check readiness</button><button class="button danger" type="button" data-delete-project="${escapeHtml(project.id)}">Delete</button></div>
    </article>
  `).join('') : empty('No render profiles yet. Add your first Unreal project to start queueing renders.');
}

function renderProfiles() {
  byId('profiles').innerHTML = profiles().length ? profiles().map(profile => {
    const settings = profile.settings || {};
    return `
      <article class="item">
        <div class="item-head"><div class="item-title">${escapeHtml(profile.displayName)}</div>${badge(profile.type)}</div>
        <div class="meta">Project ${escapeHtml(profile.projectId)} | Render asset ${escapeHtml(profile.assetPath) || 'template or manual launch'}</div>
        <div class="meta">Map ${escapeHtml(settings.map || settings.level) || 'not set'} | Chunking ${profile.supportsChunking ? 'configured but gated' : 'off'}</div>
        <div class="meta">Requirements: ${renderProfileRequirements(settings)}</div>
        <div class="inline-actions"><button class="button primary" type="button" data-queue-profile="${escapeHtml(profile.id)}">Queue render</button><button class="button danger" type="button" data-delete-profile="${escapeHtml(profile.id)}">Delete</button></div>
      </article>
    `;
  }).join('') : empty('No render setups are available yet. Use New Render to create a setup manually while queueing your first job.');
}

export function renderJobs() {
  const rows = jobs();
  renderQueueFilters(rows);
  const visibleRows = rows.filter(row => queueGroup(row.job?.state) === state.queueFilter || state.queueFilter === 'all');
  byId('jobs').innerHTML = visibleRows.length ? visibleRows.map(row => {
    const job = row.job;
    const output = jobOutputPath(job);
    return `
      <tr data-job-id="${escapeHtml(job.id)}" tabindex="0" title="Open job details">
        <td><strong>${escapeHtml(job.name || job.id)}</strong><div class="meta">${escapeHtml(job.id)}</div></td>
        <td>${badge(job.state)}</td>
        <td>${escapeHtml(job.projectId)}</td>
        <td>${escapeHtml(job.renderProfileId)}</td>
        <td>${escapeHtml(job.assignedWorkerId) || '-'}</td>
        <td>${escapeHtml(row.attemptCount)}</td>
        <td>${escapeHtml(job.failureCategory || 'None')}<div class="meta">${escapeHtml(job.error || '')}</div></td>
        <td>${output ? `<button class="path-button" type="button" data-copy="${escapeHtml(output)}" data-copy-label="output path">${escapeHtml(output)}</button>` : '-'}</td>
        <td>${formatDate(job.updatedAtUtc)}</td>
      </tr>
    `;
  }).join('') : `<tr><td colspan="9"><div class="empty">${escapeHtml(queueEmptyMessage(state.queueFilter))}</div></td></tr>`;
}

function renderQueueFilters(rows) {
  const counts = { all: rows.length, running: 0, queued: 0, completed: 0, failed: 0, cancelled: 0 };
  rows.forEach(row => {
    const group = queueGroup(row.job?.state);
    if (counts[group] !== undefined) counts[group] += 1;
  });
  document.querySelectorAll('[data-queue-filter]').forEach(button => {
    const filter = button.dataset.queueFilter || 'all';
    button.classList.toggle('active', state.queueFilter === filter);
    const count = button.querySelector('[data-queue-count]');
    if (count) count.textContent = String(counts[filter] ?? 0);
  });
}

function queueGroup(value) {
  const stateName = className(value);
  if (stateName === 'failed' || stateName === 'stale') return 'failed';
  if (stateName === 'cancelled' || stateName === 'cancelrequested' || stateName === 'cancelling') return 'cancelled';
  if (stateName === 'succeeded' || stateName === 'completed') return 'completed';
  if (stateName === 'queued' || stateName === 'retrywait' || stateName === 'created') return 'queued';
  if (isRunningState(stateName)) return 'running';
  return 'all';
}

function isRunningState(value) {
  const stateName = className(value);
  return ['reserved', 'running', 'validatingworker', 'preparingunrealqueue', 'launchingunreal', 'rendering', 'verifyingoutputs'].includes(stateName);
}

function queueEmptyMessage(filter) {
  return {
    running: 'No renders are currently running. Workers will claim queued work when they are ready.',
    queued: 'No renders are waiting for workers.',
    completed: 'No completed renders yet.',
    failed: 'No failed renders need review.',
    cancelled: 'No cancelled renders are recorded.'
  }[filter] || 'No render jobs are queued. Use New Render when a project and render setup are ready.';
}

function jobOutputPath(job) {
  return job?.outputDirectory || '';
}

function buildJobFailureSummary(job) {
  const category = job.failureCategory && className(job.failureCategory) !== 'none' ? job.failureCategory : 'Unknown';
  return job.error ? `${category}: ${job.error}` : `${category}: no detailed worker error was reported.`;
}

function renderRecentCompletedRenders() {
  const target = byId('recentCompletedRenders');
  if (!target) return;
  const completed = jobs()
    .map(row => row.job)
    .filter(job => ['succeeded', 'completed'].includes(className(job.state)))
    .sort((a, b) => new Date(b.finishedAtUtc || b.updatedAtUtc || 0) - new Date(a.finishedAtUtc || a.updatedAtUtc || 0))
    .slice(0, 6);

  target.innerHTML = completed.length ? completed.map(job => {
    const output = jobOutputPath(job);
    return `
      <article class="recent-render" data-job-id="${escapeHtml(job.id)}" tabindex="0">
        <div>
          <strong>${escapeHtml(job.name || job.id)}</strong>
          <p>${escapeHtml(job.projectId)} / ${escapeHtml(job.renderProfileId)}${job.assignedWorkerId ? ` on ${escapeHtml(job.assignedWorkerId)}` : ''}</p>
          <small>Finished ${escapeHtml(formatRelative(job.finishedAtUtc || job.updatedAtUtc))}</small>
        </div>
        <div class="recent-render-actions">
          ${output ? `<button class="button" type="button" data-copy="${escapeHtml(output)}" data-copy-label="output path">Copy output</button>` : '<span class="meta">No output path reported</span>'}
          <button class="button" type="button" data-job-id="${escapeHtml(job.id)}">Details</button>
        </div>
      </article>
    `;
  }).join('') : empty('Completed renders with verified output paths will appear here.');
}

function renderDiagnosticsPanel() {
  const target = byId('diagnosticsPanel');
  if (!target) return;
  const data = diagnostics();
  if (!data) {
    target.innerHTML = empty('Diagnostics are unavailable. Refresh the farm to request controller diagnostics.');
    return;
  }

  const counts = data.counts || {};
  const controller = data.controller || {};
  const database = data.database || {};
  const outputs = data.sharedOutputRoots || [];
  const heartbeats = data.recentWorkerHeartbeats || [];
  const warnings = data.recentWarnings || [];
  target.innerHTML = `
    <div class="diagnostics-grid">
      <article class="diagnostic-block"><span>Controller</span><strong>${escapeHtml(controller.service || 'RenderFarm Controller')}</strong><p>${escapeHtml(controller.version || '-')} | ${escapeHtml(controller.runtime || 'csharp')}</p><button class="button" type="button" data-copy="${escapeHtml(controller.url || window.location.origin)}" data-copy-label="controller URL">Copy URL</button></article>
      <article class="diagnostic-block"><span>Database</span><strong>${escapeHtml(database.usesDefaultPath ? 'Default path' : 'Configured path')}</strong><p>${escapeHtml(database.path || '-')}</p><button class="button" type="button" data-copy="${escapeHtml(database.path || '')}" data-copy-label="database path">Copy path</button></article>
      <article class="diagnostic-block"><span>Workers</span><strong>${escapeHtml(counts.readyWorkers ?? 0)} ready</strong><p>${escapeHtml(counts.workers ?? 0)} registered, ${escapeHtml(counts.pendingWorkers ?? 0)} pending approval</p></article>
      <article class="diagnostic-block"><span>Queue</span><strong>${escapeHtml(counts.activeJobs ?? 0)} active</strong><p>${escapeHtml(counts.queuedJobs ?? 0)} queued, ${escapeHtml(counts.failedJobs ?? 0)} failed, ${escapeHtml(counts.completedJobs ?? 0)} complete</p></article>
    </div>
    <div class="diagnostics-columns">
      <section><h3>Recent worker heartbeats</h3>${heartbeats.length ? heartbeats.map(item => `<div class="diagnostic-row"><strong>${escapeHtml(item.workerName || item.workerId)}</strong><span>${badge(item.status)} ${escapeHtml(item.secondsSinceHeartbeat)}s ago</span><small>${escapeHtml(item.hostname || '')} ${item.currentJobId ? `| job ${escapeHtml(item.currentJobId)}` : ''}</small></div>`).join('') : empty('No worker heartbeats have been recorded yet.')}</section>
      <section><h3>Shared output roots</h3>${outputs.length ? outputs.slice(0, 12).map(root => `<div class="diagnostic-row"><strong>${escapeHtml(root.path)}</strong><span>${badge(root.writable ? 'Writable' : 'Check')}</span><small>${escapeHtml(root.workerName || root.workerId)} | ${escapeHtml(root.freeDiskGb ?? '-')} GB free</small></div>`).join('') : empty('No shared output roots reported.')}</section>
      <section class="wide"><h3>Recent warnings</h3>${warnings.length ? warnings.map(item => `<div class="diagnostic-row"><strong>${escapeHtml(item.title)}</strong><span>${badge(item.severity)}</span><small>${escapeHtml(item.message)}</small></div>`).join('') : empty('No recent warnings or errors recorded.')}</section>
    </div>
  `;
}
function renderNavBadges() {
  const pending = workers().filter(worker => className(worker.approval) === 'pending').length;
  const badgeElement = byId('workersNavBadge');
  badgeElement.textContent = String(pending);
  badgeElement.classList.toggle('hidden', pending === 0);
}

function renderProfileRequirements(settings) {
  const requirements = [];
  if (settings.minCpuCores) requirements.push(`${escapeHtml(settings.minCpuCores)} CPU cores`);
  if (settings.minRamGb) requirements.push(`${escapeHtml(settings.minRamGb)} GB RAM`);
  if (settings.minVramGb) requirements.push(`${escapeHtml(settings.minVramGb)} GB VRAM`);
  if (settings.gpuNameContains) requirements.push(`GPU contains ${escapeHtml(settings.gpuNameContains)}`);
  return requirements.length ? requirements.join(' | ') : 'none';
}

export function renderSelects() {
  const currentProjectValues = new Map(Array.from(document.querySelectorAll('select[name="projectId"]')).map(select => [select, select.value]));
  const projectOptions = projects().length
    ? projects().map(project => `<option value="${escapeHtml(project.id)}">${escapeHtml(project.displayName)}</option>`).join('')
    : '<option value="">No projects registered</option>';

  document.querySelectorAll('select[name="projectId"]').forEach(select => {
    const current = currentProjectValues.get(select);
    select.innerHTML = projectOptions;
    if (current && projects().some(project => project.id === current)) select.value = current;
  });

  const jobForm = byId('jobForm');
  const selectedProject = jobForm?.querySelector('select[name="projectId"]')?.value;
  const filteredProfiles = selectedProject ? profiles().filter(profile => profile.projectId === selectedProject) : profiles();
  const profileSelect = jobForm?.querySelector('select[name="renderProfileId"]');
  if (profileSelect) {
    const currentProfile = profileSelect.value;
    profileSelect.innerHTML = filteredProfiles.length
      ? filteredProfiles.map(profile => `<option value="${escapeHtml(profile.id)}">${escapeHtml(profile.displayName)}</option>`).join('')
      : '<option value="">No profiles for selected project</option>';
    if (currentProfile && filteredProfiles.some(profile => profile.id === currentProfile)) profileSelect.value = currentProfile;
  }
}

function renderDatalists() {
  const installs = workers().flatMap(worker => worker.capabilities?.unrealInstallations || []);
  setOptions('workerIdList', workers().map(worker => worker.id));
  setOptions('engineRootList', installs.map(install => install.rootPath));
  setOptions('engineVersionList', installs.map(install => install.version));
  setOptions('projectPathList', workers().flatMap(worker => (worker.capabilities?.projectPaths || []).map(path => path.path)));
  setOptions('outputRootList', workers().flatMap(worker => (worker.capabilities?.sharedOutputRoots || []).map(root => root.path)));
}

function setOptions(id, values) {
  const element = byId(id);
  if (!element) return;
  element.innerHTML = [...new Set(values.filter(Boolean))].sort().map(value => `<option value="${escapeHtml(value)}"></option>`).join('');
}

function countWorkers(status) {
  return workers().filter(worker => className(worker.effectiveStatus || worker.status) === status).length;
}

function availableWorkers() {
  return countWorkers('online') + countWorkers('idle') + countWorkers('busy');
}

function empty(message) {
  return `<div class="empty">${escapeHtml(message)}</div>`;
}









