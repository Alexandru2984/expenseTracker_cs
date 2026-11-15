<script setup>
import { ref, onMounted, onUnmounted, nextTick } from 'vue'
import { X, History, ArrowRight, RefreshCw } from 'lucide-vue-next'
import { subscriptionsApi } from '../api.js'
import { useDialog } from '../useDialog.js'

const props = defineProps({ subscription: { type: Object, required: true } })
const emit = defineEmits(['close'])
const dialog = ref(null)
useDialog(dialog, () => emit('close'))
const data = ref(null), loading = ref(false), failed = ref(false), skip = ref(0)
const take = 20
let controller, epoch = 0
async function load(nextSkip = skip.value) {
  const request = ++epoch
  controller?.abort(); controller = new AbortController()
  loading.value = true; failed.value = false
  try {
    const response = await subscriptionsApi.priceHistory(props.subscription.id, { skip: nextSkip, take }, controller.signal)
    if (request !== epoch) return
    data.value = response.data; skip.value = nextSkip
    await nextTick()
    if (request === epoch && dialog.value) dialog.value.scrollTop = 0
  } catch (error) {
    if (request === epoch && error.code !== 'ERR_CANCELED') failed.value = true
  } finally { if (request === epoch) loading.value = false }
}
onMounted(() => load())
onUnmounted(() => { epoch++; controller?.abort() })
const number = value => Number(value).toLocaleString('ro-RO', { minimumFractionDigits: 2, maximumFractionDigits: 2 })
const price = value => `${number(value.cost)} ${value.currency} / ${value.billingPeriod === 'Yearly' ? 'an' : 'lună'}`
const signed = value => `${value > 0 ? '+' : ''}${number(value)}`
const date = value => new Date(value).toLocaleString('ro-RO', { dateStyle: 'medium', timeStyle: 'short' })
</script>

<template>
  <Teleport to="body">
    <div class="fixed inset-0 z-50 flex items-end sm:items-center justify-center p-0 sm:p-4 bg-black/60 backdrop-blur-sm" @mousedown.self="emit('close')">
      <section ref="dialog" role="dialog" aria-modal="true" aria-labelledby="price-history-title" tabindex="-1" class="bg-white dark:bg-gray-900 text-gray-900 dark:text-gray-100 border border-gray-200 dark:border-gray-800 w-full max-w-xl rounded-t-3xl sm:rounded-3xl p-5 sm:p-6 shadow-2xl max-h-[90vh] overflow-y-auto">
        <header class="sticky top-0 z-10 bg-white dark:bg-gray-900 py-2 flex items-start justify-between gap-3">
          <div class="min-w-0">
            <p class="flex items-center gap-2 text-xs font-semibold text-indigo-700 dark:text-indigo-300"><History :size="16" aria-hidden="true" /> Evoluția abonamentului</p>
            <h2 id="price-history-title" class="text-xl font-bold mt-2 break-words">Istoric preț</h2>
            <p class="text-sm text-gray-600 dark:text-gray-300 mt-1 break-words">{{ subscription.name }}</p>
          </div>
          <button type="button" @click="emit('close')" aria-label="Închide istoricul" class="p-2 rounded-xl hover:bg-gray-100 dark:hover:bg-gray-800 shrink-0"><X :size="20" /></button>
        </header>
        <p class="mt-4 text-xs text-gray-600 dark:text-gray-300 leading-relaxed">Sunt înregistrate modificările salvate în tracker după activarea istoricului. Acestea nu reprezintă plăți efectuate la furnizor.</p>
        <div class="mt-4 rounded-2xl bg-indigo-50 dark:bg-indigo-500/10 p-4" v-if="data">
          <p class="text-xs font-semibold text-indigo-700 dark:text-indigo-300">Preț curent</p>
          <p class="font-bold mt-1 break-words">{{ price(data.current) }}</p>
        </div>
        <div class="mt-4 flex items-center justify-between gap-3">
          <p class="text-xs text-gray-600 dark:text-gray-300" aria-live="polite">{{ loading ? 'Se încarcă istoricul…' : data ? `${data.total} modificări înregistrate` : '' }}</p>
          <button type="button" @click="load(0)" :disabled="loading" class="inline-flex items-center gap-1.5 text-xs font-semibold text-indigo-700 dark:text-indigo-300 px-2 py-2 disabled:opacity-50" aria-label="Reîncarcă istoricul"><RefreshCw :size="14" aria-hidden="true" /> Reîncarcă</button>
        </div>
        <p v-if="failed" role="alert" class="mt-4 rounded-xl p-3 bg-red-50 dark:bg-red-500/10 text-red-700 dark:text-red-300 text-sm">Istoricul nu a putut fi încărcat. Reîncearcă folosind butonul de reîncărcare.</p>
        <p v-else-if="data && !data.entries.length && !loading" class="mt-4 py-6 text-center text-sm text-gray-600 dark:text-gray-300">Nu există încă modificări de preț înregistrate.</p>
        <ol v-if="data?.entries.length" class="mt-4 space-y-3" :aria-busy="loading">
          <li v-for="entry in data.entries" :key="entry.id" class="border border-gray-200 dark:border-gray-700 rounded-2xl p-4 space-y-2">
            <time :datetime="entry.changedAt" class="text-xs text-gray-600 dark:text-gray-300">{{ date(entry.changedAt) }}</time>
            <div class="flex flex-wrap items-center gap-2 text-sm">
              <span class="text-gray-600 dark:text-gray-300 break-words">{{ price(entry.previous) }}</span>
              <ArrowRight :size="14" aria-label="devine" />
              <span class="font-bold break-words">{{ price(entry.current) }}</span>
            </div>
            <p v-if="entry.monthlyDelta !== null" class="text-xs font-semibold" :class="entry.monthlyDelta > 0 ? 'text-red-700 dark:text-red-300' : entry.monthlyDelta < 0 ? 'text-emerald-700 dark:text-emerald-300' : 'text-gray-600 dark:text-gray-300'">
              {{ signed(entry.monthlyDelta) }} {{ entry.current.currency }} / lună echivalent<span v-if="entry.changePercent !== null"> · {{ signed(entry.changePercent) }}%</span>
            </p>
            <p v-else class="text-xs text-gray-600 dark:text-gray-300">Valută schimbată; variația nu se compară automat.</p>
          </li>
        </ol>
        <nav v-if="data && data.total > take" aria-label="Paginare istoric preț" class="mt-4 flex flex-wrap justify-between gap-2 text-xs font-semibold">
          <button type="button" @click="load(Math.max(0, skip - take))" :disabled="loading || skip === 0" class="rounded-xl px-3 py-2.5 bg-gray-100 dark:bg-gray-800 disabled:opacity-50">Mai noi</button>
          <span class="py-2.5">{{ skip + 1 }}–{{ Math.min(skip + data.entries.length, data.total) }} din {{ data.total }}</span>
          <button type="button" @click="load(skip + take)" :disabled="loading || skip + data.entries.length >= data.total" class="rounded-xl px-3 py-2.5 bg-gray-100 dark:bg-gray-800 disabled:opacity-50">Mai vechi</button>
        </nav>
      </section>
    </div>
  </Teleport>
</template>
