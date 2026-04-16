<script setup>
defineProps({
  subscriptions: {
    type: Array,
    required: true
  }
})

const emit = defineEmits(['edit', 'delete'])

const CATEGORY_COLORS = {
  Streaming: 'bg-pink-500/20 text-pink-300',
  Hosting: 'bg-blue-500/20 text-blue-300',
  Infrastructure: 'bg-cyan-500/20 text-cyan-300',
  SaaS: 'bg-violet-500/20 text-violet-300',
  Entertainment: 'bg-orange-500/20 text-orange-300',
  Music: 'bg-green-500/20 text-green-300',
  Gaming: 'bg-yellow-500/20 text-yellow-300'
}

function categoryColor(cat) {
  return CATEGORY_COLORS[cat] ?? 'bg-gray-700/50 text-gray-300'
}

function formatDate(dateStr) {
  // dateStr is YYYY-MM-DD (DateOnly from API)
  const [year, month, day] = dateStr.split('-').map(Number)
  return new Date(year, month - 1, day).toLocaleDateString('ro-RO', {
    day: '2-digit', month: 'short', year: 'numeric'
  })
}
</script>

<template>
  <div v-if="subscriptions.length === 0" class="text-center text-gray-500 py-16">
    Niciun abonament găsit. 🚀
  </div>

  <div v-else class="grid grid-cols-1 sm:grid-cols-2 xl:grid-cols-3 gap-4 p-4">
    <div
      v-for="sub in subscriptions"
      :key="sub.id"
      class="bg-white dark:bg-gray-900 border rounded-2xl p-5 flex flex-col gap-3 transition hover:border-indigo-500/50 shadow-sm"
      :class="sub.isActive ? 'border-gray-100 dark:border-gray-800' : 'border-gray-100 dark:border-gray-800 opacity-60'"
    >
      <!-- Header -->
      <div class="flex items-start justify-between gap-2">
        <div>
          <h3 class="font-bold text-gray-900 dark:text-white text-base leading-tight">{{ sub.name }}</h3>
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
          {{ sub.isActive ? 'Activ' : 'Inactiv' }}
        </span>
      </div>

      <!-- Cost -->
      <div class="flex items-baseline gap-1">
        <span class="text-2xl font-black text-gray-900 dark:text-white">{{ sub.cost.toFixed(2) }}</span>
        <span class="text-xs font-bold text-gray-400">{{ sub.currency }}</span>
        <span class="text-[10px] font-bold text-gray-400 uppercase ml-1">/ {{ sub.billingPeriod === 'Monthly' ? 'lună' : 'an' }}</span>
      </div>

      <!-- Next billing -->
      <div class="text-[11px] text-gray-400 font-medium">
        Următoarea plată: <span class="text-gray-700 dark:text-gray-300">{{ formatDate(sub.nextBillingDate) }}</span>
      </div>

      <!-- Actions -->
      <div class="flex gap-2 pt-1 mt-auto">
        <button
          @click="emit('edit', sub)"
          class="flex-1 text-xs font-bold bg-gray-50 dark:bg-gray-800 hover:bg-gray-100 dark:hover:bg-gray-700 text-gray-600 dark:text-gray-300 hover:text-gray-900 dark:hover:text-white py-2 rounded-xl transition"
        >
          Editează
        </button>
        <button
          @click="emit('delete', sub.id)"
          class="flex-1 text-xs font-bold bg-red-500/5 dark:bg-red-500/10 hover:bg-red-500/10 dark:hover:bg-red-500/20 text-red-500 py-2 rounded-xl transition"
        >
          Șterge
        </button>
      </div>
    </div>
  </div>
</template>
