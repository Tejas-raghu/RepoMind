export interface RepositorySummary {
  id: number
  name: string
}

export interface RepositoryUploadResponse {
  repositoryId: number
  status: string
  filesIndexed: number
  chunksCreated: number
  filesSkipped: number
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
