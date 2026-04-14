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
    Niciun abonament. Adaugă primul! 🚀
  </div>

  <div v-else class="grid grid-cols-1 sm:grid-cols-2 xl:grid-cols-3 gap-4">
    <div
      v-for="sub in subscriptions"
      :key="sub.id"
      class="bg-gray-900 border rounded-2xl p-5 flex flex-col gap-3 transition hover:border-indigo-500/50"
      :class="sub.isActive ? 'border-gray-800' : 'border-gray-800 opacity-50'"
    >
      <!-- Header -->
      <div class="flex items-start justify-between gap-2">
        <div>
          <h3 class="font-semibold text-white text-base leading-tight">{{ sub.name }}</h3>
          <span
            v-if="sub.category"
            class="inline-block mt-1 text-xs px-2 py-0.5 rounded-full font-medium"
            :class="categoryColor(sub.category)"
          >
            {{ sub.category }}
          </span>
        </div>
        <span
          class="text-xs px-2 py-0.5 rounded-full font-medium shrink-0"
          :class="sub.isActive ? 'bg-emerald-500/20 text-emerald-400' : 'bg-gray-700 text-gray-400'"
        >
          {{ sub.isActive ? 'Activ' : 'Inactiv' }}
        </span>
      </div>

      <!-- Cost -->
      <div class="flex items-baseline gap-1">
        <span class="text-2xl font-bold text-white">{{ sub.cost.toFixed(2) }}</span>
        <span class="text-sm text-gray-400">{{ sub.currency }}</span>
        <span class="text-xs text-gray-500 ml-1">/ {{ sub.billingPeriod === 'Monthly' ? 'lună' : 'an' }}</span>
      </div>

      <!-- Next billing -->
      <div class="text-xs text-gray-500">
        Următoarea plată: <span class="text-gray-300">{{ formatDate(sub.nextBillingDate) }}</span>
      </div>

      <!-- Actions -->
      <div class="flex gap-2 pt-1 mt-auto">
        <button
          @click="emit('edit', sub)"
          class="flex-1 text-xs bg-gray-800 hover:bg-gray-700 text-gray-300 hover:text-white py-1.5 rounded-lg transition"
        >
          Editează
        </button>
        <button
          @click="emit('delete', sub.id)"
          class="flex-1 text-xs bg-red-500/10 hover:bg-red-500/20 text-red-400 hover:text-red-300 py-1.5 rounded-lg transition"
        >
          Șterge
        </button>
      </div>
    </div>
  </div>
</template>
