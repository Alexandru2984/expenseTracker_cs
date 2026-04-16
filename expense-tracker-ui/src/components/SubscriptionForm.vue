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
  <div class="bg-white dark:bg-gray-900 rounded-3xl p-6 border border-gray-100 dark:border-gray-800 shadow-xl shadow-indigo-500/5">
    <h2 class="text-xl font-bold dark:text-white mb-6">
      {{ editingItem ? 'Editează abonament' : 'Abonament nou' }}
    </h2>

    <form @submit.prevent="handleSubmit" class="grid grid-cols-1 sm:grid-cols-2 gap-5">
      <!-- Name -->
      <div class="sm:col-span-2">
        <label class="block text-xs font-bold text-gray-400 uppercase mb-1.5 ml-1">Nume</label>
        <input
          v-model="form.name"
          required
          placeholder="Netflix, Spotify, VPS..."
          class="w-full bg-gray-50 dark:bg-gray-800 border-none rounded-xl px-4 py-2.5 dark:text-white placeholder-gray-400 outline-none focus:ring-2 ring-indigo-500/20 transition"
        />
      </div>

      <!-- Cost -->
      <div>
        <label class="block text-xs font-bold text-gray-400 uppercase mb-1.5 ml-1">Cost</label>
        <input
          v-model="form.cost"
          type="number"
          step="0.01"
          min="0"
          required
          placeholder="0.00"
          class="w-full bg-gray-50 dark:bg-gray-800 border-none rounded-xl px-4 py-2.5 dark:text-white placeholder-gray-400 outline-none focus:ring-2 ring-indigo-500/20 transition"
        />
      </div>

      <!-- Currency -->
      <div>
        <label class="block text-xs font-bold text-gray-400 uppercase mb-1.5 ml-1">Valută</label>
        <select
          v-model="form.currency"
          class="w-full bg-gray-50 dark:bg-gray-800 border-none rounded-xl px-4 py-2.5 dark:text-white outline-none focus:ring-2 ring-indigo-500/20 transition"
        >
          <option>RON</option>
          <option>EUR</option>
          <option>USD</option>
          <option>GBP</option>
          <option>CHF</option>
        </select>
      </div>

      <!-- Billing Period -->
      <div>
        <label class="block text-xs font-bold text-gray-400 uppercase mb-1.5 ml-1">Frecvență</label>
        <select
          v-model="form.billingPeriod"
          class="w-full bg-gray-50 dark:bg-gray-800 border-none rounded-xl px-4 py-2.5 dark:text-white outline-none focus:ring-2 ring-indigo-500/20 transition"
        >
          <option value="Monthly">Lunar</option>
          <option value="Yearly">Anual</option>
        </select>
      </div>

      <!-- Next Billing Date -->
      <div>
        <label class="block text-xs font-bold text-gray-400 uppercase mb-1.5 ml-1">Următoarea plată</label>
        <input
          v-model="form.nextBillingDate"
          type="date"
          required
          class="w-full bg-gray-50 dark:bg-gray-800 border-none rounded-xl px-4 py-2.5 dark:text-white outline-none focus:ring-2 ring-indigo-500/20 transition"
        />
      </div>

      <!-- Category -->
      <div>
        <label class="block text-xs font-bold text-gray-400 uppercase mb-1.5 ml-1">Categorie</label>
        <input
          v-model="form.category"
          placeholder="Streaming, SaaS..."
          class="w-full bg-gray-50 dark:bg-gray-800 border-none rounded-xl px-4 py-2.5 dark:text-white placeholder-gray-400 outline-none focus:ring-2 ring-indigo-500/20 transition"
        />
      </div>

      <!-- IsActive -->
      <div class="flex items-center gap-3 sm:col-span-2">
        <input
          id="isActive"
          v-model="form.isActive"
          type="checkbox"
          class="w-5 h-5 rounded-lg border-none bg-gray-50 dark:bg-gray-800 text-indigo-600 focus:ring-offset-0 focus:ring-2 ring-indigo-500/20 transition"
        />
        <label for="isActive" class="text-sm font-bold text-gray-500 dark:text-gray-400 uppercase tracking-wider">Activ</label>
      </div>

      <!-- Buttons -->
      <div class="sm:col-span-2 flex gap-4 pt-2">
        <button
          type="submit"
          class="flex-1 bg-indigo-600 hover:bg-indigo-500 text-white font-bold py-3 rounded-xl shadow-lg shadow-indigo-500/20 transition-all active:scale-[0.98]"
        >
          {{ editingItem ? 'Salvează modificările' : 'Adaugă abonament' }}
        </button>
        <button
          v-if="editingItem"
          type="button"
          @click="emit('cancel')"
          class="flex-1 bg-gray-100 dark:bg-gray-800 hover:bg-gray-200 dark:hover:bg-gray-700 text-gray-600 dark:text-gray-300 font-bold py-3 rounded-xl transition"
        >
          Anulează
        </button>
      </div>
    </form>
  </div>
</template>
