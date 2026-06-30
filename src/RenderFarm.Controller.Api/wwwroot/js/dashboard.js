import { getJson, sendJson, post, del, getStoredApiToken, setStoredApiToken } from './api.js';
import { state, setSnapshot, setActivities, setDiagnostics, workers, projects, profiles, jobs, diagnostics } from './state.js';
import { badge, byId, className, clearError, escapeHtml, formatDate, formatRelative, renderAll, renderJobs, renderLoading, renderSelects, renderWorkers, setBusy, showError, toast } from './render.js';

const compatibilityEndpoints = [
  '/api/dashboard',
  '/api/workers/status',
  '/api/projects',
  '/api/render-profiles',
  '/api/jobs'
];
void compatibilityEndpoints;

const refreshIntervalMs = 8000;
let refreshInFlight = false;
let activeJobDetailsRequest = 0;
let newRenderStep = 1;

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

function captureJobStates() {
  return new Map(jobs().map(row => [row.job.id, row.job.state]));
}

function captureWorkerApprovals() {
  return new Map(workers().map(worker => [worker.id, worker.approval]));
}

async function refresh({ rescan = false, quiet = false } = {}) {
  if (refreshInFlight || document.hidden) return;
  refreshInFlight = true;
  if (!quiet) setBusy(true);
  const previousJobs = captureJobStates();
  const previousApprovals = captureWorkerApprovals();
  try {
    if (rescan) {
      await post('/api/workers/rescan');
    }

    const [snapshot, recentActivity, diagnosticSnapshot] = await Promise.all([
      getJson('/api/dashboard'),
      getJson('/api/activity/recent?limit=100').catch(error => {
        console.warn('Activity feed unavailable', error);
        return [];
      }),
      getJson('/api/diagnostics').catch(error => {
        console.warn('Diagnostics unavailable', error);
        return null;
      })
    ]);
    setSnapshot(snapshot);
    setActivities(recentActivity);
    setDiagnostics(diagnosticSnapshot);
    const newActivityCount = notifyActivityChanges(recentActivity);
    notifySnapshotChanges(snapshot, previousJobs, previousApprovals, newActivityCount > 0);
    clearError();
    renderAll();
  } catch (error) {
    showError(error.message);
    toast(error.message, 'error');
  } finally {
    if (!quiet) setBusy(false);
    refreshInFlight = false;
  }
}

function notifyActivityChanges(recentActivity) {
  const events = Array.isArray(recentActivity) ? recentActivity : [];
  if (!state.activityNotificationsPrimed) {
    events.forEach(event => { if (event.id) state.seenActivityIds.add(event.id); });
    state.activityNotificationsPrimed = true;
    return 0;
  }

  const fresh = events
    .filter(event => event.id && !state.seenActivityIds.has(event.id))
    .sort((a, b) => new Date(a.timestampUtc || 0) - new Date(b.timestampUtc || 0));
  fresh.forEach(event => state.seenActivityIds.add(event.id));

  const toastable = fresh.filter(event => {
    const severity = className(event.severity);
    const type = className(event.type);
    return severity === 'warning' || severity === 'error' || severity === 'success' || type === 'approval' || type === 'job';
  });

  if (toastable.length === 1) {
    const event = toastable[0];
    const tone = className(event.severity) === 'error' ? 'error' : className(event.severity) === 'warning' ? 'warning' : className(event.severity) === 'success' ? 'success' : 'info';
    toast(`${event.title}: ${event.message}`, tone);
  } else if (toastable.length > 1) {
    toast(`${toastable.length} farm updates received. Review Activity for details.`, 'info');
  }

  return fresh.length;
}
function notifySnapshotChanges(snapshot, previousJobs, previousApprovals, suppressToast = false) {
  if (!state.notificationsPrimed) {
    state.notificationsPrimed = true;
    return;
  }

  const notifications = [];
  for (const worker of snapshot.workers || []) {
    const oldApproval = className(previousApprovals.get(worker.id));
    const newApproval = className(worker.approval);
    if (newApproval === 'pending' && oldApproval !== 'pending') {
      notifications.push({ message: `Worker approval required: ${worker.name || worker.id}`, tone: 'warning' });
    }
  }

  for (const row of snapshot.jobs || []) {
    const job = row.job;
    const oldState = className(previousJobs.get(job.id));
    const newState = className(job.state);
    if (!oldState || oldState === newState) continue;
    if (newState === 'succeeded' || newState === 'completed') {
      notifications.push({ message: `Render completed: ${job.name || job.id}`, tone: 'success' });
    } else if (newState === 'failed') {
      notifications.push({ message: `Render failed: ${job.name || job.id}`, tone: 'error' });
    } else if (newState === 'running') {
      notifications.push({ message: `Render started: ${job.name || job.id}`, tone: 'info' });
    }
  }

  if (suppressToast) return;

  if (notifications.length === 1) {
    toast(notifications[0].message, notifications[0].tone);
  } else if (notifications.length > 1) {
    toast(`${notifications.length} farm updates received. Review Activity for details.`, 'info');
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
    toast('No worker telemetry is available yet. Start or rescan a worker, then try again.', 'warning');
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

  toast(`Project setup populated from ${worker.name || worker.id}`, 'success');
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
    toast('Choose a farm configuration file or paste validated JSON first.', 'warning');
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

  toast(`Imported ${projectCount} project(s) and ${profileCount} profile(s).`, 'success');
  await refresh();
}

function openModal(id) {
  renderSelects();
  const modal = byId(id);
  if (!modal) return;
  if (typeof modal.showModal === 'function' && !modal.open) {
    modal.showModal();
  } else {
    modal.classList.add('is-open');
  }
}

function closeModal(id) {
  const modal = byId(id);
  if (!modal) return;
  if (typeof modal.close === 'function' && modal.open) {
    modal.close();
  }
  modal.classList.remove('is-open');
}

function openProfileModal(projectId = '') {
  renderSelects();
  const form = byId('profileForm');
  if (projectId) {
    form.elements.projectId.value = projectId;
    form.elements.id.value ||= `${projectId}-profile`;
    form.elements.displayName.value ||= `${projects().find(project => project.id === projectId)?.displayName || projectId} render profile`;
  }
  openModal('profileModal');
}

function openJobModalForProfile(profileId = '') {
  renderSelects();
  const form = byId('jobForm');
  const profile = profiles().find(item => item.id === profileId);
  if (profile) {
    form.elements.projectId.value = profile.projectId;
    renderSelects();
    form.elements.renderProfileId.value = profile.id;
    form.elements.name.value ||= `${profile.displayName} render`;
  }
  openModal('jobModal');
}
function wizardForm() {
  return byId('newRenderForm');
}

function wizardMode(name) {
  return wizardForm().elements[name]?.value || 'existing';
}

function setWizardMode(name, value) {
  const field = wizardForm().elements[name];
  if (field) field.value = value;
}

function selectedWizardProjectId() {
  const form = wizardForm();
  if (wizardMode('wizardProjectMode') === 'new') {
    return slug(form.elements.newProjectId.value || form.elements.newProjectName.value);
  }

  return byId('newRenderProjectSelect').value;
}

function selectedWizardProfileId() {
  const form = wizardForm();
  if (wizardMode('wizardProfileMode') === 'new') {
    return slug(form.elements.newProfileId.value || form.elements.newProfileName.value || `${selectedWizardProjectId()}-render-setup`);
  }

  return byId('newRenderProfileSelect').value;
}

function selectedWizardProject() {
  const projectId = selectedWizardProjectId();
  return projects().find(project => project.id === projectId);
}

function selectedWizardProfile() {
  const profileId = selectedWizardProfileId();
  return profiles().find(profile => profile.id === profileId);
}

function optionList(items, emptyText) {
  return items.length
    ? items.map(item => `<option value="${escapeHtml(item.id)}">${escapeHtml(item.displayName || item.id)}</option>`).join('')
    : `<option value="">${escapeHtml(emptyText)}</option>`;
}

function populateNewRenderWizard(seedProfileId = '') {
  renderSelects();
  const form = wizardForm();
  if (!form) return;

  const projectSelect = byId('newRenderProjectSelect');
  const profileSelect = byId('newRenderProfileSelect');
  const seededProfile = profiles().find(profile => profile.id === seedProfileId);

  projectSelect.innerHTML = optionList(projects(), 'No projects registered');
  if (seededProfile && projects().some(project => project.id === seededProfile.projectId)) {
    projectSelect.value = seededProfile.projectId;
  } else if (!projectSelect.value && projects()[0]) {
    projectSelect.value = projects()[0].id;
  }

  setWizardMode('wizardProjectMode', projects().length ? 'existing' : 'new');
  updateWizardProfileOptions(seedProfileId);
  if (!profileSelect.value && profiles().length === 0) {
    setWizardMode('wizardProfileMode', 'new');
  }

  const writableRoot = collectOutputRoots().find(root => root.writable);
  if (!form.elements.outputDirectory.value && writableRoot?.path) {
    form.elements.outputDirectory.value = writableRoot.path;
  }

  form.elements.priority.value ||= '0';
  updateWizardModes();
  updateWizardRenderFields();
  updateWizardReadiness();
  renderWizardReview();
}

function openNewRenderWizard(seedProfileId = '') {
  populateNewRenderWizard(seedProfileId);
  setNewRenderStep(1);
  openModal('newRenderModal');
}

function updateWizardProfileOptions(seedProfileId = '') {
  const profileSelect = byId('newRenderProfileSelect');
  const current = seedProfileId || profileSelect.value;
  const projectId = selectedWizardProjectId();
  const available = projectId ? profiles().filter(profile => profile.projectId === projectId) : profiles();
  profileSelect.innerHTML = optionList(available, 'No render setups for this project');
  if (current && available.some(profile => profile.id === current)) {
    profileSelect.value = current;
  }
  if (!available.length) {
    setWizardMode('wizardProfileMode', 'new');
  }
}

function updateWizardModes() {
  const projectMode = wizardMode('wizardProjectMode');
  const profileMode = wizardMode('wizardProfileMode');
  const newProject = projectMode === 'new';
  const existingSetupRadio = wizardForm().querySelector('input[name="wizardProfileMode"][value="existing"]');

  document.querySelectorAll('[data-project-existing]').forEach(element => element.classList.toggle('hidden', newProject));
  document.querySelectorAll('[data-project-new]').forEach(element => element.classList.toggle('hidden', !newProject));

  if (existingSetupRadio) {
    existingSetupRadio.disabled = newProject;
    if (newProject) setWizardMode('wizardProfileMode', 'new');
  }

  document.querySelectorAll('[data-profile-existing]').forEach(element => element.classList.toggle('hidden', wizardMode('wizardProfileMode') === 'new'));
  document.querySelectorAll('[data-profile-new]').forEach(element => element.classList.toggle('hidden', wizardMode('wizardProfileMode') !== 'new'));
  wizardForm().classList.toggle('is-new-project', newProject);
  wizardForm().classList.toggle('is-new-profile', profileMode === 'new');
}

function updateWizardRenderFields() {
  const type = wizardForm().elements.newProfileType?.value || 'MrqQueue';
  const customCommand = type === 'CommandTemplate';
  const usesAsset = type === 'MrqQueue' || type === 'MrgGraph';
  document.querySelectorAll('[data-wizard-command-field]').forEach(element => element.classList.toggle('hidden', !customCommand));
  document.querySelectorAll('[data-wizard-asset-field]').forEach(element => element.classList.toggle('hidden', !usesAsset));
}

function collectOutputRoots() {
  return workers().flatMap(worker => (worker.capabilities?.sharedOutputRoots || []).map(root => ({
    workerId: worker.id,
    workerName: worker.name || worker.id,
    approved: className(worker.approval || 'accepted') === 'accepted',
    status: className(worker.effectiveStatus || worker.status),
    mode: className(worker.schedulingMode || 'active'),
    ...root
  })));
}

function collectWizardReadiness() {
  const roots = collectOutputRoots();
  const approvedWorkers = workers().filter(worker => className(worker.approval || 'accepted') === 'accepted');
  const onlineWorkers = approvedWorkers.filter(worker => {
    const status = className(worker.effectiveStatus || worker.status);
    const mode = className(worker.schedulingMode || 'active');
    return (status === 'online' || status === 'idle') && mode === 'active';
  });
  const busyWorkers = approvedWorkers.filter(worker => className(worker.effectiveStatus || worker.status) === 'busy');
  const writableRoots = roots.filter(root => root.approved && root.writable);
  const warnings = [];

  if (!workers().length) warnings.push('No workers connected yet. Start a worker machine to make it appear here.');
  if (!approvedWorkers.length) warnings.push('No approved workers are available for scheduling.');
  if (!onlineWorkers.length) warnings.push(busyWorkers.length ? 'Workers are connected, but all approved workers are currently busy.' : 'No approved worker is currently idle or online.');
  if (!writableRoots.length) warnings.push('No validated shared output location yet. Start a worker and confirm its output root.');
  if (!wizardForm().elements.outputDirectory.value.trim()) warnings.push('No output location has been selected. The worker will use its configured default if the backend allows it.');

  return { roots, approvedWorkers, onlineWorkers, busyWorkers, writableRoots, warnings };
}

function updateWizardReadiness() {
  const target = byId('newRenderReadiness');
  if (!target) return;
  const readiness = collectWizardReadiness();
  const selectedOutput = wizardForm().elements.outputDirectory.value.trim();
  const outputCards = readiness.writableRoots.slice(0, 4).map(root => `
    <article class="readiness-card good">
      <div class="item-head"><div class="item-title">${escapeHtml(root.path)}</div>${badge('Writable')}</div>
      <div class="meta">Worker ${escapeHtml(root.workerName)} | ${escapeHtml(root.freeDiskGb) || '-'} GB free</div>
      <button class="button" type="button" data-copy="${escapeHtml(root.path)}" data-copy-label="output path">Copy path</button>
    </article>
  `).join('');

  target.innerHTML = `
    <div class="readiness-counters">
      <div><span>Approved workers</span><strong>${escapeHtml(readiness.approvedWorkers.length)}</strong></div>
      <div><span>Ready now</span><strong>${escapeHtml(readiness.onlineWorkers.length)}</strong></div>
      <div><span>Writable outputs</span><strong>${escapeHtml(readiness.writableRoots.length)}</strong></div>
    </div>
    ${selectedOutput ? `<div class="selected-output"><span>Selected output</span><code>${escapeHtml(selectedOutput)}</code></div>` : ''}
    ${readiness.warnings.length ? `<div class="wizard-warning">${readiness.warnings.map(warning => `<p>${escapeHtml(warning)}</p>`).join('')}</div>` : '<div class="callout">At least one approved worker has reported a writable output root.</div>'}
    <div class="readiness-grid">${outputCards || '<div class="empty">No validated shared output location yet. Start a worker and confirm its output root.</div>'}</div>
  `;
}

function renderWizardReview() {
  const target = byId('newRenderReview');
  if (!target) return;
  const form = wizardForm();
  const project = selectedWizardProject();
  const profile = selectedWizardProfile();
  const readiness = collectWizardReadiness();
  const projectName = wizardMode('wizardProjectMode') === 'new' ? form.elements.newProjectName.value || 'New project' : project?.displayName || selectedWizardProjectId() || 'No project selected';
  const profileName = wizardMode('wizardProfileMode') === 'new' ? form.elements.newProfileName.value || 'New render setup' : profile?.displayName || selectedWizardProfileId() || 'No render setup selected';
  const profileType = wizardMode('wizardProfileMode') === 'new' ? form.elements.newProfileType.value : profile?.type || '-';
  const jobName = form.elements.jobName.value || `${profileName} render`;
  const output = form.elements.outputDirectory.value || 'Worker/controller default';

  target.innerHTML = `
    <article class="review-card"><span>Project</span><strong>${escapeHtml(projectName)}</strong><p>${escapeHtml(wizardMode('wizardProjectMode') === 'new' ? 'Will be registered before queueing.' : 'Saved project')}</p></article>
    <article class="review-card"><span>Render Setup</span><strong>${escapeHtml(profileName)}</strong><p>${escapeHtml(profileType)}</p></article>
    <article class="review-card"><span>Output Location</span><strong>${escapeHtml(output)}</strong><p>${escapeHtml(readiness.writableRoots.length ? `${readiness.writableRoots.length} writable root(s) reported` : 'No writable output root reported')}</p></article>
    <article class="review-card"><span>Queue</span><strong>${escapeHtml(jobName)}</strong><p>Priority ${escapeHtml(form.elements.priority.value || 0)}</p></article>
    <article class="review-card full"><span>Worker Readiness</span><strong>${escapeHtml(readiness.onlineWorkers.length)} ready / ${escapeHtml(readiness.approvedWorkers.length)} approved</strong><p>${escapeHtml(readiness.warnings.join(' ') || 'No readiness warnings reported.')}</p></article>
  `;
}

function setNewRenderStep(step) {
  newRenderStep = Math.max(1, Math.min(4, Number(step || 1)));
  byId('newRenderError')?.classList.add('hidden');
  document.querySelectorAll('[data-wizard-pane]').forEach(pane => pane.classList.toggle('active', Number(pane.dataset.wizardPane) === newRenderStep));
  document.querySelectorAll('[data-wizard-jump]').forEach(button => {
    const buttonStep = Number(button.dataset.wizardJump);
    button.classList.toggle('active', buttonStep === newRenderStep);
    button.classList.toggle('complete', buttonStep < newRenderStep);
    button.setAttribute('aria-current', buttonStep === newRenderStep ? 'step' : 'false');
  });
  byId('newRenderBackBtn').classList.toggle('hidden', newRenderStep === 1);
  byId('newRenderNextBtn').classList.toggle('hidden', newRenderStep === 4);
  byId('newRenderQueueBtn').classList.toggle('hidden', newRenderStep !== 4);
  if (newRenderStep === 3) updateWizardReadiness();
  if (newRenderStep === 4) renderWizardReview();
}

function showWizardError(message, step = null) {
  if (step) setNewRenderStep(step);
  const error = byId('newRenderError');
  error.textContent = message;
  error.classList.remove('hidden');
}

function validateWizardStep(step) {
  const form = wizardForm();
  if (step === 1) {
    if (wizardMode('wizardProjectMode') === 'existing') {
      if (!selectedWizardProjectId()) return 'Choose an existing project or add a new project.';
      return '';
    }

    const name = form.elements.newProjectName.value.trim();
    const path = form.elements.newProjectPath.value.trim();
    const id = slug(form.elements.newProjectId.value || name);
    if (!name) return 'Enter a project name.';
    if (!path) return 'Enter the absolute path to the Unreal .uproject file.';
    if (!/\.uproject$/i.test(path)) return 'Project path should point to a .uproject file.';
    if (projects().some(project => className(project.id) === className(id))) return `Project ID "${id}" already exists. Choose a different ID.`;
  }

  if (step === 2) {
    if (wizardMode('wizardProfileMode') === 'existing') {
      if (!selectedWizardProfileId()) return 'Choose an existing render setup or add a new setup.';
      return '';
    }

    const name = form.elements.newProfileName.value.trim();
    const id = slug(form.elements.newProfileId.value || name || `${selectedWizardProjectId()}-render-setup`);
    if (!name) return 'Enter a render setup name.';
    if (profiles().some(profile => className(profile.id) === className(id))) return `Render setup ID "${id}" already exists. Choose a different ID.`;
    if (form.elements.newProfileType.value === 'CommandTemplate' && !form.elements.newProfileCommandTemplate.value.trim()) {
      return 'Enter a command template for custom command/template mode.';
    }
  }

  return '';
}

function validateWizardThrough(step) {
  for (let current = 1; current <= step; current += 1) {
    const error = validateWizardStep(current);
    if (error) {
      showWizardError(error, current);
      return false;
    }
  }
  return true;
}

function projectDtoFromWizard() {
  const form = wizardForm();
  const id = slug(form.elements.newProjectId.value || form.elements.newProjectName.value);
  const workerPaths = form.elements.newProjectWorkerId.value ? [{
    id: `${id}-${form.elements.newProjectWorkerId.value}`,
    projectId: id,
    workerId: form.elements.newProjectWorkerId.value,
    enginePath: normalizeEngineRoot(form.elements.newProjectEnginePath.value),
    projectPath: form.elements.newProjectWorkerPath.value || form.elements.newProjectPath.value,
    logDirectory: null
  }] : [];

  return {
    id,
    displayName: form.elements.newProjectName.value.trim(),
    uProjectPath: form.elements.newProjectPath.value.trim(),
    preferredEngineVersion: form.elements.newProjectEngineVersion.value.trim() || null,
    allowedEngineVersions: [],
    sharedOutputPolicyId: null,
    workerPaths
  };
}

function profileDtoFromWizard(projectId) {
  const form = wizardForm();
  const settings = {};
  addOptionalSetting(settings, 'map', form.elements.newProfileMap.value);
  addOptionalSetting(settings, 'extraArgs', form.elements.newProfileExtraArgs.value);

  return {
    id: slug(form.elements.newProfileId.value || form.elements.newProfileName.value || `${projectId}-render-setup`),
    projectId,
    displayName: form.elements.newProfileName.value.trim(),
    type: form.elements.newProfileType.value,
    assetPath: form.elements.newProfileAsset.value.trim() || null,
    commandTemplate: form.elements.newProfileCommandTemplate.value.trim() || null,
    defaultOutputType: 'png',
    supportsChunking: false,
    settings
  };
}

async function submitNewRenderWizard(event) {
  event.preventDefault();
  if (!validateWizardThrough(3)) return;

  setBusy(true);
  try {
    let projectId = selectedWizardProjectId();
    let profileId = selectedWizardProfileId();

    if (wizardMode('wizardProjectMode') === 'new') {
      const project = await sendJson('/api/projects', 'POST', projectDtoFromWizard());
      projectId = project?.id || projectId;
    }

    if (wizardMode('wizardProfileMode') === 'new') {
      const profile = await sendJson('/api/render-profiles', 'POST', profileDtoFromWizard(projectId));
      profileId = profile?.id || profileId;
    }

    const form = wizardForm();
    const profileName = selectedWizardProfile()?.displayName || form.elements.newProfileName.value || profileId;
    const job = await sendJson('/api/jobs', 'POST', {
      projectId,
      renderProfileId: profileId,
      name: form.elements.jobName.value.trim() || `${profileName} render`,
      priority: Number(form.elements.priority.value || 0),
      outputDirectory: form.elements.outputDirectory.value.trim() || null
    });

    toast('Render queued', 'success');
    form.reset();
    closeModal('newRenderModal');
    switchView('queue');
    await refresh();
    if (job?.id) await openJobDetails(job.id);
  } catch (error) {
    showWizardError(error.message || 'Unable to queue render.');
    toast(error.message || 'Unable to queue render.', 'error');
  } finally {
    setBusy(false);
  }
}

async function openJobDetails(jobId) {
  const requestId = ++activeJobDetailsRequest;
  state.selectedJobId = jobId;
  const drawer = byId('jobDrawer');
  setJobDrawerOpen(true);
  byId('jobDrawerTitle').textContent = 'Loading job...';
  byId('jobDrawerMeta').textContent = jobId;
  byId('jobDrawerActions').innerHTML = '';
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
    byId('jobDrawerActions').innerHTML = '';
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

function renderJobDrawer(job, attempts = [], events = []) {
  const artifact = latestArtifactSummary(events);
  const outputPath = job.outputDirectory || artifact?.outputDirectory || '';
  const failureText = buildFailureDetails(job, attempts, events);
  byId('jobDrawerTitle').textContent = job.name || job.id;
  byId('jobDrawerMeta').textContent = `${job.id} | ${job.projectId} | ${job.renderProfileId}`;
  byId('jobDrawerActions').innerHTML = renderJobDrawerActions(job, outputPath, failureText);

  const duration = formatDuration(job.startedAtUtc, job.finishedAtUtc || (isActiveJob(job.state) ? new Date().toISOString() : null));
  const summary = [
    ['State', badge(job.state)],
    ['Job ID', `${escapeHtml(job.id)}<button class="copy-inline" type="button" data-copy="${escapeHtml(job.id)}" data-copy-label="job ID">Copy</button>`],
    ['Project', `${escapeHtml(job.projectId)}<button class="copy-inline" type="button" data-copy="${escapeHtml(job.projectId)}" data-copy-label="project ID">Copy</button>`],
    ['Render profile', `${escapeHtml(job.renderProfileId)}<button class="copy-inline" type="button" data-copy="${escapeHtml(job.renderProfileId)}" data-copy-label="profile ID">Copy</button>`],
    ['Worker', job.assignedWorkerId ? `${escapeHtml(job.assignedWorkerId)}<button class="copy-inline" type="button" data-copy="${escapeHtml(job.assignedWorkerId)}" data-copy-label="worker ID">Copy</button>` : '-'],
    ['Attempts', escapeHtml(attempts.length || 0)],
    ['Output', outputPath ? `${escapeHtml(outputPath)}<button class="copy-inline" type="button" data-copy="${escapeHtml(outputPath)}" data-copy-label="output path">Copy</button>` : '-'],
    ['Failure', failureText ? `${escapeHtml(failureText)}<button class="copy-inline" type="button" data-copy="${escapeHtml(failureText)}" data-copy-label="failure details">Copy</button>` : 'None'],
    ['Queued', formatDate(job.queuedAtUtc)],
    ['Started', formatDate(job.startedAtUtc)],
    ['Finished', formatDate(job.finishedAtUtc)],
    ['Duration', duration],
    ['Cancellation', job.cancellationRequested ? badge('Cancel requested') : 'No']
  ];

  byId('jobDrawerSummary').innerHTML = summary.map(([label, value]) => `<div><span>${escapeHtml(label)}</span><strong>${value}</strong></div>`).join('');
  byId('jobDrawerAttempts').innerHTML = attempts.length ? attempts.map(attempt => {
    const attemptFailure = attempt.error || attempt.failureCategory || '';
    return `
      <article class="item attempt-card">
        <div class="item-head"><div class="item-title">Attempt ${escapeHtml(attempt.attemptNumber)}</div>${badge(attempt.state)}</div>
        <div class="meta">Worker ${escapeHtml(attempt.workerId) || '-'} | Exit ${escapeHtml(attempt.exitCode ?? '-')} | ${escapeHtml(attempt.failureCategory || 'None')}</div>
        <div class="meta">Started ${formatDate(attempt.startedAtUtc)} | Finished ${formatDate(attempt.finishedAtUtc)} | Duration ${escapeHtml(formatDuration(attempt.startedAtUtc, attempt.finishedAtUtc))}</div>
        ${attempt.commandLine ? `<div class="path-line"><span>Command</span><code>${escapeHtml(attempt.commandLine)}</code><button class="button" type="button" data-copy="${escapeHtml(attempt.commandLine)}" data-copy-label="attempt command">Copy command</button></div>` : ''}
        ${attempt.logFilePath ? `<div class="path-line"><span>Log</span><code>${escapeHtml(attempt.logFilePath)}</code><button class="button" type="button" data-copy="${escapeHtml(attempt.logFilePath)}" data-copy-label="log path">Copy log path</button></div>` : ''}
        ${attemptFailure ? `<div class="failure-box">${escapeHtml(attemptFailure)}<button class="button" type="button" data-copy="${escapeHtml(attemptFailure)}" data-copy-label="attempt failure">Copy failure</button></div>` : ''}
      </article>
    `;
  }).join('') : '<div class="empty">No attempts recorded for this job.</div>';

  byId('jobDrawerEvents').innerHTML = events.length ? events.map(event => `
    <article class="event-item">
      <div class="item-head"><div class="item-title">${escapeHtml(event.message)}</div>${event.state ? badge(event.state) : ''}</div>
      <div class="meta">${formatDate(event.createdAtUtc)} | ${escapeHtml(event.failureCategory || 'None')} | Worker ${escapeHtml(event.workerId) || '-'}</div>
    </article>
  `).join('') : '<div class="empty">No events recorded for this job.</div>';
  renderArtifactSummary(artifact, outputPath);
}

function renderJobDrawerActions(job, outputPath, failureText) {
  const actions = [
    `<button class="button" type="button" data-copy="${escapeHtml(job.id)}" data-copy-label="job ID">Copy job ID</button>`
  ];
  if (outputPath) actions.push(`<button class="button" type="button" data-copy="${escapeHtml(outputPath)}" data-copy-label="output path">Copy output</button>`);
  if (failureText) actions.push(`<button class="button" type="button" data-copy="${escapeHtml(failureText)}" data-copy-label="failure details">Copy failure</button>`);
  if (canRetryJob(job)) actions.push(`<button class="button primary" type="button" data-retry-job="${escapeHtml(job.id)}">Retry now</button>`);
  if (canCancelJob(job)) actions.push(`<button class="button danger" type="button" data-cancel-job="${escapeHtml(job.id)}">Cancel render</button>`);
  return actions.join('');
}

function canRetryJob(job) {
  return ['stale', 'retrywait'].includes(className(job.state));
}

function canCancelJob(job) {
  return !['succeeded', 'completed', 'failed', 'cancelled', 'cancelrequested', 'cancelling'].includes(className(job.state));
}

function isActiveJob(value) {
  return ['reserved', 'running', 'validatingworker', 'preparingunrealqueue', 'launchingunreal', 'rendering', 'verifyingoutputs'].includes(className(value));
}

function buildFailureDetails(job, attempts, events) {
  const parts = [];
  if (job.failureCategory && className(job.failureCategory) !== 'none') parts.push(`Category: ${job.failureCategory}`);
  if (job.error) parts.push(`Job error: ${job.error}`);
  const failedAttempt = [...attempts].reverse().find(attempt => attempt.error || (attempt.failureCategory && className(attempt.failureCategory) !== 'none'));
  if (failedAttempt) parts.push(`Attempt ${failedAttempt.attemptNumber}: ${failedAttempt.error || failedAttempt.failureCategory}`);
  const failureEvent = [...events].reverse().find(event => event.failureCategory && className(event.failureCategory) !== 'none');
  if (failureEvent) parts.push(`Event: ${failureEvent.message}`);
  return parts.join('\n');
}

function latestArtifactSummary(events) {
  return events.map(event => parseArtifactSummary(event.dataJson)).filter(Boolean).at(-1) || null;
}

function formatDuration(start, end) {
  if (!start || !end) return '-';
  const ms = Math.max(0, new Date(end).getTime() - new Date(start).getTime());
  const seconds = Math.floor(ms / 1000);
  if (seconds < 60) return `${seconds}s`;
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return `${minutes}m ${seconds % 60}s`;
  const hours = Math.floor(minutes / 60);
  return `${hours}h ${minutes % 60}m`;
}

function renderArtifactSummary(artifact, outputPath = '') {
  if (!artifact && !outputPath) {
    byId('jobDrawerArtifacts').innerHTML = '<div class="empty">No artifact summary has been recorded yet. Completed renders should report an output directory and file count after validation.</div>';
    return;
  }

  const files = (artifact?.sampleFiles || []).map(file => `<li>${escapeHtml(file)}</li>`).join('');
  byId('jobDrawerArtifacts').innerHTML = `
    <article class="item artifact-card">
      <div class="item-head"><div class="item-title">${escapeHtml(outputPath || artifact.outputDirectory)}</div>${artifact ? badge('Verified') : badge('Output path')}</div>
      <div class="meta">${artifact ? `${escapeHtml(artifact.fileCount)} file(s), ${formatBytes(artifact.totalBytes)}` : 'Output path recorded; artifact scan has not reported file counts yet.'}</div>
      ${outputPath ? `<button class="button" type="button" data-copy="${escapeHtml(outputPath)}" data-copy-label="output path">Copy output path</button>` : ''}
      ${files ? `<ul class="artifact-list">${files}</ul>` : ''}
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

function renderReadinessMatrix(matrix) {
  const rows = matrix.workers || [];
  byId('readinessResults').innerHTML = rows.length ? rows.map(worker => `
    <article class="item compact-item">
      <div class="item-head"><div class="item-title">${escapeHtml(worker.workerId)}</div>${badge(worker.canRun ? 'Ready' : 'Blocked')}</div>
      <div class="meta">${(worker.reasons || []).map(reason => escapeHtml(reason)).join('<br>') || 'Worker meets project and profile requirements.'}</div>
    </article>
  `).join('') : '<div class="empty">No workers are registered for readiness evaluation.</div>';
  switchView('projects');
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

async function copyText(label, value) {
  const text = String(value || '').trim();
  if (!text) {
    toast(`Nothing to copy for ${label || 'this field'}.`, 'warning');
    return;
  }

  try {
    if (navigator.clipboard?.writeText) {
      await navigator.clipboard.writeText(text);
    } else {
      fallbackCopyText(text);
    }
    toast(`Copied ${label || 'text'}`, 'success');
  } catch {
    fallbackCopyText(text);
    toast(`Copied ${label || 'text'}`, 'success');
  }
}

function fallbackCopyText(text) {
  const textarea = document.createElement('textarea');
  textarea.value = text;
  textarea.setAttribute('readonly', '');
  textarea.style.position = 'fixed';
  textarea.style.opacity = '0';
  document.body.appendChild(textarea);
  textarea.select();
  document.execCommand('copy');
  textarea.remove();
}

function diagnosticsText() {
  const data = diagnostics();
  if (!data) return 'RenderFarm diagnostics are not available yet. Refresh the controller dashboard and try again.';
  return JSON.stringify(data, null, 2);
}
async function handleDocumentClick(event) {
  const modalClose = event.target?.closest?.('[data-close-modal]');
  if (modalClose) {
    event.preventDefault();
    closeModal(modalClose.dataset.closeModal);
    return;
  }

  const newRenderTarget = event.target?.closest?.('[data-new-render]');
  if (newRenderTarget) {
    event.preventDefault();
    openNewRenderWizard();
    return;
  }

  const modalOpen = event.target?.closest?.('[data-modal-target]');
  if (modalOpen) {
    event.preventDefault();
    openModal(modalOpen.dataset.modalTarget);
    return;
  }

  const closeTarget = event.target?.closest?.('[data-close-job-drawer]');
  if (closeTarget) {
    closeJobDrawer(event);
    return;
  }

  const wizardJump = event.target?.closest?.('[data-wizard-jump]');
  if (wizardJump) {
    event.preventDefault();
    const targetStep = Number(wizardJump.dataset.wizardJump);
    if (targetStep <= newRenderStep || validateWizardThrough(targetStep - 1)) setNewRenderStep(targetStep);
    return;
  }

  const copyDiagnostics = event.target?.closest?.('#copyDiagnosticsBtn');
  if (copyDiagnostics) {
    event.preventDefault();
    await copyText('diagnostics', diagnosticsText());
    return;
  }

  const copyTarget = event.target?.closest?.('[data-copy]');
  if (copyTarget) {
    event.preventDefault();
    event.stopPropagation();
    await copyText(copyTarget.dataset.copyLabel || 'text', copyTarget.dataset.copy);
    return;
  }

  const queueFilter = event.target?.closest?.('[data-queue-filter]');
  if (queueFilter) {
    event.preventDefault();
    state.queueFilter = queueFilter.dataset.queueFilter || 'all';
    renderJobs();
    return;
  }

  const retryJob = event.target?.closest?.('[data-retry-job]');
  if (retryJob) {
    event.preventDefault();
    const jobId = retryJob.dataset.retryJob;
    await post(`/api/jobs/${encodeURIComponent(jobId)}/retry`);
    toast('Render requeued', 'success');
    await refresh();
    await openJobDetails(jobId);
    return;
  }

  const cancelJob = event.target?.closest?.('[data-cancel-job]');
  if (cancelJob) {
    event.preventDefault();
    const jobId = cancelJob.dataset.cancelJob;
    if (!window.confirm(`Cancel render ${jobId}?`)) return;
    await post(`/api/jobs/${encodeURIComponent(jobId)}/cancel`);
    toast('Cancellation requested', 'warning');
    await refresh();
    await openJobDetails(jobId);
    return;
  }

  const jobTarget = event.target?.closest?.('[data-job-id]');
  if (jobTarget) {
    event.preventDefault();
    await openJobDetails(jobTarget.dataset.jobId);
    return;
  }

  const actionTarget = event.target?.closest?.('[data-view], [data-fill-project-worker], [data-worker-mode], [data-accept-worker], [data-reject-worker], [data-readiness-project], [data-delete-project], [data-delete-profile], [data-open-profile-project], [data-queue-profile]');
  const data = actionTarget?.dataset;
  if (!data) return;

  try {
    if (data.view) {
      switchView(data.view);
      return;
    }

    if (data.fillProjectWorker) {
      openModal('projectModal');
      fillProjectFormFromWorker(data.fillProjectWorker);
    }
    if (data.openProfileProject) {
      openProfileModal(data.openProfileProject);
    }
    if (data.queueProfile) {
      openNewRenderWizard(data.queueProfile);
    }
    if (data.workerMode) {
      await sendJson(`/api/workers/${encodeURIComponent(data.workerMode)}/scheduling`, 'POST', { mode: data.mode });
      toast(`Worker mode set to ${data.mode}`, 'success');
      await refresh();
    }
    if (data.acceptWorker) {
      await post(`/api/workers/${encodeURIComponent(data.acceptWorker)}/approve`);
      toast('Worker approved for scheduling', 'success');
      await refresh();
    }
    if (data.rejectWorker) {
      await post(`/api/workers/${encodeURIComponent(data.rejectWorker)}/reject`);
      toast('Worker blocked from scheduling', 'warning');
      await refresh();
    }
    if (data.readinessProject) {
      const matrix = await getJson(`/api/projects/${encodeURIComponent(data.readinessProject)}/readiness`);
      const ready = matrix.workers.filter(worker => worker.canRun).length;
      renderReadinessMatrix(matrix);
      toast(`Readiness: ${ready}/${matrix.workers.length} worker(s) can run this project.`, ready ? 'success' : 'warning');
    }
    if (data.deleteProject && window.confirm(`Delete project ${data.deleteProject}? Jobs that depend on it will prevent deletion.`)) {
      await del(`/api/projects/${encodeURIComponent(data.deleteProject)}`);
      toast('Project removed', 'success');
      await refresh();
    }
    if (data.deleteProfile && window.confirm(`Delete render profile ${data.deleteProfile}? Jobs that depend on it will prevent deletion.`)) {
      await del(`/api/render-profiles/${encodeURIComponent(data.deleteProfile)}`);
      toast('Render profile removed', 'success');
      await refresh();
    }
  } catch (error) {
    toast(error.message, 'error');
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
    toast('Stale lease recovery requested', 'success');
    await refresh();
  });
  byId('clearJobsBtn').addEventListener('click', async () => {
    if (!window.confirm('Clear queue history, attempts, leases, and events? Projects, profiles, and workers will remain.')) return;
    const result = await del('/api/jobs');
    toast(result.message || 'Queue history cleared', 'success');
    await refresh();
  });
  byId('resetStateBtn').addEventListener('click', async () => {
    if (!window.confirm('Reset the controller database? This clears workers, projects, profiles, jobs, events, leases, and settings.')) return;
    const result = await del('/api/admin/state');
    toast(result.message || 'Controller database reset', 'success');
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
      toast(error.message, 'error');
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
  byId('saveApiTokenBtn').addEventListener('click', () => { setStoredApiToken(byId('apiTokenInput').value); toast('Controller token saved in this browser', 'success'); });
  byId('clearApiTokenBtn').addEventListener('click', () => { byId('apiTokenInput').value = ''; setStoredApiToken(''); toast('Controller token cleared', 'success'); });
  byId('previewChunksBtn').addEventListener('click', async () => { try { await previewChunks(); } catch (error) { toast(error.message, 'error'); } });

  byId('newRenderNextBtn').addEventListener('click', () => {
    if (validateWizardThrough(newRenderStep)) setNewRenderStep(newRenderStep + 1);
  });
  byId('newRenderBackBtn').addEventListener('click', () => setNewRenderStep(newRenderStep - 1));
  byId('newRenderForm').addEventListener('submit', submitNewRenderWizard);
  byId('newRenderForm').addEventListener('change', event => {
    if (event.target?.id === 'newRenderProjectSelect' || event.target?.name === 'wizardProjectMode') updateWizardProfileOptions();
    updateWizardModes();
    updateWizardRenderFields();
    updateWizardReadiness();
    renderWizardReview();
  });
  byId('newRenderForm').addEventListener('input', event => {
    if (event.target?.name === 'newProjectName' && !event.target.form.elements.newProjectId.value) {
      event.target.form.elements.newProjectId.placeholder = slug(event.target.value || 'project-id');
    }
    if (event.target?.name === 'newProfileName' && !event.target.form.elements.newProfileId.value) {
      event.target.form.elements.newProfileId.placeholder = slug(event.target.value || 'render-setup-id');
    }
    if (newRenderStep >= 3) updateWizardReadiness();
    renderWizardReview();
  });

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

    toast('Project registered', 'success');
    form.reset();
    closeModal('projectModal');
    switchView('projects');
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

    toast('Render profile created', 'success');
    form.reset();
    closeModal('profileModal');
    switchView('projects');
    await refresh();
  });

  byId('jobForm').addEventListener('submit', async event => {
    event.preventDefault();
    const form = event.target;
    const data = formData(form);
    const job = await sendJson('/api/jobs', 'POST', {
      projectId: data.projectId,
      renderProfileId: data.renderProfileId,
      name: data.name,
      priority: Number(data.priority || 0),
      outputDirectory: data.outputDirectory || null
    });

    toast('Render queued', 'success');
    form.reset();
    closeModal('jobModal');
    switchView('queue');
    await refresh();
    if (job?.id) await openJobDetails(job.id);
  });
}

renderLoading();
bindEvents();
await refresh();
state.refreshTimer = window.setInterval(() => refresh({ quiet: true }), refreshIntervalMs);
document.addEventListener('visibilitychange', () => {
  if (!document.hidden) void refresh({ quiet: true });
});












