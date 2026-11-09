// Small synthetic fixture for testing Cloudflare ingestion and source citations.
export function addBearerToken(token: string): Headers {
  const headers = new Headers();
  headers.set('Authorization', `Bearer ${token}`);
  return headers;
}
