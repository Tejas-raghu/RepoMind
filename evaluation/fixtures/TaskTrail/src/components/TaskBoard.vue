<script setup lang="ts">
import { computed, onMounted, ref } from 'vue';
import { completeTask, isOverdue, listTasks, type Task } from '../services/taskService';

const tasks = ref<Task[]>([]);
const loading = ref(false);
const overdueCount = computed(() => tasks.value.filter((task) => isOverdue(task)).length);

async function refreshTasks() {
  loading.value = true;
  try {
    tasks.value = await listTasks();
  } finally {
    loading.value = false;
  }
}

async function markDone(taskId: string) {
  await completeTask(taskId);
  await refreshTasks();
}

onMounted(refreshTasks);
</script>

<template>
  <section>
    <h2>Task board</h2>
    <p>Overdue tasks: {{ overdueCount }}</p>
    <p v-if="loading">Loading tasks...</p>
    <ul v-else>
      <li v-for="task in tasks" :key="task.id">
        <span>{{ task.title }}</span>
        <button v-if="task.status !== 'done'" @click="markDone(task.id)">Complete</button>
      </li>
    </ul>
  </section>
</template>
