export const state = {
  snapshot: null,
  currentView: 'overview',
  search: '',
  workerStatus: '',
  busy: false,
  selectedJobId: null,
  lastScan: null,
  chunkPreview: null,
  refreshTimer: null
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

