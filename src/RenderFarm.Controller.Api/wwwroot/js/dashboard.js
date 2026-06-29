import { getJson, sendJson, post, del, getStoredApiToken, setStoredApiToken } from './api.js';
import { state, setSnapshot, workers, projects } from './state.js';
import { badge, byId, className, clearError, escapeHtml, formatDate, renderAll, renderLoading, renderSelects, renderWorkers, setBusy, showError, toast } from './render.js';

const compatibilityEndpoints = [
  '/api/dashboard',
  '/api/workers/status',
  '/api/projects',
  '/api/render-profiles',
  '/api/jobs'
];
void compatibilityEndpoints;

const refreshIntervalMs = 10000;
let refreshInFlight = false;
let activeJobDetailsRequest = 0;

function setJobDrawerOpen(isOpen) {
  const drawer = byId('jobDrawer');
  const backdrop = byId('jobDrawerBackdrop');
  drawer.classList.toggle('hidden', !isOpen);
  drawer.classList.toggle('is-open', isOpen);
  backdrop.classList.toggle('hidden', !isOpen);
  backdrop.classList.toggle('is-open', isOpen);
  drawer.setAttribute('aria-hidden', String(!isOpen));
  document.body.classList.toggle('drawer-open', isOpen);
}

async function refresh({ rescan = false } = {}) {
  if (refreshInFlight || document.hidden) return;
  refreshInFlight = true;
  setBusy(true);
  try {
    if (rescan) {
      await post('/api/workers/rescan');
    }

    const snapshot = await getJson('/api/dashboard');
    setSnapshot(snapshot);
    clearError();
    renderAll();
  } catch (error) {
    showError(error.message);
    toast(error.message);
  } finally {
    setBusy(false);
    refreshInFlight = false;
  }
}

function switchView(view) {
  state.currentView = view;
  document.querySelectorAll('.nav-tab').forEach(tab => tab.classList.toggle('active', tab.dataset.view === view));
  document.querySelectorAll('.view').forEach(panel => panel.classList.toggle('active', panel.dataset.panel === view));
}

function formData(form) {
  return Object.fromEntries(new FormData(form).entries());
}

function workerById(workerId) {
  return workers().find(worker => worker.id.toLowerCase() === String(workerId || '').toLowerCase());
}

function firstExisting(items) {
  return (items || []).find(item => item?.exists) || (items || [])[0];
}

function normalizeEngineRoot(value) {
  if (!value) return '';
  return value.replace(/[\\/]Engine[\\/]Binaries[\\/]Win64[\\/]UnrealEditor-Cmd\.exe$/i, '');
}

function fillProjectFormFromWorker(workerId) {
  const form = byId('projectForm');
  const worker = workerById(workerId) || workers()[0];
  if (!worker) {
    toast('No worker telemetry is available yet. Start or rescan a worker, then try again.');
    return;
  }

  const caps = worker.capabilities || {};
  const install = firstExisting(caps.unrealInstallations || []);
  const projectPath = firstExisting(caps.projectPaths || []);
  const output = firstExisting(caps.sharedOutputRoots || []);

  form.elements.workerId.value = worker.id;
  if (install) {
    form.elements.enginePath.value = normalizeEngineRoot(install.rootPath || install.executablePath);
    form.elements.preferredEngineVersion.value ||= install.version || '';
    form.elements.allowedEngineVersions.value ||= (caps.unrealInstallations || []).map(item => item.version).filter(Boolean).join(',');
  }

  if (projectPath) {
    form.elements.workerProjectPath.value ||= projectPath.path;
    form.elements.uProjectPath.value ||= projectPath.path;
  }

  if (output) {
    form.elements.logDirectory.value ||= `${output.path}\\logs`;
  }

  toast(`Project form populated from ${worker.id}`);
}

function slug(value) {
  return String(value || 'project').trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '') || 'project';
}

function renderType(value) {
  const key = String(value || '').toLowerCase().replace(/[^a-z0-9]/g, '');
  return {
    mrqqueue: 'MrqQueue',
    moviepipelinequeue: 'MrqQueue',
    mrggraph: 'MrgGraph',
    commandtemplate: 'CommandTemplate',
    manual: 'Manual'
  }[key] || 'MrqQueue';
}

function projectDtoFromLegacy(project) {
  const id = project.id || slug(project.displayName || project.name);
  const workerPathsObject = project.worker_paths || project.workerPaths || {};
  const workerPaths = Object.entries(workerPathsObject).map(([workerId, path]) => ({
    id: `${id}-${workerId}`,
    projectId: id,
    workerId,
    enginePath: normalizeEngineRoot(path.engine_path || path.enginePath || ''),
    projectPath: path.project_path || path.projectPath || project.uproject_path || project.uProjectPath || '',
    logDirectory: path.log_dir || path.logDirectory || null
  }));
  const firstProjectPath = workerPaths.find(path => path.projectPath)?.projectPath || project.uproject_path || project.uProjectPath || null;

  return {
    id,
    displayName: project.displayName || project.name || id,
    uProjectPath: firstProjectPath,
    preferredEngineVersion: project.preferred_engine_version || project.preferredEngineVersion || null,
    allowedEngineVersions: project.allowed_engine_versions || project.allowedEngineVersions || [],
    sharedOutputPolicyId: null,
    workerPaths
  };
}

function profileDtosFromLegacy(project, projectId) {
  const renderProfiles = project.render_profiles || project.renderProfiles || project.presets || [];
  const firstMap = project.default_map || project.defaultMap || ((project.maps || [])[0] && ((project.maps || [])[0].command || (project.maps || [])[0].path || (project.maps || [])[0].name)) || '';

  return renderProfiles.map(profile => {
    const id = profile.id || slug(profile.displayName || profile.name || profile.path || profile.asset_path);
    const settings = {};
    const map = profile.map || profile.map_name || profile.mapName || profile.level || profile.levelName || firstMap;
    const extraArgs = profile.extra_args || profile.extraArgs || '';
    if (map) settings.map = String(map);
    if (extraArgs) settings.extraArgs = String(extraArgs);
    if (profile.notes) settings.notes = String(profile.notes);

    return {
      id,
      projectId,
      displayName: profile.displayName || profile.name || id,
      type: renderType(profile.type),
      assetPath: profile.asset_path || profile.assetPath || profile.path || null,
      commandTemplate: profile.command_template || profile.commandTemplate || null,
      defaultOutputType: profile.default_output_type || profile.defaultOutputType || 'png',
      supportsChunking: Boolean(profile.supports_chunking ?? profile.supportsChunking),
      settings
    };
  });
}

async function readConfigText() {
  const file = byId('configFile').files[0];
  if (file) return file.text();
  return byId('configText').value;
}

async function importConfig() {
  const raw = (await readConfigText()).trim();
  if (!raw) {
    toast('Choose a farm configuration file or paste validated JSON first.');
    return;
  }

  const config = JSON.parse(raw);
  const importedProjects = Array.isArray(config.projects) ? config.projects : [config];
  let projectCount = 0;
  let profileCount = 0;

  for (const item of importedProjects) {
    const project = projectDtoFromLegacy(item);
    await sendJson('/api/projects', 'POST', project);
    projectCount += 1;

    for (const profile of profileDtosFromLegacy(item, project.id)) {
      await sendJson('/api/render-profiles', 'POST', profile);
      profileCount += 1;
    }
  }

  toast(`Imported ${projectCount} project(s) and ${profileCount} profile(s).`);
  await refresh();
}

async function openJobDetails(jobId) {
  const requestId = ++activeJobDetailsRequest;
  state.selectedJobId = jobId;
  const drawer = byId('jobDrawer');
  setJobDrawerOpen(true);
  byId('jobDrawerTitle').textContent = 'Loading job...';
  byId('jobDrawerMeta').textContent = jobId;
  byId('jobDrawerSummary').innerHTML = '<div class="skeleton"></div>';
  byId('jobDrawerAttempts').innerHTML = '<div class="skeleton"></div>';
  byId('jobDrawerEvents').innerHTML = '<div class="skeleton"></div>';
  byId('jobDrawerArtifacts').innerHTML = '<div class="skeleton"></div>';

  try {
    const [job, attempts, events] = await Promise.all([
      getJson(`/api/jobs/${encodeURIComponent(jobId)}`),
      getJson(`/api/jobs/${encodeURIComponent(jobId)}/attempts`),
      getJson(`/api/jobs/${encodeURIComponent(jobId)}/events`)
    ]);
    if (requestId !== activeJobDetailsRequest || state.selectedJobId !== jobId || drawer.classList.contains('hidden')) return;
    renderJobDrawer(job, attempts, events);
  } catch (error) {
    if (requestId !== activeJobDetailsRequest || state.selectedJobId !== jobId || drawer.classList.contains('hidden')) return;
    byId('jobDrawerSummary').innerHTML = `<div class="empty">${escapeHtml(error.message)}</div>`;
    byId('jobDrawerAttempts').innerHTML = '';
    byId('jobDrawerEvents').innerHTML = '';
    byId('jobDrawerArtifacts').innerHTML = '';
  }
}

function closeJobDrawer(event) {
  event?.preventDefault?.();
  event?.stopPropagation?.();
  event?.stopImmediatePropagation?.();
  activeJobDetailsRequest += 1;
  state.selectedJobId = null;
  setJobDrawerOpen(false);
  byId('closeJobDrawer').blur();
}

function renderJobDrawer(job, attempts, events) {
  byId('jobDrawerTitle').textContent = job.name || job.id;
  byId('jobDrawerMeta').textContent = `${job.id} | ${job.projectId} | ${job.renderProfileId}`;
  const summary = [
    ['State', badge(job.state)],
    ['Project', escapeHtml(job.projectId)],
    ['Render profile', escapeHtml(job.renderProfileId)],
    ['Worker', escapeHtml(job.assignedWorkerId) || '-'],
    ['Output', escapeHtml(job.outputDirectory) || '-'],
    ['Failure', escapeHtml(job.failureCategory || 'None')],
    ['Error', escapeHtml(job.error) || '-'],
    ['Created', formatDate(job.createdAtUtc)],
    ['Queued', formatDate(job.queuedAtUtc)],
    ['Started', formatDate(job.startedAtUtc)],
    ['Finished', formatDate(job.finishedAtUtc)],
    ['Cancellation', job.cancellationRequested ? 'Requested' : 'No']
  ];
  byId('jobDrawerSummary').innerHTML = summary.map(([label, value]) => `<div><span>${escapeHtml(label)}</span><strong>${value}</strong></div>`).join('');
  byId('jobDrawerAttempts').innerHTML = attempts.length ? attempts.map(attempt => `
    <article class="item">
      <div class="item-head"><div class="item-title">Attempt ${escapeHtml(attempt.attemptNumber)}</div>${badge(attempt.state)}</div>
      <div class="meta">Worker ${escapeHtml(attempt.workerId) || '-'} | Exit ${escapeHtml(attempt.exitCode) || '-'} | ${escapeHtml(attempt.failureCategory || 'None')}</div>
      <div class="meta">Started ${formatDate(attempt.startedAtUtc)} | Finished ${formatDate(attempt.finishedAtUtc)}</div>
      <div class="meta">${escapeHtml(attempt.error)}</div>
    </article>
  `).join('') : '<div class="empty">No attempts recorded for this job.</div>';
  byId('jobDrawerEvents').innerHTML = events.length ? events.map(event => `
    <article class="event-item">
      <div class="item-head"><div class="item-title">${escapeHtml(event.message)}</div>${event.state ? badge(event.state) : ''}</div>
      <div class="meta">${formatDate(event.createdAtUtc)} | ${escapeHtml(event.failureCategory || 'None')} | Worker ${escapeHtml(event.workerId) || '-'}</div>
    </article>
  `).join('') : '<div class="empty">No events recorded for this job.</div>';
  renderArtifactSummary(events);
}

function renderArtifactSummary(events) {
  const summaries = events.map(event => parseArtifactSummary(event.dataJson)).filter(Boolean);
  const artifact = summaries.at(-1);
  if (!artifact) {
    byId('jobDrawerArtifacts').innerHTML = '<div class="empty">No artifact summary has been recorded yet.</div>';
    return;
  }

  const files = (artifact.sampleFiles || []).map(file => `<li>${escapeHtml(file)}</li>`).join('');
  byId('jobDrawerArtifacts').innerHTML = `
    <article class="item">
      <div class="item-head"><div class="item-title">${escapeHtml(artifact.outputDirectory)}</div>${badge('Succeeded')}</div>
      <div class="meta">${escapeHtml(artifact.fileCount)} file(s), ${formatBytes(artifact.totalBytes)}</div>
      <ul class="artifact-list">${files}</ul>
    </article>
  `;
}

function parseArtifactSummary(dataJson) {
  if (!dataJson) return null;
  try {
    const parsed = JSON.parse(dataJson);
    return parsed && typeof parsed === 'object' && 'outputDirectory' in parsed && 'fileCount' in parsed ? parsed : null;
  } catch {
    return null;
  }
}

function formatBytes(value) {
  const bytes = Number(value || 0);
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  if (bytes < 1024 * 1024 * 1024) return `${(bytes / 1024 / 1024).toFixed(1)} MB`;
  return `${(bytes / 1024 / 1024 / 1024).toFixed(2)} GB`;
}

function scanAssetOptions(scan) {
  const options = [];
  (scan.movieRenderQueueConfigs || []).forEach(asset => options.push({ type: 'MrqQueue', asset }));
  (scan.movieRenderGraphs || []).forEach(asset => options.push({ type: 'MrgGraph', asset }));
  (scan.levelSequences || []).forEach(asset => options.push({ type: 'Manual', asset }));
  return options;
}

function renderSelectOptions(select, values, emptyLabel) {
  select.innerHTML = values.length
    ? values.map(value => `<option value="${escapeHtml(value)}">${escapeHtml(value)}</option>`).join('')
    : `<option value="">${escapeHtml(emptyLabel)}</option>`;
}

function renderScanWorkflow(projectId, scan) {
  state.lastScan = { projectId, scan };
  byId('scanProjectSelect').value = projectId;
  const maps = scan.maps || [];
  const assets = scanAssetOptions(scan);
  renderSelectOptions(byId('scanMapSelect'), maps, 'No maps discovered');
  byId('scanAssetSelect').innerHTML = assets.length
    ? assets.map(item => `<option value="${escapeHtml(`${item.type}::${item.asset}`)}">${escapeHtml(item.type)} - ${escapeHtml(item.asset)}</option>`).join('')
    : '<option value="">No render assets discovered</option>';

  const defaultAsset = assets[0];
  if (defaultAsset) byId('scanProfileType').value = defaultAsset.type;
  const project = projects().find(item => item.id === projectId);
  const base = slug(`${project?.displayName || projectId}-${defaultAsset?.asset || maps[0] || 'scan'}`);
  byId('scanProfileId').value = base;
  byId('scanProfileName').value = project ? `${project.displayName} scanned profile` : 'Scanned render profile';
  byId('scanResults').classList.remove('empty');
  byId('scanResults').innerHTML = `
    <div class="scan-grid">
      <div><strong>${escapeHtml(maps.length)}</strong><span>maps</span></div>
      <div><strong>${escapeHtml((scan.levelSequences || []).length)}</strong><span>sequences</span></div>
      <div><strong>${escapeHtml((scan.movieRenderQueueConfigs || []).length)}</strong><span>MRQ configs</span></div>
      <div><strong>${escapeHtml((scan.movieRenderGraphs || []).length)}</strong><span>MRG graphs</span></div>
    </div>
    <div class="meta">${escapeHtml(scan.projectPath)}${scan.engineVersion ? ` | UE ${escapeHtml(scan.engineVersion)}` : ''}</div>
  `;
}

async function runProjectScan(projectId) {
  if (!projectId) {
    toast('Select a project before scanning.');
    return;
  }

  const scan = await sendJson(`/api/projects/${encodeURIComponent(projectId)}/scan`, 'POST', {});
  renderScanWorkflow(projectId, scan);
  switchView('create');
  toast(`Scan found ${scan.maps.length} map(s), ${scan.levelSequences.length} sequence(s), ${scan.movieRenderQueueConfigs.length} MRQ config(s), and ${scan.movieRenderGraphs.length} MRG graph(s).`);
}

async function createProfileFromScan() {
  const projectId = byId('scanProjectSelect').value;
  const selectedAsset = byId('scanAssetSelect').value;
  const [assetType, assetPath] = selectedAsset.includes('::') ? selectedAsset.split('::', 2) : [byId('scanProfileType').value, selectedAsset];
  const map = byId('scanMapSelect').value;
  const settings = {};
  if (map) settings.map = map;
  if (assetType === 'Manual' && assetPath) settings.sequence = assetPath;

  await sendJson('/api/render-profiles', 'POST', {
    id: byId('scanProfileId').value || slug(`${projectId}-${assetPath || map || 'scan'}`),
    projectId,
    displayName: byId('scanProfileName').value || 'Scanned render profile',
    type: assetType || byId('scanProfileType').value || 'MrqQueue',
    assetPath: assetPath || null,
    commandTemplate: null,
    defaultOutputType: 'png',
    supportsChunking: false,
    settings
  });

  toast('Render profile created from scan');
  await refresh();
}

function renderReadinessMatrix(matrix) {
  const rows = matrix.workers || [];
  byId('scanResults').classList.remove('empty');
  byId('scanResults').innerHTML = rows.length ? rows.map(worker => `
    <article class="item compact-item">
      <div class="item-head"><div class="item-title">${escapeHtml(worker.workerId)}</div>${badge(worker.canRun ? 'Ready' : 'Blocked')}</div>
      <div class="meta">${(worker.reasons || []).map(reason => escapeHtml(reason)).join('<br>') || 'Worker meets project and profile requirements.'}</div>
    </article>
  `).join('') : '<div class="empty">No workers are registered for readiness evaluation.</div>';
  switchView('create');
}

async function previewChunks() {
  const form = byId('jobForm');
  const data = formData(form);
  const frameStart = Number(data.frameStart);
  const frameEnd = Number(data.frameEnd);
  const chunkSizeFrames = Number(data.chunkSizeFrames);
  if (!Number.isInteger(frameStart) || !Number.isInteger(frameEnd) || !Number.isInteger(chunkSizeFrames)) {
    byId('chunkPreview').innerHTML = '<div class="empty">Enter whole-number frame start, frame end, and chunk size to preview chunks.</div>';
    return;
  }

  const result = await sendJson('/api/jobs/chunk-preview', 'POST', {
    projectId: data.projectId,
    renderProfileId: data.renderProfileId,
    frameStart,
    frameEnd,
    chunkSizeFrames,
    outputDirectory: data.outputDirectory || null
  });
  state.chunkPreview = result;
  byId('chunkPreview').classList.remove('empty');
  byId('chunkPreview').innerHTML = `
    <div class="chunk-list">
      ${result.chunks.map(chunk => `<div><strong>#${escapeHtml(chunk.chunkIndex + 1)}</strong><span>${escapeHtml(chunk.frameStart)}-${escapeHtml(chunk.frameEnd)}</span><em>${escapeHtml(chunk.outputNameHint)}</em></div>`).join('')}
    </div>
  `;
}

function addOptionalSetting(settings, key, value) {
  if (value !== undefined && value !== null && String(value).trim() !== '') settings[key] = String(value).trim();
}
async function handleDocumentClick(event) {
  const closeTarget = event.target?.closest?.('[data-close-job-drawer]');
  if (closeTarget) {
    closeJobDrawer(event);
    return;
  }

  const jobRow = event.target?.closest?.('tr[data-job-id]');
  if (jobRow) {
    await openJobDetails(jobRow.dataset.jobId);
    return;
  }

  const actionTarget = event.target?.closest?.('[data-view], [data-fill-project-worker], [data-worker-mode], [data-accept-worker], [data-reject-worker], [data-scan-project], [data-readiness-project], [data-delete-project], [data-delete-profile]');
  const data = actionTarget?.dataset;
  if (!data) return;

  try {
    if (data.view) {
      switchView(data.view);
      return;
    }

    if (data.fillProjectWorker) fillProjectFormFromWorker(data.fillProjectWorker);
    if (data.workerMode) {
      await sendJson(`/api/workers/${encodeURIComponent(data.workerMode)}/scheduling`, 'POST', { mode: data.mode });
      toast(`Worker mode set to ${data.mode}`);
      await refresh();
    }
    if (data.acceptWorker) {
      await post(`/api/workers/${encodeURIComponent(data.acceptWorker)}/approve`);
      toast('Worker approved for scheduling');
      await refresh();
    }
    if (data.rejectWorker) {
      await post(`/api/workers/${encodeURIComponent(data.rejectWorker)}/reject`);
      toast('Worker blocked from scheduling');
      await refresh();
    }
    if (data.scanProject) {
      await runProjectScan(data.scanProject);
    }
    if (data.readinessProject) {
      const matrix = await getJson(`/api/projects/${encodeURIComponent(data.readinessProject)}/readiness`);
      const ready = matrix.workers.filter(worker => worker.canRun).length;
      renderReadinessMatrix(matrix);
      toast(`Readiness: ${ready}/${matrix.workers.length} worker(s) can run this project.`);
    }
    if (data.deleteProject && window.confirm(`Delete project ${data.deleteProject}? Jobs that depend on it will prevent deletion.`)) {
      await del(`/api/projects/${encodeURIComponent(data.deleteProject)}`);
      toast('Project removed');
      await refresh();
    }
    if (data.deleteProfile && window.confirm(`Delete render profile ${data.deleteProfile}? Jobs that depend on it will prevent deletion.`)) {
      await del(`/api/render-profiles/${encodeURIComponent(data.deleteProfile)}`);
      toast('Render profile removed');
      await refresh();
    }
  } catch (error) {
    toast(error.message);
  }
}

function bindEvents() {
  document.addEventListener('click', handleDocumentClick);
  byId('closeJobDrawer').addEventListener('click', closeJobDrawer);
  byId('jobDrawerBackdrop').addEventListener('click', closeJobDrawer);
  document.addEventListener('keydown', event => {
    if (event.key === 'Escape') closeJobDrawer();
    if (event.key === 'Enter' && event.target?.matches?.('[data-job-id]')) void openJobDetails(event.target.dataset.jobId);
  });
  byId('refreshBtn').addEventListener('click', () => refresh({ rescan: true }));
  byId('rescanWorkersBtn').addEventListener('click', () => refresh({ rescan: true }));
  byId('expireBtn').addEventListener('click', async () => {
    await post('/api/jobs/expire-leases');
    toast('Stale lease recovery requested');
    await refresh();
  });
  byId('clearJobsBtn').addEventListener('click', async () => {
    if (!window.confirm('Clear queue history, attempts, leases, and events? Projects, profiles, and workers will remain.')) return;
    const result = await del('/api/jobs');
    toast(result.message || 'Queue history cleared');
    await refresh();
  });
  byId('resetStateBtn').addEventListener('click', async () => {
    if (!window.confirm('Reset the controller database? This clears workers, projects, profiles, jobs, events, leases, and settings.')) return;
    const result = await del('/api/admin/state');
    toast(result.message || 'Controller database reset');
    await refresh();
  });
  byId('fillWorkerBtn').addEventListener('click', () => fillProjectFormFromWorker(byId('projectForm').elements.workerId.value));
  byId('clearConfigBtn').addEventListener('click', () => {
    byId('configFile').value = '';
    byId('configText').value = '';
  });
  byId('importConfigBtn').addEventListener('click', async () => {
    setBusy(true);
    try {
      await importConfig();
    } catch (error) {
      toast(error.message);
    } finally {
      setBusy(false);
    }
  });
  byId('searchBox').addEventListener('input', event => {
    state.search = event.target.value;
    renderWorkers();
  });
  byId('statusFilter').addEventListener('change', event => {
    state.workerStatus = className(event.target.value);
    renderWorkers();
  });
  byId('jobForm').addEventListener('change', renderSelects);
  byId('apiTokenInput').value = getStoredApiToken();
  byId('saveApiTokenBtn').addEventListener('click', () => { setStoredApiToken(byId('apiTokenInput').value); toast('Controller token saved in this browser'); });
  byId('clearApiTokenBtn').addEventListener('click', () => { byId('apiTokenInput').value = ''; setStoredApiToken(''); toast('Controller token cleared'); });
  byId('scanSelectedProjectBtn').addEventListener('click', () => runProjectScan(byId('scanProjectSelect').value));
  byId('createProfileFromScanBtn').addEventListener('click', createProfileFromScan);
  byId('scanAssetSelect').addEventListener('change', event => { const [type] = event.target.value.split('::', 2); if (type) byId('scanProfileType').value = type; });
  byId('previewChunksBtn').addEventListener('click', async () => { try { await previewChunks(); } catch (error) { toast(error.message); } });

  byId('projectForm').addEventListener('submit', async event => {
    event.preventDefault();
    const form = event.target;
    const data = formData(form);
    const projectId = data.id.trim();
    const workerPaths = data.workerId ? [{
      id: `${projectId}-${data.workerId}`,
      projectId,
      workerId: data.workerId,
      enginePath: normalizeEngineRoot(data.enginePath),
      projectPath: data.workerProjectPath || data.uProjectPath,
      logDirectory: data.logDirectory || null
    }] : [];

    await sendJson('/api/projects', 'POST', {
      id: projectId,
      displayName: data.displayName,
      uProjectPath: data.uProjectPath || null,
      preferredEngineVersion: data.preferredEngineVersion || null,
      allowedEngineVersions: (data.allowedEngineVersions || '').split(',').map(value => value.trim()).filter(Boolean),
      sharedOutputPolicyId: null,
      workerPaths
    });

    toast('Project registered');
    form.reset();
    await refresh();
  });

  byId('profileForm').addEventListener('submit', async event => {
    event.preventDefault();
    const form = event.target;
    const data = formData(form);
    const settings = {};
    if (data.mapName) settings.map = data.mapName;
    if (data.extraArgs) settings.extraArgs = data.extraArgs;
    addOptionalSetting(settings, 'minCpuCores', data.minCpuCores);
    addOptionalSetting(settings, 'minRamGb', data.minRamGb);
    addOptionalSetting(settings, 'minVramGb', data.minVramGb);
    addOptionalSetting(settings, 'gpuNameContains', data.gpuNameContains);

    await sendJson('/api/render-profiles', 'POST', {
      id: data.id,
      projectId: data.projectId,
      displayName: data.displayName,
      type: data.type,
      assetPath: data.assetPath || null,
      commandTemplate: data.commandTemplate || null,
      defaultOutputType: 'png',
      supportsChunking: false,
      settings
    });

    toast('Render profile created');
    form.reset();
    await refresh();
  });

  byId('jobForm').addEventListener('submit', async event => {
    event.preventDefault();
    const form = event.target;
    const data = formData(form);
    await sendJson('/api/jobs', 'POST', {
      projectId: data.projectId,
      renderProfileId: data.renderProfileId,
      name: data.name,
      priority: Number(data.priority || 0),
      outputDirectory: data.outputDirectory || null
    });

    toast('Render queued');
    form.reset();
    await refresh();
  });
}

renderLoading();
bindEvents();
await refresh();
state.refreshTimer = window.setInterval(() => refresh(), refreshIntervalMs);
document.addEventListener('visibilitychange', () => {
  if (!document.hidden) void refresh();
});

