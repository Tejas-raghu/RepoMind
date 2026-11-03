<script setup lang="ts">
import { computed } from 'vue'
import FileTreeBranch from './FileTreeBranch.vue'
import { buildFileTree } from '../services/fileTree'
import type { IndexedFile } from '../types/api'

const props = defineProps<{
  files: IndexedFile[]; selectedPath: string | null; loading: boolean; error: string
}>()
const emit = defineEmits<{ open: [path: string] }>()
const tree = computed(() => buildFileTree(props.files.map((file) => file.path)))
</script>

<template>
  <div class="file-tree" aria-label="Indexed files">
    <p v-if="loading" role="status">Loading indexed files…</p>
    <p v-else-if="error" role="alert" class="field-message error">{{ error }}</p>
    <p v-else-if="!files.length" class="field-message">No indexed files are available.</p>
    <ul v-else class="tree-root">
      <FileTreeBranch v-for="folder in tree.folders" :key="folder.path" :node="folder"
        :selected-path="selectedPath" @open="emit('open', $event)" />
      <li v-for="file in tree.files" :key="file.path">
        <button class="tree-file" :class="{ active: file.path === selectedPath }" type="button"
          :aria-current="file.path === selectedPath ? 'true' : undefined"
          @click="emit('open', file.path)">{{ file.name }}</button>
      </li>
    </ul>
  </div>
</template>
