import type { AgentRunResponse, IndexedFileContent, RagAnswerResponse } from '../../src/types/api'

export const answered: RagAnswerResponse = {
  status: 'answered',
  answer: 'The client sets the header. [source:556]',
  sources: [{
    chunkId: 556, filePath: 'TaskTrail/src/services/apiClient.ts', startLine: 1, endLine: 4,
    content: "headers.set('Authorization', `Bearer ${token}`)\n<script>alert(1)</script>",
  }],
  model: 'qwen3:4b', promptTokens: 10, generatedTokens: 5, durationMs: 1000,
  verifierDurationMs: 500, rerankingStatus: 'disabled', summary: 'The client sets the header.', explanation: null,
}

export const abstained: RagAnswerResponse = {
  ...answered, status: 'insufficient_evidence', answer: 'No supplied source directly supports the request.',
  sources: [], model: null, promptTokens: null, generatedTokens: null, summary: null,
}

export const reviewRun: AgentRunResponse = {
  runId: 7, repositoryId: 38, task: 'Review apiClient.ts', route: 'review', routeReason: 'Review keywords matched.',
  status: 'completed', summary: 'Token storage is risky.',
  findings: [
    { title: 'Token in localStorage', detail: 'XSS can read <b>it</b>.', severity: 'medium', origin: 'model',
      citations: [{ filePath: 'TaskTrail/src/services/apiClient.ts', startLine: 6, endLine: 6 }], ruleId: null },
    { title: 'Unfinished work marker', detail: 'TODO found.', severity: 'low', origin: 'rule',
      citations: [{ filePath: 'TaskTrail/src/services/apiClient.ts', startLine: 9, endLine: 9 }], ruleId: 'todo-marker' },
  ],
  evidence: [{ id: 'E1', filePath: 'TaskTrail/src/services/apiClient.ts', startLine: 1, endLine: 9,
    content: 'export const apiClient = {}', source: 'read_file' }],
  plan: [{ step: 1, agent: 'supervisor', action: 'route', description: 'Classify the task as review.' }],
  trace: [
    { step: 1, agent: 'supervisor', action: 'route', status: 'ok', durationMs: 2, detail: 'review' },
    { step: 2, agent: 'review', action: 'synthesize', status: 'timeout', durationMs: 150000, detail: 'model exceeded 150s' },
  ],
  durationMs: 21000, model: 'qwen3:4b', answer: null, createdAt: '2026-11-09T10:00:00Z',
}

export const fileContent: IndexedFileContent = {
  path: 'TaskTrail/src/services/apiClient.ts', complete: false, fidelity: 'indexed_coverage',
  lines: [
    { number: 1, content: 'export const apiClient = {', status: 'covered' },
    { number: 2, content: "  headers.set('Authorization', token)", status: 'covered' },
    { number: 3, content: null, status: 'gap' },
    { number: 4, content: '}', status: 'covered' },
  ],
  coveredRanges: [{ startLine: 1, endLine: 2 }, { startLine: 4, endLine: 4 }],
  gapRanges: [{ startLine: 3, endLine: 3 }], conflictRanges: [],
}

// A location question routed by the Supervisor to the verified RAG flow.
export const questionRun: AgentRunResponse = {
  ...reviewRun, runId: 8, task: 'Where is the Bearer token added?', route: 'question',
  routeReason: 'Locate/implementation question.', status: 'completed', summary: answered.answer,
  findings: [{ title: 'Grounded answer', detail: 'The client sets the header.', severity: null, origin: 'model',
    citations: [{ filePath: answered.sources[0].filePath, startLine: 1, endLine: 4 }], ruleId: null }],
  evidence: [{ id: 'E1', filePath: answered.sources[0].filePath, startLine: 1, endLine: 4,
    content: answered.sources[0].content, source: 'rag_answer' }],
  answer: answered,
}

export const abstainedQuestionRun: AgentRunResponse = {
  ...questionRun, runId: 9, task: 'Where is Stripe used?', status: 'abstained', summary: abstained.answer,
  findings: [], evidence: [], answer: abstained,
}
