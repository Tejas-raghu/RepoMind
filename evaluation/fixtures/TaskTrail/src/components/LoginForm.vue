<script setup lang="ts">
import { ref } from 'vue';
import { signIn } from '../services/authService';

const email = ref('');
const password = ref('');
const loading = ref(false);
const error = ref('');

async function submitLogin() {
  error.value = '';
  loading.value = true;
  try {
    await signIn({ email: email.value, password: password.value });
    window.location.assign('/tasks');
  } catch (err) {
    error.value = err instanceof Error ? err.message : 'Unable to sign in';
  } finally {
    loading.value = false;
  }
}
</script>

<template>
  <form @submit.prevent="submitLogin">
    <label>Email <input v-model="email" type="email" required /></label>
    <label>Password <input v-model="password" type="password" required /></label>
    <p v-if="error" role="alert">{{ error }}</p>
    <button :disabled="loading" type="submit">Sign in</button>
  </form>
</template>
