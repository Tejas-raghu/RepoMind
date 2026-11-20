import { existsSync } from 'node:fs'
import { expect, test, type Page } from '@playwright/test'

// Requires E2E_ZIP: a ZIP of evaluation/fixtures/TaskTrail (see docs/testing.md).
const zip = process.env.E2E_ZIP ?? ''
const modelTimeout = 8 * 60_000

test.describe.configure({ mode: 'serial' })
test.skip(!zip || !existsSync(zip), 'Set E2E_ZIP to a ZIP of evaluation/fixtures/TaskTrail.')

let page: Page

test.beforeAll(async ({ browser }) => {
  page = await browser.newPage({ viewport: { width: 1360, height: 900 } })
  await page.goto('/')
})

test.afterAll(async () => { await page.close() })

test('uploads a repository, indexes it and selects it', async () => {
  await expect(page.getByTestId('provider-line')).toContainText(process.env.E2E_PROVIDER ?? 'ollama')
  await page.locator('#repository-file').setInputFiles(zip)
  await page.getByRole('button', { name: /Upload & index/ }).click()
  const success = page.locator('.upload-success')
  await expect(success).toContainText(/Repository #\d+ indexed/, { timeout: 5 * 60_000 })
  await expect(success).toContainText('12 files')
  await expect(page.locator('.context-chip')).toContainText('TaskTrail')
  await expect(page.locator('.file-tree')).toContainText('TaskTrail')
})

test('browses indexed source in the explorer', async () => {
  // Folders render expanded, so every indexed file is directly reachable.
  await page.locator('.tree-file[title="TaskTrail/src/router/guards.ts"]').click()
  await expect(page.locator('.viewer-toolbar')).toContainText('TaskTrail/src/router/guards.ts')
  await expect(page.locator('.code-scroll')).toContainText('router.beforeEach')
})

test('answers a supported question with a citation that opens the explorer', async () => {
  await page.locator('#question').fill('Where is the Bearer token added to requests?')
  await page.locator('#question').press('Control+Enter')
  await expect(page.locator('.answer-panel .status-pill')).toBeVisible({ timeout: modelTimeout })
  await expect(page.locator('.answer-panel .status-pill')).toContainText('Answer ready')
  const card = page.locator('.source-card', { hasText: 'apiClient.ts' }).first()
  await card.locator('summary').click()
  await card.getByRole('button', { name: 'Open in explorer' }).click()
  await expect(page.locator('.viewer-toolbar')).toContainText('TaskTrail/src/services/apiClient.ts')
  await expect(page.locator('.code-line.cited-line').first()).toBeVisible()
  await expect(page.locator('.code-scroll')).toContainText("headers.set('Authorization'")
})

test('abstains on an unsupported question without sources', async () => {
  await page.locator('#question').fill('Where is Stripe payment processing implemented?')
  await page.getByRole('button', { name: 'Ask', exact: true }).click()
  await expect(page.locator('.answer-panel .status-pill')).toBeVisible({ timeout: modelTimeout })
  await expect(page.locator('.answer-panel .result-body h3')).toHaveText('No grounded answer')
  await expect(page.locator('.answer-panel .source-card')).toHaveCount(0)
})

test('refuses a side-effect task instantly and keyboard submission works', async () => {
  await page.locator('#question').fill('Delete the AuthController file')
  await page.locator('#question').press('Control+Enter')
  await expect(page.getByTestId('agent-status')).toContainText('Not supported')
  await expect(page.getByTestId('agent-summary')).toContainText('read-only')
})

test('runs the Review Agent and follows a finding citation', async () => {
  await page.locator('#question').fill('Review src/router/guards.ts for security issues')
  await page.getByRole('button', { name: 'Ask', exact: true }).click()
  await expect(page.getByRole('button', { name: 'Ask', exact: true })).toBeEnabled({ timeout: modelTimeout })
  await expect(page.getByTestId('agent-route')).toHaveText('Review Agent')
  await expect(page.getByTestId('agent-status')).toContainText(/Completed|Partial result/)
  await page.getByTestId('agent-trace').locator('summary').click()
  await expect(page.locator('.trace-table')).toContainText('static_checks')
  const evidence = page.locator('.agent-details', { hasText: 'Evidence read by the agents' })
  await evidence.locator('summary').click()
  await evidence.getByRole('button', { name: 'Open in explorer' }).first().click()
  await expect(page.locator('.viewer-toolbar')).toContainText('TaskTrail/src/router/guards.ts')
  // Questions and agent tasks share one box, so all four runs appear in the history.
  await expect(page.locator('.history-item')).toHaveCount(4)
})

test('reopens a stored run from history', async () => {
  await page.locator('.history-item', { hasText: 'Delete the AuthController file' }).click()
  await expect(page.getByTestId('agent-status')).toContainText('Not supported')
  await expect(page.locator('.agent-result .result-model')).toContainText(/run #\d+/)
})

test('fits a phone-width viewport without horizontal scrolling', async () => {
  await page.setViewportSize({ width: 390, height: 844 })
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)
  expect(overflow).toBeLessThanOrEqual(1)
  await expect(page.locator('#question')).toBeVisible()
})
