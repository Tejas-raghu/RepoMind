import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { test } from 'node:test'
import ts from 'typescript'

const source = readFileSync(new URL('../src/services/repomindApi.ts', import.meta.url), 'utf8')
const testSource = source.replace('import.meta.env.VITE_API_BASE_URL', '"/api"')
const { outputText } = ts.transpileModule(testSource, {
  compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 },
})
const api = await import(`data:text/javascript;base64,${Buffer.from(outputText).toString('base64')}`)

function reply(body) {
  globalThis.fetch = async () => new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  })
}

const answer = {
  status: 'answered', answer: 'Supported [source:556]',
  sources: [{ chunkId: 556, filePath: 'src/api.ts', startLine: 1, endLine: 4, content: '<script>alert(1)</script>' }],
  model: 'qwen3:4b', promptTokens: 10, generatedTokens: 5, durationMs: 100,
  verifierDurationMs: 50, rerankingStatus: null, summary: 'Supported', explanation: null,
}

test('accepts the real repository and answer contract', async () => {
  reply([{ id: 38, name: 'TaskTrail' }])
  assert.deepEqual(await api.listRepositories(), [{ id: 38, name: 'TaskTrail' }])
  reply(answer)
  assert.deepEqual(await api.askRepository(38, 'Where is the token?'), answer)
})

test('rejects malformed repository and answer responses', async () => {
  reply([{ id: 38, name: '' }])
  await assert.rejects(api.listRepositories(), /unexpected repository list/)
  reply({ ...answer, sources: [{ ...answer.sources[0], startLine: '1' }] })
  await assert.rejects(api.askRepository(38, 'Where is the token?'), /unexpected answer/)
  reply({ ...answer, status: 'answered', sources: [] })
  await assert.rejects(api.askRepository(38, 'Where is the token?'), /unexpected answer/)
  reply({ ...answer, status: 'unknown_status' })
  await assert.rejects(api.askRepository(38, 'Where is the token?'), /unexpected answer/)
})

test('validates indexed file coverage and encodes paths', async () => {
  let requested = ''
  globalThis.fetch = async (url) => {
    requested = url
    return new Response(JSON.stringify({ path: 'src/a b.ts', complete: false,
      fidelity: 'indexed_coverage',
      lines: [{ number: 1, content: '<script>', status: 'covered' },
        { number: 2, content: null, status: 'gap' }],
      coveredRanges: [{ startLine: 1, endLine: 1 }],
      gapRanges: [{ startLine: 2, endLine: 2 }], conflictRanges: [] }), { status: 200 })
  }
  const content = await api.getIndexedFile(38, 'src/a b.ts')
  assert.match(requested, /path=src%2Fa%20b\.ts/)
  assert.equal(content.lines[0].content, '<script>')
  reply([{ path: 'src/a.ts' }])
  assert.deepEqual(await api.listIndexedFiles(38), [{ path: 'src/a.ts' }])
  reply({ ...content, lines: [{ number: 1, content: '<script>', status: 'gap' }] })
  await assert.rejects(api.getIndexedFile(38, 'src/a b.ts'), /unexpected indexed source content/)
})

const run = {
  runId: 7, repositoryId: 38, task: 'Review apiClient.ts', route: 'review', routeReason: 'r', status: 'completed',
  summary: 's', durationMs: 10, model: 'qwen3:4b', answer: null, createdAt: '2026-11-09T10:00:00Z',
  findings: [{ title: 't', detail: 'd', severity: 'high', origin: 'model', ruleId: null,
    citations: [{ filePath: 'src/a.ts', startLine: 2, endLine: 3 }] }],
  evidence: [{ id: 'E1', filePath: 'src/a.ts', startLine: 1, endLine: 9, content: '<b>x</b>', source: 'read_file' }],
  plan: [{ step: 1, agent: 'supervisor', action: 'route', description: 'd' }],
  trace: [{ step: 1, agent: 'supervisor', action: 'route', status: 'ok', durationMs: 1, detail: 'd' }],
  gaps: [],
}

test('validates agent runs, history and system info', async () => {
  let sent
  globalThis.fetch = async (url, init) => { sent = { url, init }; return new Response(JSON.stringify(run), { status: 200 }) }
  assert.deepEqual(await api.runAgent(38, 'Review apiClient.ts'), run)
  assert.equal(sent.url, '/api/repositories/38/agents/run')
  assert.deepEqual(JSON.parse(sent.init.body), { task: 'Review apiClient.ts' })
  reply(run)
  assert.equal((await api.getAgentRun(38, 7)).runId, 7)
  await assert.rejects(api.getAgentRun(38, 8), /unexpected agent run/)
  await assert.rejects(api.runAgent(39, 'x'), /unexpected agent result/)
  reply({ ...run, findings: [{ ...run.findings[0], citations: [{ filePath: 'src/a.ts', startLine: 5, endLine: 2 }] }] })
  await assert.rejects(api.runAgent(38, 'x'), /unexpected agent result/)
  reply({ ...run, gaps: ['Remote server behavior'] })
  assert.deepEqual((await api.runAgent(38, 'x')).gaps, ['Remote server behavior'])
  const { gaps: _omitted, ...older } = run
  reply(older)
  assert.deepEqual((await api.runAgent(38, 'x')).gaps, [])
  reply({ ...run, gaps: 'not a list' })
  await assert.rejects(api.runAgent(38, 'x'), /unexpected agent result/)
  reply({ ...run, gaps: [5] })
  await assert.rejects(api.runAgent(38, 'x'), /unexpected agent result/)
  reply({ ...run, status: 'done' })
  await assert.rejects(api.runAgent(38, 'x'), /unexpected agent result/)
  reply({ ...run, findings: [{ ...run.findings[0], severity: 'critical' }] })
  await assert.rejects(api.runAgent(38, 'x'), /unexpected agent result/)
  reply([{ runId: 7, task: 't', route: 'review', status: 'completed', summary: 's', durationMs: 1, createdAt: 'x' }])
  assert.equal((await api.listAgentRuns(38)).length, 1)
  reply([{ runId: 0, task: 't' }])
  await assert.rejects(api.listAgentRuns(38), /unexpected agent run history/)
  reply({ provider: 'ollama', embeddingModel: 'embeddinggemma', chatModel: 'qwen3:4b', rerankingEnabled: false, agentModelPlanning: true })
  assert.equal((await api.getSystemInfo()).provider, 'ollama')
  reply({ provider: 'ollama' })
  await assert.rejects(api.getSystemInfo(), /unexpected system information/)
})
