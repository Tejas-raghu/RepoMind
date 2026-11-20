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

// One box for everything: the Supervisor sends "where/how" questions to the verified
// RAG flow and "explain"/"review" tasks to the matching agent.
const task = ref('')
const examples = [
  'Where is the Bearer token added to requests?',
  'Explain what apiClient.ts does',
  'Review src/router/guards.ts for security issues',
  'How are overdue tasks identified?',
]

function submit() {
  const trimmed = task.value.trim()
  if (trimmed) emit('run', trimmed)
}
</script>

<template>
  <section class="panel answer-panel" aria-labelledby="ask-title">
    <div class="panel-heading answer-heading">
      <div>
        <div class="eyebrow">02 / ASK</div>
        <h2 id="ask-title">Ask RepoMind</h2>
        <p>Ask where or how something is implemented, or ask RepoMind to explain or review code.
          Every answer links to the stored source lines.</p>
      </div>
      <div v-if="repositoryName" class="context-chip" :title="repositoryName">
        <span class="context-dot" aria-hidden="true"></span>{{ repositoryName }}
      </div>
    </div>

    <form class="question-form" @submit.prevent="submit">
      <label for="question">Your question or task</label>
      <textarea id="question" v-model="task" maxlength="500" rows="4" :disabled="!repositoryName || busy"
        placeholder="Where is the Bearer token added to requests?"
        @keydown.ctrl.enter.prevent="submit" @keydown.meta.enter.prevent="submit"></textarea>
      <div class="example-row" aria-label="Examples">
        <button v-for="example in examples" :key="example" class="example-chip" type="button"
          :disabled="!repositoryName || busy" @click="task = example">{{ example }}</button>
      </div>
      <div class="question-footer">
        <span class="field-message">{{ task.length }}/500 · Ctrl + Enter to ask</span>
        <div class="question-actions">
          <button v-if="busy" class="secondary-button" type="button" @click="emit('cancel')">Cancel</button>
          <button class="primary-button" type="submit" :disabled="!repositoryName || busy || !task.trim()">
            <span v-if="busy" class="spinner" aria-hidden="true"></span>
            {{ busy ? 'Thinking…' : 'Ask' }}
            <span v-if="!busy" aria-hidden="true">↗</span>
          </button>
        </div>
      </div>
      <p v-if="!repositoryName" class="field-message">Select a repository to start asking questions.</p>
      <p v-if="busy" class="processing-message" role="status">
        Searching code and checking evidence · {{ elapsedSeconds }}s elapsed
      </p>
    </form>

    <div v-if="error" class="notice error-notice" role="alert">{{ error }}</div>
    <AgentResult v-if="run" :run="run" @navigate="emit('navigate', $event)" />
    <div v-else-if="!busy && !error" class="empty-result">
      <div class="empty-art" aria-hidden="true"><span>◇</span><span>⌁</span><span>◇</span></div>
      <h3>Answers with a trail back to code</h3>
      <p>Ask a question, or ask for an explanation or a review, to see grounded results and their source lines here.</p>
    </div>

    <div v-if="repositoryName" class="agent-history">
      <h3>Recent questions and tasks</h3>
      <p v-if="historyError" class="field-message error" role="alert">{{ historyError }}</p>
      <p v-else-if="history.length === 0" class="field-message">Nothing asked in this repository yet.</p>
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
