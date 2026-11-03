<script setup lang="ts">
import { ref } from 'vue'

defineProps<{ repositoryName: string | null; busy: boolean; elapsedSeconds: number }>()
const emit = defineEmits<{ ask: [question: string]; cancel: [] }>()
const question = ref('')

function submit() {
  const trimmed = question.value.trim()
  if (trimmed) emit('ask', trimmed)
}
</script>

<template>
  <form class="question-form" @submit.prevent="submit">
    <label for="question">Your question</label>
    <textarea id="question" v-model="question" maxlength="500" rows="4"
      :disabled="!repositoryName || busy"
      placeholder="Where is the Bearer token added to requests?"
      @keydown.ctrl.enter.prevent="submit" @keydown.meta.enter.prevent="submit"></textarea>
    <div class="question-footer">
      <span class="field-message">{{ question.length }}/500 · Ctrl + Enter to ask</span>
      <div class="question-actions">
        <button v-if="busy" class="secondary-button" type="button" @click="emit('cancel')">Cancel</button>
        <button class="primary-button" type="submit"
          :disabled="!repositoryName || busy || !question.trim()">
          <span v-if="busy" class="spinner" aria-hidden="true"></span>
          {{ busy ? 'Thinking…' : 'Ask question' }}
          <span v-if="!busy" aria-hidden="true">↗</span>
        </button>
      </div>
    </div>
    <p v-if="!repositoryName" class="field-message">Select a repository to start asking questions.</p>
    <p v-if="busy" class="processing-message" role="status">
      Searching code and checking evidence locally · {{ elapsedSeconds }}s elapsed
    </p>
  </form>
</template>
