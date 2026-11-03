<script setup lang="ts">
import QuestionPanel from './QuestionPanel.vue'
import SourceCodeCard from './SourceCodeCard.vue'
import type { AnswerSource, RagAnswerResponse } from '../types/api'

defineProps<{
  repositoryName: string | null
  busy: boolean
  elapsedSeconds: number
  answer: RagAnswerResponse | null
  error: string
}>()

const emit = defineEmits<{ ask: [question: string]; cancel: []; navigate: [source: AnswerSource] }>()

function statusLabel(status: string): string {
  const labels: Record<string, string> = {
    answered: 'Answer ready',
    insufficient_evidence: 'Insufficient evidence',
    uncertain: 'Evidence uncertain',
    verification_unavailable: 'Verification unavailable',
    insufficient_context: 'Context limit reached',
    generation_unavailable: 'Generation unavailable',
  }
  return labels[status] || status.replaceAll('_', ' ')
}
</script>

<template>
  <section class="panel answer-panel" aria-labelledby="ask-title">
    <div class="panel-heading answer-heading">
      <div>
        <div class="eyebrow">02 / EXPLORE</div>
        <h2 id="ask-title">Ask RepoMind</h2>
        <p>Trace an implementation back to the code that supports it.</p>
      </div>
      <div v-if="repositoryName" class="context-chip" :title="repositoryName">
        <span class="context-dot" aria-hidden="true"></span>{{ repositoryName }}
      </div>
    </div>

    <QuestionPanel :repository-name="repositoryName" :busy="busy" :elapsed-seconds="elapsedSeconds"
      @ask="emit('ask', $event)" @cancel="emit('cancel')" />

    <div v-if="error" class="notice error-notice" role="alert">{{ error }}</div>

    <div v-if="answer" class="result" aria-live="polite">
      <div class="result-topline">
        <span class="status-pill" :class="answer.status === 'answered' ? 'status-answered' : 'status-muted'">
          <span class="status-indicator" aria-hidden="true"></span>{{ statusLabel(answer.status) }}
        </span>
        <span v-if="answer.model" class="result-model">{{ answer.model }}</span>
      </div>
      <div class="result-body">
        <h3>{{ answer.status === 'answered' ? 'What the code says' : 'No grounded answer' }}</h3>
        <p>{{ answer.answer }}</p>
      </div>
      <div v-if="answer.status === 'answered' && answer.sources.length" class="sources-section">
        <div class="sources-heading">
          <h3>Sources</h3>
          <span>{{ answer.sources.length }} {{ answer.sources.length === 1 ? 'reference' : 'references' }}</span>
        </div>
        <SourceCodeCard v-for="source in answer.sources" :key="source.chunkId" :source="source"
          @navigate="emit('navigate', $event)" />
      </div>
    </div>

    <div v-else-if="!busy && !error" class="empty-result">
      <div class="empty-art" aria-hidden="true"><span>◇</span><span>⌁</span><span>◇</span></div>
      <h3>Answers with a trail back to code</h3>
      <p>Ask a question to see a grounded answer and its original source lines here.</p>
    </div>
  </section>
</template>
