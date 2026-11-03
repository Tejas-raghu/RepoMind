<script setup lang="ts">
import { ref } from 'vue'
import type { FileNode } from '../services/fileTree'

defineProps<{ node: FileNode; selectedPath: string | null }>()
const emit = defineEmits<{ open: [path: string] }>()
const expanded = ref(true)
</script>

<template>
  <li>
    <button class="tree-folder" type="button" :aria-expanded="expanded" @click="expanded = !expanded">
      <span aria-hidden="true">{{ expanded ? '▾' : '▸' }}</span>{{ node.name }}
    </button>
    <ul v-if="expanded" class="tree-children">
      <FileTreeBranch v-for="folder in node.folders" :key="folder.path" :node="folder"
        :selected-path="selectedPath" @open="emit('open', $event)" />
      <li v-for="file in node.files" :key="file.path">
        <button class="tree-file" :class="{ active: file.path === selectedPath }" type="button"
          :title="file.path" :aria-current="file.path === selectedPath ? 'true' : undefined"
          @click="emit('open', file.path)">{{ file.name }}</button>
      </li>
    </ul>
  </li>
</template>
