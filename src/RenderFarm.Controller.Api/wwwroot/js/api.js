function controllerHeaders(json = false) {
  const headers = { Accept: 'application/json' };
  if (json) headers['Content-Type'] = 'application/json';
  const token = window.localStorage.getItem('renderFarmApiToken');
  if (token) headers.Authorization = `Bearer ${token}`;
  return headers;
}

export function getStoredApiToken() {
  return window.localStorage.getItem('renderFarmApiToken') || '';
}

export function setStoredApiToken(token) {
  const trimmed = String(token || '').trim();
  if (trimmed) window.localStorage.setItem('renderFarmApiToken', trimmed);
  else window.localStorage.removeItem('renderFarmApiToken');
}

export async function getJson(path) {
  const response = await fetch(path, { headers: controllerHeaders() });
  if (!response.ok) {
    throw new Error(`${path}: ${response.status} ${await response.text()}`);
  }

  return response.json();
}

export async function sendJson(path, method, body) {
  const response = await fetch(path, {
    method,
    headers: controllerHeaders(true),
    body: JSON.stringify(body)
  });

  if (!response.ok) {
    throw new Error(`${path}: ${response.status} ${await response.text()}`);
  }

  return response.status === 204 ? null : response.json();
}

export async function post(path) {
  const response = await fetch(path, { method: 'POST', headers: controllerHeaders() });
  if (!response.ok) {
    throw new Error(`${path}: ${response.status} ${await response.text()}`);
  }

  return response.status === 204 ? null : response.json();
}

export async function del(path) {
  const response = await fetch(path, { method: 'DELETE', headers: controllerHeaders() });
  if (!response.ok) {
    throw new Error(`${path}: ${response.status} ${await response.text()}`);
  }

  return response.status === 204 ? null : response.json();
}