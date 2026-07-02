import { getJson, sendJson, post, del, getStoredApiToken, setStoredApiToken } from './api.js';
import { state, setSnapshot, setActivities, setDiagnostics, setRenderDefaults, workers, projects, profiles, jobs, diagnostics, renderDefaults } from './state.js';
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
let pendingConfirmation = null;

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

    const [snapshot, recentActivity, diagnosticSnapshot, renderDefaultsSnapshot] = await Promise.all([
      getJson('/api/dashboard'),
      getJson('/api/activity/recent?limit=100').catch(error => {
        console.warn('Activity feed unavailable', error);
        return [];
      }),
      getJson('/api/diagnostics').catch(error => {
        console.warn('Diagnostics unavailable', error);
        return null;
      }),
      getJson('/api/settings/render-defaults').catch(error => {
        console.warn('Render defaults unavailable', error);
        return null;
      })
    ]);
    setSnapshot(snapshot);
    setActivities(recentActivity);
    setDiagnostics(diagnosticSnapshot);
    setRenderDefaults(renderDefaultsSnapshot);
    populateRenderDefaultsForm();
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

function populateRenderDefaultsForm() {
  const form = byId('renderDefaultsForm');
  if (!form || form.matches(':focus-within')) return;
  const defaults = renderDefaults() || {};
  form.elements.unrealExecutablePath.value = defaults.unrealExecutablePath || '';
  form.elements.unrealSearchRoot.value = defaults.unrealSearchRoot || '';
  form.elements.sharedOutputRoot.value = defaults.sharedOutputRoot || '';
  form.elements.outputSubfolderPattern.value = defaults.outputSubfolderPattern || '{JobId}';
}

async function saveRenderDefaults(event) {
  event.preventDefault();
  const data = formData(event.target);
  const saved = await sendJson('/api/settings/render-defaults', 'PUT', {
    unrealExecutablePath: data.unrealExecutablePath || null,
    unrealSearchRoot: data.unrealSearchRoot || null,
    sharedOutputRoot: data.sharedOutputRoot || null,
    outputSubfolderPattern: data.outputSubfolderPattern || '{JobId}'
  });
  setRenderDefaults(saved);
  populateRenderDefaultsForm();
  toast('Controller render defaults saved', 'success');
  await refresh({ quiet: true });
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

const unrealPathKind = {
  world: 'WorldPackagePath',
  sequence: 'LevelSequenceObjectPath',
  config: 'MoviePipelineConfigObjectPath',
  queue: 'MoviePipelineQueueObjectPath'
};

function isSimpleMapUrl(value) {
  return /^[A-Za-z0-9_-]+$/.test(String(value || ''));
}

function normalizeModeKey(value) {
  return String(value || '').toLowerCase().replace(/[^a-z0-9]/g, '');
}

function isSingleSequenceModeValue(value) {
  return ['single', 'singlesequence', 'sequence', 'levelsequence', 'config', 'configpreset', 'sequenceconfig', 'singlelevelsequence'].includes(normalizeModeKey(value));
}

function isQueueModeValue(value) {
  return ['queue', 'savedqueue', 'queuepreset', 'mrqqueue', 'moviepipelinequeue'].includes(normalizeModeKey(value));
}

function movieRenderMode(profile) {
  const explicit = profileSetting(profile, 'mrqMode') || profileSetting(profile, 'renderMode') || profileSetting(profile, 'movieRenderMode') || profileSetting(profile, 'moviePipelineMode') || profileSetting(profile, 'launchMode');
  if (isSingleSequenceModeValue(explicit)) return 'single';
  if (isQueueModeValue(explicit)) return 'queue';

  const sequence = profileSetting(profile, 'sequence') || profileSetting(profile, 'levelSequence');
  return sequence ? 'single' : 'queue';
}

function normalizeUnrealReference(raw, expectedKind) {
  const original = String(raw || '').trim();
  if (!original) return { ok: false, value: '', warnings: [], error: 'Path is required.' };

  let value = original.replace(/^"|"$/g, '').trim();
  const warnings = [];

  const wrapped = value.match(/^[A-Za-z0-9_]+\s*'([^']+)'$/);
  if (wrapped) {
    value = wrapped[1];
    warnings.push('Removed the copied Unreal reference wrapper.');
  }

  const uasset = value.replace(/\\/g, '/').match(/\/Content\/(.+)\.uasset$/i);
  if (uasset) {
    value = `/Game/${uasset[1]}`;
    warnings.push('Converted a .uasset filesystem path to an Unreal /Game path.');
  }

  value = value.replace(/\\/g, '/').trim();
  if (value.includes('/Content/') || value.toLowerCase().startsWith('content/')) {
    return { ok: false, value, warnings, error: 'Use Unreal mount paths such as /Game/RenderConfig.RenderConfig; do not include /Content/.' };
  }

  if (/^game\//i.test(value)) {
    value = `/${value}`;
    warnings.push('Added the missing leading slash.');
  }

  if (expectedKind === unrealPathKind.world && isSimpleMapUrl(value)) {
    if (value !== original) warnings.push(`Normalised to ${value}.`);
    return { ok: true, value, warnings: [...new Set(warnings)], error: '' };
  }

  if (!/^\/[A-Za-z0-9_]+\/.+/.test(value)) {
    return { ok: false, value, warnings, error: 'Asset paths must start with /Game/ or another Unreal mount point such as /PluginName/, or be a simple map name such as Minimal_Default1 when used in the map slot.' };
  }

  const lastSlash = value.lastIndexOf('/');
  const leaf = value.slice(lastSlash + 1);
  if (expectedKind === unrealPathKind.world) {
    const dot = value.indexOf('.', Math.max(0, lastSlash));
    if (dot > lastSlash) {
      value = value.slice(0, dot);
      warnings.push('Converted the map object path to a world package path.');
    }
  } else if (expectedKind !== unrealPathKind.queue && !leaf.includes('.')) {
    value = `${value}.${leaf}`;
    warnings.push('Appended the object name to the package path.');
  }

  if (value !== original) warnings.push(`Normalised to ${value}.`);
  return { ok: true, value, warnings: [...new Set(warnings)], error: '' };
}

function profileSetting(profile, key) {
  const settings = profile?.settings || {};
  const exact = settings[key];
  if (exact != null) return exact;
  const match = Object.keys(settings).find(item => item.toLowerCase() === key.toLowerCase());
  return match ? settings[match] : '';
}

function activeWizardProfileCandidate() {
  const form = wizardForm();
  if (wizardMode('wizardProfileMode') === 'new') {
    return {
      type: form.elements.newProfileType.value,
      assetPath: form.elements.newProfileAsset.value,
      commandTemplate: form.elements.newProfileCommandTemplate.value,
      settings: {
        map: form.elements.newProfileMap.value,
        sequence: form.elements.newProfileSequence?.value || '',
        mrqMode: form.elements.newProfileMrqMode?.value || 'Queue',
        extraArgs: form.elements.newProfileExtraArgs.value,
        defaultOutputRoot: form.elements.newProfileDefaultOutputRoot?.value || '',
        unrealExecutablePath: form.elements.newProfileUnrealExecutablePath?.value || '',
        outputSubfolderPattern: form.elements.newProfileOutputSubfolderPattern?.value || ''
      }
    };
  }

  return selectedWizardProfile() || null;
}

function validateProfilePaths(profile) {
  if (!profile) return { blockers: ['Choose a render setup before starting the render.'], warnings: [], normalized: {} };
  const type = renderType(profile.type);
  const blockers = [];
  const warnings = [];
  const normalized = {};
  const map = profileSetting(profile, 'map') || profileSetting(profile, 'mapName') || profileSetting(profile, 'level') || profileSetting(profile, 'levelName');
  const sequence = profileSetting(profile, 'sequence') || profileSetting(profile, 'levelSequence');
  const asset = profile.assetPath || profileSetting(profile, 'moviePipelineConfig') || profileSetting(profile, 'mrqConfig') || profileSetting(profile, 'queue');
  const mode = movieRenderMode(profile);

  if (type === 'CommandTemplate') {
    if (!String(profile.commandTemplate || '').trim()) blockers.push('Command/template mode requires a command template.');
    return { blockers, warnings, normalized };
  }

  if (type !== 'MrqQueue' && type !== 'MrgGraph') {
    return { blockers, warnings, normalized };
  }

  const mapResult = normalizeUnrealReference(map, unrealPathKind.world);
  if (!mapResult.ok) blockers.push(`Map/world path: ${mapResult.error}`); else { normalized.map = mapResult.value; warnings.push(...mapResult.warnings); }

  if (mode === 'single') {
    const sequenceResult = normalizeUnrealReference(sequence, unrealPathKind.sequence);
    if (!sequenceResult.ok) blockers.push(`Level Sequence path: ${sequenceResult.error}`); else { normalized.sequence = sequenceResult.value; warnings.push(...sequenceResult.warnings); }
  } else if (sequence && isQueueModeValue(profileSetting(profile, 'mrqMode') || profileSetting(profile, 'renderMode') || profileSetting(profile, 'movieRenderMode'))) {
    blockers.push('Saved MRQ Queue mode should not set a separate Level Sequence. Remove the sequence field, or switch to Single Level Sequence + config preset mode.');
  }

  const configResult = normalizeUnrealReference(asset, mode === 'single' ? unrealPathKind.config : unrealPathKind.queue);
  if (!configResult.ok) blockers.push(`Movie Pipeline config/queue: ${configResult.error}`); else { normalized.asset = configResult.value; warnings.push(...configResult.warnings); }

  return { blockers, warnings: [...new Set(warnings)], normalized };
}

function applyWizardPathNormalisation(target = null) {
  const form = wizardForm();
  if (!form || wizardMode('wizardProfileMode') !== 'new') return;
  const mode = form.elements.newProfileMrqMode?.value === 'SingleSequence' ? 'single' : 'queue';
  const fields = [
    [form.elements.newProfileMap, unrealPathKind.world],
    [form.elements.newProfileSequence, unrealPathKind.sequence],
    [form.elements.newProfileAsset, mode === 'single' ? unrealPathKind.config : unrealPathKind.queue]
  ].filter(([field]) => field && (!target || field === target));

  const warnings = [];
  for (const [field, kind] of fields) {
    if (!field.value.trim()) continue;
    const result = normalizeUnrealReference(field.value, kind);
    if (result.ok && result.value !== field.value.trim()) {
      field.value = result.value;
      warnings.push(...result.warnings);
    }
  }

  if (warnings.length) toast([...new Set(warnings)].join(' '), 'info');
}

function shellQuote(value) {
  const textValue = String(value || '');
  return textValue.includes(' ') || textValue.includes('"') ? `"${textValue.replace(/"/g, '\\"')}"` : textValue;
}

function buildWizardCommandPreview() {
  const form = wizardForm();
  const profile = activeWizardProfileCandidate();
  const validation = validateProfilePaths(profile);
  if (validation.blockers.length) return { ok: false, command: '', warnings: validation.warnings, error: validation.blockers[0] };

  const defaults = renderDefaults() || {};
  const project = selectedWizardProject();
  const projectPath = wizardMode('wizardProjectMode') === 'new' ? form.elements.newProjectPath.value.trim() : project?.uProjectPath || '<Project.uproject>';
  const unrealExe = profileSetting(profile, 'unrealExecutablePath') || defaults.unrealExecutablePath || '<UnrealEditor-Cmd.exe>';
  const args = [shellQuote(unrealExe), shellQuote(projectPath), shellQuote(validation.normalized.map), '-game'];
  if (validation.normalized.sequence) args.push(`-LevelSequence="${validation.normalized.sequence.replace(/"/g, '\\"')}"`);
  args.push(`-MoviePipelineConfig="${validation.normalized.asset.replace(/"/g, '\\"')}"`);
  args.push('-windowed', '-Log', '-StdOut', '-allowStdOutLogVerbosity', '-Unattended');
  const extraArgs = profileSetting(profile, 'extraArgs');
  if (extraArgs) args.push(extraArgs);
  return { ok: true, command: args.join(' '), warnings: validation.warnings, error: '' };
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

function confirmAction({ title, message, details = '', confirmLabel = 'Continue', tone = 'danger' }) {
  const modal = byId('confirmActionModal');
  if (!modal) return Promise.resolve(false);
  byId('confirmActionTitle').textContent = title || 'Confirm action';
  byId('confirmActionMessage').textContent = message || 'Review the action before continuing.';
  byId('confirmActionDetails').textContent = details || '';
  const proceed = byId('confirmActionProceed');
  proceed.textContent = confirmLabel || 'Continue';
  proceed.classList.toggle('danger', tone !== 'primary');
  proceed.classList.toggle('primary', tone === 'primary');
  return new Promise(resolve => {
    pendingConfirmation = resolve;
    if (typeof modal.showModal === 'function' && !modal.open) modal.showModal(); else modal.classList.add('is-open');
  });
}

function resolveConfirmation(result) {
  const modal = byId('confirmActionModal');
  if (typeof modal?.close === 'function' && modal.open) modal.close();
  modal?.classList.remove('is-open');
  if (pendingConfirmation) {
    const resolve = pendingConfirmation;
    pendingConfirmation = null;
    resolve(result);
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
  const singleSequence = wizardForm().elements.newProfileMrqMode?.value === 'SingleSequence';
  document.querySelectorAll('[data-wizard-command-field]').forEach(element => element.classList.toggle('hidden', !customCommand));
  document.querySelectorAll('[data-wizard-asset-field]').forEach(element => element.classList.toggle('hidden', !usesAsset));
  document.querySelectorAll('[data-wizard-mrq-mode-field]').forEach(element => element.classList.toggle('hidden', !usesAsset));
  document.querySelectorAll('[data-wizard-sequence-field]').forEach(element => element.classList.toggle('hidden', !usesAsset || !singleSequence));
}

function collectOutputRoots() {
  const defaults = renderDefaults() || {};
  const roots = workers().flatMap(worker => (worker.capabilities?.sharedOutputRoots || []).map(root => ({
    workerId: worker.id,
    workerName: worker.name || worker.id,
    approved: className(worker.approval || 'accepted') === 'accepted',
    status: className(worker.effectiveStatus || worker.status),
    mode: className(worker.schedulingMode || 'active'),
    ...root
  })));
  if (defaults.sharedOutputRoot) {
    roots.unshift({
      workerId: 'controller',
      workerName: 'Controller default',
      approved: true,
      status: 'controller',
      mode: 'active',
      path: defaults.sharedOutputRoot,
      exists: true,
      writable: true,
      message: 'Controller-owned output root; each worker validates access when it receives a job.'
    });
  }

  return roots;
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
  if (!writableRoots.length) warnings.push('Configure a controller output default or start a worker and confirm its output root.');
  if (!wizardForm().elements.outputDirectory.value.trim()) warnings.push('No output location has been selected. The controller will apply the saved render default when available.');

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
      <div class="meta">${escapeHtml(root.workerName)} | ${escapeHtml(root.freeDiskGb) || '-'} GB free</div>
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
    ${readiness.warnings.length ? `<div class="wizard-warning">${readiness.warnings.map(warning => `<p>${escapeHtml(warning)}</p>`).join('')}</div>` : '<div class="callout">Controller-owned scheduling will choose an eligible worker when the job is queued.</div>'}
    <div class="readiness-grid">${outputCards || '<div class="empty">No worker output telemetry yet. A controller output default or explicit output folder can still be used.</div>'}</div>
  `;
  renderWizardPathValidation();
}

function renderWizardPathValidation() {
  const target = byId('newRenderPathValidation');
  if (!target) return;
  const profile = activeWizardProfileCandidate();
  const validation = validateProfilePaths(profile);
  const preview = buildWizardCommandPreview();
  const output = wizardForm().elements.outputDirectory.value.trim() || renderDefaults()?.sharedOutputRoot || profileSetting(profile, 'defaultOutputRoot') || profileSetting(profile, 'outputRoot') || '';
  const outputBlocker = output ? '' : 'Output directory is required. Enter an output folder or configure Controller Render Defaults.';
  const blockers = [...validation.blockers, ...(outputBlocker ? [outputBlocker] : [])];
  const warnings = [...new Set([...validation.warnings, ...preview.warnings])];

  target.innerHTML = `
    ${blockers.length ? `<div class="wizard-warning error-tone">${blockers.map(item => `<p>${escapeHtml(item)}</p>`).join('')}</div>` : '<div class="callout">Launch validation is clear. The controller will re-check paths before queueing.</div>'}
    ${warnings.length ? `<div class="wizard-warning">${warnings.map(item => `<p>${escapeHtml(item)}</p>`).join('')}</div>` : ''}
    <details class="command-preview" ${preview.ok ? 'open' : ''}>
      <summary>Command preview</summary>
      ${preview.ok ? `<code>${escapeHtml(preview.command)}</code><button class="button" type="button" data-copy="${escapeHtml(preview.command)}" data-copy-label="command preview">Copy command</button>` : `<div class="meta">${escapeHtml(preview.error || 'Complete the render setup to generate the command.')}</div>`}
    </details>
  `;
}

function renderWizardReview() {
  const target = byId('newRenderReview');
  if (!target) return;
  const form = wizardForm();
  const project = selectedWizardProject();
  const profile = activeWizardProfileCandidate();
  const readiness = collectWizardReadiness();
  const validation = validateProfilePaths(profile);
  const preview = buildWizardCommandPreview();
  const projectName = wizardMode('wizardProjectMode') === 'new' ? form.elements.newProjectName.value || 'New project' : project?.displayName || selectedWizardProjectId() || 'No project selected';
  const profileName = wizardMode('wizardProfileMode') === 'new' ? form.elements.newProfileName.value || 'New render setup' : selectedWizardProfile()?.displayName || selectedWizardProfileId() || 'No render setup selected';
  const profileType = renderType(profile?.type);
  const jobName = form.elements.jobName.value || `${profileName} render`;
  const output = form.elements.outputDirectory.value || renderDefaults()?.sharedOutputRoot || profileSetting(profile, 'defaultOutputRoot') || 'Output must be supplied before launch';
  const blockers = [...validation.blockers];
  if (!form.elements.outputDirectory.value.trim() && !renderDefaults()?.sharedOutputRoot && !profileSetting(profile, 'defaultOutputRoot')) {
    blockers.push('Output directory is required.');
  }

  target.innerHTML = `
    <article class="review-card"><span>Project</span><strong>${escapeHtml(projectName)}</strong><p>${escapeHtml(wizardMode('wizardProjectMode') === 'new' ? 'Will be registered before queueing.' : 'Saved project')}</p></article>
    <article class="review-card"><span>Render setup</span><strong>${escapeHtml(profileName)}</strong><p>${escapeHtml(profileType)}</p></article>
    <article class="review-card"><span>Output location</span><strong>${escapeHtml(output)}</strong><p>${escapeHtml(readiness.writableRoots.length ? `${readiness.writableRoots.length} writable root(s) reported` : 'Worker access is validated at assignment time.')}</p></article>
    <article class="review-card"><span>Queue</span><strong>${escapeHtml(jobName)}</strong><p>Priority ${escapeHtml(form.elements.priority.value || 0)}</p></article>
    <article class="review-card full"><span>Launch validation</span><strong>${blockers.length ? 'Blocked' : 'Ready to submit'}</strong><p>${escapeHtml(blockers.join(' ') || [...new Set([...validation.warnings, ...preview.warnings])].join(' ') || 'No launch blockers found.')}</p></article>
    <article class="review-card full command-review"><span>Command preview</span>${preview.ok ? `<details class="command-preview" open><summary>Generated Unreal command</summary><code>${escapeHtml(preview.command)}</code><button class="button" type="button" data-copy="${escapeHtml(preview.command)}" data-copy-label="command preview">Copy command</button></details>` : `<p>${escapeHtml(preview.error || 'Complete render setup to generate the command.')}</p>`}</article>
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
  updateWizardReadiness();
  renderWizardReview();
  byId('newRenderQueueBtn').disabled = Boolean(validateWizardStep(1) || validateWizardStep(2) || validateWizardStep(3));
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
      const project = selectedWizardProject();
      if (!selectedWizardProjectId()) return 'Choose an existing project or add a new project.';
      if (!String(project?.uProjectPath || '').trim()) return 'Project .uproject path is required before starting a render.';
      if (!/\.uproject$/i.test(project.uProjectPath)) return 'Project path should point to a .uproject file.';
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
    } else {
      const name = form.elements.newProfileName.value.trim();
      const id = slug(form.elements.newProfileId.value || name || `${selectedWizardProjectId()}-render-setup`);
      if (!name) return 'Enter a render setup name.';
      if (profiles().some(profile => className(profile.id) === className(id))) return `Render setup ID "${id}" already exists. Choose a different ID.`;
      if (form.elements.newProfileType.value === 'CommandTemplate' && !form.elements.newProfileCommandTemplate.value.trim()) {
        return 'Enter a command template for custom command/template mode.';
      }
    }

    const validation = validateProfilePaths(activeWizardProfileCandidate());
    if (validation.blockers.length) return validation.blockers[0];
  }

  if (step === 3) {
    const profile = activeWizardProfileCandidate();
    const output = form.elements.outputDirectory.value.trim() || renderDefaults()?.sharedOutputRoot || profileSetting(profile, 'defaultOutputRoot') || profileSetting(profile, 'outputRoot');
    if (!output) return 'Enter an output directory or configure Controller Render Defaults before starting a render.';
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
  const type = form.elements.newProfileType.value;
  const mrqMode = form.elements.newProfileMrqMode?.value || 'Queue';
  const isSingleSequence = mrqMode === 'SingleSequence';
  const sequenceRaw = form.elements.newProfileSequence?.value || '';
  const mapResult = form.elements.newProfileMap.value.trim() ? normalizeUnrealReference(form.elements.newProfileMap.value, unrealPathKind.world) : null;
  const sequenceResult = sequenceRaw.trim() ? normalizeUnrealReference(sequenceRaw, unrealPathKind.sequence) : null;
  const assetKind = isSingleSequence ? unrealPathKind.config : unrealPathKind.queue;
  const assetResult = form.elements.newProfileAsset.value.trim() ? normalizeUnrealReference(form.elements.newProfileAsset.value, assetKind) : null;

  addOptionalSetting(settings, 'map', mapResult?.ok ? mapResult.value : form.elements.newProfileMap.value);
  if (isSingleSequence) addOptionalSetting(settings, 'sequence', sequenceResult?.ok ? sequenceResult.value : sequenceRaw);
  addOptionalSetting(settings, 'mrqMode', mrqMode);
  addOptionalSetting(settings, 'extraArgs', form.elements.newProfileExtraArgs.value);
  addOptionalSetting(settings, 'defaultOutputRoot', form.elements.newProfileDefaultOutputRoot?.value);
  addOptionalSetting(settings, 'unrealExecutablePath', form.elements.newProfileUnrealExecutablePath?.value);
  addOptionalSetting(settings, 'outputSubfolderPattern', form.elements.newProfileOutputSubfolderPattern?.value);

  return {
    id: slug(form.elements.newProfileId.value || form.elements.newProfileName.value || `${projectId}-render-setup`),
    projectId,
    displayName: form.elements.newProfileName.value.trim(),
    type,
    assetPath: assetResult?.ok ? assetResult.value : (form.elements.newProfileAsset.value.trim() || null),
    commandTemplate: form.elements.newProfileCommandTemplate.value.trim() || null,
    defaultOutputType: 'png',
    supportsChunking: false,
    settings
  };
}

async function submitNewRenderWizard(event) {
  event.preventDefault();
  applyWizardPathNormalisation();
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

    toast('Render submitted', 'success');
    form.reset();
    closeModal('newRenderModal');
    switchView('queue');
    await refresh();
    if (job?.id) await openJobDetails(job.id);
  } catch (error) {
    showWizardError(error.message || 'Unable to submit render.');
    toast(error.message || 'Unable to submit render.', 'error');
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
  byId('jobDrawerCommand').innerHTML = '<div class="skeleton"></div>';
  byId('jobDrawerValidation').innerHTML = '<div class="skeleton"></div>';
  byId('jobDrawerWarnings').innerHTML = '<div class="skeleton"></div>';
  byId('jobDrawerLogDiagnostics').innerHTML = '<div class="skeleton"></div>';
  byId('jobDrawerRawLog').innerHTML = '<div class="skeleton"></div>';

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
    byId('jobDrawerCommand').innerHTML = '';
    byId('jobDrawerValidation').innerHTML = '';
    byId('jobDrawerWarnings').innerHTML = '';
    byId('jobDrawerLogDiagnostics').innerHTML = '';
    byId('jobDrawerRawLog').innerHTML = '';
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

function parseJsonOrNull(value) {
  if (!value) return null;
  try { return JSON.parse(value); } catch { return null; }
}

function latestLogClassification(events, failureText = '') {
  const fromEvent = [...events].reverse()
    .map(event => parseJsonOrNull(event.dataJson))
    .find(data => data && (Array.isArray(data.diagnostics) || Array.isArray(data.loadErrors) || data.rawExcerpt));
  if (fromEvent) return fromEvent;
  return classifyLogTextClient(failureText);
}

function classifyLogTextClient(text) {
  const raw = String(text || '');
  const diagnostics = [];
  const push = (code, severity, message, evidence, fix) => diagnostics.push({ code, severity, message, evidence, fix });
  if (/Failed to find Pipeline Configuration asset to render/i.test(raw)) push('mrq-config-not-found', 'error', 'MRQ config/queue preset was not found. Use a saved asset path like /Game/RenderConfig.RenderConfig and make sure the asset exists.', 'Failed to find Pipeline Configuration asset to render', 'Save the config or queue preset in Unreal and paste the object path without /Content.');
  if (/\/Content\/ part of the on-disk structure should be omitted/i.test(raw)) push('content-path-in-asset-reference', 'error', 'Remove /Content from Unreal asset paths. Content/Render/MyPreset.uasset becomes /Game/Render/MyPreset.MyPreset.', '/Content path warning', 'Use /Game object paths, not filesystem Content paths.');
  if (/RLPlugin is Incompatible/i.test(raw)) push('rlplugin-incompatible', 'warning', 'Plugin built for an older Unreal version. Disable/rebuild/update it before command-line rendering if it is required.', 'RLPlugin is Incompatible', 'Update or disable the plugin on render workers.');
  if (/DatasmithContent\/Materials\/C4DMaster/i.test(raw)) push('datasmith-c4d-material-missing', 'warning', 'Datasmith/C4D material dependency is missing. Enable/install Datasmith Content or repair/reimport affected materials.', 'DatasmithContent/Materials/C4DMaster', 'Enable Datasmith Content or repair affected imported materials.');
  const config = raw.match(/MoviePipelineConfig\s*=\s*([^\s"']+|"[^"]+"|'[^']+')/i)?.[1]?.replace(/^['"]|['"]$/g, '') || null;
  if (config) push('movie-pipeline-config-value', 'info', `Unreal received MoviePipelineConfig=${config}.`, `MoviePipelineConfig=${config}`, 'Compare this with the saved render setup asset path.');
  const loadErrors = raw.includes('LoadErrors:') ? raw.split(/\r?\n/).filter(line => /LoadErrors:|Missing|Error|Warning/i.test(line)).slice(0, 20) : [];
  if (loadErrors.length) push('load-errors', 'warning', 'Unreal reported load errors. Review the grouped load errors before retrying the render.', loadErrors.slice(0, 6).join('\n'), 'Open the project in Unreal and resolve missing assets, classes, plugins, or redirectors.');
  return { diagnostics, loadErrors, moviePipelineConfigValue: config, rawExcerpt: raw || null };
}

function renderJobCommandSection(job, attempts) {
  const command = [...attempts].reverse().find(attempt => attempt.commandLine)?.commandLine || job.validation?.commandPreview || '';
  byId('jobDrawerCommand').innerHTML = command
    ? `<details class="command-preview" open><summary>Command Preview</summary><code>${escapeHtml(command)}</code><button class="button" type="button" data-copy="${escapeHtml(command)}" data-copy-label="command preview">Copy command</button></details>`
    : '<div class="empty">No command has been generated yet. It will appear after validation or the first worker attempt.</div>';
}

function renderJobValidationSection(job) {
  const validation = job.validation;
  if (!validation) {
    byId('jobDrawerValidation').innerHTML = '<div class="empty">No validation snapshot is stored for this job. Requeueing or creating a new render will run fast validation.</div>';
    return;
  }
  const issues = validation.issues || [];
  byId('jobDrawerValidation').innerHTML = `
    <article class="validation-card ${escapeHtml(className(validation.status))}">
      <div class="item-head"><div class="item-title">${escapeHtml(validation.summary)}</div>${badge(validation.status)}</div>
      <div class="meta">${validation.deepValidationRan ? 'Deep validation confirmed Unreal asset checks.' : 'Fast validation only: path format looks valid; asset existence requires deep validation.'}</div>
      ${validation.commandPreview ? `<details class="command-preview"><summary>Stored command preview</summary><code>${escapeHtml(validation.commandPreview)}</code></details>` : ''}
    </article>
    ${issues.length ? issues.map(renderValidationIssue).join('') : '<div class="empty">No validation issues were recorded.</div>'}
  `;
}

function renderValidationIssue(issue) {
  const fix = issue.autoFixAvailable && issue.fixedValue
    ? `<div class="path-line"><span>Apply auto-fix</span><code>${escapeHtml(issue.fixedValue)}</code><button class="button" type="button" data-copy="${escapeHtml(issue.fixedValue)}" data-copy-label="auto-fix value">Copy fixed value</button></div>`
    : issue.fix ? `<div class="meta">Fix: ${escapeHtml(issue.fix)}</div>` : '';
  return `<article class="item validation-issue ${escapeHtml(className(issue.severity))}"><div class="item-head"><div class="item-title">${escapeHtml(issue.field || issue.code)}</div>${badge(issue.severity)}</div><p>${escapeHtml(issue.message)}</p>${issue.originalValue ? `<div class="meta">Original: ${escapeHtml(issue.originalValue)}</div>` : ''}${fix}</article>`;
}

function renderJobWarningsSection(job, classification) {
  const warnings = [...(job.validation?.issues || []).filter(issue => className(issue.severity) === 'warning'), ...(classification.diagnostics || []).filter(item => className(item.severity) === 'warning')];
  byId('jobDrawerWarnings').innerHTML = warnings.length
    ? warnings.map(item => `<article class="item warning-card"><div class="item-title">${escapeHtml(item.field || item.code || 'Warning')}</div><p>${escapeHtml(item.message)}</p>${item.fix ? `<div class="meta">Fix: ${escapeHtml(item.fix)}</div>` : ''}</article>`).join('')
    : '<div class="empty">No warnings are currently recorded for this job.</div>';
}

function renderLogDiagnosticsSection(classification) {
  const diagnostics = classification.diagnostics || [];
  byId('jobDrawerLogDiagnostics').innerHTML = diagnostics.length
    ? diagnostics.map(item => `<article class="item log-diagnostic ${escapeHtml(className(item.severity))}"><div class="item-head"><div class="item-title">${escapeHtml(item.message)}</div>${badge(item.severity)}</div>${item.evidence ? `<pre>${escapeHtml(item.evidence)}</pre>` : ''}${item.fix ? `<div class="meta">Fix: ${escapeHtml(item.fix)}</div>` : ''}</article>`).join('')
    : '<div class="empty">No known Unreal log signatures have been detected yet.</div>';
}

function renderRawLogSection(classification, attempts, failureText) {
  const raw = classification.rawExcerpt || failureText || '';
  const latestLogPath = [...attempts].reverse().find(attempt => attempt.logFilePath)?.logFilePath || '';
  byId('jobDrawerRawLog').innerHTML = raw
    ? `<details class="raw-log"><summary>Raw log excerpt</summary><pre>${escapeHtml(raw)}</pre></details>${latestLogPath ? `<div class="path-line"><span>Worker log path</span><code>${escapeHtml(latestLogPath)}</code><button class="button" type="button" data-copy="${escapeHtml(latestLogPath)}" data-copy-label="log path">Copy log path</button></div>` : ''}`
    : latestLogPath ? `<div class="path-line"><span>Worker log path</span><code>${escapeHtml(latestLogPath)}</code><button class="button" type="button" data-copy="${escapeHtml(latestLogPath)}" data-copy-label="log path">Copy log path</button></div>` : '<div class="empty">No raw log excerpt has been reported yet.</div>';
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
        ${attempt.commandLine ? `<details class="command-preview" open><summary>Command preview</summary><code>${escapeHtml(attempt.commandLine)}</code><button class="button" type="button" data-copy="${escapeHtml(attempt.commandLine)}" data-copy-label="attempt command">Copy command</button></details>` : ''}
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
  const logClassification = latestLogClassification(events, failureText);
  renderJobCommandSection(job, attempts);
  renderJobValidationSection(job);
  renderJobWarningsSection(job, logClassification);
  renderLogDiagnosticsSection(logClassification);
  renderRawLogSection(logClassification, attempts, failureText);
  renderArtifactSummary(artifact, outputPath);
}

function renderJobDrawerActions(job, outputPath, failureText) {
  const actions = [
    `<button class="button" type="button" data-copy="${escapeHtml(job.id)}" data-copy-label="job ID">Copy job ID</button>`
  ];
  if (outputPath) actions.push(`<button class="button" type="button" data-copy="${escapeHtml(outputPath)}" data-copy-label="output path">Copy output</button>`);
  if (failureText) actions.push(`<button class="button" type="button" data-copy="${escapeHtml(failureText)}" data-copy-label="failure details">Copy failure</button>`);
  if (canRetryJob(job)) actions.push(`<button class="button primary" type="button" data-retry-job="${escapeHtml(job.id)}">Retry as new job</button>`);
  if (canCancelJob(job)) actions.push(`<button class="button danger" type="button" data-cancel-job="${escapeHtml(job.id)}">Cancel render</button>`);
  const note = recoveryActionNote(job);
  if (note) actions.push(`<div class="action-note">${escapeHtml(note)}</div>`);
  return actions.join('');
}

function canRetryJob(job) {
  return className(job.state) === 'failed';
}

function canCancelJob(job) {
  return isActiveJob(job.state);
}

function recoveryActionNote(job) {
  const state = className(job.state);
  if (state === 'retrywait') {
    return job.queuedAtUtc ? `Retry policy is waiting until ${formatDate(job.queuedAtUtc)} before requeueing this job.` : 'Retry policy is waiting before this job is requeued.';
  }
  if (state === 'stale') return 'This job was marked stale by recovery. Use Recover stale leases or review events before retrying.';
  if (state === 'succeeded' || state === 'completed') return 'Completed jobs are terminal and are not reopened.';
  if (state === 'cancelled') return 'Cancelled jobs are terminal. Queue a fresh render from the profile if needed.';
  return '';
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
  const detected = (artifact?.detectedExtensions || []).map(ext => `.${ext}`).join(', ');
  byId('jobDrawerArtifacts').innerHTML = `
    <article class="item artifact-card">
      <div class="item-head"><div class="item-title">${escapeHtml(outputPath || artifact.outputDirectory)}</div>${artifact ? badge('Verified') : badge('Output path')}</div>
      <div class="meta">${artifact ? `${escapeHtml(artifact.fileCount)} file(s), ${formatBytes(artifact.totalBytes)}${detected ? ` | ${escapeHtml(detected)}` : ``}` : 'Output path recorded; artifact scan has not reported file counts yet.'}</div>
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
  const confirmProceed = event.target?.closest?.('[data-confirm-proceed]');
  if (confirmProceed) {
    event.preventDefault();
    resolveConfirmation(true);
    return;
  }

  const confirmCancel = event.target?.closest?.('[data-confirm-cancel]');
  if (confirmCancel) {
    event.preventDefault();
    resolveConfirmation(false);
    return;
  }
  const helpTarget = event.target?.closest?.('[data-help-anchor]');
  if (helpTarget) {
    event.preventDefault();
    switchView('help');
    const anchor = document.getElementById(helpTarget.dataset.helpAnchor);
    if (anchor) anchor.scrollIntoView({ behavior: 'smooth', block: 'start' });
    return;
  }
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
    const proceed = await confirmAction({
      title: 'Retry failed render as new job',
      message: 'This creates a clean queued job and leaves the failed job terminal for traceability.',
      details: jobId,
      confirmLabel: 'Retry as new job',
      tone: 'primary'
    });
    if (!proceed) return;
    const retry = await post(`/api/jobs/${encodeURIComponent(jobId)}/retry`);
    toast('Retry queued as a new job', 'success');
    await refresh();
    await openJobDetails(retry?.id || jobId);
    return;
  }

  const cancelJob = event.target?.closest?.('[data-cancel-job]');
  if (cancelJob) {
    event.preventDefault();
    const jobId = cancelJob.dataset.cancelJob;
    const proceed = await confirmAction({
      title: 'Cancel running render',
      message: 'The worker will receive a cancellation request and stop the owned Unreal process if it is still running.',
      details: jobId,
      confirmLabel: 'Request cancellation'
    });
    if (!proceed) return;
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

  const actionTarget = event.target?.closest?.('[data-view], [data-fill-project-worker], [data-worker-mode], [data-accept-worker], [data-reject-worker], [data-delete-project], [data-delete-profile], [data-open-profile-project], [data-queue-profile]');
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

    if (data.deleteProject && await confirmAction({ title: 'Delete project', message: 'Jobs that depend on this project will prevent deletion.', details: data.deleteProject, confirmLabel: 'Delete project' })) {
      await del(`/api/projects/${encodeURIComponent(data.deleteProject)}`);
      toast('Project removed', 'success');
      await refresh();
    }
    if (data.deleteProfile && await confirmAction({ title: 'Delete render profile', message: 'Jobs that depend on this profile will prevent deletion.', details: data.deleteProfile, confirmLabel: 'Delete profile' })) {
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
    if (!await confirmAction({ title: 'Clear queue history', message: 'This clears jobs, attempts, leases, and events. Projects, profiles, and workers remain.', confirmLabel: 'Clear queue history' })) return;
    const result = await del('/api/jobs');
    toast(result.message || 'Queue history cleared', 'success');
    await refresh();
  });
  byId('resetStateBtn').addEventListener('click', async () => {
    if (!await confirmAction({ title: 'Reset controller database', message: 'This clears workers, projects, profiles, jobs, events, leases, and settings.', details: 'This cannot be undone from the dashboard.', confirmLabel: 'Reset database' })) return;
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
  byId('helpSearch')?.addEventListener('input', event => {
    const query = className(event.target.value);
    document.querySelectorAll('#helpContent .help-card').forEach(card => {
      card.classList.toggle('hidden', query && !className(card.textContent).includes(query));
    });
  });
  byId('jobForm').addEventListener('change', renderSelects);
  byId('renderDefaultsForm').addEventListener('submit', saveRenderDefaults);
  byId('apiTokenInput').value = getStoredApiToken();
  byId('saveApiTokenBtn').addEventListener('click', () => { setStoredApiToken(byId('apiTokenInput').value); toast('Controller token saved in this browser', 'success'); });
  byId('clearApiTokenBtn').addEventListener('click', () => { byId('apiTokenInput').value = ''; setStoredApiToken(''); toast('Controller token cleared', 'success'); });
  byId('previewChunksBtn')?.addEventListener('click', async () => { try { await previewChunks(); } catch (error) { toast(error.message, 'error'); } });

  byId('newRenderNextBtn').addEventListener('click', () => {
    if (validateWizardThrough(newRenderStep)) setNewRenderStep(newRenderStep + 1);
  });
  byId('newRenderBackBtn').addEventListener('click', () => setNewRenderStep(newRenderStep - 1));
  byId('newRenderForm').addEventListener('submit', submitNewRenderWizard);
  byId('newRenderForm').addEventListener('change', event => {
    if (event.target?.id === 'newRenderProjectSelect' || event.target?.name === 'wizardProjectMode') updateWizardProfileOptions();
    if (['newProfileMap', 'newProfileSequence', 'newProfileAsset', 'newProfileMrqMode'].includes(event.target?.name)) applyWizardPathNormalisation(event.target);
    updateWizardModes();
    updateWizardRenderFields();
    updateWizardReadiness();
    renderWizardReview();
    byId('newRenderQueueBtn').disabled = Boolean(validateWizardStep(1) || validateWizardStep(2) || validateWizardStep(3));
  });
  byId('newRenderForm').addEventListener('input', event => {
    if (event.target?.name === 'newProjectName' && !event.target.form.elements.newProjectId.value) {
      event.target.form.elements.newProjectId.placeholder = slug(event.target.value || 'project-id');
    }
    if (event.target?.name === 'newProfileName' && !event.target.form.elements.newProfileId.value) {
      event.target.form.elements.newProfileId.placeholder = slug(event.target.value || 'render-setup-id');
    }
    updateWizardReadiness();
    renderWizardReview();
    byId('newRenderQueueBtn').disabled = Boolean(validateWizardStep(1) || validateWizardStep(2) || validateWizardStep(3));
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
    if (data.levelSequence) settings.sequence = data.levelSequence;
    if (data.mrqMode) settings.mrqMode = data.mrqMode;
    if (data.extraArgs) settings.extraArgs = data.extraArgs;
    addOptionalSetting(settings, 'minCpuCores', data.minCpuCores);
    addOptionalSetting(settings, 'minRamGb', data.minRamGb);
    addOptionalSetting(settings, 'minVramGb', data.minVramGb);
    addOptionalSetting(settings, 'gpuNameContains', data.gpuNameContains);
    addOptionalSetting(settings, 'unrealExecutablePath', data.unrealExecutablePath);
    addOptionalSetting(settings, 'unrealSearchRoot', data.unrealSearchRoot);
    addOptionalSetting(settings, 'defaultOutputRoot', data.defaultOutputRoot);
    addOptionalSetting(settings, 'outputSubfolderPattern', data.outputSubfolderPattern);
    addOptionalSetting(settings, 'timeoutSeconds', data.timeoutSeconds);

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

  const confirmModal = byId('confirmActionModal');
  confirmModal?.addEventListener('cancel', event => {
    event.preventDefault();
    resolveConfirmation(false);
  });
  confirmModal?.addEventListener('close', () => {
    if (pendingConfirmation) resolveConfirmation(false);
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

    toast('Render submitted', 'success');
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





