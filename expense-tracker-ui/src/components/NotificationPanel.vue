<script setup>
import { computed, nextTick, onMounted, onUnmounted, ref, watch } from 'vue'
import { Bell } from 'lucide-vue-next'
import { notificationsApi } from '../api.js'

const props = defineProps({ email: { type: String, default: '' }, verified: Boolean })
const emit = defineEmits(['toast'])
const panel = ref(null)
const saved = ref(null), form = ref(null), zones = ref([]), history = ref([]), preview = ref(null)
const loading = ref(true), busy = ref(false), previewBusy = ref(false), historyBusy = ref(false), error = ref('')
let active = true, generation = 0
const fields = ['enabled', 'timeZone', 'sendHour', 'leadDays', 'renewals', 'trials', 'goalDeadlines', 'includeNames', 'version', 'recipientContext']
const payload = source => Object.fromEntries(fields.map(key => [key, source[key]]))
const dirty = computed(() => form.value && saved.value && JSON.stringify(payload(form.value)) !== JSON.stringify(payload(saved.value)))
const windowOptions = computed(() => [...new Set([0, 1, 3, 7, 14, form.value?.leadDays ?? 7])].sort((a,b)=>a-b))
const states = { Pending: 'În așteptare', Preparing: 'Se pregătește', Sending: 'În curs de trimitere', Sent: 'Acceptat de serverul email', Failed: 'Eșuat', Canceled: 'Anulat', Uncertain: 'Acceptare neconfirmată' }
const field = 'w-full min-w-0 rounded-xl border border-gray-200 dark:border-gray-700 bg-gray-50 dark:bg-gray-800 px-3 py-2.5 text-sm text-gray-900 dark:text-gray-100 disabled:opacity-60'
function message(e) { const data = e.response?.data; return data?.errors ? Object.values(data.errors).flat().join(' ') : data?.detail ?? data?.title ?? 'Preferințele nu au putut fi actualizate. Reîncearcă.' }
function report(e) { if (active && e.code !== 'ERR_CANCELED') error.value = message(e) }
function adopt(data) {
  if (!data || !/^[a-f0-9-]{36}$/i.test(data.version ?? '')) throw new Error('Răspuns invalid.')
  saved.value = data; form.value = payload(data); preview.value = null
}
async function load() {
  const request = ++generation
  loading.value = true; error.value = ''
  try {
    const [preferences, zoneList, recent] = await Promise.all([notificationsApi.get(), notificationsApi.zones(), notificationsApi.history()])
    if (!active || request !== generation) return
    adopt(preferences.data); zones.value = Array.isArray(zoneList.data) ? zoneList.data : []
    history.value = Array.isArray(recent.data) ? recent.data : []
  } catch (e) { if (request === generation) report(e) }
  finally { if (active && request === generation) loading.value = false }
}
async function recent() {
  if (historyBusy.value) return
  historyBusy.value = true
  try { const { data } = await notificationsApi.history(); if (active) history.value = Array.isArray(data) ? data : [] }
  catch (e) { report(e) }
  finally { if (active) historyBusy.value = false }
}
async function save() {
  if (busy.value || !form.value) return
  busy.value = true; error.value = ''
  try {
    const { data } = await notificationsApi.save(payload(form.value))
    if (!active) return
    adopt(data); await recent()
    if (active) emit('toast', data.enabled ? 'Notificări activate. Vei primi rezumatul când există termene apropiate.' : 'Notificări email oprite.', 'success')
  } catch (e) { report(e) }
  finally { if (active) busy.value = false }
}
async function showPreview() {
  if (previewBusy.value || dirty.value) return
  previewBusy.value = true; error.value = ''
  try { const { data } = await notificationsApi.preview(); if (active) preview.value = data }
  catch (e) { report(e) }
  finally { if (active) previewBusy.value = false }
}
function deviceZone() {
  const zone = Intl.DateTimeFormat().resolvedOptions().timeZone
  if (zones.value.includes(zone)) form.value.timeZone = zone
  else error.value = 'Fusul dispozitivului nu este în listă. Alege manual fusul orar.'
}
function nextLabel() {
  if (!saved.value?.nextDigestAtUtc) return ''
  const value = new Date(saved.value.nextDigestAtUtc)
  try { return value.toLocaleString('ro-RO', { timeZone: saved.value.timeZone, dateStyle: 'short', timeStyle: 'short' }) + ' · ' + saved.value.timeZone }
  catch { return value.toISOString().slice(0,16).replace('T',' ') + ' UTC' }
}
watch([()=>props.email,()=>props.verified],load)
onMounted(async()=>{
  await load()
  if (active && window.location.hash === '#notifications') {
    await nextTick()
    panel.value?.scrollIntoView({ block: 'start' })
    panel.value?.focus({ preventScroll: true })
  }
})
onUnmounted(()=>{active=false;generation++})
</script>

<template>
  <section ref="panel" tabindex="-1" aria-labelledby="notification-title" class="border-t border-gray-200 dark:border-gray-700 pt-5 space-y-3 text-gray-900 dark:text-gray-100" :aria-busy="busy">
    <div class="flex flex-wrap items-center justify-between gap-3">
      <h3 id="notification-title" class="font-bold text-sm flex gap-2 items-center"><Bell :size="17" aria-hidden="true" class="text-indigo-600 dark:text-indigo-300" /> Notificări email</h3>
      <button type="button" @click="load" :disabled="busy || loading" class="text-xs underline text-indigo-700 dark:text-indigo-300 disabled:opacity-50">Reîncarcă preferințele</button>
    </div>
    <p class="text-xs text-gray-600 dark:text-gray-300">Un rezumat pe zi în fusul orar ales, numai dacă există termene apropiate. Nu confirmă plăți sau anulări.</p>
    <p v-if="error" role="alert" class="rounded-xl bg-red-50 dark:bg-red-500/10 p-3 text-sm text-red-800 dark:text-red-300 break-words">{{ error }}</p>
    <p v-if="loading" role="status" class="text-sm text-gray-600 dark:text-gray-300">Se încarcă notificările…</p>
    <template v-else-if="form && saved">
      <p v-if="!saved.available" class="text-xs text-amber-800 dark:text-amber-300">Trimiterea emailurilor este indisponibilă momentan. Poți opri notificările deja activate.</p>
      <p v-if="!saved.canEnable" class="text-xs text-amber-800 dark:text-amber-300">Verifică adresa curentă de email înainte de a activa notificările.</p>
      <form @submit.prevent="save" class="space-y-3" aria-label="Preferințe notificări">
        <fieldset :disabled="busy" class="min-w-0 space-y-3">
          <legend class="sr-only">Programul și conținutul notificărilor</legend>
          <label class="flex gap-3 items-start rounded-xl bg-indigo-50 dark:bg-indigo-500/10 p-3 text-sm">
            <input v-model="form.enabled" type="checkbox" :disabled="!form.enabled && (!saved.available || !saved.canEnable)" class="mt-1 h-4 w-4 shrink-0 accent-indigo-600" />
            <span class="min-w-0"><span class="font-semibold block">Vreau rezumatul zilnic prin email</span><span v-if="saved.recipient" class="text-xs block break-all mt-1 text-gray-600 dark:text-gray-300">Către {{ saved.recipient }}</span></span>
          </label>
          <label class="block text-xs font-semibold">Fus orar<select v-model="form.timeZone" aria-label="Fus orar pentru notificări" :class="[field,'mt-1.5']"><option v-for="zone in zones" :key="zone" :value="zone">{{ zone }}</option></select></label>
          <button type="button" @click="deviceZone" class="text-xs underline text-indigo-700 dark:text-indigo-300">Folosește fusul dispozitivului</button>
          <div class="grid grid-cols-1 sm:grid-cols-2 gap-3">
            <label class="text-xs font-semibold">Ora locală<select v-model.number="form.sendHour" aria-label="Ora notificărilor" :class="[field,'mt-1.5']"><option v-for="hour in 24" :key="hour-1" :value="hour-1">{{ String(hour-1).padStart(2,'0') }}:00</option></select></label>
            <label class="text-xs font-semibold">Termene în următoarele<select v-model.number="form.leadDays" aria-label="Fereastra notificărilor" :class="[field,'mt-1.5']"><option v-for="days in windowOptions" :key="days" :value="days">{{ days === 0 ? 'Doar azi' : days + ' zile' }}</option></select></label>
          </div>
          <div class="space-y-2 text-sm">
            <label class="flex items-start gap-3"><input v-model="form.renewals" type="checkbox" class="mt-1 h-4 w-4 shrink-0 accent-indigo-600" /><span>Scadențe de abonamente</span></label>
            <label class="flex items-start gap-3"><input v-model="form.trials" type="checkbox" class="mt-1 h-4 w-4 shrink-0 accent-indigo-600" /><span>Încheierea perioadelor de probă</span></label>
            <label class="flex items-start gap-3"><input v-model="form.goalDeadlines" type="checkbox" class="mt-1 h-4 w-4 shrink-0 accent-indigo-600" /><span>Termene ale obiectivelor de economisire</span></label>
            <label class="flex items-start gap-3"><input v-model="form.includeNames" type="checkbox" class="mt-1 h-4 w-4 shrink-0 accent-indigo-600" /><span>Include numele în email (opțional)</span></label>
          </div>
          <p class="text-xs text-gray-600 dark:text-gray-300">Implicit, emailul arată numai numărul de termene. Sumele, notele și linkurile personale nu sunt incluse.</p>
          <button type="submit" :disabled="!dirty" class="w-full rounded-xl bg-indigo-600 hover:bg-indigo-700 disabled:opacity-50 text-white font-semibold text-sm px-4 py-2.5">{{ busy ? 'Se salvează…' : 'Salvează notificările' }}</button>
        </fieldset>
      </form>
      <p v-if="saved.enabled && saved.nextDigestAtUtc" class="text-xs text-gray-600 dark:text-gray-300 break-words">Următoarea verificare: {{ nextLabel() }}. Trimiterea are loc în jurul orei alese.</p>
      <p class="text-xs text-gray-600 dark:text-gray-300">Schimbarea adresei de email oprește notificările. Dezactivarea nu retrage un mesaj deja în curs de trimitere.</p>
      <button type="button" @click="showPreview" :disabled="busy || previewBusy || dirty" class="text-xs font-semibold underline text-indigo-700 dark:text-indigo-300 disabled:opacity-50">{{ previewBusy ? 'Se pregătește…' : 'Previzualizează rezumatul' }}</button>
      <p v-if="dirty" class="text-xs text-gray-600 dark:text-gray-300">Salvează preferințele înainte de previzualizare.</p>
      <div v-if="preview" class="rounded-xl bg-gray-50 dark:bg-gray-800 p-3 space-y-2">
        <p class="text-xs font-semibold">Exemplu pentru azi, pe datele actuale. Nu se trimite niciun email.</p>
        <pre class="font-sans text-xs whitespace-pre-wrap break-words text-gray-700 dark:text-gray-200">{{ preview.messageText }}</pre>
      </div>
      <div class="flex flex-wrap items-center justify-between gap-3 pt-2"><h4 class="text-xs font-semibold">Istoricul notificărilor</h4><button type="button" @click="recent" :disabled="busy || historyBusy" class="text-xs underline text-indigo-700 dark:text-indigo-300 disabled:opacity-50">Actualizează istoricul emailurilor</button></div>
      <p v-if="!history.length" class="text-xs text-gray-600 dark:text-gray-300">Încă nu există notificări înregistrate.</p>
      <ul v-else tabindex="0" aria-label="Istoricul notificărilor email" class="max-h-56 overflow-y-auto divide-y divide-gray-200 dark:divide-gray-700">
        <li v-for="row in history" :key="row.localDate" class="py-2 text-xs space-y-1">
          <p class="font-semibold">{{ row.localDate }} · {{ states[row.state] ?? 'Stare necunoscută' }}</p>
          <p class="text-gray-600 dark:text-gray-300">{{ row.attempts }} încercări{{ row.state === 'Uncertain' ? ' · Verifică inboxul; această trimitere nu este reluată automat.' : '' }}</p>
        </li>
      </ul>
      <p class="text-xs text-gray-600 dark:text-gray-300">Ultimele 20 de înregistrări din 30 de zile. Acceptarea de către serverul email nu garantează sosirea în inbox.</p>
    </template>
  </section>
</template>
