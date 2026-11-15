<script setup>
defineProps({
  subscriptions: {
    type: Array,
    required: true
  },
  pendingIds: { type: Set, default: () => new Set() }
})

const emit = defineEmits(['edit', 'delete', 'status', 'duplicate', 'history'])

const CATEGORY_COLORS = {
  Streaming: 'bg-pink-500/15 text-pink-600 dark:text-pink-300',
  Hosting: 'bg-blue-500/15 text-blue-600 dark:text-blue-300',
  Infrastructure: 'bg-cyan-500/15 text-cyan-600 dark:text-cyan-300',
  SaaS: 'bg-violet-500/15 text-violet-600 dark:text-violet-300',
  Entertainment: 'bg-orange-500/15 text-orange-600 dark:text-orange-300',
  Music: 'bg-green-500/15 text-green-600 dark:text-green-300',
  Gaming: 'bg-yellow-500/15 text-yellow-700 dark:text-yellow-300'
}

function categoryColor(cat) {
  return CATEGORY_COLORS[cat] ?? 'bg-gray-500/15 text-gray-600 dark:text-gray-300'
}

function formatDate(dateStr) {
  // dateStr is YYYY-MM-DD (DateOnly from API)
  const [year, month, day] = dateStr.split('-').map(Number)
  return new Date(year, month - 1, day).toLocaleDateString('ro-RO', {
    day: '2-digit', month: 'short', year: 'numeric'
  })
}

// Highlight subscriptions whose next payment is overdue or within a week.
function dueInfo(sub) {
  if (!sub.isActive || !sub.nextBillingDate) return null
  const [y, m, d] = sub.nextBillingDate.split('-').map(Number)
  const due = new Date(y, m - 1, d)
  const today = new Date()
  today.setHours(0, 0, 0, 0)
  const days = Math.round((due - today) / 86400000)
  if (days < 0) return { label: 'Restant', cls: 'bg-red-500/15 text-red-500' }
  if (days === 0) return { label: 'Scadent azi', cls: 'bg-amber-500/15 text-amber-600 dark:text-amber-400' }
  if (days <= 7) return { label: `În ${days} ${days === 1 ? 'zi' : 'zile'}`, cls: 'bg-amber-500/15 text-amber-600 dark:text-amber-400' }
  return null
}
</script>

<template>
  <div v-if="subscriptions.length === 0" class="flex flex-col items-center justify-center text-center py-16 px-4">
    <div class="w-14 h-14 rounded-2xl bg-indigo-500/10 flex items-center justify-center text-2xl mb-3">📭</div>
    <p class="font-bold text-gray-600 dark:text-gray-300">Niciun abonament găsit</p>
    <p class="text-sm text-gray-400 mt-1">Adaugă primul abonament cu butonul „+ Nou" sau ajustează filtrele.</p>
  </div>

  <div v-else class="grid grid-cols-1 sm:grid-cols-2 xl:grid-cols-3 gap-4 p-4">
    <article
      v-for="sub in subscriptions"
      :key="sub.id"
      class="bg-white dark:bg-gray-900 border rounded-2xl p-5 flex flex-col gap-3 transition hover:border-indigo-500/50 shadow-sm"
      :class="sub.isActive ? 'border-gray-100 dark:border-gray-800' : 'border-dashed border-gray-300 dark:border-gray-600'"
    >
      <!-- Header -->
      <div class="flex items-start justify-between gap-2">
        <div class="min-w-0">
          <h3 class="font-bold text-gray-900 dark:text-white text-base leading-tight break-words">{{ sub.name }}</h3>
          <span
            v-if="sub.category"
            class="inline-block mt-1 text-[10px] uppercase tracking-wider px-2 py-0.5 rounded-md font-bold"
            :class="categoryColor(sub.category)"
          >
            {{ sub.category }}
          </span>
        </div>
        <span
          class="text-[10px] uppercase tracking-wider px-2 py-0.5 rounded-md font-bold shrink-0"
          :class="sub.isActive ? 'bg-emerald-500/10 text-emerald-600 dark:text-emerald-400' : 'bg-gray-100 dark:bg-gray-800 text-gray-500'"
        >
          {{ sub.isActive ? 'Activ' : 'Suspendat' }}
        </span>
      </div>

      <!-- Cost -->
      <div class="flex flex-wrap items-baseline gap-1">
        <span class="text-2xl font-black text-gray-900 dark:text-white break-all">{{ sub.cost.toFixed(2) }}</span>
        <span class="text-xs font-bold text-gray-400">{{ sub.currency }}</span>
        <span class="text-[10px] font-bold text-gray-400 uppercase ml-1">/ {{ sub.billingPeriod === 'Monthly' ? 'lună' : 'an' }}</span>
      </div>

      <!-- Next billing -->
      <div class="flex flex-wrap items-center justify-between gap-2">
        <div class="text-[11px] text-gray-400 font-medium">
          Următoarea plată: <span class="text-gray-700 dark:text-gray-300">{{ formatDate(sub.nextBillingDate) }}</span>
        </div>
        <span v-if="dueInfo(sub)" :class="dueInfo(sub).cls" class="text-[10px] font-bold px-2 py-0.5 rounded-md shrink-0 whitespace-nowrap">
          {{ dueInfo(sub).label }}
        </span>
      </div>

      <!-- Actions -->
      <button type="button" @click="emit('history', sub)" :aria-label="`Istoric preț ${sub.name}`" class="text-left text-xs font-semibold text-indigo-700 dark:text-indigo-300 py-2 hover:underline">Istoric preț →</button>
      <div class="flex flex-wrap gap-2">
        <button type="button" :disabled="pendingIds.has(sub.id)" @click="emit('status', sub)" :aria-label="`${sub.isActive ? 'Suspendă' : 'Reactivează'} ${sub.name}`" class="flex-1 text-xs font-semibold rounded-xl px-3 py-2.5 text-indigo-700 dark:text-indigo-300 bg-indigo-50 dark:bg-indigo-500/10 disabled:opacity-50">{{ sub.isActive ? 'Suspendă' : 'Reactivează' }}</button>
        <button type="button" :disabled="pendingIds.has(sub.id)" @click="emit('duplicate', sub)" :aria-label="`Duplică ${sub.name}`" class="flex-1 text-xs font-semibold rounded-xl px-3 py-2.5 text-gray-600 dark:text-gray-300 bg-gray-50 dark:bg-gray-800 disabled:opacity-50">Duplică</button>
      </div>
      <div class="flex gap-2 pt-1 mt-auto">
        <button
          @click="emit('edit', sub)"
          :disabled="pendingIds.has(sub.id)"
          :aria-label="`Editează ${sub.name}`"
          class="flex-1 text-xs font-bold bg-gray-50 dark:bg-gray-800 hover:bg-gray-100 dark:hover:bg-gray-700 text-gray-600 dark:text-gray-300 hover:text-gray-900 dark:hover:text-white py-2 rounded-xl transition"
        >
          Editează
        </button>
        <button
          @click="emit('delete', sub.id)"
          :disabled="pendingIds.has(sub.id)"
          :aria-label="`Șterge ${sub.name}`"
          class="flex-1 text-xs font-bold bg-red-500/5 dark:bg-red-500/10 hover:bg-red-500/10 dark:hover:bg-red-500/20 text-red-500 py-2 rounded-xl transition"
        >
          Șterge
        </button>
      </div>
    </article>
  </div>
</template>
