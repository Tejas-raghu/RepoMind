<script setup lang="ts">
import RepositorySelector from './RepositorySelector.vue'
import RepositoryUpload from './RepositoryUpload.vue'
import type { RepositorySummary, RepositoryUploadResponse } from '../types/api'

defineProps<{
  repositories: RepositorySummary[]
  selectedId: number | null
  loading: boolean
  listError: string
  uploading: boolean
  uploadError: string
  uploadResult: RepositoryUploadResponse | null
}>()

const emit = defineEmits<{
  refresh: []
  select: [id: number | null]
  upload: [file: File]
}>()
</script>

<template>
  <section class="panel repository-panel" aria-labelledby="repository-title">
    <div class="panel-heading">
      <div class="eyebrow">01 / WORKSPACE</div>
      <h2 id="repository-title">Your repositories</h2>
      <p>Choose an indexed codebase, or bring in a new one.</p>
    </div>
    <RepositorySelector :repositories="repositories" :selected-id="selectedId" :loading="loading"
      :error="listError" @refresh="emit('refresh')" @select="emit('select', $event)" />
    <RepositoryUpload :uploading="uploading" :error="uploadError" :result="uploadResult"
      @upload="emit('upload', $event)" />
  </section>
</template>
