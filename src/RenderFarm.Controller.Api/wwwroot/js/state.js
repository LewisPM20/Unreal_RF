export const state = {
  snapshot: null,
  currentView: 'overview',
  search: '',
  workerStatus: '',
  busy: false,
  selectedJobId: null,
  queueFilter: 'all',
  chunkPreview: null,
  refreshTimer: null,
  notificationsPrimed: false,
  activityNotificationsPrimed: false,
  activities: [],
  diagnostics: null,
  seenActivityIds: new Set()
};

export function setSnapshot(snapshot) {
  state.snapshot = snapshot;
}

export function workers() {
  return state.snapshot?.workers ?? [];
}

export function projects() {
  return state.snapshot?.projects ?? [];
}

export function profiles() {
  return state.snapshot?.renderProfiles ?? [];
}

export function jobs() {
  return state.snapshot?.jobs ?? [];
}

export function summary() {
  return state.snapshot?.summary ?? null;
}

export function setActivities(activities) {
  state.activities = Array.isArray(activities) ? activities : [];
}

export function activities() {
  return state.activities ?? [];
}
export function setDiagnostics(diagnostics) {
  state.diagnostics = diagnostics || null;
}

export function diagnostics() {
  return state.diagnostics;
}


