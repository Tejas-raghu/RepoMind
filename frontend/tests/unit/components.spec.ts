import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import AgentPanel from '../../src/components/AgentPanel.vue'
import AgentResult from '../../src/components/AgentResult.vue'
import RepositorySelector from '../../src/components/RepositorySelector.vue'
import RepositoryUpload from '../../src/components/RepositoryUpload.vue'
import { abstainedQuestionRun, answered, questionRun, reviewRun } from './fixtures'

const info = { provider: 'ollama', embeddingModel: 'embeddinggemma', chatModel: 'qwen3:4b',
  rerankingEnabled: false, agentModelPlanning: true }

describe('AgentPanel (single ask box)', () => {
  const props = { repositoryName: 'TaskTrail', busy: false, elapsedSeconds: 0, run: null, error: '',
    history: [], historyError: '' }

  it('shows a question result as an answer with escaped source code and citation navigation', async () => {
    const wrapper = mount(AgentPanel, { props: { ...props, run: questionRun } })
    expect(wrapper.find('[data-testid="agent-status"]').text()).toContain('Answer ready')
    expect(wrapper.find('[data-testid="agent-route"]').text()).toBe('Retrieval Agent · RAG answer')
    expect(wrapper.find('.result-body h3').text()).toBe('What the code says')
    expect(wrapper.text()).toContain('TaskTrail/src/services/apiClient.ts')
    expect(wrapper.findAll('[data-testid="agent-finding"]')).toHaveLength(0)
    expect(wrapper.find('pre code').text()).toContain('<script>alert(1)</script>')
    expect(wrapper.find('script').exists()).toBe(false)
    await wrapper.findAll('button').find((b) => b.text() === 'Open in explorer')!.trigger('click')
    expect(wrapper.emitted('navigate')?.[0]?.[0]).toMatchObject({ filePath: answered.sources[0].filePath, startLine: 1 })
  })

  it('shows abstentions without sources and surfaces errors, progress and the empty state', () => {
    const none = mount(AgentPanel, { props: { ...props, run: abstainedQuestionRun } })
    expect(none.text()).toContain('Insufficient evidence')
    expect(none.text()).toContain('No grounded answer')
    expect(none.find('.source-card').exists()).toBe(false)

    const failed = mount(AgentPanel, { props: { ...props, error: 'The RepoMind API is unavailable.' } })
    expect(failed.find('[role="alert"]').text()).toContain('unavailable')

    const busy = mount(AgentPanel, { props: { ...props, busy: true, elapsedSeconds: 12 } })
    expect(busy.find('[role="status"]').text()).toContain('12s elapsed')
    expect(busy.find('textarea').attributes('disabled')).toBeDefined()
    expect(busy.findAll('button').some((b) => b.text() === 'Cancel')).toBe(true)

    expect(mount(AgentPanel, { props }).find('.empty-result').exists()).toBe(true)
  })

  it('submits a trimmed question or task and blocks empty input', async () => {
    const wrapper = mount(AgentPanel, { props })
    expect(wrapper.find('button[type="submit"]').attributes('disabled')).toBeDefined()
    await wrapper.find('textarea').setValue('  Review guards.ts  ')
    await wrapper.find('form').trigger('submit')
    expect(wrapper.emitted('run')).toEqual([['Review guards.ts']])
  })
})

describe('AgentResult', () => {
  it('renders route, status, findings, trace and emits citation navigation', async () => {
    const wrapper = mount(AgentResult, { props: { run: reviewRun } })
    expect(wrapper.find('[data-testid="agent-status"]').text()).toContain('Completed')
    expect(wrapper.find('[data-testid="agent-route"]').text()).toBe('Review Agent')
    const findings = wrapper.findAll('[data-testid="agent-finding"]')
    expect(findings).toHaveLength(2)
    expect(findings[0].text()).toContain('medium')
    expect(findings[0].text()).toContain('XSS can read <b>it</b>.')
    expect(findings[0].find('b').exists()).toBe(false)
    expect(findings[1].text()).toContain('rule · todo-marker')
    expect(wrapper.find('.trace-timeout').text()).toContain('model exceeded 150s')
    await findings[0].find('.citation-button').trigger('click')
    expect(wrapper.emitted('navigate')?.[0]?.[0]).toEqual(reviewRun.findings[0].citations[0])
  })

  it('labels partial and unsupported results honestly', () => {
    const partial = mount(AgentResult, { props: { run: { ...reviewRun, status: 'partial' } } })
    expect(partial.text()).toContain('Partial result')
    const refused = mount(AgentResult, { props: { run: { ...reviewRun, status: 'unsupported', route: 'unsupported',
      findings: [], evidence: [], model: null } } })
    expect(refused.text()).toContain('Not supported')
    expect(refused.text()).toContain('No grounded result')
    expect(refused.text()).toContain('no model call')
  })
})

describe('AgentPanel', () => {
  const props = { repositoryName: 'TaskTrail', busy: false, elapsedSeconds: 0, run: null, error: '',
    history: [], historyError: '' }

  it('requires a repository and runs a trimmed task from an example', async () => {
    const disabled = mount(AgentPanel, { props: { ...props, repositoryName: null } })
    expect(disabled.find('textarea').attributes('disabled')).toBeDefined()
    expect(disabled.text()).toContain('Select a repository to start asking questions.')

    const wrapper = mount(AgentPanel, { props })
    await wrapper.find('.example-chip').trigger('click')
    await wrapper.find('form').trigger('submit')
    expect(wrapper.emitted('run')).toEqual([['Where is the Bearer token added to requests?']])
  })

  it('offers cancel while busy and reopens a past run', async () => {
    const history = [{ runId: 7, task: 'Review apiClient.ts', route: 'review' as const, status: 'completed' as const,
      summary: 's', durationMs: 21000, createdAt: '2026-11-09T10:00:00Z' }]
    const busy = mount(AgentPanel, { props: { ...props, busy: true, elapsedSeconds: 4, history } })
    await busy.findAll('button').find((b) => b.text() === 'Cancel')!.trigger('click')
    expect(busy.emitted('cancel')).toHaveLength(1)
    expect(busy.find('.history-item').attributes('disabled')).toBeDefined()

    const idle = mount(AgentPanel, { props: { ...props, history, error: 'Agent failed.' } })
    expect(idle.find('[role="alert"]').text()).toBe('Agent failed.')
    await idle.find('.history-item').trigger('click')
    expect(idle.emitted('open')).toEqual([[7]])
  })
})

describe('RepositorySelector', () => {
  const repositories = [
    { id: 38, name: 'TaskTrail', embeddingProvider: 'ollama', embeddingModel: 'embeddinggemma' },
    { id: 581, name: 'Cloud', embeddingProvider: 'cloudflare', embeddingModel: '@cf/qwen/qwen3-embedding-0.6b' },
  ]
  const props = { repositories, selectedId: 38, systemInfo: info, loading: false, error: '' }

  it('shows the active provider and warns about an embedding-model mismatch', async () => {
    const ok = mount(RepositorySelector, { props })
    expect(ok.find('[data-testid="provider-line"]').text()).toContain('ollama')
    expect(ok.find('[data-testid="provider-mismatch"]').exists()).toBe(false)
    expect(ok.text()).toContain('Cloud · #581 · cloudflare')

    const mismatch = mount(RepositorySelector, { props: { ...props, selectedId: 581 } })
    expect(mismatch.find('[data-testid="provider-mismatch"]').text()).toContain('re-upload')

    await ok.find('select').setValue('581')
    expect(ok.emitted('select')).toEqual([[581]])
  })
})

describe('RepositoryUpload', () => {
  it('validates the file locally and reports redacted secrets', async () => {
    const wrapper = mount(RepositoryUpload, { props: { uploading: false, error: '', result: null } })
    await wrapper.find('form').trigger('submit')
    expect(wrapper.text()).toContain('Choose a ZIP file first.')

    const input = wrapper.find('input[type="file"]')
    Object.defineProperty(input.element, 'files', { value: [new File(['x'], 'repo.tar')], configurable: true })
    await input.trigger('change')
    await wrapper.find('form').trigger('submit')
    expect(wrapper.text()).toContain('Choose a .zip file.')

    const zip = new File(['PK'], 'repo.zip', { type: 'application/zip' })
    Object.defineProperty(input.element, 'files', { value: [zip], configurable: true })
    await input.trigger('change')
    await wrapper.find('form').trigger('submit')
    expect((wrapper.emitted('upload')?.[0]?.[0] as File).name).toBe(zip.name)

    const done = mount(RepositoryUpload, { props: { uploading: false, error: '',
      result: { repositoryId: 9, status: 'completed', filesIndexed: 2, chunksCreated: 3, filesSkipped: 1, secretsRedacted: 2 } } })
    expect(done.text()).toContain('2 secret value(s) redacted')
  })

  it('warns public-demo visitors that uploads are visible to everyone', () => {
    const local = mount(RepositoryUpload, { props: { uploading: false, error: '', result: null } })
    expect(local.find('[data-testid="demo-notice"]').exists()).toBe(false)
    const demo = mount(RepositoryUpload, { props: { uploading: false, error: '', result: null, publicDemo: true } })
    expect(demo.find('[data-testid="demo-notice"]').text()).toContain('every visitor can see uploaded code')
  })
})
