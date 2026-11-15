<script setup>
import { ref, computed, onMounted, onUnmounted, watch, nextTick } from 'vue'
import { subscriptionsApi, authApi, accountApi } from './api.js'
import SubscriptionForm from './components/SubscriptionForm.vue'
import SubscriptionList from './components/SubscriptionList.vue'
import LoginView from './components/LoginView.vue'
import ConfirmModal from './components/ConfirmModal.vue'
import AccountSettings from './components/AccountSettings.vue'
import PlanningPanel from './components/PlanningPanel.vue'
import BudgetPanel from './components/BudgetPanel.vue'
import DuplicatePanel from './components/DuplicatePanel.vue'
import PriceHistoryDialog from './components/PriceHistoryDialog.vue'
import { Sun, Moon, Search, Download, Upload, BarChart3, ListFilter, ChevronLeft, ChevronRight, Settings } from 'lucide-vue-next'
import { Chart as ChartJS, ArcElement, Tooltip, Legend, BarElement, CategoryScale, LinearScale } from 'chart.js'
import { Doughnut, Bar } from 'vue-chartjs'
ChartJS.register(ArcElement, Tooltip, Legend, BarElement, CategoryScale, LinearScale)

function storedTheme() { try { return localStorage.getItem('theme') } catch { return null } }
const isDark = ref(storedTheme() !== 'light')
watch(isDark, value => document.documentElement.classList.toggle('dark', value), { immediate: true })
function toggleTheme() {
  isDark.value = !isDark.value
  try { localStorage.setItem('theme', isDark.value ? 'dark' : 'light') } catch { /* Session preference still works. */ }
}

const isAuthenticated = ref(false), currentUser = ref(''), authReady = ref(false)
watch(isAuthenticated, value => {
  document.querySelector('meta[name="robots"]')?.setAttribute('content', value ? 'noindex,nofollow' : 'index,follow')
}, { immediate: true })
const account = ref(null), showSettings = ref(false)
const subscriptions = ref([]), summary = ref(null), insights = ref(null), totalItems = ref(0)
const loading = ref(false), error = ref(null), saving = ref(false), importBusy = ref(false)
const exportingCsv = ref(false), exportingCalendar = ref(false), logoutBusy = ref(false)
const editingItem = ref(null), initialItem = ref(null), showForm = ref(false), importReport = ref(null)
const historyItem = ref(null)
const exportScope = ref('all')
const reviewing = ref(false)
const pendingIds = ref(new Set())
const filters = ref({ search: '', sortBy: 'name', sortDesc: false, status: 'all', skip: 0, take: 10 })
const currentPage = computed(() => Math.floor(filters.value.skip / filters.value.take) + 1)
const totalPages = computed(() => Math.max(1, Math.ceil(totalItems.value / filters.value.take)))
const statusOptions = [{ value: 'all', label: 'Toate' }, { value: 'active', label: 'Active' }, { value: 'inactive', label: 'Inactive' }]
let requestEpoch = 0, authEpoch = 0, searchTimer
function scheduleFetch() {
  requestEpoch++
  clearTimeout(searchTimer)
  searchTimer = setTimeout(fetchAll, 250)
}
watch([() => filters.value.search, () => filters.value.sortBy, () => filters.value.sortDesc, () => filters.value.status], () => {
  filters.value.skip = 0
  scheduleFetch()
})
watch(() => filters.value.skip, scheduleFetch)

const targetCurrency = ref('RON')
const availableCurrencies = ['RON', 'EUR', 'USD', 'GBP', 'CHF']
const ratesToRon = ref({ RON: 1, EUR: .201, USD: .215, GBP: .171, CHF: .196 })
const rateSource = ref('fallback'), ratesFetchedAt = ref(null)
function rate(currency) {
  const value = ratesToRon.value[currency?.toUpperCase()]
  return Number.isFinite(value) && value > 0 ? value : null
}
const excludedCurrencies = computed(() => (summary.value?.byCurrency ?? []).filter(c => !rate(c.currency)).map(c => c.currency))
function convertedTotal(field) {
  return (summary.value?.byCurrency ?? []).reduce((sum, c) => sum + (rate(c.currency) ? c[field] / rate(c.currency) : 0), 0) * (rate(targetCurrency.value) ?? 1)
}
const grandTotalMonthly = computed(() => convertedTotal('monthlyTotal'))
const grandTotalYearly = computed(() => convertedTotal('yearlyTotal'))
async function fetchRates() {
  const epoch = authEpoch
  try {
    const { data } = await subscriptionsApi.getRatesStatus()
    if (!isAuthenticated.value || epoch !== authEpoch) return
    if (data.rates) { ratesToRon.value = data.rates; rateSource.value = data.source; ratesFetchedAt.value = data.fetchedAt }
  } catch { /* The clearly marked indicative fallback remains available. */ }
}
const chartData = computed(() => {
  const categories = new Map()
  for (const item of insights.value?.byCategory ?? []) {
    const fx = rate(item.currency)
    if (fx) categories.set(item.category, (categories.get(item.category) ?? 0) + item.monthlyTotal / fx)
  }
  return { labels: [...categories.keys()], datasets: [{
    backgroundColor: ['#6366f1', '#10b981', '#f59e0b', '#ef4444', '#ec4899', '#8b5cf6', '#06b6d4'],
    data: [...categories.values()].map(value => value * (rate(targetCurrency.value) ?? 1))
  }] }
})
const tickColor = computed(() => isDark.value ? '#9ca3af' : '#4b5563')
const gridColor = computed(() => isDark.value ? '#1f2937' : '#f3f4f6')
const chartOptions = computed(() => ({ responsive: true, maintainAspectRatio: false,
  plugins: { legend: { position: 'bottom', labels: { color: tickColor.value } } } }))
const barData = computed(() => ({ labels: (summary.value?.byCurrency ?? []).map(c => c.currency),
  datasets: [{ label: 'Lunar', backgroundColor: '#6366f1', borderRadius: 6, data: (summary.value?.byCurrency ?? []).map(c => c.monthlyTotal) }] }))
const barOptions = computed(() => ({ responsive: true, maintainAspectRatio: false,
  plugins: { legend: { display: false } }, scales: {
    x: { ticks: { color: tickColor.value }, grid: { display: false } },
    y: { ticks: { color: tickColor.value }, grid: { color: gridColor.value } } } }))

async function fetchAccount() {
  const epoch = authEpoch
  try { const { data } = await accountApi.get(); if (isAuthenticated.value && epoch === authEpoch) account.value = data }
  catch { /* The request interceptor handles expired authentication. */ }
}
async function fetchAll() {
  if (!isAuthenticated.value) return
  const epoch = ++requestEpoch
  loading.value = true; error.value = null
  try {
    const [list, totals, analysis] = await Promise.all([
      subscriptionsApi.getAll({ ...filters.value }), subscriptionsApi.getSummary(), subscriptionsApi.getInsights()
    ])
    if (epoch !== requestEpoch || !isAuthenticated.value) return
    if (list.data.total > 0 && filters.value.skip >= list.data.total) {
      filters.value.skip = Math.floor((list.data.total - 1) / filters.value.take) * filters.value.take
      return
    }
    subscriptions.value = list.data.items; totalItems.value = list.data.total
    summary.value = totals.data; insights.value = analysis.data
  } catch (e) {
    if (epoch === requestEpoch && e.response?.status !== 401) error.value = 'Datele nu au putut fi actualizate. Reîncearcă încărcarea.'
  } finally { if (epoch === requestEpoch) loading.value = false }
}
async function bootstrapAuth() {
  try { handleLogin((await authApi.me()).data) }
  catch { isAuthenticated.value = false }
  finally { authReady.value = true }
}
function handleLogin(data) {
  authEpoch++
  isAuthenticated.value = true; currentUser.value = data.username
  fetchAll(); fetchRates(); fetchAccount()
}
function resetAuthState() {
  authEpoch++
  requestEpoch++; clearTimeout(searchTimer)
  isAuthenticated.value = false; currentUser.value = ''; account.value = null; showSettings.value = false
  subscriptions.value = []; summary.value = null; insights.value = null; totalItems.value = 0
  editingItem.value = null; initialItem.value = null; showForm.value = false; importReport.value = null
  historyItem.value = null
  exportScope.value = 'all'
  reviewing.value = false
  error.value = null; loading.value = false; toasts.value = []
  if (confirmModal.value.visible) onCancel()
  filters.value = { search: '', sortBy: 'name', sortDesc: false, status: 'all', skip: 0, take: 10 }
}
async function handleLogout() {
  if (logoutBusy.value) return
  logoutBusy.value = true
  try { await authApi.logout(); resetAuthState() }
  catch { showToast('Deconectarea nu a putut fi confirmată. Reîncearcă.') }
  finally { logoutBusy.value = false }
}
function onAuthLogout() { resetAuthState() }
function onSettingsClose() { showSettings.value = false; fetchAccount() }

const toasts = ref([]), toastTimers = new Set()
let toastId = 0
function showToast(message, type = 'error') {
  const id = ++toastId
  toasts.value.push({ id, message, type })
  if (toasts.value.length > 3) toasts.value.shift()
  const timer = setTimeout(() => { toasts.value = toasts.value.filter(t => t.id !== id); toastTimers.delete(timer) }, 6000)
  toastTimers.add(timer)
}
function errorMessage(e, fallback) {
  const data = e.response?.data
  return data?.detail ?? data?.title ?? (data?.errors ? Object.values(data.errors).flat().join(' ') : fallback)
}
const confirmModal = ref({ visible: false, message: '', resolve: null })
function askConfirm(message) {
  if (confirmModal.value.visible) confirmModal.value.resolve(false)
  return new Promise(resolve => { confirmModal.value = { visible: true, message, resolve } })
}
function onConfirm() { confirmModal.value.resolve?.(true); confirmModal.value.visible = false }
function onCancel() { confirmModal.value.resolve?.(false); confirmModal.value.visible = false }

function download(blob, filename) {
  const url = URL.createObjectURL(blob)
  const link = document.createElement('a'); link.href = url; link.download = filename
  document.body.appendChild(link); link.click(); link.remove(); URL.revokeObjectURL(url)
}
async function handleExport() {
  if (exportingCsv.value) return
  exportingCsv.value = true
  try {
    const pageOnly = exportScope.value === 'page'
    const response = await subscriptionsApi.exportCsv(pageOnly ? { ...filters.value, pageOnly: true } : {})
    download(response.data, `abonamente_${pageOnly ? 'pagina_' : ''}${new Date().toISOString().slice(0, 10)}.csv`)
    showToast('Export CSV descărcat.', 'success')
  } catch (error) {
    showToast(error.response?.status === 413 ? 'Exportul complet depășește 1 MB. Alege CSV: pagina curentă și exportă pe rând paginile.' : 'Eroare la export CSV.')
  }
  finally { exportingCsv.value = false }
}
async function handleCalendar() {
  if (exportingCalendar.value) return
  exportingCalendar.value = true
  try { download((await subscriptionsApi.exportCalendar()).data, 'abonamente.ics'); showToast('Calendar descărcat.', 'success') }
  catch { showToast('Calendarul nu a putut fi exportat.') }
  finally { exportingCalendar.value = false }
}
const fileInput = ref(null)
function triggerImport() { if (!importBusy.value) fileInput.value?.click() }
async function handleImport(event) {
  const file = event.target.files?.[0]
  if (!file || importBusy.value) return
  if (file.size > 1_000_000) { showToast('Fișierul CSV poate avea maximum 1 MB.'); event.target.value = ''; return }
  importBusy.value = true
  try {
    const form = new FormData(); form.append('file', file)
    const { data } = await subscriptionsApi.importCsv(form)
    importReport.value = data
    showToast(`Import: ${data.imported} adăugate, ${data.skipped} duplicate, ${data.rejected ?? 0} respinse.`, data.rejected ? 'error' : 'success')
    await fetchAll()
  } catch (e) { showToast(errorMessage(e, 'Eroare la import CSV.')) }
  finally { importBusy.value = false; event.target.value = '' }
}
async function handleSubmit(payload) {
  if (saving.value) return
  saving.value = true
  const id = editingItem.value?.id
  try {
    if (id) await subscriptionsApi.update(id, { ...payload, expectedUpdatedAt: editingItem.value.updatedAt })
    else await subscriptionsApi.create(payload)
    handleCancel(); await fetchAll()
    showToast(id ? 'Abonament actualizat.' : 'Abonament adăugat.', 'success')
  } catch (e) {
    showToast(errorMessage(e, 'Eroare la salvare. Datele introduse au fost păstrate.'))
    if (e.response?.status === 409) await fetchAll()
  }
  finally { saving.value = false }
}
async function handleDelete(id) {
  if (pendingIds.value.has(id)) return
  pendingIds.value.add(id)
  try {
    if (!await askConfirm('Ștergi acest abonament? Datele lui vor fi eliminate.')) return
    await subscriptionsApi.remove(id); await fetchAll(); showToast('Abonament șters.', 'success')
  } catch (e) { showToast(errorMessage(e, 'Eroare la ștergere.')) }
  finally { pendingIds.value.delete(id) }
}
async function handleStatus(item) {
  if (pendingIds.value.has(item.id)) return
  pendingIds.value.add(item.id)
  try {
    if (item.isActive && !await askConfirm(`Suspendă „${item.name}” în tracker? Plata la furnizor se gestionează separat.`)) return
    await subscriptionsApi.setStatus(item.id, !item.isActive); await fetchAll()
    showToast(item.isActive ? 'Abonament suspendat în tracker.' : 'Abonament reactivat.', 'success')
  } catch (e) { showToast(errorMessage(e, 'Starea nu a putut fi schimbată.')) }
  finally { pendingIds.value.delete(item.id) }
}
async function revealForm() {
  showForm.value = true; await nextTick()
  document.getElementById('subscription-name')?.focus({ preventScroll: true })
  document.getElementById('subscription-form')?.scrollIntoView({ behavior: 'smooth', block: 'start' })
}
function handleEdit(item) { initialItem.value = null; editingItem.value = item; revealForm() }
async function handleReview(id) {
  if (reviewing.value) return
  reviewing.value = true
  const epoch = authEpoch
  try {
    const response = await subscriptionsApi.getById(id)
    if (epoch === authEpoch && isAuthenticated.value) handleEdit(response.data)
  } catch (error) { if (epoch === authEpoch) showToast(errorMessage(error, 'Abonamentul nu a putut fi deschis. Reîncearcă.')) }
  finally { if (epoch === authEpoch) reviewing.value = false }
}
function handleDuplicate(item) { editingItem.value = null; initialItem.value = { ...item, name: `${item.name.slice(0, 90)} (copie)` }; revealForm() }
function handleCancel() { editingItem.value = null; initialItem.value = null; showForm.value = false }
function handleNew() { if (showForm.value && !editingItem.value) handleCancel(); else { editingItem.value = null; initialItem.value = null; revealForm() } }

onMounted(() => { window.addEventListener('auth:logout', onAuthLogout); bootstrapAuth() })
onUnmounted(() => {
  requestEpoch++; clearTimeout(searchTimer)
  for (const timer of toastTimers) clearTimeout(timer)
  window.removeEventListener('auth:logout', onAuthLogout)
})
</script>

<template>
  <div class="min-h-screen bg-gray-50 dark:bg-gray-950 text-gray-900 dark:text-gray-100 transition-colors duration-300">
    <div v-if="!authReady" class="min-h-screen flex items-center justify-center">
      <div class="animate-pulse text-gray-400 text-sm">Se încarcă...</div>
    </div>

    <LoginView v-else-if="!isAuthenticated" @login="handleLogin" />

    <template v-else>
      <!-- Confirm modal -->
      <ConfirmModal v-if="confirmModal.visible" :message="confirmModal.message" @confirm="onConfirm" @cancel="onCancel" />

      <!-- Account settings -->
      <AccountSettings v-if="showSettings" @close="onSettingsClose" @logout="onAuthLogout" @toast="(m, t) => showToast(m, t)" />

        <!-- Toasts -->
        <div aria-live="polite" aria-atomic="true" class="fixed bottom-4 right-4 z-[60] flex flex-col gap-2 max-w-[calc(100vw-2rem)] sm:max-w-sm">
          <TransitionGroup name="toast">
            <div
              v-for="t in toasts"
              :key="t.id"
              class="text-white text-sm px-4 py-3 rounded-xl shadow-lg"
              :class="t.type === 'success' ? 'bg-emerald-700' : 'bg-red-700'"
            >
              {{ t.message }}
            </div>
          </TransitionGroup>
        </div>

        <!-- Header -->
        <header class="border-b border-gray-200 dark:border-gray-800 bg-white/80 dark:bg-gray-950/80 backdrop-blur sticky top-0 z-10">
          <div class="max-w-6xl mx-auto px-4 py-4 flex flex-wrap gap-3 items-center justify-between">
            <div class="flex items-center gap-3">
              <div class="w-8 h-8 bg-indigo-600 rounded-lg flex items-center justify-center text-sm font-bold text-white">$</div>
              <span class="font-bold">Expense Tracker</span>
            </div>
            <div class="flex items-center gap-3">
              <button @click="toggleTheme" :aria-label="isDark ? 'Comută pe tema luminoasă' : 'Comută pe tema întunecată'" class="p-2 rounded-lg hover:bg-gray-100 dark:hover:bg-gray-800 transition">
                <Sun v-if="isDark" :size="20" />
                <Moon v-else :size="20" />
              </button>
              <button @click="handleNew" :disabled="saving" class="bg-indigo-600 hover:bg-indigo-700 disabled:opacity-50 text-white text-sm px-3 sm:px-4 py-2 rounded-lg transition">
                {{ showForm && !editingItem ? '✕ Închide' : '+ Nou' }}
              </button>
              <button @click="showSettings = true" class="p-2 rounded-lg hover:bg-gray-100 dark:hover:bg-gray-800 transition" title="Setări cont" aria-label="Setări cont">
                <Settings :size="20" />
              </button>
              <button @click="handleLogout" :disabled="logoutBusy" class="text-xs text-gray-500 hover:text-gray-700 dark:hover:text-gray-300 disabled:opacity-50">{{ logoutBusy ? 'Se închide…' : 'Ieșire' }}</button>
            </div>
          </div>
        </header>

        <main class="max-w-6xl mx-auto px-4 py-8 space-y-6">
          <div class="flex flex-wrap items-end justify-between gap-4">
            <div class="min-w-0"><p class="text-xs font-semibold uppercase tracking-widest text-indigo-600 dark:text-indigo-400">Finanțele tale, mai clare</p><h1 class="text-2xl sm:text-3xl font-bold tracking-tight mt-2">Bună, {{ currentUser }}.</h1><p class="text-sm text-gray-500 dark:text-gray-400 mt-2">Toate abonamentele într-un loc. Mai puține surprize la final de lună.</p></div>
            <a href="#subscriptions" class="text-sm font-semibold text-indigo-600 dark:text-indigo-300 rounded-lg px-3 py-2 bg-indigo-50 dark:bg-indigo-500/10">Vezi abonamentele ↓</a>
          </div>
          <div v-if="error" role="alert" class="bg-red-500/10 border border-red-500/30 text-red-700 dark:text-red-300 rounded-xl p-4 text-sm">{{ error }} <button type="button" @click="fetchAll" class="underline font-bold mt-2 sm:mt-0 sm:ml-2">Reîncearcă</button></div>

          <div v-if="summary" class="grid grid-cols-3 gap-2 sm:gap-4">
            <div class="panel p-3 sm:p-5"><p class="text-2xl font-bold">{{ summary.activeSubscriptions }}</p><p class="text-xs sm:text-sm text-gray-500 dark:text-gray-400 mt-1">Active</p></div>
            <div class="panel p-3 sm:p-5"><p class="text-2xl font-bold">{{ (summary.totalSubscriptions ?? 0) - (summary.activeSubscriptions ?? 0) }}</p><p class="text-xs sm:text-sm text-gray-500 dark:text-gray-400 mt-1">Suspendate</p></div>
            <div class="panel p-3 sm:p-5"><p class="text-2xl font-bold text-amber-700 dark:text-amber-300">{{ summary.dueThisWeek }}</p><p class="text-xs sm:text-sm text-gray-500 dark:text-gray-400 mt-1">În 7 zile</p></div>
          </div>

          <!-- Unverified email banner -->
          <div v-if="account && account.email && !account.emailVerified" class="bg-amber-500/10 border border-amber-500/30 text-amber-700 dark:text-amber-400 rounded-xl p-4 text-sm flex flex-col sm:flex-row sm:items-center justify-between gap-3">
            <span>⚠️ Adresa ta de email nu este verificată.</span>
            <button @click="showSettings = true" class="text-xs font-bold underline whitespace-nowrap self-start sm:self-auto">Verifică acum</button>
          </div>

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
                    <h3 class="text-xs font-bold text-gray-500 dark:text-gray-400 uppercase tracking-widest mb-1">Total lunar estimat</h3>
                    <div class="flex flex-wrap items-baseline gap-x-2 gap-y-1">
                      <span class="text-3xl sm:text-4xl font-black text-emerald-700 dark:text-emerald-400 break-all">{{ grandTotalMonthly.toFixed(2) }}</span>
                      <span class="text-base font-bold text-gray-500 dark:text-gray-400">{{ targetCurrency }} / lună</span>
                    </div>
                    <p class="text-sm text-gray-400 mt-1">≈ {{ grandTotalYearly.toFixed(2) }} {{ targetCurrency }} / an</p>
                    <p v-if="summary?.dueThisWeek" class="inline-flex items-center gap-1.5 mt-3 text-xs font-bold bg-amber-500/15 text-amber-600 dark:text-amber-400 px-3 py-1.5 rounded-lg">
                      ⏰ {{ summary.dueThisWeek }} {{ summary.dueThisWeek === 1 ? 'plată' : 'plăți' }} în următoarele 7 zile
                    </p>
                  </div>
                  <div class="w-full md:w-auto">
                    <label for="conversion-currency" class="block text-xs font-bold text-gray-500 dark:text-gray-400 uppercase mb-2">Valută conversie</label>
                    <select id="conversion-currency" v-model="targetCurrency" class="w-full md:w-32 bg-gray-50 dark:bg-gray-800 border border-gray-200 dark:border-gray-700 rounded-xl px-3 py-2 text-sm outline-none focus:ring-2 ring-indigo-500/20 transition">
                      <option v-for="c in availableCurrencies" :key="c" :value="c">{{ c }}</option>
                    </select>
                  </div>
                </div>
                <p class="mt-4 text-xs text-gray-500 dark:text-gray-400">{{ rateSource === 'live' ? `Curs preluat ${new Date(ratesFetchedAt).toLocaleString('ro-RO')}.` : 'Cursuri orientative de rezervă; furnizorul live nu este confirmat.' }} Conversia este estimativă.</p>
                <p v-if="excludedCurrencies.length" role="status" class="mt-2 text-xs text-amber-800 dark:text-amber-300">Fără curs disponibil: {{ excludedCurrencies.join(', ') }}. Aceste valute sunt excluse din estimare și din graficul pe categorii.</p>
              </div>
            </div>

            <!-- Chart -->
            <div class="bg-white dark:bg-gray-900 border border-gray-200 dark:border-gray-800 rounded-2xl p-6 shadow-sm flex flex-col">
              <h3 class="text-xs font-bold text-gray-400 uppercase tracking-widest mb-4 flex items-center gap-2">
                <BarChart3 :size="14" /> Analiză Categorii
              </h3>
              <div class="relative flex-1 min-h-[200px]">
                <Doughnut v-if="chartData.labels.length" :data="chartData" :options="chartOptions" role="img" aria-label="Costul lunar pe categorii, pentru întregul cont" />
                <p v-else class="text-sm text-gray-500 dark:text-gray-400 py-8 text-center">Adaugă un abonament activ pentru a vedea distribuția.</p>
              </div>
              <p class="text-xs text-gray-500 dark:text-gray-400 mt-3">Toate abonamentele active, indiferent de pagină sau filtre.</p>
            </div>
          </div>

          <!-- Monthly spend per currency -->
          <div v-if="summary?.byCurrency?.length" class="bg-white dark:bg-gray-900 border border-gray-200 dark:border-gray-800 rounded-2xl p-6 shadow-sm">
            <h3 class="text-xs font-bold text-gray-400 uppercase tracking-widest mb-4 flex items-center gap-2">
              <BarChart3 :size="14" /> Cheltuieli lunare pe valută
            </h3>
            <div class="relative h-48">
              <Bar :data="barData" :options="barOptions" role="img" aria-label="Cost lunar separat pentru fiecare valută" />
            </div>
          </div>

          <PlanningPanel :data="insights" :exporting="exportingCalendar" :reviewing="reviewing" @calendar="handleCalendar" @review="handleReview" />
          <BudgetPanel :refresh-key="summary" :categories="insights?.byCategory ?? []" @toast="(m, t) => showToast(m, t)" />
          <DuplicatePanel :refresh-key="summary" :reviewing="reviewing" @review="handleReview" />

          <div v-if="importReport" role="status" class="panel p-4 space-y-2 text-sm">
            <div class="flex justify-between gap-3"><h2 class="font-bold">Rezultatul importului</h2><button type="button" @click="importReport = null" class="text-xs underline">Închide raportul</button></div>
            <p>{{ importReport.imported }} adăugate · {{ importReport.skipped }} duplicate · {{ importReport.rejected ?? 0 }} respinse</p>
            <ul v-if="importReport.errors?.length" class="list-disc pl-5 text-red-700 dark:text-red-300"><li v-for="message in importReport.errors" :key="message">{{ message }}</li></ul>
          </div>

          <!-- Form -->
          <Transition name="slide">
            <SubscriptionForm v-if="showForm || editingItem" :editing-item="editingItem" :initial-item="initialItem" :busy="saving" @submit="handleSubmit" @cancel="handleCancel" />
          </Transition>

          <!-- Main Table Section -->
          <section id="subscriptions" aria-label="Abonamentele tale" class="bg-white dark:bg-gray-900 border border-gray-200 dark:border-gray-800 rounded-2xl overflow-hidden shadow-sm">
            <div class="p-4 border-b border-gray-200 dark:border-gray-800 flex flex-col md:flex-row gap-4 justify-between items-center">
              <div class="relative w-full md:w-80">
                <Search class="absolute left-3 top-1/2 -translate-y-1/2 text-gray-400" :size="16" />
                <input v-model="filters.search" maxlength="200" aria-label="Caută abonamente, categorii sau note" placeholder="Caută abonamente, categorii sau note..." class="w-full bg-gray-50 dark:bg-gray-800 border-none rounded-xl pl-10 pr-4 py-2 text-sm outline-none focus:ring-2 ring-indigo-500/20 transition" />
              </div>
              <div class="flex flex-wrap items-center gap-2 sm:gap-3 w-full md:w-auto">
                <div class="flex bg-gray-50 dark:bg-gray-800 rounded-xl p-1 text-xs font-bold">
                  <button
                    v-for="opt in statusOptions"
                    :key="opt.value"
                    @click="filters.status = opt.value"
                    :aria-pressed="filters.status === opt.value"
                    :class="filters.status === opt.value
                      ? 'bg-white dark:bg-gray-700 text-indigo-600 dark:text-indigo-300 shadow-sm'
                      : 'text-gray-400 hover:text-gray-600 dark:hover:text-gray-300'"
                    class="px-3 py-1.5 rounded-lg transition"
                  >
                    {{ opt.label }}
                  </button>
                </div>
                <select v-model="filters.sortBy" aria-label="Sortează abonamentele" class="max-w-full bg-gray-50 dark:bg-gray-800 border-none rounded-xl px-3 py-2 text-sm outline-none">
                  <option value="name">Sortează după Nume</option>
                  <option value="cost">Sortează după Preț</option>
                  <option value="nextbillingdate">Sortează după Dată</option>
                  <option value="category">Sortează după Categorie</option>
                </select>
                <button @click="filters.sortDesc = !filters.sortDesc" :aria-label="filters.sortDesc ? 'Sortează crescător' : 'Sortează descrescător'" class="p-2 rounded-xl bg-gray-50 dark:bg-gray-800 hover:bg-gray-100 dark:hover:bg-gray-700 transition">
                  <ListFilter :size="18" :class="{ 'rotate-180': filters.sortDesc }" />
                </button>
                <button @click="triggerImport" :disabled="importBusy" class="p-2 rounded-xl bg-gray-50 dark:bg-gray-800 hover:bg-indigo-500/10 text-indigo-600 dark:text-indigo-400 disabled:opacity-50 transition" title="Import CSV" aria-label="Import CSV">
                  <Upload :size="18" />
                </button>
                <input ref="fileInput" type="file" accept=".csv,text/csv" class="hidden" @change="handleImport" />
                <select v-model="exportScope" aria-label="Conținut export CSV" class="max-w-full bg-gray-50 dark:bg-gray-800 border-none rounded-xl px-3 py-2 text-sm outline-none"><option value="all">CSV: tot contul</option><option value="page">CSV: pagina curentă</option></select>
                <button @click="handleExport" :disabled="exportingCsv" class="p-2 rounded-xl bg-gray-50 dark:bg-gray-800 hover:bg-emerald-500/10 text-emerald-700 dark:text-emerald-400 disabled:opacity-50 transition" title="Export CSV" aria-label="Export CSV">
                  <Download :size="18" />
                </button>
              </div>
            </div>

            <div v-if="loading" class="grid grid-cols-1 sm:grid-cols-2 xl:grid-cols-3 gap-4 p-4">
              <div v-for="n in 6" :key="n" class="bg-white dark:bg-gray-900 border border-gray-100 dark:border-gray-800 rounded-2xl p-5 animate-pulse">
                <div class="h-4 w-2/3 bg-gray-200 dark:bg-gray-800 rounded mb-3"></div>
                <div class="h-6 w-1/3 bg-gray-200 dark:bg-gray-800 rounded mb-4"></div>
                <div class="h-3 w-1/2 bg-gray-200 dark:bg-gray-800 rounded mb-5"></div>
                <div class="flex gap-2">
                  <div class="h-8 flex-1 bg-gray-100 dark:bg-gray-800 rounded-xl"></div>
                  <div class="h-8 flex-1 bg-gray-100 dark:bg-gray-800 rounded-xl"></div>
                </div>
              </div>
            </div>
            <SubscriptionList v-else :subscriptions="subscriptions" :pending-ids="pendingIds" @edit="handleEdit" @delete="handleDelete" @status="handleStatus" @duplicate="handleDuplicate" @history="item => historyItem = item" />
            <PriceHistoryDialog v-if="historyItem" :key="historyItem.id" :subscription="historyItem" @close="historyItem = null" />

            <!-- Pagination -->
            <div class="p-4 border-t border-gray-200 dark:border-gray-800 flex flex-wrap gap-3 items-center justify-between">
              <p class="text-xs text-gray-500">Afișare {{ subscriptions.length }} din {{ totalItems }} abonamente</p>
              <div class="flex items-center gap-2">
                <button @click="filters.skip = Math.max(0, filters.skip - filters.take)" :disabled="currentPage <= 1" aria-label="Pagina anterioară" class="p-2 rounded-lg hover:bg-gray-100 dark:hover:bg-gray-800 disabled:opacity-30 disabled:cursor-not-allowed">
                  <ChevronLeft :size="18" />
                </button>
                <span class="text-xs font-bold">{{ currentPage }} / {{ totalPages }}</span>
                <button @click="filters.skip += filters.take" :disabled="currentPage >= totalPages" aria-label="Pagina următoare" class="p-2 rounded-lg hover:bg-gray-100 dark:hover:bg-gray-800 disabled:opacity-30 disabled:cursor-not-allowed">
                  <ChevronRight :size="18" />
                </button>
              </div>
            </div>
          </section>
        </main>
      </template>
    </div>
</template>

<style>
.slide-enter-active, .slide-leave-active { transition: all 0.2s ease; }
.slide-enter-from, .slide-leave-to { opacity: 0; transform: translateY(-8px); }
.toast-enter-active, .toast-leave-active { transition: all 0.3s ease; }
.toast-enter-from, .toast-leave-to { opacity: 0; transform: translateX(100%); }
</style>

