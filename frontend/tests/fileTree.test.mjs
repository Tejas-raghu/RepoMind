import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { test } from 'node:test'
import ts from 'typescript'

const source = readFileSync(new URL('../src/services/fileTree.ts', import.meta.url), 'utf8')
const { outputText } = ts.transpileModule(source, {
  compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 },
})
const { buildFileTree } = await import(`data:text/javascript;base64,${Buffer.from(outputText).toString('base64')}`)

test('builds nested folders and keeps exact indexed paths', () => {
  const tree = buildFileTree(['src/router/guards.ts', 'README.md', 'src/services/api.ts',
    'src/router/guards.ts'])
  assert.deepEqual(tree.files, [{ name: 'README.md', path: 'README.md' }])
  assert.equal(tree.folders[0].path, 'src')
  assert.equal(tree.folders[0].folders[0].path, 'src/router')
  assert.deepEqual(tree.folders[0].folders[0].files,
    [{ name: 'guards.ts', path: 'src/router/guards.ts' }])
  assert.deepEqual(tree.folders[0].folders[1].files,
    [{ name: 'api.ts', path: 'src/services/api.ts' }])
})
