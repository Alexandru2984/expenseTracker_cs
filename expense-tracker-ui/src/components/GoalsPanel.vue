<script setup>
import { computed, nextTick, onMounted, onUnmounted, ref, watch } from 'vue'
import { Target, Plus, CheckCircle2 } from 'lucide-vue-next'
import { goalsApi } from '../api.js'
import ConfirmModal from './ConfirmModal.vue'

const props = defineProps({ suggestion: { type: Object, default: null } })
const emit = defineEmits(['toast'])
const goals = ref([]), loading = ref(true), busy = ref(false), error = ref(''), filter = ref('active')
const editor = ref(false), editing = ref(null), contributor = ref(null), historyGoal = ref(null), confirming = ref(null)
const empty = () => ({ name: '', description: '', currency: 'RON', targetAmount: '', monthlyContribution: '', targetDate: '', isArchived: false })
const form = ref(empty()), contribution = ref(null)
const history = ref({ items: [], total: 0, skip: 0 }), historyLoading = ref(false)
const today = () => new Date().toISOString().slice(0, 10)
let active = true, generation = 0, historyGeneration = 0
const money = value => new Intl.NumberFormat('ro-RO', { minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(value)
const date = value => new Date(value + 'T12:00:00Z').toLocaleDateString('ro-RO', { timeZone: 'UTC' })
const statuses = { completed: 'Obiectiv atins', archived: 'Arhivat', overdue: 'Termen depășit', 'due-soon': 'Termen înainte de următoarea contribuție', behind: 'Ritm sub țintă', 'on-track': 'În ritmul planificat', 'no-plan': 'Fără ritm lunar' }
const shown = computed(() => goals.value.filter(g => filter.value === 'archived' ? g.isArchived : filter.value === 'completed' ? !g.isArchived && g.status === 'completed' : !g.isArchived && g.status !== 'completed'))
const currencies = computed(() => [...new Set(['RON', 'EUR', 'USD', 'GBP', 'CHF', ...goals.value.map(g => g.currency), form.value.currency])])
const field = 'w-full min-w-0 rounded-xl border border-gray-200 dark:border-gray-700 bg-gray-50 dark:bg-gray-800 px-3 py-2.5 text-sm text-gray-900 dark:text-gray-100 disabled:opacity-60'
function message(e) {
  const data = e.response?.data
  return data?.errors ? Object.values(data.errors).flat().join(' ') : data?.detail ?? data?.title ?? 'Obiectivele nu au putut fi actualizate. Reîncearcă.'
}
function report(e) { if (active && e.code !== 'ERR_CANCELED') error.value = message(e) }
async function load() {
  const request = ++generation
  loading.value = true
  try { const { data } = await goalsApi.list(); if (active && generation === request) goals.value = Array.isArray(data) ? data : [] }
  catch (e) { if (generation === request) report(e) }
  finally { if (active && generation === request) loading.value = false }
}
function closeForms() { editor.value = false; editing.value = null; contributor.value = null; form.value = empty(); contribution.value = null }
async function refresh() {
  closeForms(); historyGoal.value = null; historyGeneration++; error.value = ''
  await load()
}
async function openEditor(goal = null) {
  if (busy.value) return
  closeForms(); editing.value = goal; form.value = goal ? { ...goal, targetDate: goal.targetDate ?? '' } : empty()
  editor.value = true; error.value = ''
  await nextTick()
  document.getElementById('goal-name')?.focus()
}
function payload(goal) {
  return { name: goal.name, description: goal.description, currency: goal.currency,
    targetAmount: Number(goal.targetAmount), monthlyContribution: Number(goal.monthlyContribution || 0),
    targetDate: goal.targetDate || null, isArchived: goal.isArchived, version: goal.version }
}
async function save() {
  if (busy.value) return
  busy.value = true; error.value = ''
  try {
    const data = payload({ ...form.value, version: editing.value?.version })
    if (editing.value) await goalsApi.update(editing.value.id, data)
    else await goalsApi.create(data)
    if (!active) return
    filter.value = form.value.isArchived ? 'archived' : 'active'
    closeForms(); await load()
    if (active) emit('toast', 'Obiectiv salvat.', 'success')
  } catch (e) { report(e) }
  finally { if (active) busy.value = false }
}
function openContribution(goal) {
  if (busy.value) return
  closeForms(); contributor.value = goal; error.value = ''
  contribution.value = { id: crypto.randomUUID(), version: goal.version, amount: '', contributedOn: today(), note: '' }
  nextTick(() => document.getElementById('goal-contribution-amount')?.focus())
}
async function contribute() {
  if (busy.value || !contributor.value) return
  busy.value = true; error.value = ''
  const goalId = contributor.value.id
  try {
    await goalsApi.contribute(goalId, { ...contribution.value, amount: Number(contribution.value.amount) })
    if (!active) return
    closeForms(); await load()
    if (!active) return
    const current = goals.value.find(g => g.id === goalId)
    filter.value = current?.status === 'completed' ? 'completed' : 'active'
    if (historyGoal.value?.id === goalId && current) await showHistory(current)
    if (active) emit('toast', 'Contribuție înregistrată.', 'success')
  } catch (e) { report(e) }
  finally { if (active) busy.value = false }
}
async function archive(goal) {
  if (busy.value) return
  busy.value = true; error.value = ''
  try {
    await goalsApi.update(goal.id, payload({ ...goal, isArchived: !goal.isArchived }))
    if (!active) return
    closeForms(); filter.value = goal.isArchived ? (goal.remaining === 0 ? 'completed' : 'active') : 'archived'; await load()
    if (active) emit('toast', goal.isArchived ? 'Obiectiv reactivat.' : 'Obiectiv arhivat.', 'success')
  } catch (e) { report(e) }
  finally { if (active) busy.value = false }
}
async function showHistory(goal, skip = 0) {
  const request = ++historyGeneration
  historyGoal.value = goal; historyLoading.value = true; error.value = ''
  // Never show the previous goal's entries beneath a different name.
  history.value = { items: [], total: 0, skip }
  try { const { data } = await goalsApi.history(goal.id, { skip, take: 20 }); if (active && request === historyGeneration) history.value = data }
  catch (e) { if (request === historyGeneration) report(e) }
  finally { if (active && request === historyGeneration) historyLoading.value = false }
}
async function remove() {
  if (busy.value || !confirming.value) return
  const action = confirming.value; busy.value = true; error.value = ''
  try {
    if (action.entry) await goalsApi.removeContribution(action.goal.id, action.entry.id, action.goal.version)
    else await goalsApi.remove(action.goal.id, action.goal.version)
    if (!active) return
    closeForms(); await load()
    if (!active) return
    if (action.entry) {
      const current = goals.value.find(g => g.id === action.goal.id)
      if (current) await showHistory(current)
    } else if (historyGoal.value?.id === action.goal.id) { historyGoal.value = null; historyGeneration++ }
    if (active) emit('toast', action.entry ? 'Contribuție eliminată.' : 'Obiectiv șters.', 'success')
  } catch (e) { report(e) }
  finally { if (active) { busy.value = false; confirming.value = null } }
}
watch(() => props.suggestion, async draft => {
  if (!draft || busy.value) return
  await openEditor()
  if (!active) return
  form.value.currency = draft.currency; form.value.monthlyContribution = draft.monthlyContribution
  document.getElementById('goals-panel')?.scrollIntoView({ behavior: 'smooth', block: 'start' })
})
onMounted(load)
onUnmounted(() => { active = false; generation++; historyGeneration++ })
</script>

<template>
  <section id="goals-panel" aria-labelledby="goals-title" :aria-busy="busy" class="panel p-5 sm:p-6 space-y-5 scroll-mt-24">
    <div class="flex flex-wrap gap-3 items-center justify-between">
      <div><h2 id="goals-title" class="text-lg font-bold flex items-center gap-2"><Target :size="20" class="text-emerald-600 dark:text-emerald-400" aria-hidden="true" /> Obiective de economisire</h2><p class="text-sm text-gray-600 dark:text-gray-300 mt-1">Dă un scop banilor puși deoparte.</p></div>
      <div class="flex gap-3 items-center">
        <button type="button" @click="refresh" :disabled="busy || loading" class="text-xs underline text-indigo-700 dark:text-indigo-300 disabled:opacity-50">Reîncarcă obiectivele</button>
        <button type="button" @click="openEditor()" :disabled="busy" class="inline-flex gap-1 items-center rounded-xl bg-emerald-700 hover:bg-emerald-800 text-white text-sm font-semibold px-3 py-2 disabled:opacity-50"><Plus :size="16" aria-hidden="true" /> Obiectiv nou</button>
      </div>
    </div>
    <p class="text-xs text-gray-600 dark:text-gray-300">Înregistrezi manual sumele economisite. Aplicația nu mută bani. Estimările presupun contribuții lunare constante, fără dobândă sau conversii valutare; datele folosesc UTC.</p>
    <p v-if="error" role="alert" class="rounded-xl bg-red-50 dark:bg-red-500/10 p-3 text-sm text-red-800 dark:text-red-300 break-words">{{ error }}</p>

    <form v-if="editor" @submit.prevent="save" aria-label="Formular obiectiv" class="rounded-2xl border border-emerald-200 dark:border-emerald-700 p-4 space-y-4">
      <fieldset :disabled="busy" class="grid grid-cols-1 sm:grid-cols-2 gap-3">
        <legend class="font-semibold mb-3">{{ editing ? 'Editează obiectivul' : 'Un nou obiectiv' }}</legend>
        <label for="goal-name" class="text-xs font-semibold sm:col-span-2">Numele obiectivului<input id="goal-name" v-model="form.name" maxlength="100" required :class="[field, 'mt-1.5']" placeholder="Ex. Fond de rezervă" /></label>
        <label class="text-xs font-semibold">Suma țintă<input v-model="form.targetAmount" type="number" min="0.01" max="1000000" step="0.01" required :class="[field, 'mt-1.5']" /></label>
        <label class="text-xs font-semibold">Valuta obiectivului<select v-model="form.currency" aria-label="Valuta obiectivului" :disabled="editing?.contributionCount > 0" :class="[field, 'mt-1.5']"><option v-for="currency in currencies" :key="currency">{{ currency }}</option></select></label>
        <p v-if="editing?.contributionCount > 0" class="sm:col-span-2 text-xs text-gray-600 dark:text-gray-300">Valuta rămâne fixă cât timp există contribuții.</p>
        <label class="text-xs font-semibold">Plan lunar<input v-model="form.monthlyContribution" type="number" min="0" max="1000000" step="0.01" placeholder="Opțional" :class="[field, 'mt-1.5']" /></label>
        <label class="text-xs font-semibold">Termen dorit<input v-model="form.targetDate" type="date" min="1900-01-01" max="2100-12-31" :class="[field, 'mt-1.5']" /></label>
        <label class="text-xs font-semibold sm:col-span-2">Descriere opțională<textarea v-model="form.description" maxlength="500" rows="2" :class="[field, 'mt-1.5']" /></label>
      </fieldset>
      <div class="flex flex-wrap gap-3 items-center">
        <button type="submit" :disabled="busy" class="rounded-xl bg-emerald-700 hover:bg-emerald-800 text-white text-sm font-semibold px-4 py-2.5 disabled:opacity-50">{{ busy ? 'Se salvează…' : editing ? 'Salvează obiectivul' : 'Creează obiectivul' }}</button>
        <button type="button" @click="closeForms" :disabled="busy" class="text-sm underline disabled:opacity-50">Anulează obiectivul</button>
        <button v-if="editing" type="button" @click="confirming = { goal: editing }" :disabled="busy" class="text-sm underline text-red-700 dark:text-red-300 disabled:opacity-50">Șterge obiectivul</button>
      </div>
    </form>

    <form v-if="contributor" @submit.prevent="contribute" aria-label="Formular contribuție" class="rounded-2xl border border-emerald-200 dark:border-emerald-700 p-4 space-y-4">
      <fieldset :disabled="busy" class="grid grid-cols-1 sm:grid-cols-2 gap-3">
        <legend class="font-semibold break-words mb-3">Contribuție · {{ contributor.name }} · {{ contributor.currency }}</legend>
        <label for="goal-contribution-amount" class="text-xs font-semibold">Suma economisită<input id="goal-contribution-amount" v-model="contribution.amount" type="number" min="0.01" max="1000000" step="0.01" required :class="[field, 'mt-1.5']" /></label>
        <label class="text-xs font-semibold">Data contribuției<input v-model="contribution.contributedOn" type="date" min="1900-01-01" :max="today()" required :class="[field, 'mt-1.5']" /></label>
        <label class="text-xs font-semibold sm:col-span-2">Notă opțională<textarea v-model="contribution.note" maxlength="200" rows="2" :class="[field, 'mt-1.5']" /></label>
      </fieldset>
      <div class="flex flex-wrap items-center gap-3"><button type="submit" :disabled="busy" class="rounded-xl bg-emerald-700 hover:bg-emerald-800 text-white text-sm font-semibold px-4 py-2.5 disabled:opacity-50">{{ busy ? 'Se înregistrează…' : 'Înregistrează contribuția' }}</button><button type="button" @click="closeForms" :disabled="busy" class="text-sm underline disabled:opacity-50">Anulează contribuția</button></div>
    </form>

    <div role="group" aria-label="Filtrează obiectivele" class="flex flex-wrap gap-2">
      <button v-for="option in [{ value: 'active', label: 'În lucru' }, { value: 'completed', label: 'Atinse' }, { value: 'archived', label: 'Arhivate' }]" :key="option.value" type="button" @click="filter = option.value" :aria-pressed="filter === option.value" class="text-xs font-semibold rounded-xl border px-3 py-2" :class="filter === option.value ? 'bg-emerald-700 border-emerald-700 text-white' : 'border-gray-200 dark:border-gray-700 text-gray-700 dark:text-gray-200'">{{ option.label }}</button>
    </div>
    <p v-if="loading && !goals.length" class="text-sm text-gray-500 dark:text-gray-400">Se încarcă obiectivele…</p>
    <p v-else-if="!shown.length" class="text-sm text-gray-600 dark:text-gray-300 rounded-xl bg-gray-50 dark:bg-gray-800 p-4">{{ goals.length ? 'Niciun obiectiv în această categorie.' : 'Primul pas: alege o țintă. Poți porni și de la simulatorul de economii de mai sus.' }}</p>
    <div v-else class="grid grid-cols-1 lg:grid-cols-2 gap-4">
      <article v-for="goal in shown" :key="goal.id" :aria-label="'Obiectiv ' + goal.name" class="min-w-0 rounded-2xl border border-gray-200 dark:border-gray-700 p-4 space-y-3">
        <div class="flex gap-3 items-start justify-between"><h3 class="font-bold break-words min-w-0">{{ goal.name }}</h3><CheckCircle2 v-if="goal.status === 'completed'" :size="20" class="shrink-0 text-emerald-600 dark:text-emerald-400" aria-hidden="true" /></div>
        <p v-if="goal.description" class="text-xs text-gray-600 dark:text-gray-300 break-words whitespace-pre-wrap">{{ goal.description }}</p>
        <p class="text-sm break-words"><strong>{{ money(goal.savedAmount) }}</strong> / {{ money(goal.targetAmount) }} {{ goal.currency }}</p>
        <div role="progressbar" :aria-label="'Progres ' + goal.name" :aria-valuenow="goal.progressPercent" aria-valuemin="0" aria-valuemax="100" :aria-valuetext="money(goal.savedAmount) + ' din ' + money(goal.targetAmount) + ' ' + goal.currency" class="h-2.5 rounded-full overflow-hidden bg-gray-200 dark:bg-gray-700"><div class="h-full rounded-full bg-emerald-600" :style="{ width: goal.progressPercent + '%' }" /></div>
        <p class="text-xs font-semibold break-words" :class="['behind', 'overdue', 'due-soon'].includes(goal.status) ? 'text-amber-800 dark:text-amber-300' : 'text-emerald-800 dark:text-emerald-300'">{{ statuses[goal.status] }} · Rămas: {{ money(goal.remaining) }} {{ goal.currency }}</p>
        <p v-if="goal.status === 'due-soon'" class="text-xs text-amber-800 dark:text-amber-300">Proiecția lunară începe peste o lună. Pentru acest termen, economisește suma rămasă înainte de data aleasă.</p>
        <dl class="grid grid-cols-1 sm:grid-cols-2 gap-2 text-xs">
          <div><dt class="text-gray-600 dark:text-gray-300">Plan lunar</dt><dd class="font-semibold break-words mt-1">{{ money(goal.monthlyContribution) }} {{ goal.currency }}</dd></div>
          <div><dt class="text-gray-600 dark:text-gray-300">Termen dorit</dt><dd class="font-semibold mt-1">{{ goal.targetDate ? date(goal.targetDate) : 'Fără termen' }}</dd></div>
          <div v-if="goal.remaining > 0 && goal.monthlyContribution > 0"><dt class="text-gray-600 dark:text-gray-300">Atingere estimată</dt><dd class="font-semibold mt-1">{{ goal.estimatedCompletionDate ? date(goal.estimatedCompletionDate) + ' · ' + goal.monthsToGoal + ' luni' : 'Peste 100 de ani la ritmul actual' }}</dd></div>
          <div v-if="goal.requiredMonthlyContribution !== null && goal.remaining > 0"><dt class="text-gray-600 dark:text-gray-300">Lunar necesar până la termen</dt><dd class="font-semibold break-words mt-1">{{ money(goal.requiredMonthlyContribution) }} {{ goal.currency }}</dd></div>
        </dl>
        <div class="flex flex-wrap gap-2 pt-1">
          <button v-if="!goal.isArchived" type="button" @click="openContribution(goal)" :disabled="busy" :aria-label="'Contribuie la ' + goal.name" class="rounded-lg bg-emerald-700 hover:bg-emerald-800 text-white text-xs font-semibold px-3 py-2 disabled:opacity-50">Contribuie</button>
          <button type="button" @click="showHistory(goal)" :disabled="busy" :aria-label="'Istoric ' + goal.name" class="rounded-lg bg-gray-100 dark:bg-gray-800 text-xs font-semibold px-3 py-2 disabled:opacity-50">Istoric ({{ goal.contributionCount }})</button>
          <button type="button" @click="openEditor(goal)" :disabled="busy" :aria-label="'Editează obiectiv ' + goal.name" class="rounded-lg bg-gray-100 dark:bg-gray-800 text-xs font-semibold px-3 py-2 disabled:opacity-50">Editează</button>
          <button type="button" @click="archive(goal)" :disabled="busy" :aria-label="(goal.isArchived ? 'Reactivează ' : 'Arhivează ') + goal.name" class="text-xs underline px-2 py-2 disabled:opacity-50">{{ goal.isArchived ? 'Reactivează' : 'Arhivează' }}</button>
        </div>
      </article>
    </div>

    <section v-if="historyGoal" aria-labelledby="goal-history-title" class="rounded-2xl border border-gray-200 dark:border-gray-700 p-4 space-y-3">
      <div class="flex flex-wrap justify-between gap-3"><h3 id="goal-history-title" class="font-semibold break-words min-w-0">Contribuții · {{ historyGoal.name }}</h3><button type="button" @click="historyGoal = null; historyGeneration++" class="text-xs underline">Închide istoricul contribuțiilor</button></div>
      <p v-if="historyLoading" role="status" class="text-sm">Se încarcă istoricul…</p>
      <template v-else>
        <p v-if="!history.items.length" class="text-sm text-gray-600 dark:text-gray-300">Nicio contribuție pe această pagină.</p>
        <ul tabindex="0" aria-label="Istoricul contribuțiilor" class="max-h-80 overflow-y-auto divide-y divide-gray-100 dark:divide-gray-800">
          <li v-for="entry in history.items" :key="entry.id" class="py-3 flex flex-wrap items-center justify-between gap-3 text-sm">
            <div class="min-w-0"><p class="font-semibold break-words">{{ money(entry.amount) }} {{ historyGoal.currency }} · {{ date(entry.contributedOn) }}</p><p v-if="entry.note" class="mt-1 text-xs text-gray-600 dark:text-gray-300 whitespace-pre-wrap break-words">{{ entry.note }}</p></div>
            <button type="button" :disabled="busy" @click="confirming = { goal: historyGoal, entry }" :aria-label="'Elimină contribuția ' + date(entry.contributedOn) + ' de ' + money(entry.amount) + ' ' + historyGoal.currency" class="text-xs underline text-red-700 dark:text-red-300 disabled:opacity-50">Elimină</button>
          </li>
        </ul>
        <div v-if="history.total > 20" class="flex flex-wrap items-center justify-between gap-3 text-xs">
          <button type="button" :disabled="busy || history.skip === 0" @click="showHistory(historyGoal, Math.max(0, history.skip - 20))" class="underline disabled:opacity-50">Contribuții mai noi</button>
          <span>{{ history.skip + 1 }}–{{ Math.min(history.skip + 20, history.total) }} din {{ history.total }}</span>
          <button type="button" :disabled="busy || history.skip + 20 >= history.total" @click="showHistory(historyGoal, history.skip + 20)" class="underline disabled:opacity-50">Contribuții mai vechi</button>
        </div>
      </template>
    </section>
    <ConfirmModal v-if="confirming" :message="confirming.entry ? 'Elimini această contribuție? Progresul obiectivului va scădea.' : 'Ștergi obiectivul și toate contribuțiile lui? Acțiunea este definitivă.'" :confirm-label="confirming.entry ? 'Elimină contribuția' : 'Șterge obiectivul'" @confirm="remove" @cancel="confirming = null" />
  </section>
</template>
