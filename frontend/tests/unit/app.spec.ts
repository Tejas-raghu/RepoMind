import { flushPromises, mount } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import App from '../../src/App.vue'
import { fileContent, questionRun, reviewRun } from './fixtures'

// Syntax highlighting is covered by the browser E2E run; plain escaped text is enough here.
vi.mock('../../src/services/sourceHighlight', () => ({ highlightSource: async () => null }))

type Handler = (url: string, init?: RequestInit) => unknown | Promise<unknown>
const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })

let routes: Record<string, Handler>
let calls: string[]

function backend(overrides: Record<string, Handler> = {}) {
  calls = []
  routes = {
    'GET /api/repositories': () => [
      { id: 38, name: 'TaskTrail', embeddingProvider: 'ollama', embeddingModel: 'embeddinggemma' },
      { id: 40, name: 'Other', embeddingProvider: 'ollama', embeddingModel: 'embeddinggemma' }],
    'GET /api/system/info': () => ({ provider: 'ollama', embeddingModel: 'embeddinggemma', chatModel: 'qwen3:4b',
      rerankingEnabled: false, agentModelPlanning: true }),
    'GET /api/repositories/38/files': () => [{ path: fileContent.path }],
    'GET /api/repositories/40/files': () => [{ path: 'other/a.ts' }],
    'GET /api/repositories/38/agents/runs': () => [],
    'GET /api/repositories/40/agents/runs': () => [],
    'GET /api/repositories/38/files/content': () => fileContent,
    // The single ask box always calls the Supervisor; it routes questions to RAG itself.
    'POST /api/repositories/38/agents/run': (_url, init) =>
      String(init?.body).includes('Review') ? reviewRun : questionRun,
    ...overrides,
  }
  vi.stubGlobal('fetch', async (input: string, init?: RequestInit) => {
    const url = new URL(input, 'http://localhost')
    const key = `${init?.method ?? 'GET'} ${url.pathname}`
    calls.push(`${key}${url.search}`)
    const handler = routes[key]
    if (!handler) return json({ detail: 'not found' }, 404)
    const result = await handler(input, init)
    return result instanceof Response ? result : json(result)
  })
}

async function mountApp() {
  const wrapper = mount(App, { attachTo: document.body })
  await flushPromises()
  return wrapper
}

async function select(wrapper: Awaited<ReturnType<typeof mountApp>>, id: string) {
  await wrapper.find('#repository-select').setValue(id)
  await flushPromises()
}

beforeEach(() => {
  Element.prototype.scrollIntoView = vi.fn()
  backend()
})
afterEach(() => { document.body.innerHTML = '' })

describe('App', () => {
  it('loads repositories and provider info, then scopes files and history to the selection', async () => {
    const wrapper = await mountApp()
    expect(wrapper.find('#repository-select').text()).toContain('TaskTrail · #38 · ollama')
    expect(wrapper.find('[data-testid="provider-line"]').text()).toContain('qwen3:4b')
    await select(wrapper, '38')
    expect(calls).toContain('GET /api/repositories/38/files')
    expect(calls).toContain('GET /api/repositories/38/agents/runs')
    expect(wrapper.find('.file-tree').text()).toContain('TaskTrail')
    wrapper.unmount()
  })

  it('asks a question and follows a citation into the code explorer', async () => {
    const wrapper = await mountApp()
    await select(wrapper, '38')
    await wrapper.find('#question').setValue('Where is the Bearer token added?')
    await wrapper.find('.question-form').trigger('submit')
    await flushPromises()
    expect(wrapper.text()).toContain('The client sets the header.')
    expect(wrapper.find('[data-testid="agent-status"]').text()).toContain('Answer ready')
    expect(calls).toContain('POST /api/repositories/38/agents/run')
    expect(calls.some((call) => call.includes('/rag/answer'))).toBe(false)

    await wrapper.findAll('button').find((b) => b.text() === 'Open in explorer')!.trigger('click')
    await flushPromises()
    expect(calls).toContain(`GET /api/repositories/38/files/content?path=${encodeURIComponent(fileContent.path)}`)
    const cited = wrapper.findAll('.code-line.cited-line')
    expect(cited.map((line) => line.attributes('data-line'))).toEqual(['1', '2', '3', '4'])
    expect(wrapper.find('.code-scroll').text()).toContain("headers.set('Authorization', token)")
    expect(Element.prototype.scrollIntoView).toHaveBeenCalled()
    wrapper.unmount()
  })

  it('discards a slow answer after the repository changes', async () => {
    let release: (value: unknown) => void = () => {}
    backend({ 'POST /api/repositories/38/agents/run': () => new Promise((resolve) => { release = resolve }) })
    const wrapper = await mountApp()
    await select(wrapper, '38')
    await wrapper.find('#question').setValue('Where is the Bearer token added?')
    await wrapper.find('.question-form').trigger('submit')
    await flushPromises()
    expect(wrapper.text()).toContain('Thinking')

    await select(wrapper, '40')
    release(json(questionRun))
    await flushPromises()
    expect(wrapper.text()).not.toContain('The client sets the header.')
    expect(wrapper.find('.context-chip').text()).toContain('Other')
    wrapper.unmount()
  })

  it('runs a review through the same box, refreshes history and navigates from a finding', async () => {
    let history: unknown[] = []
    backend({ 'GET /api/repositories/38/agents/runs': () => history,
      'POST /api/repositories/38/agents/run': () => { history = [{ runId: 7, task: reviewRun.task, route: 'review',
        status: 'completed', summary: reviewRun.summary, durationMs: 21000, createdAt: '2026-11-09T10:00:00Z' }]
        return reviewRun } })
    const wrapper = await mountApp()
    await select(wrapper, '38')
    await wrapper.find('#question').setValue('Review apiClient.ts')
    await wrapper.find('.question-form').trigger('submit')
    await flushPromises()
    expect(wrapper.find('[data-testid="agent-summary"]').text()).toBe('Token storage is risky.')
    expect(wrapper.find('.history-task').text()).toBe('Review apiClient.ts')

    await wrapper.find('.citation-button').trigger('click')
    await flushPromises()
    expect(calls.some((call) => call.startsWith('GET /api/repositories/38/files/content'))).toBe(true)
    expect(wrapper.find('.viewer-toolbar').text()).toContain(fileContent.path)
    wrapper.unmount()
  })

  it('shows clear errors when the API is unavailable or rejects a run', async () => {
    backend({ 'GET /api/repositories': () => new Response('proxy error', { status: 500 }),
      'GET /api/system/info': () => new Response('proxy error', { status: 500 }) })
    const down = await mountApp()
    expect(down.text()).toContain('The RepoMind API is unavailable')
    down.unmount()

    backend({ 'POST /api/repositories/38/agents/run': () => json({ detail: 'Task must not be empty.' }, 400) })
    const wrapper = await mountApp()
    await select(wrapper, '38')
    await wrapper.find('#question').setValue('x')
    await wrapper.find('.question-form').trigger('submit')
    await flushPromises()
    expect(wrapper.find('.answer-panel [role="alert"]').text()).toBe('Task must not be empty.')
    wrapper.unmount()
  })
})
