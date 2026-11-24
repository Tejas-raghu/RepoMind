export interface RepositorySummary {
  id: number
  name: string
  embeddingProvider?: string | null
  embeddingModel?: string | null
}

export interface SystemInfo {
  provider: string
  embeddingModel: string
  chatModel: string
  rerankingEnabled: boolean
  agentModelPlanning: boolean
  // True on the hosted public demo (rate-limited, uploads visible to every visitor).
  publicDemo?: boolean
}

export interface RepositoryUploadResponse {
  repositoryId: number
  status: string
  filesIndexed: number
  chunksCreated: number
  filesSkipped: number
  secretsRedacted?: number
}

export type AnswerStatus =
  | 'answered'
  | 'insufficient_evidence'
  | 'uncertain'
  | 'verification_unavailable'
  | 'insufficient_context'
  | 'generation_unavailable'
  | (string & {})

export interface AnswerSource {
  chunkId: number
  filePath: string
  startLine: number
  endLine: number
  content: string
}

export interface RagAnswerResponse {
  status: AnswerStatus
  answer: string
  sources: AnswerSource[]
  model: string | null
  promptTokens: number | null
  generatedTokens: number | null
  durationMs: number | null
  verifierDurationMs: number | null
  rerankingStatus: string | null
  summary: string | null
  explanation: string | null
}

export interface IndexedFile { path: string }
export interface SourceLine { number: number; content: string | null; status: 'covered' | 'gap' | 'conflict' }
export interface SourceRange { startLine: number; endLine: number }
export interface IndexedFileContent {
  path: string
  complete: boolean
  fidelity: 'indexed_coverage'
  lines: SourceLine[]
  coveredRanges: SourceRange[]
  gapRanges: SourceRange[]
  conflictRanges: SourceRange[]
}

export type AgentRoute = 'question' | 'analysis' | 'review' | 'unsupported'
export type AgentStatus = 'completed' | 'partial' | 'abstained' | 'unsupported' | 'failed'

export interface AgentCitation { filePath: string; startLine: number; endLine: number }
export interface AgentFinding {
  title: string
  detail: string
  severity: 'low' | 'medium' | 'high' | null
  origin: 'model' | 'rule'
  citations: AgentCitation[]
  ruleId: string | null
}
export interface AgentEvidence {
  id: string
  filePath: string
  startLine: number
  endLine: number
  content: string
  source: string
}
export interface AgentPlanStep { step: number; agent: string; action: string; description: string }
export interface AgentTraceStep {
  step: number
  agent: string
  action: string
  status: string
  durationMs: number
  detail: string
}
export interface AgentRunResponse {
  runId: number | null
  repositoryId: number
  task: string
  route: AgentRoute
  routeReason: string
  status: AgentStatus
  summary: string
  findings: AgentFinding[]
  evidence: AgentEvidence[]
  plan: AgentPlanStep[]
  trace: AgentTraceStep[]
  durationMs: number
  model: string | null
  answer: RagAnswerResponse | null
  createdAt: string | null
}
export interface AgentRunSummary {
  runId: number
  task: string
  route: AgentRoute
  status: AgentStatus
  summary: string
  durationMs: number
  createdAt: string
}
