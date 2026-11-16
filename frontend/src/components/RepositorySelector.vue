<script setup lang="ts">
import { computed } from 'vue'
import type { RepositorySummary, SystemInfo } from '../types/api'

const props = defineProps<{
  repositories: RepositorySummary[]
  selectedId: number | null
  systemInfo: SystemInfo | null
  loading: boolean
  error: string
}>()

// A repository can only be searched with the embedding model that indexed it.
const mismatch = computed(() => {
  const selected = props.repositories.find((item) => item.id === props.selectedId)
  const info = props.systemInfo
  if (!selected?.embeddingModel || !info) return ''
  return selected.embeddingProvider !== info.provider || selected.embeddingModel !== info.embeddingModel
    ? `Indexed with ${selected.embeddingProvider}/${selected.embeddingModel}, but the API is running ` +
      `${info.provider}/${info.embeddingModel}. Questions will be rejected; switch provider or re-upload the ZIP.`
    : ''
})
const emit = defineEmits<{ refresh: []; select: [id: number | null] }>()

function selectRepository(event: Event) {
  const value = (event.target as HTMLSelectElement).value
  emit('select', value ? Number(value) : null)
}
</script>

<template>
  <div class="field-block">
    <div class="field-heading">
      <label for="repository-select">Active repository</label>
      <button class="text-button" type="button" :disabled="loading" @click="emit('refresh')">
        {{ loading ? 'Loading…' : 'Refresh' }}
      </button>
    </div>
    <select id="repository-select" :value="selectedId ?? ''" :disabled="loading || repositories.length === 0"
      @change="selectRepository">
      <option value="">{{ loading ? 'Loading repositories…' : repositories.length ? 'Select a repository' : 'No repositories yet' }}</option>
      <option v-for="repository in repositories" :key="repository.id" :value="repository.id">
        {{ repository.name }} · #{{ repository.id }}<template v-if="repository.embeddingProvider"> · {{ repository.embeddingProvider }}</template>
      </option>
    </select>
    <p v-if="error" class="field-message error" role="alert">{{ error }}</p>
    <p v-else-if="mismatch" class="field-message error" role="alert" data-testid="provider-mismatch">{{ mismatch }}</p>
    <p v-else-if="!loading && repositories.length === 0" class="field-message">
      Upload a ZIP to index your first codebase.
    </p>
    <p v-else class="field-message">Answers are scoped to the selected repository.</p>
    <p v-if="systemInfo" class="provider-line" data-testid="provider-line">
      AI provider: <strong>{{ systemInfo.provider }}</strong> · {{ systemInfo.embeddingModel }} · {{ systemInfo.chatModel }}
    </p>
  </div>
</template>
