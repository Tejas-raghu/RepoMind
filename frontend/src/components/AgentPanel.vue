<script setup lang="ts">
import { ref } from 'vue'
import AgentResult from './AgentResult.vue'
import type { AgentCitation, AgentRunResponse, AgentRunSummary } from '../types/api'

defineProps<{
  repositoryName: string | null
  busy: boolean
  elapsedSeconds: number
  run: AgentRunResponse | null
  error: string
  history: AgentRunSummary[]
  historyError: string
}>()
const emit = defineEmits<{
  run: [task: string]
  cancel: []
  open: [runId: number]
  navigate: [citation: AgentCitation]
}>()

const task = ref('')
const examples = [
  'Explain what apiClient.ts does',
  'Review src/router/guards.ts for security issues',
  'Explain how overdue tasks are identified',
  'Where is the Bearer token added to requests?',
]

function submit() {
  const trimmed = task.value.trim()
  if (trimmed) emit('run', trimmed)
}
</script>

<template>
  <section class="panel agent-panel" aria-labelledby="agents-title">
    <div class="panel-heading">
      <div class="eyebrow">04 / AGENTS</div>
      <h2 id="agents-title">Multi-agent workspace</h2>
      <p>The Supervisor routes a task to the Retrieval, Code Analysis or Review Agent. Agents are read-only
        and every finding links to stored source lines.</p>
    </div>

    <form class="question-form" @submit.prevent="submit">
      <label for="agent-task">Task</label>
      <textarea id="agent-task" v-model="task" maxlength="500" rows="3" :disabled="!repositoryName || busy"
        placeholder="Review src/router/guards.ts for security issues"
        @keydown.ctrl.enter.prevent="submit" @keydown.meta.enter.prevent="submit"></textarea>
      <div class="example-row" aria-label="Example tasks">
        <button v-for="example in examples" :key="example" class="example-chip" type="button"
          :disabled="!repositoryName || busy" @click="task = example">{{ example }}</button>
      </div>
      <div class="question-footer">
        <span class="field-message">{{ task.length }}/500 · explain, review or locate code</span>
        <div class="question-actions">
          <button v-if="busy" class="secondary-button" type="button" @click="emit('cancel')">Cancel</button>
          <button class="primary-button" type="submit" :disabled="!repositoryName || busy || !task.trim()">
            <span v-if="busy" class="spinner" aria-hidden="true"></span>
            {{ busy ? 'Agents working…' : 'Run agents' }}
          </button>
        </div>
      </div>
      <p v-if="!repositoryName" class="field-message">Select a repository to run agents.</p>
      <p v-if="busy" class="processing-message" role="status">
        Routing, gathering evidence and validating findings locally · {{ elapsedSeconds }}s elapsed
      </p>
    </form>

    <div v-if="error" class="notice error-notice" role="alert">{{ error }}</div>
    <AgentResult v-if="run" :run="run" @navigate="emit('navigate', $event)" />

    <div v-if="repositoryName" class="agent-history">
      <h3>Recent runs</h3>
      <p v-if="historyError" class="field-message error" role="alert">{{ historyError }}</p>
      <p v-else-if="history.length === 0" class="field-message">No agent runs for this repository yet.</p>
      <ul v-else>
        <li v-for="item in history" :key="item.runId">
          <button class="history-item" type="button" :disabled="busy" @click="emit('open', item.runId)">
            <span class="history-status" :class="`history-${item.status}`">{{ item.status }}</span>
            <span class="history-task">{{ item.task }}</span>
            <span class="history-meta">{{ item.route }} · {{ (item.durationMs / 1000).toFixed(0) }}s</span>
          </button>
        </li>
      </ul>
    </div>
  </section>
</template>
