import { state, workers, projects, profiles, jobs, summary } from './state.js';

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

export function toast(message) {
  const element = byId('toast');
  element.textContent = message;
  element.classList.add('show');
  window.clearTimeout(element.dataset.timer);
  element.dataset.timer = window.setTimeout(() => element.classList.remove('show'), 3400);
}

export function setBusy(isBusy) {
  state.busy = isBusy;
  ['refreshBtn', 'rescanWorkersBtn', 'expireBtn', 'clearJobsBtn', 'resetStateBtn', 'importConfigBtn'].forEach(id => {
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
  byId('workers').innerHTML = Array.from({ length: 4 }, () => '<div class="skeleton"></div>').join('');
  byId('jobs').innerHTML = '<tr><td colspan="8"><div class="skeleton"></div></td></tr>';
}

export function renderAll() {
  const data = summary();
  if (!data) return;

  byId('controllerVersion').textContent = `${data.service} ${data.version} | ${data.runtime}`;
  byId('navStatus').textContent = data.ok ? 'Online' : 'Degraded';
  byId('navUpdated').textContent = `Updated ${new Date().toLocaleTimeString()}`;
  renderSummaryCards();
  renderHealthRail();
  renderWorkers();
  renderPendingWorkers();
  renderOutputs();
  renderProjects();
  renderProfiles();
  renderJobs();
  renderSelects();
  renderDatalists();
}

function renderSummaryCards() {
  const data = summary();
  const jobStates = data.jobStates || {};
  const running = (jobStates.Running || 0) + (jobStates.Reserved || 0);
  const failed = jobStates.Failed || 0;
  const cards = [
    ['Workers', data.workers, `${countWorkers('online') + countWorkers('idle') + countWorkers('busy')} available`],
    ['Queue', data.jobs, `${jobStates.Queued || 0} queued, ${running} active`],
    ['Projects', data.projects, `${data.renderProfiles} profiles`],
    ['Failures', failed, failed ? 'action required' : 'clear']
  ];

  byId('summaryCards').innerHTML = cards.map(([title, value, detail]) => `
    <article class="summary-card">
      <span>${escapeHtml(title)}</span>
      <strong>${escapeHtml(value)}</strong>
      <b>${escapeHtml(detail)}</b>
    </article>
  `).join('');
}

function renderHealthRail() {
  const pending = workers().filter(w => className(w.approval) === 'pending').length;
  const stale = countWorkers('stale') + countWorkers('offline');
  const failedJobs = jobs().filter(row => className(row.job?.state) === 'failed').length;
  const active = jobs().filter(row => ['running', 'reserved'].includes(className(row.job?.state))).length;
  const cells = [
    ['Controller', '1', 'good'],
    ['Ready workers', countWorkers('online') + countWorkers('idle') + countWorkers('busy'), stale ? 'warn' : 'good'],
    ['Running work', active, active ? 'good' : 'warn'],
    ['Pending approvals', pending, pending ? 'warn' : 'good'],
    ['Failed jobs', failedJobs, failedJobs ? 'bad' : 'good']
  ];

  byId('healthTimeline').innerHTML = cells.map(([label, value, tone]) => `
    <div class="health-cell ${tone}">
      <strong>${escapeHtml(value)}</strong>
      <span>${escapeHtml(label)}</span>
    </div>
  `).join('');
}

export function renderWorkers() {
  const list = filteredWorkers();
  byId('workers').innerHTML = list.length ? list.map(workerCard).join('') : empty('No workers match this view. Adjust the filter or rescan registered workers.');
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
  const installText = installs.length ? installs.map(i => `UE ${escapeHtml(i.version)} ${i.exists ? 'found' : 'not set'}`).join(' | ') : 'No Unreal installations reported';
  const pathText = paths.length ? paths.slice(0, 3).map(p => `${escapeHtml(p.path)} (${p.exists ? 'exists' : 'not set'})`).join('<br>') : 'No project paths reported';

  return `
    <article class="worker-card">
      <div class="worker-card-head">
        <div class="item-title">${escapeHtml(worker.name || worker.id)}</div>
        ${badge(worker.effectiveStatus || worker.status)}
      </div>
      <div class="meta">${escapeHtml(worker.hostname)} ${escapeHtml(worker.ipAddress)} ${escapeHtml(worker.serviceUrl)}</div>
      <div class="meta">Approval: ${escapeHtml(worker.approval || 'accepted')} | Mode: ${escapeHtml(worker.schedulingMode || 'Active')} | Heartbeat: ${formatDate(worker.lastHeartbeatUtc)} | Job: ${escapeHtml(worker.currentJobId) || 'none'}</div>
      <div class="meta">CPU ${escapeHtml(caps.cpuCores) || '-'} | GPU ${escapeHtml(caps.gpuName) || '-'} | Free disk ${escapeHtml(caps.freeDiskGb) || '-'} GB</div>
      <div class="meta">${installText}</div>
      <div class="meta">${pathText}</div>
      <div class="inline-actions" style="margin-top:10px">${workerActions(worker)}</div>
    </article>
  `;
}

function workerActions(worker) {
  const approval = className(worker.approval);
  let actions = `<button class="button" type="button" data-fill-project-worker="${escapeHtml(worker.id)}">Use in project form</button>`;
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
  byId('pendingWorkers').innerHTML = pending.length ? pending.map(worker => `
    <article class="item">
      <div class="item-head"><div class="item-title">${escapeHtml(worker.name || worker.id)}</div>${badge('pending')}</div>
      <div class="meta">${escapeHtml(worker.hostname)} ${escapeHtml(worker.ipAddress)} | ${escapeHtml(worker.serviceUrl)}</div>
      <div class="inline-actions" style="margin-top:10px"><button class="button primary" type="button" data-accept-worker="${escapeHtml(worker.id)}">Approve worker</button><button class="button" type="button" data-reject-worker="${escapeHtml(worker.id)}">Reject</button></div>
    </article>
  `).join('') : empty('No workers are pending approval. New machines appear here after their first heartbeat.');
}

function renderOutputs() {
  const roots = [];
  workers().forEach(worker => (worker.capabilities?.sharedOutputRoots || []).forEach(root => roots.push({ worker: worker.id, ...root })));
  byId('outputs').innerHTML = roots.length ? roots.map(root => `
    <article class="item">
      <div class="item-head"><div class="item-title">${escapeHtml(root.path)}</div>${badge(root.writable ? 'online' : 'error')}</div>
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
      <div class="inline-actions" style="margin-top:10px"><button class="button" type="button" data-scan-project="${escapeHtml(project.id)}">Scan project</button><button class="button" type="button" data-readiness-project="${escapeHtml(project.id)}">Check readiness</button><button class="button danger" type="button" data-delete-project="${escapeHtml(project.id)}">Delete</button></div>
    </article>
  `).join('') : empty('No projects are registered. Add a project before queueing renders.');
}

function renderProfiles() {
  byId('profiles').innerHTML = profiles().length ? profiles().map(profile => {
    const settings = profile.settings || {};
    return `
      <article class="item">
        <div class="item-head"><div class="item-title">${escapeHtml(profile.displayName)}</div>${badge(profile.type)}</div>
        <div class="meta">Project ${escapeHtml(profile.projectId)} | Render asset ${escapeHtml(profile.assetPath) || 'template or manual launch'}</div>
        <div class="meta">Map ${escapeHtml(settings.map || settings.level) || 'not set'} | Chunking ${profile.supportsChunking ? 'configured but not enabled' : 'off'}</div>
        <div class="meta">Requirements: ${renderProfileRequirements(settings)}</div>
        <div class="inline-actions" style="margin-top:10px"><button class="button danger" type="button" data-delete-profile="${escapeHtml(profile.id)}">Delete</button></div>
      </article>
    `;
  }).join('') : empty('No render profiles are available. Create a profile for a registered project.');
}

function renderJobs() {
  const rows = jobs();
  byId('jobs').innerHTML = rows.length ? rows.map(row => {
    const job = row.job;
    return `
      <tr data-job-id="${escapeHtml(job.id)}" tabindex="0" title="Open job details">
        <td><strong>${escapeHtml(job.name)}</strong><div class="meta">${escapeHtml(job.id)}</div></td>
        <td>${badge(job.state)}</td>
        <td>${escapeHtml(job.projectId)}</td>
        <td>${escapeHtml(job.renderProfileId)}</td>
        <td>${escapeHtml(job.assignedWorkerId) || '-'}</td>
        <td>${escapeHtml(row.attemptCount)}</td>
        <td>${escapeHtml(job.failureCategory || 'None')}</td>
        <td>${formatDate(job.updatedAtUtc)}</td>
      </tr>
    `;
  }).join('') : '<tr><td colspan="8"><div class="empty">No render jobs are queued. Create a job when a project and profile are ready.</div></td></tr>';
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
  document.querySelectorAll('select[name="projectId"], #scanProjectSelect').forEach(select => {
    const current = select.value;
    select.innerHTML = projects().map(project => `<option value="${escapeHtml(project.id)}">${escapeHtml(project.displayName)}</option>`).join('');
    if (current) select.value = current;
  });

  const selectedProject = document.querySelector('#jobForm select[name="projectId"]')?.value;
  const filteredProfiles = selectedProject ? profiles().filter(profile => profile.projectId === selectedProject) : profiles();
  const profileSelect = document.querySelector('#jobForm select[name="renderProfileId"]');
  if (profileSelect) {
    profileSelect.innerHTML = filteredProfiles.map(profile => `<option value="${escapeHtml(profile.id)}">${escapeHtml(profile.displayName)}</option>`).join('');
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
  byId(id).innerHTML = [...new Set(values.filter(Boolean))].sort().map(value => `<option value="${escapeHtml(value)}"></option>`).join('');
}

function countWorkers(status) {
  return workers().filter(worker => className(worker.effectiveStatus || worker.status) === status).length;
}

function empty(message) {
  return `<div class="empty">${escapeHtml(message)}</div>`;
}
