<script setup lang="ts">
import type { RepositorySummary } from '../types/api'

defineProps<{ repositories: RepositorySummary[]; selectedId: number | null; loading: boolean; error: string }>()
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
        {{ repository.name }} · #{{ repository.id }}
      </option>
    </select>
    <p v-if="error" class="field-message error" role="alert">{{ error }}</p>
    <p v-else-if="!loading && repositories.length === 0" class="field-message">
      Upload a ZIP to index your first codebase.
    </p>
    <p v-else class="field-message">Answers are scoped to the selected repository.</p>
  </div>
</template>
