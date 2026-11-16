<script setup>
import { computed, ref } from 'vue'
import { CalendarDays, TrendingUp, Sparkles, AlertCircle, Download } from 'lucide-vue-next'

const props = defineProps({ data: { type: Object, default: null }, exporting: Boolean, reviewing: Boolean })
defineEmits(['calendar', 'review', 'goal-plan'])
const selectedMonth = ref('')
const selectedSavings = ref([])
const months = computed(() => props.data?.forecast ?? [])
const current = computed(() => months.value.find(m => m.month === selectedMonth.value) ?? months.value[0])
const opportunities = computed(() => props.data?.savingsOpportunities ?? [])
const savings = computed(() => {
  const totals = new Map()
  for (const item of opportunities.value.filter(s => selectedSavings.value.includes(s.id)))
    totals.set(item.currency, (totals.get(item.currency) ?? 0) + item.monthlyTotal)
  return [...totals].map(([currency, amount]) => ({ currency, amount }))
})
const amount = value => new Intl.NumberFormat('ro-RO', { minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(value)
function dateLabel(value) {
  return new Date(`${value}T12:00:00Z`).toLocaleDateString('ro-RO', { day: 'numeric', month: 'short', timeZone: 'UTC' })
}
function monthLabel(value) {
  return new Date(`${value}-01T12:00:00Z`).toLocaleDateString('ro-RO', { month: 'short', year: 'numeric', timeZone: 'UTC' })
}
</script>

<template>
  <section v-if="data?.forecast" aria-labelledby="planning-title" class="space-y-4">
    <div class="flex flex-wrap items-center justify-between gap-3">
      <div>
        <h2 id="planning-title" class="text-lg font-bold flex items-center gap-2"><CalendarDays :size="20" class="text-indigo-500" /> Planifică din timp</h2>
        <p class="text-sm text-gray-500 dark:text-gray-400 mt-1">O privire înainte, pentru toate abonamentele tale.</p>
      </div>
      <button type="button" :disabled="exporting" @click="$emit('calendar')" class="inline-flex items-center gap-2 text-sm font-semibold px-4 py-2.5 bg-white dark:bg-gray-900 border border-gray-200 dark:border-gray-700 rounded-xl disabled:opacity-50">
        <Download :size="16" /> {{ exporting ? 'Se exportă…' : 'Exportă calendar .ics' }}
      </button>
    </div>
    <div class="grid grid-cols-1 lg:grid-cols-2 gap-4">
      <article class="panel p-5 sm:p-6 min-w-0">
        <h3 class="font-semibold flex items-center gap-2"><TrendingUp :size="18" class="text-indigo-500" /> Prognoză pe 6 luni</h3>
        <p class="text-xs text-gray-500 dark:text-gray-400 mt-2">Plăți estimate de azi înainte. Abonamentele anuale apar integral în luna scadentă.</p>
        <div class="grid grid-cols-3 sm:grid-cols-6 gap-2 mt-5" role="group" aria-label="Alege luna prognozei">
          <button v-for="month in months" :key="month.month" type="button" @click="selectedMonth = month.month" :aria-pressed="current?.month === month.month" :class="current?.month === month.month ? 'bg-indigo-600 text-white border-indigo-600' : 'border-gray-200 dark:border-gray-700 text-gray-600 dark:text-gray-300'" class="rounded-xl border px-2 py-3 text-xs font-semibold transition">
            {{ monthLabel(month.month) }}
            <span class="block mt-1 font-normal opacity-80">{{ month.paymentCount }} plăți</span>
          </button>
        </div>
        <div class="mt-5 flex flex-wrap gap-2 min-h-12" aria-live="polite" aria-atomic="true">
          <span v-for="total in current?.byCurrency ?? []" :key="total.currency" class="rounded-xl px-4 py-3 bg-indigo-50 dark:bg-indigo-500/10 font-bold text-indigo-700 dark:text-indigo-300 break-all">{{ amount(total.amount) }} {{ total.currency }}</span>
          <p v-if="!current?.byCurrency?.length" class="text-sm text-gray-500 dark:text-gray-400">Nicio plată estimată în această lună.</p>
        </div>
        <p class="text-xs text-gray-500 dark:text-gray-400 mt-4">Proiecție după frecvență și scadență; nu reprezintă un istoric de plăți efectuate.</p>
      </article>
      <article class="panel p-5 sm:p-6 min-w-0">
        <h3 class="font-semibold flex items-center gap-2"><CalendarDays :size="18" class="text-emerald-600 dark:text-emerald-400" /> Următoarele 30 de zile</h3>
        <ul v-if="data.upcoming?.length" tabindex="0" aria-label="Plăți estimate în următoarele 30 de zile" class="mt-4 divide-y divide-gray-100 dark:divide-gray-800 max-h-64 overflow-y-auto">
          <li v-for="payment in data.upcoming" :key="`${payment.id}-${payment.date}`" class="flex gap-3 items-center justify-between py-3">
            <div class="min-w-0"><p class="font-semibold text-sm break-words">{{ payment.name }}</p><p class="text-xs text-gray-500 dark:text-gray-400 mt-1">{{ dateLabel(payment.date) }} · {{ payment.category || 'Altele' }}</p></div>
            <span class="text-sm font-bold text-right shrink-0">{{ amount(payment.cost) }}<span class="block text-xs text-gray-500 dark:text-gray-400 font-normal">{{ payment.currency }}</span></span>
          </li>
        </ul>
        <p v-else class="text-sm text-gray-500 dark:text-gray-400 mt-5">Nu ai plăți estimate în următoarele 30 de zile.</p>
        <details v-if="data.overdueCount" class="mt-4 rounded-xl bg-amber-50 dark:bg-amber-500/10 p-3">
          <summary class="cursor-pointer text-sm font-semibold text-amber-800 dark:text-amber-300"><AlertCircle :size="15" class="inline mr-1" /> {{ data.overdueCount }} scadențe de actualizat</summary>
          <ul class="mt-2 text-xs text-amber-900 dark:text-amber-200 space-y-2"><li v-for="payment in data.overdue" :key="payment.id" class="break-words">{{ payment.name }} · {{ dateLabel(payment.date) }}</li></ul>
          <p class="mt-3 text-xs text-amber-900 dark:text-amber-200">Verifică data în abonament. O scadență trecută nu dovedește că plata lipsește.</p>
        </details>
        <p class="text-xs text-gray-500 dark:text-gray-400 mt-4">Cel mult 12 plăți afișate. Exportul .ics include următoarea scadență salvată a fiecărui abonament activ.</p>
      </article>
    </div>
    <article v-if="data.trials?.length" class="panel p-5 sm:p-6 space-y-3" aria-labelledby="trials-title">
      <h3 id="trials-title" class="font-semibold flex items-center gap-2"><AlertCircle :size="18" class="text-violet-600 dark:text-violet-300" aria-hidden="true" /> Perioade de probă de verificat</h3>
      <p class="text-xs text-gray-600 dark:text-gray-300">Verifică sau anulează planul în pagina furnizorului înainte de scadență. Sunt afișate până la 12 termene apropiate de azi, din {{ data.trialCount }} înregistrate.</p>
      <ul class="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-3">
        <li v-for="trial in data.trials" :key="trial.id" class="min-w-0 border border-violet-200 dark:border-violet-500/30 rounded-2xl p-4 space-y-2 text-sm">
          <p class="font-semibold break-words">{{ trial.name }}</p>
          <p class="text-xs font-semibold text-violet-800 dark:text-violet-200">{{ trial.daysRemaining < 0 ? 'Probă încheiată' : trial.daysRemaining === 0 ? 'Se încheie azi' : `Se încheie în ${trial.daysRemaining} zile` }} · {{ dateLabel(trial.endsOn) }}</p>
          <p class="text-xs text-gray-600 dark:text-gray-300">După probă: {{ amount(trial.cost) }} {{ trial.currency }} / {{ trial.billingPeriod === 'Yearly' ? 'an' : 'lună' }}</p>
          <button type="button" @click="$emit('review', trial.id)" :disabled="reviewing" :aria-label="`Revizuiește proba ${trial.name}`" class="text-xs font-semibold text-indigo-700 dark:text-indigo-300 rounded-xl bg-indigo-50 dark:bg-indigo-500/10 px-3 py-2 disabled:opacity-50">Revizuiește</button>
        </li>
      </ul>
    </article>
    <article v-if="opportunities.length" class="panel p-5 sm:p-6" aria-labelledby="savings-title">
      <h3 id="savings-title" class="font-semibold flex items-center gap-2"><Sparkles :size="18" class="text-violet-500" /> Simulator de economii</h3>
      <p class="text-sm text-gray-500 dark:text-gray-400 mt-2">Selectează abonamentele pe care le-ai putea suspenda. Simularea nu modifică abonamentele.</p>
      <div class="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-2 mt-4">
        <label v-for="item in opportunities" :key="item.id" class="flex items-center gap-3 rounded-xl p-3 border cursor-pointer min-w-0" :class="selectedSavings.includes(item.id) ? 'border-violet-500 bg-violet-50 dark:bg-violet-500/10' : 'border-gray-200 dark:border-gray-700'">
          <input v-model="selectedSavings" :value="item.id" type="checkbox" class="h-4 w-4 accent-violet-600 shrink-0" />
          <span class="min-w-0 text-sm"><span class="block font-semibold break-words">{{ item.name }}</span><span class="text-xs text-gray-500 dark:text-gray-400">{{ amount(item.monthlyTotal) }} {{ item.currency }} / lună</span></span>
        </label>
      </div>
      <div class="mt-4 rounded-xl bg-gray-50 dark:bg-gray-800 p-4 text-sm" aria-live="polite" aria-atomic="true">
        <p v-if="!savings.length" class="text-gray-600 dark:text-gray-300">Compară până la 3 dintre cele mai mari costuri din fiecare valută.</p>
        <div v-for="total in savings" :key="total.currency" class="flex flex-wrap items-center justify-between gap-3 py-1">
          <p class="font-semibold break-words min-w-0">{{ amount(total.amount) }} {{ total.currency }} / lună · aproximativ {{ amount(total.amount * 12) }} {{ total.currency }} / an</p>
          <button type="button" @click="$emit('goal-plan', { currency: total.currency, monthlyContribution: Number(total.amount.toFixed(2)) })" :aria-label="'Creează un obiectiv cu economiile în ' + total.currency" class="text-xs font-semibold underline text-violet-800 dark:text-violet-300">Transformă în obiectiv</button>
        </div>
      </div>
    </article>
  </section>
</template>
