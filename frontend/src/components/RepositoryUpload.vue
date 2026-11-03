<script setup lang="ts">
import { ref } from 'vue'
import type { RepositoryUploadResponse } from '../types/api'

defineProps<{ uploading: boolean; error: string; result: RepositoryUploadResponse | null }>()
const emit = defineEmits<{ upload: [file: File] }>()
const file = ref<File | null>(null)
const input = ref<HTMLInputElement | null>(null)
const validationError = ref('')

function onFileChange(event: Event) {
  file.value = (event.target as HTMLInputElement).files?.[0] ?? null
  validationError.value = ''
}

function submit() {
  if (!file.value) {
    validationError.value = 'Choose a ZIP file first.'
    return
  }
  if (!/\.zip$/i.test(file.value.name)) {
    validationError.value = 'Choose a .zip file.'
    return
  }
  if (file.value.size === 0 || file.value.size > 5 * 1024 * 1024) {
    validationError.value = 'The ZIP must be nonempty and no larger than 5 MB.'
    return
  }
  emit('upload', file.value)
}

function clearFile() {
  file.value = null
  if (input.value) input.value.value = ''
}

defineExpose({ clearFile })
</script>

<template>
  <div class="divider"><span>ADD A REPOSITORY</span></div>
  <form class="upload-form" @submit.prevent="submit">
    <label class="upload-drop" for="repository-file">
      <span class="upload-icon" aria-hidden="true">↑</span>
      <span class="upload-label">{{ file?.name || 'Choose a source ZIP' }}</span>
      <span class="upload-hint">.zip · up to 5 MB</span>
    </label>
    <input id="repository-file" ref="input" type="file" accept=".zip,application/zip"
      :disabled="uploading" @change="onFileChange" />
    <p v-if="validationError || error" class="field-message error" role="alert">
      {{ validationError || error }}
    </p>
    <button class="primary-button upload-button" type="submit" :disabled="uploading">
      <span v-if="uploading" class="spinner" aria-hidden="true"></span>
      {{ uploading ? 'Indexing repository…' : 'Upload & index' }}
      <span v-if="!uploading" aria-hidden="true">↗</span>
    </button>
    <p v-if="uploading" class="field-message" role="status">
      Embedding source files locally. Larger repositories can take several minutes.
    </p>
  </form>
  <div v-if="result" class="upload-success" role="status">
    <span class="success-mark" aria-hidden="true">✓</span>
    <div>
      <strong>Repository #{{ result.repositoryId }} indexed</strong>
      <span>{{ result.filesIndexed }} files · {{ result.chunksCreated }} chunks
        <template v-if="result.filesSkipped"> · {{ result.filesSkipped }} skipped</template>
      </span>
    </div>
  </div>
</template>
