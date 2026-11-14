<script setup>
import { ref, onMounted, onBeforeUnmount } from 'vue'
import { financeIntegrationApi } from '../api.js'

const emit = defineEmits(['toast'])
const available = ref(false)
const tokens = ref([])
const password = ref('')
const days = ref(30)
const busy = ref(false)
const rawToken = ref('')
const rawId = ref('')
const showToken = ref(false)
const error = ref('')
const field = 'w-full mt-1 bg-gray-50 dark:bg-gray-800 border border-gray-200 dark:border-gray-700 rounded-xl px-3 py-2.5 text-sm dark:text-white'
async function load() {
  const { data } = await financeIntegrationApi.list()
  available.value = data.available
  tokens.value = data.tokens
}
onMounted(() => load().catch(() => { error.value = 'Integrarea nu este disponibilă momentan.' }))
onBeforeUnmount(() => { rawToken.value = ''; password.value = '' })
async function create() {
  if (busy.value) return
  busy.value = true; error.value = ''; rawToken.value = ''
  try {
    const { data } = await financeIntegrationApi.create({ currentPassword: password.value, validDays: days.value })
    rawToken.value = data.token; rawId.value = data.id; password.value = ''
    await load()
  } catch (e) { error.value = e.response?.data?.title ?? 'Tokenul nu a putut fi creat.' }
  finally { busy.value = false }
}
async function revoke(id) {
  if (busy.value || !confirm('Revoci accesul Finance pentru acest token?')) return
  busy.value = true; error.value = ''
  try {
    await financeIntegrationApi.revoke(id)
    if (id === rawId.value) rawToken.value = ''
    await load(); emit('toast', 'Accesul Finance a fost revocat.', 'success')
  } catch { error.value = 'Tokenul nu a putut fi revocat.' }
  finally { busy.value = false }
}
async function copy() {
  try { await navigator.clipboard.writeText(rawToken.value); emit('toast', 'Token copiat. Introdu-l în contul tău Finance.', 'success') }
  catch { error.value = 'Selectează și copiază manual tokenul.' }
}
</script>

<template>
  <section class="space-y-3 border-t border-gray-100 dark:border-gray-800 pt-5" aria-labelledby="finance-integration-title">
    <h3 id="finance-integration-title" class="text-sm font-bold dark:text-white">Conectează Finance (F#)</h3>
    <p class="text-xs leading-relaxed text-gray-500 dark:text-gray-400">Trimite abonamentele în prognozele și alertele Finance. Acces doar pentru citire, fără parole comune sau modificări ale abonamentelor.</p>
    <p v-if="error" class="text-xs text-red-700 dark:text-red-400" role="alert">{{ error }}</p>
    <template v-if="available">
      <form class="space-y-3" @submit.prevent="create">
        <label class="block text-xs text-gray-500 dark:text-gray-400">Confirmă parola curentă<input v-model="password" type="password" autocomplete="current-password" required maxlength="128" :class="field"></label>
        <label class="block text-xs text-gray-500 dark:text-gray-400">Valabilitate (zile)<input v-model.number="days" type="number" min="1" max="90" required :class="field"></label>
        <button type="submit" :disabled="busy || tokens.length >= 5" class="w-full bg-indigo-600 text-white rounded-xl px-4 py-2.5 text-sm font-bold disabled:opacity-50">{{ busy ? 'Se procesează…' : 'Creează token de conectare' }}</button>
      </form>
      <div v-if="rawToken" class="rounded-xl p-3 bg-emerald-500/10 space-y-2">
        <label class="block text-xs font-bold text-emerald-700 dark:text-emerald-300">Token afișat o singură dată<input readonly :type="showToken ? 'text' : 'password'" :value="rawToken" aria-label="Token Finance" :class="[field,'font-mono']"></label>
        <div class="flex gap-4 flex-wrap"><button class="text-xs font-bold text-emerald-700 dark:text-emerald-300" @click="copy">Copiază tokenul</button><button class="text-xs font-bold text-emerald-700 dark:text-emerald-300" @click="showToken = !showToken">{{ showToken ? 'Ascunde tokenul' : 'Arată tokenul' }}</button></div>
        <p class="text-xs text-gray-500 dark:text-gray-400">Deschide <a class="underline" href="https://f.micutu.com/#integrations" target="_blank" rel="noopener noreferrer">Finance → Integrations</a> și introdu tokenul. Nu îl trimite altor persoane.</p>
      </div>
      <ul v-if="tokens.length" class="space-y-2" aria-label="Tokenuri Finance active">
        <li v-for="token in tokens" :key="token.id" class="flex items-center justify-between gap-2 bg-gray-50 dark:bg-gray-800 rounded-xl p-3 text-xs text-gray-500 dark:text-gray-400">
          <span>Creat {{ new Date(token.createdAt).toLocaleDateString() }}<br>Expiră {{ new Date(token.expiresAt).toLocaleDateString() }}</span>
          <button :disabled="busy" class="text-red-700 dark:text-red-400 font-bold p-2" @click="revoke(token.id)">Revocă</button>
        </li>
      </ul>
      <p class="text-xs text-gray-500 dark:text-gray-400">Schimbarea parolei și „Deconectează toate dispozitivele” revocă și aceste tokenuri.</p>
    </template>
    <p v-else class="text-xs text-gray-500 dark:text-gray-400">Conectorul poate fi activat de administrator după instalare.</p>
  </section>
</template>
