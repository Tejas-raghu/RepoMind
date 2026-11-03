<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue'
import { highlightSource, type HighlightToken } from '../services/sourceHighlight'
import type { IndexedFileContent } from '../types/api'

const props = defineProps<{
  file: IndexedFileContent | null; loading: boolean; error: string
  citation: { startLine: number; endLine: number } | null
}>()
const viewer = ref<HTMLElement | null>(null)
const tokens = ref<HighlightToken[][] | null>(null)
const highlightError = ref(false)
const copyState = ref('Copy code')
const pathCopyState = ref('Copy path')
const covered = computed(() => props.file?.lines.filter((line) => line.status === 'covered').length ?? 0)

watch(() => props.file, async (file, _old, onCleanup) => {
  tokens.value = null
  highlightError.value = false
  if (!file) return
  let cancelled = false
  onCleanup(() => { cancelled = true })
  try {
    const result = await highlightSource(file.path, file.lines.map((line) => line.content ?? ''))
    if (!cancelled) tokens.value = result
  } catch {
    if (!cancelled) highlightError.value = true
  }
}, { immediate: true })

watch(() => [props.file, props.citation] as const, async () => {
  if (!props.file || !props.citation) return
  await nextTick()
  const line = viewer.value?.querySelector<HTMLElement>(`[data-line="${props.citation.startLine}"]`)
  line?.scrollIntoView({ block: 'center', behavior: 'smooth' })
}, { flush: 'post' })

function cited(number: number) {
  return !!props.citation && number >= props.citation.startLine && number <= props.citation.endLine
}

async function copyCode() {
  if (!props.file) return
  try {
    await navigator.clipboard.writeText(props.file.lines.map((line) => line.content ??
      (line.status === 'conflict' ? '[conflicting stored chunks]' : '[source not available in indexed chunks]')).join('\n'))
    copyState.value = 'Copied'
  } catch { copyState.value = 'Copy failed' }
}

async function copyPath() {
  if (!props.file) return
  try {
    await navigator.clipboard.writeText(props.file.path)
    pathCopyState.value = 'Copied'
  } catch { pathCopyState.value = 'Copy failed' }
}
</script>

<template>
  <div class="code-viewer">
    <p v-if="loading" role="status">Loading indexed source…</p>
    <p v-else-if="error" role="alert" class="field-message error">{{ error }}</p>
    <p v-else-if="!file" class="field-message">Select a file or open a source citation.</p>
    <template v-else>
      <div class="viewer-toolbar">
        <strong :title="file.path">{{ file.path }}</strong>
        <div><button class="text-button" type="button" @click="copyPath">{{ pathCopyState }}</button>
          <button class="text-button" type="button" @click="copyCode">{{ copyState }}</button></div>
      </div>
      <p class="coverage-note">Indexed coverage: {{ covered }}/{{ file.lines.length }} lines. Original full-file
        fidelity is unavailable because ingestion stores chunks. Gaps and conflicts are marked below.
        <span v-if="file.path.toLowerCase().endsWith('.html')">Long HTML lines may have been omitted during indexing.</span>
      </p>
      <p v-if="highlightError || file.lines.length > 1000" class="field-message">
        {{ highlightError ? 'Syntax highlighting unavailable; showing plain text.' : 'Large file: showing plain text.' }}
      </p>
      <div ref="viewer" class="code-scroll" tabindex="0" aria-label="Indexed source code">
        <div v-for="line in file.lines" :key="line.number" class="code-line"
          :class="{ 'cited-line': cited(line.number), 'missing-line': line.status !== 'covered' }"
          :data-line="line.number">
          <span class="line-number">{{ line.number }}</span>
          <span class="line-text"><template v-if="line.status === 'covered'"><template
            v-if="tokens?.[line.number - 1]"><span v-for="(token, index) in tokens[line.number - 1]"
              :key="index" :style="{ color: token.color }">{{ token.content }}</span></template><template
            v-else>{{ line.content }}</template></template><span v-else class="missing-label">
              {{ line.status === 'conflict' ? '[conflicting stored chunks]' : '[source not available in indexed chunks]' }}
            </span></span>
        </div>
      </div>
      <p v-if="citation && (citation.endLine > file.lines.length || file.lines.slice(citation.startLine - 1, citation.endLine).some(line => line?.status !== 'covered'))"
        class="field-message">Some cited lines are unavailable in the file view. The answer card still has the exact stored snippet.</p>
    </template>
  </div>
</template>
