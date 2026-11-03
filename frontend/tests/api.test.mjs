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
