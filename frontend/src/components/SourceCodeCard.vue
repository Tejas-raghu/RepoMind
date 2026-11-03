<script setup lang="ts">
import { ref } from 'vue'
import type { AnswerSource } from '../types/api'

const props = defineProps<{ source: AnswerSource }>()
const emit = defineEmits<{ navigate: [source: AnswerSource] }>()
const copyState = ref('Copy code')

async function copyCode() {
  try {
    await navigator.clipboard.writeText(props.source.content)
    copyState.value = 'Copied'
  } catch {
    copyState.value = 'Copy failed'
  }
}
</script>

<template>
  <details class="source-card">
    <summary>
      <span class="source-glyph" aria-hidden="true">⌁</span>
      <span class="source-name">{{ source.filePath }}</span>
      <span class="source-lines">L{{ source.startLine }}–{{ source.endLine }}</span>
      <span class="source-chevron" aria-hidden="true">⌄</span>
    </summary>
    <div class="source-content">
      <div class="source-toolbar">
        <div class="source-meta">CHUNK #{{ source.chunkId }} · ORIGINAL INDEXED CODE</div>
        <button class="text-button" type="button" @click="emit('navigate', source)">Open in explorer</button>
        <button class="text-button" type="button" @click="copyCode">{{ copyState }}</button>
      </div>
      <pre><code>{{ source.content }}</code></pre>
    </div>
  </details>
</template>
