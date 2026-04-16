<script setup>
import { ref, computed, onMounted, onUnmounted, watch } from 'vue'
import { subscriptionsApi } from './api.js'
import SubscriptionForm from './components/SubscriptionForm.vue'
import SubscriptionList from './components/SubscriptionList.vue'
import LoginView from './components/LoginView.vue'
import ConfirmModal from './components/ConfirmModal.vue'
import { Sun, Moon, Search, Download, BarChart3, ListFilter, ChevronLeft, ChevronRight } from 'lucide-vue-next'
import { Chart as ChartJS, ArcElement, Tooltip, Legend } from 'chart.js'
import { Doughnut } from 'vue-chartjs'

ChartJS.register(ArcElement, Tooltip, Legend)

// ── Theme ─────────────────────────────────────────────────────────────────────
const isDark = ref(localStorage.getItem('theme') !== 'light')
function toggleTheme() {
  isDark.value = !isDark.value
  localStorage.setItem('theme', isDark.value ? 'dark' : 'light')
}

// ── Currency Conversion ───────────────────────────────────────────────────────
const targetCurrency = ref('RON')
const availableCurrencies = ['RON', 'EUR', 'USD', 'GBP', 'CHF']
const ratesToRon = ref({
  RON: 1,
  EUR: 0.201,
  USD: 0.215,
  GBP: 0.171,
  CHF: 0.196
})

async function fetchRates() {
  try {
    const res = await subscriptionsApi.getRates()
    ratesToRon.value = res.data
  } catch (e) {
    console.error('Failed to fetch live rates, using fallback.')
  }
}

const grandTotalMonthly = computed(() => {
  if (!summary.value?.byCurrency) return 0
  let totalRon = 0
  for (const c of summary.value.byCurrency) {
    const rate = ratesToRon.value[c.currency.toUpperCase()] || 1
    // API returns 1 RON = X Target. So to get RON: Amount / X
    totalRon += c.monthlyTotal / rate
  }
  // To get targetCurrency: totalRon * targetRate
  return totalRon * (ratesToRon.value[targetCurrency.value] || 1)
})

const grandTotalYearly = computed(() => {
  if (!summary.value?.byCurrency) return 0
  let totalRon = 0
  for (const c of summary.value.byCurrency) {
    const rate = ratesToRon.value[c.currency.toUpperCase()] || 1
    totalRon += c.yearlyTotal / rate
  }
  return totalRon * (ratesToRon.value[targetCurrency.value] || 1)
})

// ── Charts ────────────────────────────────────────────────────────────────────
const chartData = computed(() => {
  if (!subscriptions.value.length) return { labels: [], datasets: [] }
  
  const categories = {}
  subscriptions.value.filter(s => s.isActive).forEach(s => {
    const cat = s.category || 'Altele'
    const rate = ratesToRon.value[s.currency.toUpperCase()] || 1
    const costInRon = s.billingPeriod === 'Yearly' ? (s.cost / 12) / rate : s.cost / rate
    categories[cat] = (categories[cat] || 0) + costInRon
  })

  return {
    labels: Object.keys(categories),
    datasets: [{
      backgroundColor: ['#6366f1', '#10b981', '#f59e0b', '#ef4444', '#ec4899', '#8b5cf6', '#06b6d4'],
      data: Object.values(categories).map(v => v * (ratesToRon.value[targetCurrency.value] || 1))
    }]
  }
})

const chartOptions = {
  responsive: true,
  maintainAspectRatio: false,
  plugins: {
    legend: { position: 'bottom', labels: { color: isDark.value ? '#9ca3af' : '#4b5563' } }
  }
}

// ── Auth ──────────────────────────────────────────────────────────────────────
const isAuthenticated = ref(!!localStorage.getItem('jwt_token'))
const currentUser = ref(localStorage.getItem('username') ?? '')

function handleLogin(authData) {
  isAuthenticated.value = true
  currentUser.value = authData.username
  fetchAll()
  fetchRates()
}

function handleLogout() {
  localStorage.removeItem('jwt_token')
  localStorage.removeItem('username')
  isAuthenticated.value = false
  currentUser.value = ''
}

// ── Filters & Pagination ──────────────────────────────────────────────────────
const subscriptions = ref([])
const summary = ref(null)
const totalItems = ref(0)
const loading = ref(false)
const error = ref(null)

const filters = ref({
  search: '',
  sortBy: 'name',
  sortDesc: false,
  skip: 0,
  take: 10
})

const currentPage = computed(() => Math.floor(filters.value.skip / filters.value.take) + 1)
const totalPages = computed(() => Math.ceil(totalItems.value / filters.value.take))

watch([() => filters.value.search, () => filters.value.sortBy, () => filters.value.sortDesc], () => {
  filters.value.skip = 0
  fetchAll()
})

// ── API calls ─────────────────────────────────────────────────────────────────
async function fetchAll() {
  if (!isAuthenticated.value) return
  loading.value = true
  try {
    const [subRes, sumRes] = await Promise.all([
      subscriptionsApi.getAll(filters.value),
      subscriptionsApi.getSummary()
    ])
    subscriptions.value = subRes.data.items
    totalItems.value = subRes.data.total
    summary.value = sumRes.data
  } catch (e) {
    if (e.response?.status !== 401) {
      error.value = 'Eroare la contactarea API.'
    }
  } finally {
    loading.value = false
  }
}

async function handleExport() {
  try {
    const res = await subscriptionsApi.exportCsv()
    const url = window.URL.createObjectURL(new Blob([res.data]))
    const link = document.createElement('a')
    link.href = url
    link.setAttribute('download', `abonamente_${new Date().toISOString().slice(0, 10)}.csv`)
    document.body.appendChild(link)
    link.click()
    document.body.removeChild(link)
  } catch (e) {
    showToast('Eroare la export.')
  }
}

// (rest of existing logic: handleSubmit, handleDelete, handleEdit, handleCancel, toasts, modals...)
const editingItem = ref(null)
const showForm = ref(false)
const toasts = ref([])

function showToast(message, type = 'error') {
  const id = Date.now()
  toasts.value.push({ id, message, type })
  setTimeout(() => { toasts.value = toasts.value.filter(t => t.id !== id) }, 4000)
}

const confirmModal = ref({ visible: false, message: '', resolve: null })
function askConfirm(message) { return new Promise(resolve => { confirmModal.value = { visible: true, message, resolve } }) }
function onConfirm() { confirmModal.value.resolve(true); confirmModal.value.visible = false }
function onCancel() { confirmModal.value.resolve(false); confirmModal.value.visible = false }

async function handleSubmit(payload) {
  try {
    if (editingItem.value) await subscriptionsApi.update(editingItem.value.id, payload)
    else await subscriptionsApi.create(payload)
    editingItem.value = null
    showForm.value = false
    await fetchAll()
  } catch (e) { showToast('Eroare la salvare.') }
}

async function handleDelete(id) {
  const confirmed = await askConfirm('Sigur ștergi?')
  if (!confirmed) return
  try { await subscriptionsApi.remove(id); await fetchAll() }
  catch (e) { showToast('Eroare la ștergere.') }
}

function handleEdit(item) {
  editingItem.value = item
  showForm.value = true
  window.scrollTo({ top: 0, behavior: 'smooth' })
}

function handleCancel() { editingItem.value = null; showForm.value = false }

onMounted(() => {
  if (isAuthenticated.value) { fetchAll(); fetchRates() }
})
</script>

<template>
  <div :class="{ 'dark': isDark }">
    <div class="min-h-screen bg-gray-50 dark:bg-gray-950 text-gray-900 dark:text-gray-100 transition-colors duration-300">
      <LoginView v-if="!isAuthenticated" @login="handleLogin" />

      <template v-else>
        <!-- Confirm modal -->
        <ConfirmModal v-if="confirmModal.visible" :message="confirmModal.message" @confirm="onConfirm" @cancel="onCancel" />

        <!-- Toasts -->
        <div class="fixed bottom-4 right-4 z-40 flex flex-col gap-2 max-w-sm">
          <TransitionGroup name="toast">
            <div v-for="t in toasts" :key="t.id" class="bg-red-500 text-white text-sm px-4 py-3 rounded-xl shadow-lg">
              {{ t.message }}
            </div>
          </TransitionGroup>
        </div>

        <!-- Header -->
        <header class="border-b border-gray-200 dark:border-gray-800 bg-white/80 dark:bg-gray-950/80 backdrop-blur sticky top-0 z-10">
          <div class="max-w-6xl mx-auto px-4 py-4 flex items-center justify-between">
            <div class="flex items-center gap-3">
              <div class="w-8 h-8 bg-indigo-600 rounded-lg flex items-center justify-center text-sm font-bold text-white">$</div>
              <span class="font-bold">Expense Tracker</span>
            </div>
            <div class="flex items-center gap-3">
              <button @click="toggleTheme" class="p-2 rounded-lg hover:bg-gray-100 dark:hover:bg-gray-800 transition">
                <Sun v-if="isDark" :size="20" />
                <Moon v-else :size="20" />
              </button>
              <button @click="showForm = !showForm; editingItem = null" class="bg-indigo-600 hover:bg-indigo-500 text-white text-sm px-4 py-2 rounded-lg transition">
                {{ showForm && !editingItem ? '✕ Închide' : '+ Nou' }}
              </button>
              <button @click="handleLogout" class="text-xs text-gray-500 hover:text-gray-700 dark:hover:text-gray-300">Ieșire</button>
            </div>
          </div>
        </header>

        <main class="max-w-6xl mx-auto px-4 py-8 space-y-6">
          <div v-if="error" class="bg-red-500/10 border border-red-500/30 text-red-500 rounded-xl p-4 text-sm">{{ error }}</div>

          <!-- Top Section: Totals & Chart -->
          <div class="grid grid-cols-1 lg:grid-cols-3 gap-6">
            <div class="lg:col-span-2 space-y-6">
              <!-- Summary Grid -->
              <div v-if="summary" class="grid grid-cols-1 md:grid-cols-2 gap-4">
                <div v-for="c in summary.byCurrency" :key="c.currency" class="bg-white dark:bg-gray-900 border border-gray-200 dark:border-gray-800 rounded-2xl p-4 shadow-sm">
                  <p class="text-xs text-gray-500 uppercase tracking-widest font-bold mb-1">{{ c.currency }}</p>
                  <div class="flex items-baseline gap-1">
                    <span class="text-2xl font-bold text-indigo-600 dark:text-indigo-400">{{ c.monthlyTotal.toFixed(2) }}</span>
                    <span class="text-xs text-gray-400">/ lună</span>
                  </div>
                </div>
              </div>

              <!-- Grand Total & Converter -->
              <div class="bg-white dark:bg-gray-900 border border-gray-200 dark:border-gray-800 rounded-2xl p-6 shadow-sm">
                <div class="flex flex-col md:flex-row justify-between items-start md:items-center gap-4">
                  <div>
                    <h3 class="text-xs font-bold text-gray-400 uppercase tracking-widest mb-1">Total Estimat (Toate Valutele)</h3>
                    <div class="flex items-baseline gap-2">
                      <span class="text-4xl font-black text-emerald-500">{{ grandTotalMonthly.toFixed(2) }}</span>
                      <span class="text-lg font-bold text-gray-400">{{ targetCurrency }} / lună</span>
                    </div>
                    <p class="text-sm text-gray-400 mt-1">≈ {{ grandTotalYearly.toFixed(2) }} {{ targetCurrency }} / an</p>
                  </div>
                  <div class="w-full md:w-auto">
                    <label class="block text-xs font-bold text-gray-400 uppercase mb-2">Valută Conversie</label>
                    <select v-model="targetCurrency" class="w-full md:w-32 bg-gray-50 dark:bg-gray-800 border border-gray-200 dark:border-gray-700 rounded-xl px-3 py-2 text-sm outline-none focus:ring-2 ring-indigo-500/20 transition">
                      <option v-for="c in availableCurrencies" :key="c" :value="c">{{ c }}</option>
                    </select>
                  </div>
                </div>
              </div>
            </div>

            <!-- Chart -->
            <div class="bg-white dark:bg-gray-900 border border-gray-200 dark:border-gray-800 rounded-2xl p-6 shadow-sm flex flex-col">
              <h3 class="text-xs font-bold text-gray-400 uppercase tracking-widest mb-4 flex items-center gap-2">
                <BarChart3 :size="14" /> Analiză Categorii
              </h3>
              <div class="relative flex-1 min-h-[200px]">
                <Doughnut :data="chartData" :options="chartOptions" />
              </div>
            </div>
          </div>

          <!-- Form -->
          <Transition name="slide">
            <SubscriptionForm v-if="showForm || editingItem" :editing-item="editingItem" @submit="handleSubmit" @cancel="handleCancel" />
          </Transition>

          <!-- Main Table Section -->
          <div class="bg-white dark:bg-gray-900 border border-gray-200 dark:border-gray-800 rounded-2xl overflow-hidden shadow-sm">
            <div class="p-4 border-b border-gray-200 dark:border-gray-800 flex flex-col md:flex-row gap-4 justify-between items-center">
              <div class="relative w-full md:w-80">
                <Search class="absolute left-3 top-1/2 -translate-y-1/2 text-gray-400" :size="16" />
                <input v-model="filters.search" placeholder="Caută abonamente sau categorii..." class="w-full bg-gray-50 dark:bg-gray-800 border-none rounded-xl pl-10 pr-4 py-2 text-sm outline-none focus:ring-2 ring-indigo-500/20 transition" />
              </div>
              <div class="flex items-center gap-3 w-full md:w-auto">
                <select v-model="filters.sortBy" class="bg-gray-50 dark:bg-gray-800 border-none rounded-xl px-3 py-2 text-sm outline-none">
                  <option value="name">Sortează după Nume</option>
                  <option value="cost">Sortează după Preț</option>
                  <option value="nextbillingdate">Sortează după Dată</option>
                  <option value="category">Sortează după Categorie</option>
                </select>
                <button @click="filters.sortDesc = !filters.sortDesc" class="p-2 rounded-xl bg-gray-50 dark:bg-gray-800 hover:bg-gray-100 dark:hover:bg-gray-700 transition">
                  <ListFilter :size="18" :class="{ 'rotate-180': filters.sortDesc }" />
                </button>
                <button @click="handleExport" class="p-2 rounded-xl bg-gray-50 dark:bg-gray-800 hover:bg-emerald-500/10 text-emerald-500 transition" title="Export CSV">
                  <Download :size="18" />
                </button>
              </div>
            </div>

            <div v-if="loading" class="p-12 text-center text-gray-400 animate-pulse">Se încarcă datele...</div>
            <SubscriptionList v-else :subscriptions="subscriptions" @edit="handleEdit" @delete="handleDelete" />

            <!-- Pagination -->
            <div class="p-4 border-t border-gray-200 dark:border-gray-800 flex items-center justify-between">
              <p class="text-xs text-gray-500">Afișare {{ subscriptions.length }} din {{ totalItems }} abonamente</p>
              <div class="flex items-center gap-2">
                <button @click="filters.skip -= filters.take" :disabled="currentPage === 1" class="p-2 rounded-lg hover:bg-gray-100 dark:hover:bg-gray-800 disabled:opacity-30">
                  <ChevronLeft :size="18" />
                </button>
                <span class="text-xs font-bold">{{ currentPage }} / {{ totalPages }}</span>
                <button @click="filters.skip += filters.take" :disabled="currentPage === totalPages" class="p-2 rounded-lg hover:bg-gray-100 dark:hover:bg-gray-800 disabled:opacity-30">
                  <ChevronRight :size="18" />
                </button>
              </div>
            </div>
          </div>
        </main>
      </template>
    </div>
  </div>
</template>

<style>
.slide-enter-active, .slide-leave-active { transition: all 0.2s ease; }
.slide-enter-from, .slide-leave-to { opacity: 0; transform: translateY(-8px); }
.toast-enter-active, .toast-leave-active { transition: all 0.3s ease; }
.toast-enter-from, .toast-leave-to { opacity: 0; transform: translateX(100%); }
</style>

