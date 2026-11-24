import type {
  AgentRunResponse, AgentRunSummary, IndexedFile, IndexedFileContent, RagAnswerResponse,
  RepositorySummary, RepositoryUploadResponse, SystemInfo,
} from '../types/api'

const baseUrl = (import.meta.env.VITE_API_BASE_URL || '/api').replace(/\/$/, '')

export class ApiError extends Error {
  constructor(message: string, readonly status?: number) {
    super(message)
    this.name = 'ApiError'
  }
}

const object = (value: unknown): value is Record<string, unknown> =>
  typeof value === 'object' && value !== null && !Array.isArray(value)

const positiveId = (value: unknown): value is number =>
  typeof value === 'number' && Number.isSafeInteger(value) && value > 0

const nonnegativeInteger = (value: unknown): value is number =>
  typeof value === 'number' && Number.isSafeInteger(value) && value >= 0

const optionalNumber = (value: unknown): value is number | null =>
  value === null || (typeof value === 'number' && Number.isFinite(value) && value >= 0)

const optionalString = (value: unknown): value is string | null =>
  value === null || typeof value === 'string'

const answerStatuses = new Set([
  'answered', 'insufficient_evidence', 'uncertain', 'verification_unavailable',
  'insufficient_context', 'generation_unavailable',
])

function serverMessage(value: unknown, status: number): string {
  if (object(value)) {
    if (typeof value.detail === 'string' && value.detail) return value.detail
    if (object(value.errors)) {
      const first = Object.values(value.errors).flat().find((item) => typeof item === 'string')
      if (typeof first === 'string') return first
    }
    if (typeof value.title === 'string' && value.title) return value.title
  }
  return `The API returned HTTP ${status}.`
}

async function send(path: string, init: RequestInit = {}): Promise<unknown> {
  let response: Response
  try {
    response = await fetch(`${baseUrl}${path}`, init)
  } catch (error) {
    if (error instanceof DOMException && error.name === 'AbortError') throw error
    throw new ApiError('Cannot reach the RepoMind API. Check that the backend is running.')
  }
  let body: unknown
  try {
    body = await response.json()
  } catch {
    if (response.ok) throw new ApiError('The API returned an unreadable response.')
    throw new ApiError(response.status >= 500 ?
      'The RepoMind API is unavailable. Check that the backend is running.' :
      `The API returned HTTP ${response.status}.`, response.status)
  }
  if (!response.ok) throw new ApiError(serverMessage(body, response.status), response.status)
  return body
}

export async function listRepositories(signal?: AbortSignal): Promise<RepositorySummary[]> {
  const body = await send('/repositories', { signal })
  if (!Array.isArray(body) || !body.every((item) => object(item) && positiveId(item.id) &&
    typeof item.name === 'string' && item.name.trim().length > 0))
    throw new ApiError('The API returned an unexpected repository list.')
  return body as RepositorySummary[]
}

export async function uploadRepository(file: File, signal?: AbortSignal): Promise<RepositoryUploadResponse> {
  const form = new FormData()
  form.append('file', file)
  const body = await send('/repositories/upload', { method: 'POST', body: form, signal })
  if (!object(body) || !positiveId(body.repositoryId) || body.status !== 'completed' ||
    !nonnegativeInteger(body.filesIndexed) || !nonnegativeInteger(body.chunksCreated) ||
    !nonnegativeInteger(body.filesSkipped))
    throw new ApiError('The API returned an unexpected upload result.')
  return body as unknown as RepositoryUploadResponse
}

// Gaps are plain text shown as escaped list items; older responses omit them.
const optionalGaps = (value: unknown) => value === undefined ||
  (Array.isArray(value) && value.length <= 5 && value.every((gap) => typeof gap === 'string'))

function validAnswer(body: unknown): body is RagAnswerResponse {
  return !(!object(body) || typeof body.status !== 'string' || !answerStatuses.has(body.status) ||
    typeof body.answer !== 'string' ||
    !Array.isArray(body.sources) || !body.sources.every((source) => object(source) &&
      positiveId(source.chunkId) && typeof source.filePath === 'string' &&
      positiveId(source.startLine) && positiveId(source.endLine) &&
      source.endLine >= source.startLine && typeof source.content === 'string') ||
    !optionalString(body.model) || !optionalString(body.rerankingStatus) ||
    !optionalString(body.summary) || !optionalString(body.explanation) ||
    !(body.promptTokens === null || nonnegativeInteger(body.promptTokens)) ||
    !(body.generatedTokens === null || nonnegativeInteger(body.generatedTokens)) ||
    !optionalNumber(body.durationMs) || !optionalNumber(body.verifierDurationMs) ||
    (body.status === 'answered' && (body.sources.length === 0 || !body.answer.trim())) ||
    (body.status !== 'answered' && body.sources.length !== 0))
}

export async function askRepository(repositoryId: number, query: string, signal?: AbortSignal):
  Promise<RagAnswerResponse> {
  const body = await send(`/repositories/${repositoryId}/rag/answer`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ query, topK: 3 }),
    signal,
  })
  if (!validAnswer(body)) throw new ApiError('The API returned an unexpected answer.')
  return body
}

export async function listIndexedFiles(repositoryId: number, signal?: AbortSignal): Promise<IndexedFile[]> {
  const body = await send(`/repositories/${repositoryId}/files`, { signal })
  if (!Array.isArray(body) || !body.every((item) => object(item) &&
    typeof item.path === 'string' && item.path.length > 0))
    throw new ApiError('The API returned an unexpected indexed file list.')
  return body as IndexedFile[]
}

export async function getIndexedFile(repositoryId: number, path: string, signal?: AbortSignal):
  Promise<IndexedFileContent> {
  const body = await send(`/repositories/${repositoryId}/files/content?path=${encodeURIComponent(path)}`, { signal })
  const validRange = (range: unknown) => object(range) && positiveId(range.startLine) &&
    positiveId(range.endLine) && range.endLine >= range.startLine
  if (!object(body) || body.path !== path || body.complete !== false ||
    body.fidelity !== 'indexed_coverage' || !Array.isArray(body.lines) ||
    !body.lines.every((line, index) => object(line) && line.number === index + 1 &&
      (line.status === 'covered' ? typeof line.content === 'string' :
        (line.status === 'gap' || line.status === 'conflict') && line.content === null)) ||
    !Array.isArray(body.coveredRanges) || !body.coveredRanges.every(validRange) ||
    !Array.isArray(body.gapRanges) || !body.gapRanges.every(validRange) ||
    !Array.isArray(body.conflictRanges) || !body.conflictRanges.every(validRange))
    throw new ApiError('The API returned unexpected indexed source content.')
  return body as unknown as IndexedFileContent
}

export async function getSystemInfo(signal?: AbortSignal): Promise<SystemInfo> {
  const body = await send('/system/info', { signal })
  if (!object(body) || typeof body.provider !== 'string' || typeof body.embeddingModel !== 'string' ||
    typeof body.chatModel !== 'string' || typeof body.rerankingEnabled !== 'boolean' ||
    typeof body.agentModelPlanning !== 'boolean')
    throw new ApiError('The API returned unexpected system information.')
  return body as unknown as SystemInfo
}

const agentRoutes = new Set(['question', 'analysis', 'review', 'unsupported'])
const agentStatuses = new Set(['completed', 'partial', 'abstained', 'unsupported', 'failed'])
const severities = new Set(['low', 'medium', 'high', null])

const validCitation = (value: unknown) => object(value) && typeof value.filePath === 'string' &&
  value.filePath.length > 0 && positiveId(value.startLine) && positiveId(value.endLine) &&
  value.endLine >= value.startLine

// Paths, line ranges and statuses are checked before any agent result reaches the UI.
function validAgentRun(body: unknown): body is AgentRunResponse {
  return object(body) && (body.runId === null || positiveId(body.runId)) && positiveId(body.repositoryId) &&
    typeof body.task === 'string' && agentRoutes.has(body.route as string) &&
    agentStatuses.has(body.status as string) && typeof body.summary === 'string' &&
    typeof body.routeReason === 'string' && optionalNumber(body.durationMs) && optionalString(body.model) &&
    Array.isArray(body.findings) && body.findings.every((finding) => object(finding) &&
      typeof finding.title === 'string' && typeof finding.detail === 'string' &&
      severities.has(finding.severity as string | null) && (finding.origin === 'model' || finding.origin === 'rule') &&
      Array.isArray(finding.citations) && finding.citations.every(validCitation) &&
      (finding.ruleId === undefined || optionalString(finding.ruleId))) &&
    Array.isArray(body.evidence) && body.evidence.every((item) => object(item) && typeof item.id === 'string' &&
      validCitation(item) && typeof item.content === 'string' && typeof item.source === 'string') &&
    Array.isArray(body.plan) && body.plan.every((step) => object(step) && positiveId(step.step) &&
      typeof step.agent === 'string' && typeof step.action === 'string' && typeof step.description === 'string') &&
    Array.isArray(body.trace) && body.trace.every((step) => object(step) && positiveId(step.step) &&
      typeof step.agent === 'string' && typeof step.action === 'string' && typeof step.status === 'string' &&
      optionalNumber(step.durationMs) && typeof step.detail === 'string') &&
    (body.answer === null || body.answer === undefined || validAnswer(body.answer)) && optionalGaps(body.gaps)
}

function normalizeRun(body: AgentRunResponse): AgentRunResponse {
  return {
    ...body,
    answer: body.answer ?? null,
    createdAt: body.createdAt ?? null,
    gaps: body.gaps ?? [],
    findings: body.findings.map((finding) => ({ ...finding, ruleId: finding.ruleId ?? null })),
  }
}

export async function runAgent(repositoryId: number, task: string, signal?: AbortSignal): Promise<AgentRunResponse> {
  const body = await send(`/repositories/${repositoryId}/agents/run`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ task }),
    signal,
  })
  if (!validAgentRun(body) || body.repositoryId !== repositoryId)
    throw new ApiError('The API returned an unexpected agent result.')
  return normalizeRun(body)
}

export async function listAgentRuns(repositoryId: number, signal?: AbortSignal): Promise<AgentRunSummary[]> {
  const body = await send(`/repositories/${repositoryId}/agents/runs`, { signal })
  if (!Array.isArray(body) || !body.every((run) => object(run) && positiveId(run.runId) &&
    typeof run.task === 'string' && agentRoutes.has(run.route as string) &&
    agentStatuses.has(run.status as string) && typeof run.summary === 'string' &&
    optionalNumber(run.durationMs) && typeof run.createdAt === 'string'))
    throw new ApiError('The API returned an unexpected agent run history.')
  return body as AgentRunSummary[]
}

export async function getAgentRun(repositoryId: number, runId: number, signal?: AbortSignal): Promise<AgentRunResponse> {
  const body = await send(`/repositories/${repositoryId}/agents/runs/${runId}`, { signal })
  if (!validAgentRun(body) || body.repositoryId !== repositoryId || body.runId !== runId)
    throw new ApiError('The API returned an unexpected agent run.')
  return normalizeRun(body)
}
