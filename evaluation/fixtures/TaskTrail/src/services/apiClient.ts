// Minimal demonstration client; omits production concerns such as refresh-token rotation.
const API_BASE = 'https://api.example.invalid';

type RequestOptions = RequestInit & { skipAuthentication?: boolean };

async function request<T>(path: string, options: RequestOptions = {}): Promise<T> {
  const headers = new Headers(options.headers);
  headers.set('Content-Type', 'application/json');
  if (!options.skipAuthentication) {
    const sessionRaw = localStorage.getItem('tasktrail-session');
    if (sessionRaw) {
      try {
        const session = JSON.parse(sessionRaw) as { accessToken?: string };
        if (session.accessToken) {
          headers.set('Authorization', `Bearer ${session.accessToken}`);
        }
      } catch {
        localStorage.removeItem('tasktrail-session');
      }
    }
  }

  const response = await fetch(`${API_BASE}${path}`, { ...options, headers });
  if (response.status === 401) {
    localStorage.removeItem('tasktrail-session');
    throw new Error('Your session has expired. Please sign in again.');
  }
  if (!response.ok) {
    throw new Error(`API request failed with ${response.status}`);
  }
  return (await response.json()) as T;
}

export const apiClient = {
  get: <T>(path: string) => request<T>(path),
  post: <T>(path: string, data: unknown) =>
    request<T>(path, {
      method: 'POST',
      body: JSON.stringify(data),
      skipAuthentication: path === '/auth/login',
    }),
  patch: <T>(path: string, data: unknown) =>
    request<T>(path, { method: 'PATCH', body: JSON.stringify(data) }),
};
