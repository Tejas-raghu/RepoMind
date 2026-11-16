import { defineConfig } from '@playwright/test'

// Browser end-to-end tests against the real local stack: Vite -> ASP.NET API ->
// PostgreSQL/pgvector -> Ollama. Nothing is mocked. Local model calls are slow,
// so timeouts are generous and tests run one at a time.
// The installed Chrome is used (channel), so no Playwright browser download is needed.
const apiUrl = process.env.E2E_API_URL ?? 'http://127.0.0.1:5000'
const port = Number(process.env.E2E_PORT ?? 5174)

export default defineConfig({
  testDir: 'tests/e2e',
  timeout: 20 * 60_000,
  expect: { timeout: 15_000 },
  workers: 1,
  reporter: [['list']],
  outputDir: 'test-results',
  use: {
    baseURL: `http://127.0.0.1:${port}`,
    channel: process.env.E2E_BROWSER_CHANNEL ?? 'chrome',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  webServer: {
    command: `npm run dev -- --port ${port} --strictPort`,
    url: `http://127.0.0.1:${port}`,
    reuseExistingServer: false,
    timeout: 120_000,
    env: { VITE_API_PROXY_TARGET: apiUrl },
  },
})
