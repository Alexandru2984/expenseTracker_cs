<script setup>
import { ref, computed, watch, onUnmounted } from 'vue'
import { CopyCheck, RefreshCw, ChevronDown } from 'lucide-vue-next'
import { subscriptionsApi } from '../api.js'

const props = defineProps({ refreshKey: { default: null }, reviewing: Boolean })
const emit = defineEmits(['review'])
const data = ref(null), loading = ref(false), failed = ref(false), includeInactive = ref(false), expanded = ref(false), visible = ref(10)
let controller, epoch = 0
const groups = computed(() => data.value?.groups ?? [])
async function load() {
  const request = ++epoch
  controller?.abort(); controller = new AbortController()
  loading.value = true; failed.value = false
  try {
    const response = await subscriptionsApi.duplicates({ includeInactive: includeInactive.value }, controller.signal)
    if (request === epoch) { data.value = response.data; visible.value = 10 }
  } catch (error) { if (request === epoch && error.code !== 'ERR_CANCELED') failed.value = true }
  finally { if (request === epoch) loading.value = false }
}
watch([() => props.refreshKey, includeInactive], load, { immediate: true })
onUnmounted(() => { epoch++; controller?.abort() })
const money = value => Number(value).toLocaleString('ro-RO', { minimumFractionDigits: 2, maximumFractionDigits: 2 })
</script>

<template>
  <section aria-labelledby="duplicates-title" class="bg-white dark:bg-gray-900 border border-gray-200 dark:border-gray-800 rounded-3xl p-5 sm:p-6 text-gray-900 dark:text-gray-100 space-y-4">
    <div class="flex items-start justify-between gap-3">
      <div class="min-w-0">
        <h2 id="duplicates-title" class="font-bold flex items-center gap-2"><CopyCheck :size="18" class="text-indigo-600 dark:text-indigo-300 shrink-0" aria-hidden="true" /> Verifică suprapunerile</h2>
        <p class="text-xs text-gray-600 dark:text-gray-300 mt-2">Posibile duplicate după nume și valută, pe întregul cont. Revizuiește planurile înainte de a le modifica.</p>
      </div>
      <button type="button" @click="load" :disabled="loading" aria-label="Reîncarcă suprapunerile" class="p-2 rounded-xl text-indigo-700 dark:text-indigo-300 hover:bg-indigo-50 dark:hover:bg-indigo-500/10 disabled:opacity-50 shrink-0"><RefreshCw :size="17" /></button>
    </div>
    <p v-if="failed" role="alert" class="text-sm text-red-700 dark:text-red-300">Suprapunerile nu au putut fi verificate. Reîncearcă folosind butonul de reîncărcare.</p>
    <p v-else-if="loading" role="status" class="text-sm text-gray-600 dark:text-gray-300">Se verifică abonamentele…</p>
    <p v-else-if="data && !groups.length" class="text-sm text-gray-600 dark:text-gray-300">Nu am găsit suprapuneri după nume și valută.</p>
    <button v-else-if="data" type="button" @click="expanded = !expanded" :aria-expanded="expanded" aria-controls="duplicate-groups" class="flex items-center gap-2 text-sm font-semibold text-indigo-700 dark:text-indigo-300 py-2"><ChevronDown :size="16" aria-hidden="true" /> {{ expanded ? 'Ascunde' : 'Arată' }} {{ groups.length }} {{ groups.length === 1 ? 'grup posibil duplicat' : 'grupuri posibil duplicate' }}</button>
    <label class="flex items-center gap-2 text-xs text-gray-600 dark:text-gray-300"><input type="checkbox" v-model="includeInactive" class="accent-indigo-600 w-4 h-4" /> Include și abonamentele suspendate</label>
    <div v-show="expanded && !loading && !failed && groups.length" id="duplicate-groups" class="grid grid-cols-1 lg:grid-cols-2 gap-3">
      <article v-for="(group, index) in groups.slice(0, visible)" :key="`${group.name}-${group.currency}`" :aria-labelledby="`duplicate-group-${index}`" class="min-w-0 border border-gray-200 dark:border-gray-700 rounded-2xl p-4 space-y-3">
        <h3 :id="`duplicate-group-${index}`" class="font-bold text-sm break-words">{{ group.name }} · {{ group.currency }}</h3>
        <p class="text-xs text-gray-600 dark:text-gray-300">{{ group.items.length }} abonamente · {{ money(group.activeMonthlyTotal) }} {{ group.currency }} / lună planificat activ</p>
        <ul class="space-y-3">
          <li v-for="item in group.items" :key="item.id" class="flex flex-wrap items-center justify-between gap-2 text-xs">
            <div class="min-w-0"><p class="font-semibold break-words">{{ item.name }}</p><p class="text-gray-600 dark:text-gray-300 mt-1">{{ money(item.cost) }} {{ item.currency }} / {{ item.billingPeriod === 'Yearly' ? 'an' : 'lună' }} · {{ item.isActive ? 'Activ' : 'Suspendat' }}</p></div>
            <button type="button" @click="emit('review', item.id)" :disabled="reviewing" :aria-label="`Revizuiește ${item.name} ${money(item.cost)} ${item.currency}`" class="rounded-xl px-3 py-2 text-indigo-700 dark:text-indigo-300 bg-indigo-50 dark:bg-indigo-500/10 font-semibold disabled:opacity-50">Revizuiește</button>
          </li>
        </ul>
      </article>
    </div>
    <button v-if="expanded && !loading && !failed && groups.length > visible" type="button" @click="visible += 10" class="text-sm font-semibold text-indigo-700 dark:text-indigo-300 py-2">Arată mai multe grupuri</button>
  </section>
</template>
