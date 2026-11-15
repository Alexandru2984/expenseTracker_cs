<script setup>
import { onMounted, ref } from 'vue'
import { accountApi } from '../api.js'
const emit = defineEmits(['toast', 'logout'])
const sessions = ref([])
const busy = ref(false)
const failed = ref(false)
async function load() {
  failed.value = false
  try { const response = await accountApi.sessions(); sessions.value = Array.isArray(response.data) ? response.data : [] }
  catch { failed.value = true }
}
async function revoke(session) {
  if (busy.value) return
  busy.value = true
  try {
    await accountApi.revokeSession(session.id)
    if (session.isCurrent) emit('logout')
    else { await load(); emit('toast', 'Sesiunea a fost revocată.', 'success') }
  } catch { emit('toast', 'Sesiunea nu a putut fi revocată.') }
  finally { busy.value = false }
}
onMounted(load)
const date = value => new Date(value).toLocaleString('ro-RO', { dateStyle: 'medium', timeStyle: 'short' })
</script>

<template>
  <section aria-labelledby="sessions-title" class="space-y-3 text-gray-900 dark:text-gray-100">
    <h3 id="sessions-title" class="text-sm font-bold">Sesiuni active</h3>
    <p class="text-xs text-gray-500 dark:text-gray-400">Revocarea oprește imediat accesul sesiunii alese. Sunt afișate maximum 100 de sesiuni.</p>
    <button v-if="failed" type="button" @click="load" class="text-sm text-indigo-600 dark:text-indigo-300 underline">Reîncearcă încărcarea sesiunilor</button>
    <ul class="space-y-2">
      <li v-for="session in sessions" :key="session.id" class="rounded-xl bg-gray-50 dark:bg-gray-800 p-3 flex flex-wrap justify-between gap-3 items-center">
        <div class="min-w-0 text-xs text-gray-600 dark:text-gray-300"><p class="font-bold">{{ session.isCurrent ? 'Sesiunea curentă' : 'Altă sesiune' }}</p><p class="mt-1">Creată {{ date(session.createdAt) }}</p><p class="mt-1">Expiră {{ date(session.expiresAt) }}</p></div>
        <button type="button" :disabled="busy" @click="revoke(session)" :aria-label="session.isCurrent ? 'Revocă sesiunea curentă' : `Revocă sesiunea din ${date(session.createdAt)}`" class="text-xs font-semibold text-red-700 dark:text-red-300 px-3 py-2 rounded-lg hover:bg-red-500/10 disabled:opacity-50">Deconectează</button>
      </li>
    </ul>
  </section>
</template>
