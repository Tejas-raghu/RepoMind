<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import AgentPanel from './components/AgentPanel.vue'
import CodeViewer from './components/CodeViewer.vue'
import RepositoryFileTree from './components/RepositoryFileTree.vue'
import RepositoryPanel from './components/RepositoryPanel.vue'
import {
  getAgentRun, getIndexedFile, getSystemInfo, listAgentRuns, listIndexedFiles,
  listRepositories, runAgent, uploadRepository,
} from './services/repomindApi'
import type {
  AgentCitation, AgentRunResponse, AgentRunSummary, AnswerSource, IndexedFile, IndexedFileContent,
  RepositorySummary, RepositoryUploadResponse, SystemInfo,
} from './types/api'

const repositories = ref<RepositorySummary[]>([])
const selectedId = ref<number | null>(null)
const listLoading = ref(false)
const listError = ref('')
const uploading = ref(false)
const uploadError = ref('')
const uploadResult = ref<RepositoryUploadResponse | null>(null)
const files = ref<IndexedFile[]>([])
const filesLoading = ref(false)
const filesError = ref('')
const selectedPath = ref<string | null>(null)
const fileContent = ref<IndexedFileContent | null>(null)
const fileLoading = ref(false)
const fileError = ref('')
const citation = ref<{ startLine: number; endLine: number } | null>(null)
const explorer = ref<HTMLElement | null>(null)
const systemInfo = ref<SystemInfo | null>(null)
const agentRun = ref<AgentRunResponse | null>(null)
const agentError = ref('')
const agentBusy = ref(false)
const agentElapsed = ref(0)
const agentHistory = ref<AgentRunSummary[]>([])
const agentHistoryError = ref('')

const selectedRepository = computed(() => repositories.value.find((item) => item.id === selectedId.value))
let listController: AbortController | null = null
let uploadController: AbortController | null = null
let filesController: AbortController | null = null
let fileController: AbortController | null = null
let agentController: AbortController | null = null
let historyController: AbortController | null = null
let agentTimer: ReturnType<typeof setInterval> | null = null

function message(error: unknown): string {
  return error instanceof Error ? error.message : 'An unexpected error occurred.'
}

async function refreshRepositories() {
  listController?.abort()
  const controller = new AbortController()
  listController = controller
  listLoading.value = true
  listError.value = ''
  try {
    const currentRepositories = await listRepositories(controller.signal)
    if (listController !== controller) return
    repositories.value = currentRepositories
    if (selectedId.value !== null && !repositories.value.some((item) => item.id === selectedId.value)) {
      selectRepository(null)
    }
  } catch (error) {
    if (listController === controller && !controller.signal.aborted) listError.value = message(error)
  } finally {
    if (listController === controller) {
      listLoading.value = false
      listController = null
    }
  }
}

async function upload(file: File) {
  if (uploading.value) return
  const controller = new AbortController()
  uploadController = controller
  uploading.value = true
  uploadError.value = ''
  uploadResult.value = null
  try {
    const result = await uploadRepository(file, controller.signal)
    uploadResult.value = result
    await refreshRepositories()
    if (repositories.value.some((item) => item.id === result.repositoryId))
      selectRepository(result.repositoryId)
  } catch (error) {
    if (!controller.signal.aborted) uploadError.value = message(error)
  } finally {
    if (uploadController === controller) {
      uploading.value = false
      uploadController = null
    }
  }
}

function selectRepository(id: number | null) {
  if (id === selectedId.value) return
  cancelAgent()
  historyController?.abort()
  selectedId.value = id
  agentRun.value = null
  agentError.value = ''
  agentHistory.value = []
  agentHistoryError.value = ''
  filesController?.abort()
  fileController?.abort()
  files.value = []
  filesError.value = ''
  selectedPath.value = null
  fileContent.value = null
  fileError.value = ''
  citation.value = null
  filesLoading.value = false
  fileLoading.value = false
  if (id !== null) {
    void loadFiles(id)
    void loadAgentHistory(id)
  }
}

async function loadFiles(id: number) {
  const controller = new AbortController()
  filesController = controller
  filesLoading.value = true
  try {
    const result = await listIndexedFiles(id, controller.signal)
    if (filesController === controller && selectedId.value === id) files.value = result
  } catch (error) {
    if (!controller.signal.aborted && filesController === controller) filesError.value = message(error)
  } finally {
    if (filesController === controller) { filesLoading.value = false; filesController = null }
  }
}

async function openFile(path: string, range: { startLine: number; endLine: number } | null = null) {
  if (selectedId.value === null) return
  fileController?.abort()
  selectedPath.value = path
  citation.value = range
  fileContent.value = null
  fileError.value = ''
  const id = selectedId.value
  const controller = new AbortController()
  fileController = controller
  fileLoading.value = true
  try {
    const result = await getIndexedFile(id, path, controller.signal)
    if (fileController === controller && selectedId.value === id && selectedPath.value === path)
      fileContent.value = result
  } catch (error) {
    if (!controller.signal.aborted && fileController === controller) fileError.value = message(error)
  } finally {
    if (fileController === controller) { fileLoading.value = false; fileController = null }
  }
}

function navigateSource(source: AnswerSource | AgentCitation) {
  void openFile(source.filePath, { startLine: source.startLine, endLine: source.endLine })
  explorer.value?.scrollIntoView({ block: 'start', behavior: 'smooth' })
}

function stopAgentTimer() {
  if (agentTimer !== null) clearInterval(agentTimer)
  agentTimer = null
}

function cancelAgent() {
  agentController?.abort()
  agentController = null
  agentBusy.value = false
  stopAgentTimer()
}

async function loadAgentHistory(id: number) {
  historyController?.abort()
  const controller = new AbortController()
  historyController = controller
  try {
    const result = await listAgentRuns(id, controller.signal)
    if (historyController === controller && selectedId.value === id) {
      agentHistory.value = result
      agentHistoryError.value = ''
    }
  } catch (error) {
    if (!controller.signal.aborted && historyController === controller) agentHistoryError.value = message(error)
  } finally {
    if (historyController === controller) historyController = null
  }
}

// Agent runs and history loads share one controller: the latest request wins.
async function startAgent(load: (id: number, signal: AbortSignal) => Promise<AgentRunResponse>) {
  if (agentBusy.value || selectedId.value === null) return
  const id = selectedId.value
  const controller = new AbortController()
  agentController = controller
  agentBusy.value = true
  agentRun.value = null
  agentError.value = ''
  agentElapsed.value = 0
  agentTimer = setInterval(() => agentElapsed.value++, 1000)
  try {
    const result = await load(id, controller.signal)
    if (agentController === controller && selectedId.value === id) agentRun.value = result
  } catch (error) {
    if (!controller.signal.aborted && agentController === controller) agentError.value = message(error)
  } finally {
    if (agentController === controller) {
      agentController = null
      agentBusy.value = false
      stopAgentTimer()
      if (selectedId.value === id) void loadAgentHistory(id)
    }
  }
}

const runAgents = (task: string) => startAgent((id, signal) => runAgent(id, task, signal))
const openAgentRun = (runId: number) => startAgent((id, signal) => getAgentRun(id, runId, signal))

async function loadSystemInfo() {
  try {
    systemInfo.value = await getSystemInfo()
  } catch {
    systemInfo.value = null
  }
}

onMounted(() => {
  void refreshRepositories()
  void loadSystemInfo()
})
onBeforeUnmount(() => {
  listController?.abort()
  uploadController?.abort()
  cancelAgent()
  historyController?.abort()
  filesController?.abort()
  fileController?.abort()
})
</script>

<template>
  <div class="app-shell">
    <header class="site-header">
      <div class="brand">
        <div class="brand-mark" aria-hidden="true"><span></span><span></span><span></span></div>
        <span>RepoMind</span>
      </div>
      <div class="header-tag"><span aria-hidden="true"></span> AI CODEBASE INTELLIGENCE</div>
    </header>

    <main>
      <div class="hero">
        <div class="hero-copy">
          <span class="hero-kicker"><span></span> EXPLORE WITH EVIDENCE</span>
          <h1>Understand your codebase.<br /><em>Follow the source.</em></h1>
          <p>Upload a repository, ask a question, and trace the answer to the lines that support it.</p>
        </div>
        <div class="hero-orbit" aria-hidden="true">
          <span class="orbit orbit-one"></span><span class="orbit orbit-two"></span>
          <span class="orbit-core">{ }</span>
        </div>
      </div>

      <div class="dashboard-grid">
        <RepositoryPanel :repositories="repositories" :selected-id="selectedId" :system-info="systemInfo"
          :loading="listLoading" :list-error="listError" :uploading="uploading"
          :upload-error="uploadError" :upload-result="uploadResult"
          @refresh="refreshRepositories" @select="selectRepository" @upload="upload" />
        <!-- One box: the Supervisor routes questions to RAG and explain/review tasks to agents. -->
        <AgentPanel :repository-name="selectedRepository?.name ?? null" :busy="agentBusy"
          :elapsed-seconds="agentElapsed" :run="agentRun" :error="agentError" :history="agentHistory"
          :history-error="agentHistoryError" @run="runAgents" @cancel="cancelAgent" @open="openAgentRun"
          @navigate="navigateSource" />
      </div>
      <section ref="explorer" class="panel explorer-panel" aria-labelledby="explorer-title">
        <div class="panel-heading"><div class="eyebrow">03 / SOURCE</div>
          <h2 id="explorer-title">Code explorer</h2>
          <p>Browse indexed source and follow answer citations to stored lines.</p></div>
        <p v-if="selectedId === null" class="field-message">Select a repository to browse its indexed files.</p>
        <div v-else class="explorer-grid">
          <RepositoryFileTree :files="files" :selected-path="selectedPath" :loading="filesLoading"
            :error="filesError" @open="openFile($event)" />
          <CodeViewer :file="fileContent" :loading="fileLoading" :error="fileError" :citation="citation" />
        </div>
      </section>
    </main>

    <footer class="site-footer">
      <span>RepoMind <span class="footer-divider">/</span> Local-first codebase exploration</span>
      <span>Answers are grounded in indexed source files.</span>
    </footer>
  </div>
</template>
