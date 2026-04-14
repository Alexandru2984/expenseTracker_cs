<script setup>
import { ref } from 'vue'

const emit = defineEmits(['login'])

const token = ref('')
const error = ref('')

function save() {
  if (!token.value.trim()) {
    error.value = 'Introdu un token valid.'
    return
  }
  emit('login', token.value.trim())
}
</script>

<template>
  <div class="min-h-screen bg-gray-950 flex items-center justify-center p-4">
    <div class="bg-gray-900 border border-gray-800 rounded-2xl p-8 w-full max-w-sm shadow-xl">
      <div class="flex items-center gap-3 mb-6">
        <div class="w-9 h-9 bg-indigo-600 rounded-lg flex items-center justify-center text-sm font-bold text-white">$</div>
        <h1 class="text-lg font-semibold text-white">Expense Tracker</h1>
      </div>

      <p class="text-sm text-gray-400 mb-5">Introdu token-ul API pentru a continua.</p>

      <div v-if="error" class="bg-red-500/10 border border-red-500/30 text-red-400 rounded-lg p-3 text-sm mb-4">
        {{ error }}
      </div>

      <form @submit.prevent="save" class="space-y-4">
        <div>
          <label class="block text-xs text-gray-400 mb-1.5">API Token</label>
          <input
            v-model="token"
            type="password"
            autocomplete="current-password"
            placeholder="Bearer token..."
            class="w-full bg-gray-800 border border-gray-700 rounded-lg px-3 py-2.5 text-white placeholder-gray-500 focus:outline-none focus:border-indigo-500 transition"
          />
        </div>
        <button
          type="submit"
          class="w-full bg-indigo-600 hover:bg-indigo-500 text-white font-medium py-2.5 rounded-lg transition"
        >
          Salvează &amp; Conectează
        </button>
      </form>
    </div>
  </div>
</template>
