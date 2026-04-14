<script setup>
import { ref, watch } from 'vue'

const props = defineProps({
  editingItem: {
    type: Object,
    default: null
  }
})

const emit = defineEmits(['submit', 'cancel'])

const empty = () => ({
  name: '',
  cost: '',
  currency: 'RON',
  billingPeriod: 'Monthly',
  nextBillingDate: new Date().toISOString().slice(0, 10),
  category: '',
  isActive: true
})

const form = ref(empty())

watch(
  () => props.editingItem,
  (item) => {
    if (item) {
      form.value = {
        ...item,
        nextBillingDate: item.nextBillingDate.slice(0, 10)
      }
    } else {
      form.value = empty()
    }
  },
  { immediate: true }
)

function handleSubmit() {
  const payload = {
    ...form.value,
    cost: parseFloat(form.value.cost),
    nextBillingDate: form.value.nextBillingDate  // DateOnly: YYYY-MM-DD
  }
  emit('submit', payload)
  form.value = empty()
}
</script>

<template>
  <div class="bg-gray-900 rounded-2xl p-6 border border-gray-800">
    <h2 class="text-lg font-semibold text-white mb-5">
      {{ editingItem ? 'Editează abonament' : 'Abonament nou' }}
    </h2>

    <form @submit.prevent="handleSubmit" class="grid grid-cols-1 sm:grid-cols-2 gap-4">
      <!-- Name -->
      <div class="sm:col-span-2">
        <label class="block text-xs text-gray-400 mb-1">Nume</label>
        <input
          v-model="form.name"
          required
          placeholder="Netflix, Spotify, VPS..."
          class="w-full bg-gray-800 border border-gray-700 rounded-lg px-3 py-2 text-white placeholder-gray-500 focus:outline-none focus:border-indigo-500 transition"
        />
      </div>

      <!-- Cost -->
      <div>
        <label class="block text-xs text-gray-400 mb-1">Cost</label>
        <input
          v-model="form.cost"
          type="number"
          step="0.01"
          min="0"
          required
          placeholder="0.00"
          class="w-full bg-gray-800 border border-gray-700 rounded-lg px-3 py-2 text-white placeholder-gray-500 focus:outline-none focus:border-indigo-500 transition"
        />
      </div>

      <!-- Currency -->
      <div>
        <label class="block text-xs text-gray-400 mb-1">Valută</label>
        <select
          v-model="form.currency"
          class="w-full bg-gray-800 border border-gray-700 rounded-lg px-3 py-2 text-white focus:outline-none focus:border-indigo-500 transition"
        >
          <option>RON</option>
          <option>EUR</option>
          <option>USD</option>
          <option>GBP</option>
        </select>
      </div>

      <!-- Billing Period -->
      <div>
        <label class="block text-xs text-gray-400 mb-1">Frecvență</label>
        <select
          v-model="form.billingPeriod"
          class="w-full bg-gray-800 border border-gray-700 rounded-lg px-3 py-2 text-white focus:outline-none focus:border-indigo-500 transition"
        >
          <option value="Monthly">Lunar</option>
          <option value="Yearly">Anual</option>
        </select>
      </div>

      <!-- Next Billing Date -->
      <div>
        <label class="block text-xs text-gray-400 mb-1">Următoarea plată</label>
        <input
          v-model="form.nextBillingDate"
          type="date"
          required
          class="w-full bg-gray-800 border border-gray-700 rounded-lg px-3 py-2 text-white focus:outline-none focus:border-indigo-500 transition"
        />
      </div>

      <!-- Category -->
      <div>
        <label class="block text-xs text-gray-400 mb-1">Categorie</label>
        <input
          v-model="form.category"
          placeholder="Streaming, Hosting, SaaS..."
          class="w-full bg-gray-800 border border-gray-700 rounded-lg px-3 py-2 text-white placeholder-gray-500 focus:outline-none focus:border-indigo-500 transition"
        />
      </div>

      <!-- IsActive -->
      <div class="flex items-center gap-3 sm:col-span-2">
        <input
          id="isActive"
          v-model="form.isActive"
          type="checkbox"
          class="w-4 h-4 accent-indigo-500"
        />
        <label for="isActive" class="text-sm text-gray-300">Activ</label>
      </div>

      <!-- Buttons -->
      <div class="sm:col-span-2 flex gap-3 pt-1">
        <button
          type="submit"
          class="flex-1 bg-indigo-600 hover:bg-indigo-500 text-white font-medium py-2 rounded-lg transition"
        >
          {{ editingItem ? 'Salvează' : 'Adaugă' }}
        </button>
        <button
          v-if="editingItem"
          type="button"
          @click="emit('cancel')"
          class="flex-1 bg-gray-700 hover:bg-gray-600 text-white font-medium py-2 rounded-lg transition"
        >
          Anulează
        </button>
      </div>
    </form>
  </div>
</template>
