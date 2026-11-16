<script setup>
import { computed, nextTick, onMounted, onUnmounted, ref } from 'vue'
import { ShieldCheck } from 'lucide-vue-next'
import { mfaApi } from '../api.js'
const emit = defineEmits(['toast', 'changed'])
const panel = ref(null), status = ref(null), busy = ref(false), error = ref(''), setup = ref(null), qr = ref('')
const password = ref(''), code = ref(''), recoveryCodes = ref([]), revealed = ref(false), manage = ref('')
let active = true, generation = 0
const field = 'w-full min-w-0 rounded-xl border border-gray-200 dark:border-gray-700 bg-gray-50 dark:bg-gray-800 px-3 py-2.5 text-sm text-gray-900 dark:text-gray-100'
const hasCodes = computed(() => recoveryCodes.value.length > 0)
const message = e => e.response?.data?.detail ?? e.response?.data?.title ?? 'MFA nu a putut fi actualizată. Reîncearcă.'
function report(e) { if (active && e.code !== 'ERR_CANCELED') error.value = message(e) }
async function load() {
  const request = ++generation
  try { const { data } = await mfaApi.get(); if (active && request === generation) status.value = data }
  catch (e) { if (request === generation) report(e) }
}
async function start() {
  if (busy.value) return
  busy.value = true; error.value = ''; recoveryCodes.value = []; revealed.value = false
  try {
    const { data } = await mfaApi.setup({ currentPassword: password.value })
    if (!active) return
    password.value = ''; setup.value = data
    const QRCode = (await import('qrcode')).default
    const image = await QRCode.toDataURL(data.otpauthUri, { width: 216, margin: 2 })
    if (active) qr.value = image
    await load()
  } catch (e) { report(e) }
  finally { if (active) busy.value = false }
}
async function confirm() {
  if (busy.value || !setup.value) return
  busy.value = true; error.value = ''
  try {
    const { data } = await mfaApi.confirm({ setupId: setup.value.setupId, code: code.value.trim() })
    if (!active) return
    recoveryCodes.value = data.recoveryCodes; revealed.value = false
    setup.value = null; qr.value = ''; code.value = ''; password.value = ''
    await load()
    if (active) { emit('changed'); emit('toast', 'MFA activată. Celelalte sesiuni web au fost deconectate.', 'success') }
  } catch (e) { report(e) }
  finally { if (active) busy.value = false }
}
async function cancel() {
  if (busy.value || !setup.value) return
  busy.value = true
  try { await mfaApi.cancel(setup.value.setupId); if (active) { setup.value = null; qr.value = ''; code.value = ''; await load() } }
  catch (e) { report(e) }
  finally { if (active) busy.value = false }
}
async function submitManagement() {
  if (busy.value) return
  busy.value = true; error.value = ''
  try {
    const data = { currentPassword: password.value, code: code.value.trim() }
    if (manage.value === 'disable') {
      await mfaApi.disable(data)
      if (!active) return
      recoveryCodes.value = []; revealed.value = false
    } else {
      const response = await mfaApi.recovery(data)
      if (!active) return
      recoveryCodes.value = response.data.recoveryCodes; revealed.value = false
    }
    const disabled = manage.value === 'disable'
    password.value = ''; code.value = ''; manage.value = ''
    await load()
    if (active) { if (disabled) emit('changed'); emit('toast', disabled ? 'MFA dezactivată.' : 'Codurile de recuperare au fost înlocuite.', 'success') }
  } catch (e) { report(e) }
  finally { if (active) busy.value = false }
}
async function copyCodes() {
  try { await navigator.clipboard.writeText(recoveryCodes.value.join('\n')); if (active) emit('toast', 'Coduri copiate. Păstrează-le într-un loc sigur.', 'success') }
  catch { if (active) error.value = 'Selectează și copiază manual codurile de recuperare.' }
}
onMounted(async () => {
  await load()
  if (active && location.hash === '#security') { await nextTick(); panel.value?.scrollIntoView({ block: 'start' }); panel.value?.focus({ preventScroll: true }) }
})
onUnmounted(() => { active = false; generation++; setup.value = null; recoveryCodes.value = []; qr.value = ''; password.value = ''; code.value = '' })
</script>

<template>
  <section ref="panel" tabindex="-1" aria-labelledby="mfa-title" class="border-t border-gray-200 dark:border-gray-700 pt-5 space-y-3 text-gray-900 dark:text-gray-100">
    <div class="flex flex-wrap justify-between items-center gap-3"><h3 id="mfa-title" class="font-bold text-sm flex gap-2 items-center"><ShieldCheck :size="18" aria-hidden="true" class="text-emerald-700 dark:text-emerald-300" /> Autentificare în doi pași</h3><button type="button" @click="error = ''; load()" :disabled="busy" class="text-xs underline text-indigo-700 dark:text-indigo-300">Reîncarcă MFA</button></div>
    <p class="text-xs text-gray-600 dark:text-gray-300">Protejează loginul cu un cod din aplicația de autentificare. Tokenurile de citire Finance F# rămân valabile până le revoci.</p>
    <p v-if="error" role="alert" class="rounded-xl bg-red-50 dark:bg-red-500/10 p-3 text-sm text-red-800 dark:text-red-300 break-words">{{ error }}</p>
    <template v-if="status">
      <p class="text-xs font-semibold" :class="status.enabled ? 'text-emerald-800 dark:text-emerald-300' : 'text-gray-600 dark:text-gray-300'">{{ status.enabled ? 'MFA este activată' : 'MFA este oprită' }}</p>
      <p v-if="!status.available" class="text-xs text-amber-800 dark:text-amber-300">Configurarea autentificatorului este indisponibilă momentan. Dacă MFA este deja activată, codurile de recuperare rămân disponibile.</p>
      <div v-if="hasCodes" class="rounded-xl border border-amber-300 dark:border-amber-600 p-3 space-y-3">
        <h4 class="text-sm font-semibold">Salvează codurile de recuperare</h4>
        <p class="text-xs text-gray-600 dark:text-gray-300">Fiecare cod funcționează o singură dată. Nu le putem afișa din nou după închiderea acestui panou. Păstrează-le separat de telefon.</p>
        <button type="button" @click="revealed = !revealed" :aria-pressed="revealed" class="text-xs underline text-indigo-700 dark:text-indigo-300">{{ revealed ? 'Ascunde codurile de recuperare' : 'Arată codurile de recuperare' }}</button>
        <textarea v-if="revealed" readonly aria-label="Coduri de recuperare MFA" :value="recoveryCodes.join('\n')" rows="10" spellcheck="false" :class="[field,'font-mono text-xs']" />
        <div class="flex flex-wrap gap-3"><button v-if="revealed" type="button" @click="copyCodes" class="text-xs underline text-indigo-700 dark:text-indigo-300">Copiază codurile de recuperare</button><button type="button" @click="recoveryCodes = []; revealed = false" class="text-xs underline text-emerald-800 dark:text-emerald-300">Am salvat codurile</button></div>
      </div>
      <form v-if="!status.enabled && !setup" @submit.prevent="start" class="space-y-3">
        <p v-if="status.pendingSetupId" class="text-xs text-amber-800 dark:text-amber-300">Există o configurare nefinalizată. Poți reîncepe cu parola curentă; codul QR vechi va fi înlocuit.</p>
        <label class="block text-xs font-semibold">Parola pentru configurarea MFA<input v-model="password" type="password" autocomplete="current-password" maxlength="128" required :disabled="busy || !status.available" :class="[field,'mt-1.5']" /></label>
        <button type="submit" :disabled="busy || !status.available" class="w-full rounded-xl bg-emerald-700 hover:bg-emerald-800 disabled:opacity-50 text-white text-sm font-semibold px-4 py-2.5">{{ busy ? 'Se pregătește…' : 'Configurează autentificatorul' }}</button>
      </form>
      <form v-if="setup" @submit.prevent="confirm" class="space-y-3">
        <p class="text-xs text-gray-600 dark:text-gray-300">Scanează codul QR într-o aplicație TOTP sau introdu cheia manual. Nu partaja codul QR ori cheia. Configurarea expiră în 10 minute.</p>
        <img v-if="qr" :src="qr" alt="Cod QR secret pentru configurarea autentificatorului" width="216" height="216" class="block mx-auto max-w-full bg-white rounded-xl" />
        <label class="block text-xs font-semibold">Cheie manuală MFA<input :value="setup.secret" readonly aria-label="Cheie manuală MFA" spellcheck="false" class="mt-1.5 w-full min-w-0 text-xs font-mono rounded-xl bg-gray-50 dark:bg-gray-800 px-3 py-2.5" /></label>
        <label class="block text-xs font-semibold">Codul de confirmare MFA<input v-model="code" inputmode="numeric" autocomplete="one-time-code" pattern="[0-9]{6}" minlength="6" maxlength="6" required :disabled="busy" :class="[field,'mt-1.5 text-center']" /></label>
        <button type="submit" :disabled="busy" class="w-full rounded-xl bg-emerald-700 hover:bg-emerald-800 disabled:opacity-50 text-white text-sm font-semibold px-4 py-2.5">Activează MFA</button>
        <button type="button" @click="cancel" :disabled="busy" class="text-xs underline text-gray-700 dark:text-gray-200">Anulează configurarea MFA</button>
      </form>
      <template v-if="status.enabled">
        <p class="text-xs text-gray-600 dark:text-gray-300">{{ status.recoveryCodesRemaining }} coduri de recuperare nefolosite. Resetarea parolei prin email păstrează MFA.</p>
        <div v-if="!manage" class="flex flex-wrap gap-3"><button type="button" @click="manage = 'recovery'; error = ''; password = ''; code = ''" class="text-xs underline text-indigo-700 dark:text-indigo-300">Înlocuiește codurile de recuperare</button><button type="button" @click="manage = 'disable'; error = ''; password = ''; code = ''" class="text-xs underline text-red-700 dark:text-red-300">Dezactivează MFA</button></div>
        <form v-else @submit.prevent="submitManagement" class="space-y-3">
          <h4 class="text-sm font-semibold">{{ manage === 'disable' ? 'Confirmă dezactivarea MFA' : 'Coduri de recuperare noi' }}</h4>
          <p class="text-xs text-gray-600 dark:text-gray-300">{{ manage === 'disable' ? 'Celelalte sesiuni web vor fi deconectate. Tokenurile Finance se păstrează.' : 'Codurile anterioare vor deveni invalide.' }}</p>
          <label class="block text-xs font-semibold">Parola pentru modificarea MFA<input v-model="password" type="password" autocomplete="current-password" maxlength="128" required :disabled="busy" :class="[field,'mt-1.5']" /></label>
          <label class="block text-xs font-semibold">Cod MFA sau cod de recuperare<input v-model="code" autocomplete="one-time-code" maxlength="30" required :disabled="busy" spellcheck="false" :class="[field,'mt-1.5']" /></label>
          <button type="submit" :disabled="busy" class="w-full rounded-xl bg-indigo-600 hover:bg-indigo-700 disabled:opacity-50 text-white text-sm font-semibold px-4 py-2.5">{{ manage === 'disable' ? 'Confirmă dezactivarea MFA' : 'Generează coduri noi' }}</button>
          <button type="button" @click="manage = ''; password = ''; code = ''" :disabled="busy" class="text-xs underline">Anulează modificarea MFA</button>
        </form>
      </template>
    </template>
  </section>
</template>
