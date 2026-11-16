import { apiClient } from './apiClient';

export type TaskStatus = 'todo' | 'in_progress' | 'done';
export interface Task {
  id: string;
  title: string;
  dueAt: string | null;
  status: TaskStatus;
  assignedTo: string | null;
}

export async function listTasks(): Promise<Task[]> {
  return apiClient.get<Task[]>('/tasks');
}

export async function createTask(title: string, dueAt: string | null): Promise<Task> {
  return apiClient.post<Task>('/tasks', { title, dueAt });
}

/** Mark a task as completed using the server-side task endpoint. */
export async function completeTask(taskId: string): Promise<Task> {
  return apiClient.patch<Task>(`/tasks/${taskId}`, { status: 'done' });
}

/** A task is overdue when it is unfinished and its deadline has passed. */
export function isOverdue(task: Task, now = new Date()): boolean {
  return task.status !== 'done' && !!task.dueAt && new Date(task.dueAt) < now;
}

export function groupTasksByStatus(tasks: Task[]): Record<TaskStatus, Task[]> {
  return {
    todo: tasks.filter((task) => task.status === 'todo'),
    in_progress: tasks.filter((task) => task.status === 'in_progress'),
    done: tasks.filter((task) => task.status === 'done'),
  };
}
