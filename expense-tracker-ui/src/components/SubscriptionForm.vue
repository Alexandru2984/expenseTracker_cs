<script setup>
import { ref, watch } from 'vue'

const props = defineProps({
  editingItem: {
    type: Object,
    default: null
  },
  initialItem: { type: Object, default: null },
  busy: Boolean
})

const emit = defineEmits(['submit', 'cancel'])

const empty = () => ({
  name: '',
  cost: '',
  currency: 'RON',
  billingPeriod: 'Monthly',
  nextBillingDate: new Date().toISOString().slice(0, 10),
  category: '',
  isActive: true,
  details: { notes: '', providerUrl: '', trialEndsOn: null }
})

const form = ref(empty())

watch(
  () => props.editingItem ?? props.initialItem,
  (item) => {
    if (item) {
      form.value = {
        ...item,
        details: { ...empty().details, ...item.details },
        nextBillingDate: item.nextBillingDate.slice(0, 10)
      }
    } else {
      form.value = empty()
    }
  },
  { immediate: true }
)

function handleSubmit() {
  if (props.busy) return
  const payload = {
    ...form.value,
    cost: parseFloat(form.value.cost),
    details: { ...form.value.details, trialEndsOn: form.value.details.trialEndsOn || null },
    nextBillingDate: form.value.nextBillingDate  // DateOnly: YYYY-MM-DD
  }
  emit('submit', payload)
}
</script>

<template>
  <div id="subscription-form" class="bg-white dark:bg-gray-900 rounded-3xl p-5 sm:p-6 border border-gray-100 dark:border-gray-800 shadow-xl shadow-indigo-500/5">
    <h2 class="text-xl font-bold dark:text-white mb-6">
      {{ editingItem ? 'Editează abonament' : 'Abonament nou' }}
    </h2>

    <form @submit.prevent="handleSubmit">
      <fieldset :disabled="busy" class="grid grid-cols-1 sm:grid-cols-2 gap-5">
      <!-- Name -->
      <div class="sm:col-span-2">
        <label for="subscription-name" class="block text-xs font-bold text-gray-500 dark:text-gray-400 uppercase mb-1.5 ml-1">Nume</label>
        <input
          id="subscription-name"
          maxlength="100"
          v-model="form.name"
          required
          placeholder="Netflix, Spotify, VPS..."
          class="w-full bg-gray-50 dark:bg-gray-800 border-none rounded-xl px-4 py-2.5 dark:text-white placeholder-gray-400 outline-none focus:ring-2 ring-indigo-500/20 transition"
        />
      </div>

      <!-- Cost -->
      <div>
        <label for="subscription-cost" class="block text-xs font-bold text-gray-500 dark:text-gray-400 uppercase mb-1.5 ml-1">{{ form.details.trialEndsOn ? 'Cost după probă' : 'Cost' }}</label>
        <input
          id="subscription-cost"
          max="1000000"
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
        <label for="subscription-currency" class="block text-xs font-bold text-gray-500 dark:text-gray-400 uppercase mb-1.5 ml-1">Valută</label>
        <select
          id="subscription-currency"
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
        <label for="subscription-period" class="block text-xs font-bold text-gray-500 dark:text-gray-400 uppercase mb-1.5 ml-1">Frecvență</label>
        <select
          id="subscription-period"
          v-model="form.billingPeriod"
          class="w-full bg-gray-50 dark:bg-gray-800 border-none rounded-xl px-4 py-2.5 dark:text-white outline-none focus:ring-2 ring-indigo-500/20 transition"
        >
          <option value="Monthly">Lunar</option>
          <option value="Yearly">Anual</option>
        </select>
      </div>

      <!-- Next Billing Date -->
      <div>
        <label for="subscription-date" class="block text-xs font-bold text-gray-500 dark:text-gray-400 uppercase mb-1.5 ml-1">Următoarea plată</label>
        <input
          id="subscription-date"
          :min="form.details.trialEndsOn || '1900-01-01'"
          max="9998-12-31"
          v-model="form.nextBillingDate"
          type="date"
          required
          class="w-full bg-gray-50 dark:bg-gray-800 border-none rounded-xl px-4 py-2.5 dark:text-white outline-none focus:ring-2 ring-indigo-500/20 transition"
        />
      </div>

      <!-- Category -->
      <div>
        <label for="subscription-category" class="block text-xs font-bold text-gray-500 dark:text-gray-400 uppercase mb-1.5 ml-1">Categorie</label>
        <input
          id="subscription-category"
          maxlength="100"
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
      <div>
        <label for="subscription-trial" class="block text-xs font-bold text-gray-500 dark:text-gray-400 uppercase mb-1.5 ml-1">Final perioadă de probă (opțional)</label>
        <input id="subscription-trial" v-model="form.details.trialEndsOn" type="date" min="1900-01-01" max="9998-12-31" class="w-full bg-gray-50 dark:bg-gray-800 border-none rounded-xl px-4 py-2.5 dark:text-white outline-none focus:ring-2 ring-indigo-500/20" />
        <p class="text-xs text-gray-600 dark:text-gray-300 mt-2">Alege prima scadență la finalul probei sau mai târziu.</p>
      </div>
      <div>
        <label for="subscription-provider" class="block text-xs font-bold text-gray-500 dark:text-gray-400 uppercase mb-1.5 ml-1">Pagina furnizorului (opțional)</label>
        <input id="subscription-provider" v-model="form.details.providerUrl" type="url" maxlength="2048" placeholder="https://furnizor.example/cont" class="w-full bg-gray-50 dark:bg-gray-800 border-none rounded-xl px-4 py-2.5 dark:text-white placeholder-gray-400 outline-none focus:ring-2 ring-indigo-500/20" />
      </div>
      <div class="sm:col-span-2">
        <label for="subscription-notes" class="block text-xs font-bold text-gray-500 dark:text-gray-400 uppercase mb-1.5 ml-1">Note personale (opțional)</label>
        <textarea id="subscription-notes" v-model="form.details.notes" maxlength="2000" rows="3" placeholder="Plan, condiții de reînnoire, lucruri de verificat…" class="w-full bg-gray-50 dark:bg-gray-800 border-none rounded-xl px-4 py-2.5 dark:text-white placeholder-gray-400 outline-none focus:ring-2 ring-indigo-500/20" />
        <p class="text-xs text-gray-600 dark:text-gray-300 mt-1">{{ form.details.notes?.length ?? 0 }} / 2000 caractere</p>
      </div>
      <div class="sm:col-span-2 flex gap-4 pt-2">
        <button
          type="submit"
          class="flex-1 bg-indigo-600 hover:bg-indigo-700 text-white font-bold py-3 rounded-xl shadow-lg shadow-indigo-500/20 transition-all active:scale-[0.98]"
        >
          {{ busy ? 'Se salvează…' : editingItem ? 'Salvează modificările' : 'Adaugă abonament' }}
        </button>
        <button
          type="button"
          @click="emit('cancel')"
          class="flex-1 bg-gray-100 dark:bg-gray-800 hover:bg-gray-200 dark:hover:bg-gray-700 text-gray-600 dark:text-gray-300 font-bold py-3 rounded-xl transition"
        >
          Anulează
        </button>
      </div>
      </fieldset>
    </form>
  </div>
</template>
