<script setup>
import { computed, onMounted, onUnmounted, ref, watch } from 'vue'
import { budgetsApi } from '../api.js'
import ConfirmModal from './ConfirmModal.vue'
import { Wallet } from 'lucide-vue-next'

const props = defineProps({ refreshKey: { type: Object, default: null }, categories: { type: Array, default: () => [] } })
const emit = defineEmits(['toast'])
const budgets = ref([]), loading = ref(true), busy = ref(false), error = ref(''), editing = ref(null), deleting = ref(null)
const empty = () => ({ currency: 'RON', category: '', monthlyLimit: '' })
const form = ref(empty())
let generation = 0, active = true
const currencies = computed(() => [...new Set(['RON', 'EUR', 'USD', 'GBP', 'CHF', ...budgets.value.map(b => b.currency), ...props.categories.map(c => c.currency)])])
const categoryNames = computed(() => [...new Set(props.categories.filter(c => c.currency === form.value.currency).map(c => c.category))])
const money = value => new Intl.NumberFormat('ro-RO', { minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(value)
const message = e => e.response?.data?.detail ?? e.response?.data?.title ?? 'Bugetele nu au putut fi actualizate. Reîncearcă.'
async function load() {
  const request = ++generation
  loading.value = true
  try { const response = await budgetsApi.list(); if (active && generation === request) budgets.value = Array.isArray(response.data) ? response.data : [] }
  catch (e) { if (active && generation === request) error.value = message(e) }
  finally { if (active && generation === request) loading.value = false }
}
function reset() { editing.value = null; form.value = empty(); error.value = '' }
function edit(budget) {
  editing.value = budget
  form.value = { currency: budget.currency, category: budget.category, monthlyLimit: budget.monthlyLimit }
  error.value = ''
  document.getElementById('budget-limit')?.focus()
}
async function save() {
  if (busy.value) return
  busy.value = true; error.value = ''
  try {
    const payload = { ...form.value, monthlyLimit: Number(form.value.monthlyLimit) }
    if (editing.value) await budgetsApi.update(editing.value.id, { ...payload, version: editing.value.version })
    else await budgetsApi.create(payload)
    if (!active) return
    reset(); await load(); emit('toast', 'Buget salvat.', 'success')
  } catch (e) { if (active) error.value = message(e) }
  finally { if (active) busy.value = false }
}
async function remove() {
  if (busy.value || !deleting.value) return
  busy.value = true; error.value = ''
  const budget = deleting.value
  try {
    await budgetsApi.remove(budget.id, budget.version)
    if (!active) return
    if (editing.value?.id === budget.id) reset()
    await load(); emit('toast', 'Buget șters.', 'success')
  } catch (e) { if (active) error.value = message(e) }
  finally { if (active) { busy.value = false; deleting.value = null } }
}
watch(() => props.refreshKey, load)
onMounted(load)
onUnmounted(() => { active = false; generation++ })
const field = 'w-full min-w-0 rounded-xl border border-gray-200 dark:border-gray-700 bg-gray-50 dark:bg-gray-800 px-3 py-2.5 text-sm text-gray-900 dark:text-gray-100'
</script>

<template>
  <section class="panel p-5 sm:p-6 space-y-4" aria-labelledby="budgets-title" :aria-busy="busy">
    <div class="flex flex-wrap items-center justify-between gap-3">
      <h2 id="budgets-title" class="text-lg font-bold flex items-center gap-2"><Wallet :size="20" class="text-indigo-500" /> Bugete lunare</h2>
      <button type="button" @click="error = ''; load()" :disabled="loading || busy" class="text-xs underline text-indigo-700 dark:text-indigo-300 disabled:opacity-50">Reîncarcă bugetele</button>
    </div>
    <p class="text-sm text-gray-500 dark:text-gray-400">Limite pentru abonamentele active, calculate la costul lunar echivalent. Un buget general include toate categoriile din valuta aleasă.</p>
    <p v-if="error" role="alert" class="rounded-xl bg-red-50 dark:bg-red-500/10 p-3 text-sm text-red-800 dark:text-red-300">{{ error }}</p>
    <form @submit.prevent="save">
      <fieldset :disabled="busy" class="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-3 items-end">
        <label class="text-xs font-semibold text-gray-600 dark:text-gray-300">Valuta bugetului<select v-model="form.currency" aria-label="Valuta bugetului" :class="[field, 'mt-1.5']"><option v-for="currency in currencies" :key="currency">{{ currency }}</option></select></label>
        <label class="text-xs font-semibold text-gray-600 dark:text-gray-300">Categoria bugetului<input v-model="form.category" aria-label="Categoria bugetului" maxlength="100" list="budget-categories" placeholder="Toate categoriile" :class="[field, 'mt-1.5']" /></label>
        <datalist id="budget-categories"><option v-for="category in categoryNames" :key="category" :value="category" /></datalist>
        <label for="budget-limit" class="text-xs font-semibold text-gray-600 dark:text-gray-300">Limită lunară<input id="budget-limit" v-model="form.monthlyLimit" aria-label="Limita lunară a bugetului" type="number" min="0.01" max="1000000" step="0.01" required :class="[field, 'mt-1.5']" /></label>
        <button type="submit" class="rounded-xl bg-indigo-600 hover:bg-indigo-700 disabled:opacity-50 text-white font-semibold text-sm px-4 py-2.5">{{ busy ? 'Se salvează…' : editing ? 'Salvează bugetul' : 'Creează buget' }}</button>
      </fieldset>
      <button v-if="editing" type="button" @click="reset" :disabled="busy" class="mt-3 text-xs text-gray-600 dark:text-gray-300 underline">Anulează editarea bugetului</button>
    </form>
    <p v-if="loading && !budgets.length" class="text-sm text-gray-500 dark:text-gray-400">Se încarcă bugetele…</p>
    <p v-else-if="!budgets.length" class="rounded-xl bg-gray-50 dark:bg-gray-800 p-4 text-sm text-gray-500 dark:text-gray-400">Creează primul buget pentru a vedea cât ai planificat și cât spațiu mai ai în limita aleasă.</p>
    <div v-else class="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-3">
      <article v-for="budget in budgets" :key="budget.id" :aria-label="`Buget ${budget.currency} ${budget.category || 'general'}`" class="min-w-0 rounded-2xl border border-gray-200 dark:border-gray-700 p-4 space-y-3">
        <h3 class="font-semibold break-words">{{ budget.currency }} · {{ budget.category || 'Toate categoriile' }}</h3>
        <p class="text-sm break-words"><strong>{{ money(budget.monthlyPlanned) }}</strong><span class="text-gray-500 dark:text-gray-400"> / {{ money(budget.monthlyLimit) }} {{ budget.currency }}</span></p>
        <div role="progressbar" :aria-label="`Utilizarea bugetului ${budget.currency} ${budget.category || 'general'}`" :aria-valuenow="Math.min(100, Math.max(0, budget.usagePercent))" aria-valuemin="0" aria-valuemax="100" :aria-valuetext="`${money(budget.monthlyPlanned)} ${budget.currency} din ${money(budget.monthlyLimit)} ${budget.currency}`" class="h-2 rounded-full overflow-hidden bg-gray-200 dark:bg-gray-700">
          <div :class="budget.isOverBudget ? 'bg-red-600' : 'bg-indigo-600'" :style="{ width: `${Math.min(100, Math.max(0, budget.usagePercent))}%` }" class="h-full rounded-full" />
        </div>
        <p class="text-xs font-semibold break-words" :class="budget.isOverBudget ? 'text-red-700 dark:text-red-300' : 'text-gray-600 dark:text-gray-300'">{{ budget.isOverBudget ? `Depășit cu ${money(-budget.remaining)}` : `Disponibil în buget: ${money(budget.remaining)}` }} {{ budget.currency }}</p>
        <div class="flex flex-wrap gap-2 pt-1">
          <button type="button" @click="edit(budget)" :disabled="busy" :aria-label="`Editează buget ${budget.currency} ${budget.category || 'general'}`" class="flex-1 rounded-lg bg-gray-100 dark:bg-gray-800 text-gray-700 dark:text-gray-200 text-xs font-semibold px-3 py-2 disabled:opacity-50">Editează</button>
          <button type="button" @click="deleting = budget" :disabled="busy" :aria-label="`Șterge buget ${budget.currency} ${budget.category || 'general'}`" class="flex-1 rounded-lg bg-red-50 dark:bg-red-500/10 text-red-700 dark:text-red-300 text-xs font-semibold px-3 py-2 disabled:opacity-50">Șterge</button>
        </div>
      </article>
    </div>
    <ConfirmModal v-if="deleting" :message="`Ștergi bugetul ${deleting.currency} pentru ${deleting.category || 'toate categoriile'}? Abonamentele se păstrează.`" confirm-label="Șterge bugetul" @confirm="remove" @cancel="deleting = null" />
  </section>
</template>
