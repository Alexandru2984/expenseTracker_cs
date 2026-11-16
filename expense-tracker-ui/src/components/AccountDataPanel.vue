<script setup>
import { computed, nextTick, onMounted, onUnmounted, ref } from 'vue'
import { Download, Trash2 } from 'lucide-vue-next'
import { accountDataApi } from '../api.js'
import { useCooldown } from '../useCooldown.js'
const emit = defineEmits(['toast'])
const panel = ref(null), state = ref(null), error = ref(''), busy = ref(false), intent = ref('')
const password = ref(''), code = ref(''), confirmation = ref(''), acknowledged = ref(false)
const cooldown = useCooldown()
const field = 'w-full min-w-0 rounded-xl border border-gray-200 dark:border-gray-700 bg-gray-50 dark:bg-gray-800 px-3 py-2.5 text-sm text-gray-900 dark:text-gray-100'
const canDelete = computed(() => acknowledged.value && confirmation.value === state.value?.username)
let active = true, generation = 0
async function message(e) {
  let data = e.response?.data
  if (data instanceof Blob && data.size <= 8192) { try { data = JSON.parse(await data.text()) } catch { /* No problem body. */ } }
  return data?.detail ?? data?.title ?? 'Cererea nu a putut fi confirmată. Verifică starea contului înainte de a reîncerca.'
}
async function load() {
  const request = ++generation
  try {
    const { data } = await accountDataApi.get()
    if (active && request === generation) {
      state.value = data; cooldown.start(data.exportRetryAfterSeconds)
    }
    return active && request === generation
  } catch (e) { if (active && request === generation && e.code !== 'ERR_CANCELED') error.value = await message(e) }
}
async function begin(kind) {
  if (busy.value) return
  error.value = ''; password.value = ''; code.value = ''; confirmation.value = ''; acknowledged.value = false
  // Refresh the account name/counts and MFA status before presenting confirmation.
  busy.value = true
  const loaded = await load()
  if (active) busy.value = false
  if (active && loaded) { intent.value = kind; await nextTick(); panel.value?.querySelector('input[type="password"]')?.focus() }
}
function cancel() {
  if (busy.value) return
  intent.value = ''; error.value = ''; password.value = ''; code.value = ''; confirmation.value = ''; acknowledged.value = false
}
async function submit() {
  if (busy.value || !intent.value || (intent.value === 'delete' && !canDelete.value)) return
  busy.value = true; error.value = ''
  try {
    const proof = { currentPassword: password.value, code: code.value.trim() || null }
    if (intent.value === 'export') {
      const response = await accountDataApi.export(proof)
      if (!active) return
      const url = URL.createObjectURL(response.data)
      try {
        const link = document.createElement('a'); link.href = url; link.download = `expense-tracker-account-${new Date().toISOString().slice(0, 10)}.json`
        document.body.appendChild(link); link.click(); link.remove()
      } finally { URL.revokeObjectURL(url) }
      password.value = ''; code.value = ''; intent.value = ''; await load()
      if (active) emit('toast', 'Arhiva JSON a fost descărcată. Păstreaz-o într-un loc privat.', 'success')
    } else {
      await accountDataApi.remove({ ...proof, confirmationUsername: confirmation.value })
      if (!active) return
      password.value = ''; code.value = ''; confirmation.value = ''; intent.value = ''
      // The API layer clears the app even if this panel was closed meanwhile.
    }
  } catch (e) {
    if (active && e.code !== 'ERR_CANCELED') {
      error.value = await message(e)
      if (active) {
        code.value = ''
        if (e.response?.status === 429) cooldown.start(e.response.headers?.['retry-after'] ?? 60)
        else if (intent.value === 'export' && [413, 503].includes(e.response?.status)) await load()
      }
    }
  } finally { if (active) busy.value = false }
}
onMounted(async () => {
  await load()
  if (active && location.hash === '#data') { await nextTick(); panel.value?.scrollIntoView({ block: 'start' }); panel.value?.focus({ preventScroll: true }) }
})
onUnmounted(() => { active = false; generation++; state.value = null; password.value = ''; code.value = ''; confirmation.value = '' })
</script>

<template>
  <section ref="panel" tabindex="-1" aria-labelledby="account-data-title" class="border-t border-gray-200 dark:border-gray-700 pt-5 space-y-3 text-gray-900 dark:text-gray-100">
    <div class="flex flex-wrap justify-between items-center gap-3"><h3 id="account-data-title" class="font-bold text-sm">Datele mele</h3><button type="button" @click="error = ''; load()" :disabled="busy" class="text-xs underline text-indigo-700 dark:text-indigo-300">Reîncarcă datele contului</button></div>
    <p class="text-xs text-gray-600 dark:text-gray-300">Descarcă o arhivă privată sau șterge contul. Ambele cer parola și, dacă este activată, verificarea MFA.</p>
    <p v-if="error" role="alert" class="rounded-xl bg-red-50 dark:bg-red-500/10 p-3 text-sm text-red-800 dark:text-red-300 break-words">{{ error }}</p>
    <template v-if="state">
      <dl class="grid grid-cols-2 gap-2 text-xs bg-gray-50 dark:bg-gray-800 p-3 rounded-xl">
        <div><dt class="text-gray-600 dark:text-gray-300">Abonamente</dt><dd class="font-semibold">{{ state.subscriptions }}</dd></div>
        <div><dt class="text-gray-600 dark:text-gray-300">Bugete</dt><dd class="font-semibold">{{ state.budgets }}</dd></div>
        <div><dt class="text-gray-600 dark:text-gray-300">Obiective</dt><dd class="font-semibold">{{ state.goals }}</dd></div>
        <div><dt class="text-gray-600 dark:text-gray-300">Contribuții</dt><dd class="font-semibold">{{ state.contributions }}</dd></div>
      </dl>
      <div v-if="!intent" class="space-y-3">
        <p class="text-xs text-gray-600 dark:text-gray-300">JSON include profilul, abonamentele cu note și linkuri, istoricul prețurilor, bugetele, obiectivele, contribuțiile și preferințele/istoricul notificărilor. Fără parole sau secrete de autentificare. Limita este 16 MiB; poți cere o arhivă pe minut.</p>
        <button type="button" @click="begin('export')" :disabled="busy || cooldown.remaining.value > 0" class="w-full flex items-center justify-center gap-2 rounded-xl bg-indigo-600 hover:bg-indigo-700 disabled:opacity-50 text-white text-sm font-semibold px-4 py-2.5"><Download :size="16" aria-hidden="true" />{{ cooldown.remaining.value > 0 ? `Arhivă nouă în ${cooldown.remaining.value}s` : 'Descarcă arhiva JSON' }}</button>
        <button type="button" @click="begin('delete')" :disabled="busy" class="w-full flex items-center justify-center gap-2 rounded-xl border border-red-300 dark:border-red-700 text-red-800 dark:text-red-300 hover:bg-red-50 dark:hover:bg-red-950/30 text-sm font-semibold px-4 py-2.5"><Trash2 :size="16" aria-hidden="true" />Șterge contul meu</button>
      </div>
      <form v-else @submit.prevent="submit" class="space-y-3" aria-label="Confirmare operație date cont">
        <h4 class="text-sm font-semibold">{{ intent === 'delete' ? 'Confirmă ștergerea contului' : 'Confirmă descărcarea arhivei' }}</h4>
        <template v-if="intent === 'delete'">
          <p class="text-xs text-red-800 dark:text-red-300">Se șterg toate abonamentele, bugetele, obiectivele, contribuțiile și notificările din baza activă. Toate sesiunile și cele {{ state.financeTokens }} tokenuri Finance devin invalide. Această acțiune nu poate fi anulată din aplicație.</p>
          <p class="text-xs text-gray-600 dark:text-gray-300">Datele deja importate în Finance F# și fișierele descărcate nu sunt șterse automat. Backupurile și logurile existente pot păstra copii; aplicația nu garantează eliminarea lor imediată. Se păstrează o evidență minimă cu ID-ul contului și data ștergerii pentru verificarea restaurărilor. Un email deja în curs de expediere poate ajunge în continuare.</p>
          <label class="block text-xs font-semibold">Scrie numele contului: <span class="break-all">{{ state.username }}</span><input v-model="confirmation" aria-label="Numele contului pentru ștergere" autocomplete="off" autocapitalize="off" spellcheck="false" maxlength="50" required :disabled="busy" :class="[field, 'mt-1.5']" /></label>
          <label class="flex items-start gap-2 text-xs text-gray-700 dark:text-gray-200"><input v-model="acknowledged" type="checkbox" required :disabled="busy" class="mt-0.5" />Înțeleg că ștergerea este permanentă în baza activă și revocă accesul Finance.</label>
        </template>
        <p v-else class="text-xs text-gray-600 dark:text-gray-300">Arhiva conține date personale și note private. Fișierul JSON nu reface autentificarea și nu se importă automat în aplicație sau în Finance F#.</p>
        <label class="block text-xs font-semibold">Parola pentru datele contului<input v-model="password" type="password" autocomplete="current-password" maxlength="128" required :disabled="busy" :class="[field, 'mt-1.5']" /></label>
        <label v-if="state.mfaEnabled" class="block text-xs font-semibold">Cod MFA pentru datele contului<input v-model="code" aria-label="Cod MFA pentru datele contului" aria-describedby="account-data-mfa-help" autocomplete="one-time-code" maxlength="30" spellcheck="false" required :disabled="busy" :class="[field, 'mt-1.5']" /><span id="account-data-mfa-help" class="block mt-1 font-normal text-gray-600 dark:text-gray-300">Cod nou din autentificator sau un cod de recuperare nefolosit.</span></label>
        <button type="submit" :disabled="busy || (intent === 'delete' && !canDelete) || (intent === 'export' && cooldown.remaining.value > 0)" :class="['w-full rounded-xl disabled:opacity-50 text-white text-sm font-semibold px-4 py-2.5', intent === 'delete' ? 'bg-red-700 hover:bg-red-800' : 'bg-indigo-600 hover:bg-indigo-700']">{{ busy ? 'Se procesează…' : intent === 'delete' ? 'Șterge definitiv contul' : cooldown.remaining.value > 0 ? `Disponibil în ${cooldown.remaining.value}s` : 'Descarcă datele confirmate' }}</button>
        <button type="button" @click="cancel" :disabled="busy" class="text-xs underline text-gray-700 dark:text-gray-200">Anulează operația</button>
      </form>
    </template>
  </section>
</template>
