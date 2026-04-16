<script setup>
import { ref } from 'vue'
import { authApi } from '../api.js'

const emit = defineEmits(['login'])

const username = ref('')
const password = ref('')
const error = ref('')
const loading = ref(false)
const isRegister = ref(false)

async function submit() {
  error.value = ''
  if (!username.value.trim() || !password.value) {
    error.value = 'Completează username și parolă.'
    return
  }
  if (isRegister.value && password.value.length < 6) {
    error.value = 'Parola trebuie să aibă minim 6 caractere.'
    return
  }

  loading.value = true
  try {
    const fn = isRegister.value ? authApi.register : authApi.login
    const res = await fn({
      username: username.value.trim(),
      password: password.value
    })
    localStorage.setItem('jwt_token', res.data.token)
    localStorage.setItem('username', res.data.username)
    emit('login', res.data)
  } catch (e) {
    const detail = e.response?.data?.detail ?? e.response?.data?.title
    error.value = detail ?? (isRegister.value ? 'Eroare la înregistrare.' : 'Username sau parolă greșită.')
  } finally {
    loading.value = false
  }
}
</script>

<template>
  <div class="min-h-screen bg-gray-50 dark:bg-gray-950 flex items-center justify-center p-4 transition-colors">
    <div class="bg-white dark:bg-gray-900 border border-gray-200 dark:border-gray-800 rounded-3xl p-8 w-full max-w-sm shadow-xl">
      <div class="flex items-center gap-3 mb-6">
        <div class="w-10 h-10 bg-indigo-600 rounded-xl flex items-center justify-center text-sm font-bold text-white shadow-lg shadow-indigo-500/20">$</div>
        <h1 class="text-xl font-bold dark:text-white">Expense Tracker</h1>
      </div>

      <p class="text-sm text-gray-500 dark:text-gray-400 mb-6">
        {{ isRegister ? 'Creează un cont nou.' : 'Conectează-te pentru a continua.' }}
      </p>

      <div v-if="error" class="bg-red-500/10 border border-red-500/30 text-red-500 rounded-xl p-3 text-xs mb-4 font-medium">
        {{ error }}
      </div>

      <form @submit.prevent="submit" class="space-y-4">
        <div>
          <label class="block text-xs font-bold text-gray-400 uppercase mb-1.5 ml-1">Username</label>
          <input
            v-model="username"
            type="text"
            autocomplete="username"
            placeholder="Nume utilizator"
            class="w-full bg-gray-50 dark:bg-gray-800 border-none rounded-xl px-4 py-3 dark:text-white placeholder-gray-400 outline-none focus:ring-2 ring-indigo-500/20 transition"
          />
        </div>
        <div>
          <label class="block text-xs font-bold text-gray-400 uppercase mb-1.5 ml-1">Parolă</label>
          <input
            v-model="password"
            type="password"
            autocomplete="current-password"
            :placeholder="isRegister ? 'Minim 6 caractere' : 'Parola ta'"
            class="w-full bg-gray-50 dark:bg-gray-800 border-none rounded-xl px-4 py-3 dark:text-white placeholder-gray-400 outline-none focus:ring-2 ring-indigo-500/20 transition"
          />
        </div>
        <button
          type="submit"
          :disabled="loading"
          class="w-full bg-indigo-600 hover:bg-indigo-500 disabled:opacity-50 text-white font-bold py-3 rounded-xl shadow-lg shadow-indigo-500/20 transition-all active:scale-[0.98]"
        >
          {{ loading ? 'Se procesează...' : (isRegister ? 'Creează cont' : 'Conectare') }}
        </button>
      </form>

      <div class="mt-6 text-center">
        <button
          @click="isRegister = !isRegister; error = ''"
          class="text-xs font-bold text-indigo-500 hover:text-indigo-400 transition uppercase tracking-wider"
        >
          {{ isRegister ? 'Ai deja cont? Conectează-te' : 'Prima dată? Creează un cont' }}
        </button>
      </div>
    </div>
  </div>
</template>
