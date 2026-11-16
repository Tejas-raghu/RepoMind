<script setup lang="ts">
import { computed } from 'vue'
import type { AgentCitation, AgentRunResponse } from '../types/api'

const props = defineProps<{ run: AgentRunResponse }>()
const emit = defineEmits<{ navigate: [citation: AgentCitation] }>()

const statusLabels: Record<string, string> = {
  completed: 'Completed',
  partial: 'Partial result',
  abstained: 'Abstained',
  unsupported: 'Not supported',
  failed: 'Failed',
}
const routeLabels: Record<string, string> = {
  question: 'Retrieval Agent · RAG answer',
  analysis: 'Code Analysis Agent',
  review: 'Review Agent',
  unsupported: 'Supervisor',
}

const seconds = computed(() => (props.run.durationMs / 1000).toFixed(1))
const shortPath = (path: string) => path.split('/').slice(-2).join('/')
</script>

<template>
  <div class="result agent-result" aria-live="polite">
    <div class="result-topline">
      <div class="agent-badges">
        <span class="status-pill" :class="run.status === 'completed' ? 'status-answered' : 'status-muted'"
          data-testid="agent-status">
          <span class="status-indicator" aria-hidden="true"></span>{{ statusLabels[run.status] ?? run.status }}
        </span>
        <span class="agent-route" data-testid="agent-route">{{ routeLabels[run.route] ?? run.route }}</span>
      </div>
      <span class="result-model">{{ run.model ?? 'no model call' }} · {{ seconds }}s
        <template v-if="run.runId"> · run #{{ run.runId }}</template></span>
    </div>

    <div class="result-body">
      <h3>{{ run.status === 'completed' || run.status === 'partial' ? 'What the agents found' : 'No grounded result' }}</h3>
      <p data-testid="agent-summary">{{ run.summary }}</p>
    </div>

    <div v-if="run.findings.length" class="sources-section">
      <div class="sources-heading">
        <h3>Findings</h3>
        <span>{{ run.findings.length }} grounded {{ run.findings.length === 1 ? 'finding' : 'findings' }}</span>
      </div>
      <ul class="finding-list">
        <li v-for="(finding, index) in run.findings" :key="index" class="finding-card" data-testid="agent-finding">
          <div class="finding-head">
            <span v-if="finding.severity" class="severity" :class="`severity-${finding.severity}`">{{ finding.severity }}</span>
            <strong>{{ finding.title }}</strong>
            <span class="finding-origin" :title="finding.origin === 'rule' ? 'Deterministic tool output' : 'Validated model output'">
              {{ finding.origin === 'rule' ? `rule${finding.ruleId ? ' · ' + finding.ruleId : ''}` : 'model' }}
            </span>
          </div>
          <p>{{ finding.detail }}</p>
          <div class="citation-row">
            <button v-for="citation in finding.citations" :key="`${citation.filePath}:${citation.startLine}`"
              class="citation-button" type="button" :title="citation.filePath"
              @click="emit('navigate', citation)">
              {{ shortPath(citation.filePath) }} · L{{ citation.startLine }}<template
                v-if="citation.endLine !== citation.startLine">–{{ citation.endLine }}</template>
            </button>
          </div>
        </li>
      </ul>
    </div>

    <details v-if="run.evidence.length" class="agent-details">
      <summary>Evidence read by the agents ({{ run.evidence.length }})</summary>
      <div v-for="item in run.evidence" :key="item.id" class="evidence-item">
        <div class="source-toolbar">
          <div class="source-meta">{{ item.id }} · {{ item.filePath }} · L{{ item.startLine }}–{{ item.endLine }} · {{ item.source }}</div>
          <button class="text-button" type="button"
            @click="emit('navigate', { filePath: item.filePath, startLine: item.startLine, endLine: item.endLine })">
            Open in explorer
          </button>
        </div>
        <pre><code>{{ item.content }}</code></pre>
      </div>
    </details>

    <details class="agent-details" data-testid="agent-trace">
      <summary>Plan and execution trace ({{ run.trace.length }} steps)</summary>
      <p class="trace-reason">{{ run.routeReason }}</p>
      <ol class="plan-list">
        <li v-for="step in run.plan" :key="step.step"><code>{{ step.agent }}.{{ step.action }}</code> {{ step.description }}</li>
      </ol>
      <table class="trace-table">
        <thead><tr><th>#</th><th>Agent</th><th>Action</th><th>Status</th><th>ms</th><th>Detail</th></tr></thead>
        <tbody>
          <tr v-for="step in run.trace" :key="step.step" :class="`trace-${step.status}`">
            <td>{{ step.step }}</td><td>{{ step.agent }}</td><td>{{ step.action }}</td>
            <td><span class="trace-status">{{ step.status }}</span></td>
            <td>{{ Math.round(step.durationMs) }}</td><td class="trace-detail">{{ step.detail }}</td>
          </tr>
        </tbody>
      </table>
    </details>
  </div>
</template>
