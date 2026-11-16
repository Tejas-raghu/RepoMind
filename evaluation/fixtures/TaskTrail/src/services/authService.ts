import { apiClient } from './apiClient';

export interface LoginCredentials {
  email: string;
  password: string;
}

export interface AuthSession {
  accessToken: string;
  displayName: string;
  expiresAt: string;
}

const SESSION_KEY = 'tasktrail-session';

/** Send credentials to the server and persist its returned session. */
export async function signIn(credentials: LoginCredentials): Promise<AuthSession> {
  const session = await apiClient.post<AuthSession>('/auth/login', credentials);
  localStorage.setItem(SESSION_KEY, JSON.stringify(session));
  return session;
}

/** Read locally saved session data without contacting the server. */
export function getSession(): AuthSession | null {
  const raw = localStorage.getItem(SESSION_KEY);
  if (!raw) return null;
  try {
    const session = JSON.parse(raw) as AuthSession;
    return new Date(session.expiresAt).getTime() > Date.now() ? session : null;
  } catch {
    return null;
  }
}

export function getAccessToken(): string | null {
  return getSession()?.accessToken ?? null;
}

/** Remove local authentication state and return the user to login. */
export function signOut(): void {
  localStorage.removeItem(SESSION_KEY);
  window.location.assign('/login');
}

export function isAuthenticated(): boolean {
  return getSession() !== null;
}
