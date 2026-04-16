<script setup>
import { ref, onMounted, onUnmounted } from 'vue'
import { subscriptionsApi } from './api.js'
import SubscriptionForm from './components/SubscriptionForm.vue'
import SubscriptionList from './components/SubscriptionList.vue'
import LoginView from './components/LoginView.vue'
import ConfirmModal from './components/ConfirmModal.vue'

// ── Auth ──────────────────────────────────────────────────────────────────────
const isAuthenticated = ref(!!localStorage.getItem('jwt_token'))
const currentUser = ref(localStorage.getItem('username') ?? '')

function handleLogin(authData) {
  isAuthenticated.value = true
  currentUser.value = authData.username
  fetchAll()
}

function handleLogout() {
  localStorage.removeItem('jwt_token')
  localStorage.removeItem('username')
  isAuthenticated.value = false
  currentUser.value = ''
}

// Listen for 401 responses from the axios interceptor
onMounted(() => {
  window.addEventListener('auth:logout', handleLogout)
  window.addEventListener('storage', handleStorageChange)
})
onUnmounted(() => {
  window.removeEventListener('auth:logout', handleLogout)
  window.removeEventListener('storage', handleStorageChange)
})

// Sync auth state when another tab changes localStorage
function handleStorageChange(e) {
  if (e.key === 'jwt_token') {
    if (!e.newValue) {
      // Other tab logged out
      isAuthenticated.value = false
      currentUser.value = ''
    } else if (e.newValue !== e.oldValue) {
      // Other tab logged in (possibly as different user) — reload to sync
      const newUsername = localStorage.getItem('username')
      if (newUsername !== currentUser.value) {
        currentUser.value = newUsername ?? ''
        isAuthenticated.value = true
        fetchAll()
      }
    }
  } else if (e.key === 'username' && e.newValue) {
    currentUser.value = e.newValue
  }
}

// ── Data ──────────────────────────────────────────────────────────────────────
const subscriptions = ref([])
const summary = ref(null)
const editingItem = ref(null)
const showForm = ref(false)
const loading = ref(false)
const error = ref(null)

// ── Toast notifications ───────────────────────────────────────────────────────
const toasts = ref([])

function showToast(message, type = 'error') {
  const id = Date.now()
  toasts.value.push({ id, message, type })
  setTimeout(() => {
    toasts.value = toasts.value.filter(t => t.id !== id)
  }, 4000)
}

// ── Confirm modal ─────────────────────────────────────────────────────────────
const confirmModal = ref({ visible: false, message: '', resolve: null })

function askConfirm(message) {
  return new Promise(resolve => {
    confirmModal.value = { visible: true, message, resolve }
  })
}

function onConfirm() {
  confirmModal.value.resolve(true)
  confirmModal.value.visible = false
}

function onCancel() {
  confirmModal.value.resolve(false)
  confirmModal.value.visible = false
}

// ── API calls ─────────────────────────────────────────────────────────────────
async function fetchAll() {
  loading.value = true
  error.value = null
  try {
    const [subRes, sumRes] = await Promise.all([
      subscriptionsApi.getAll(),
      subscriptionsApi.getSummary()
    ])
    subscriptions.value = subRes.data.items
    summary.value = sumRes.data
  } catch (e) {
    if (e.response?.status !== 401) {
      error.value = 'Nu pot contacta API-ul. Asigură-te că backend-ul rulează.'
    }
  } finally {
    loading.value = false
  }
}

async function handleSubmit(payload) {
  try {
    if (editingItem.value) {
      await subscriptionsApi.update(editingItem.value.id, payload)
      editingItem.value = null
      showForm.value = false
    } else {
      await subscriptionsApi.create(payload)
      showForm.value = false
    }
    await fetchAll()
  } catch (e) {
    const msg = e.response?.data?.title ?? e.response?.data?.errors
      ? Object.values(e.response.data.errors).flat().join(', ')
      : 'Eroare la salvare. Verifică datele introduse.'
    showToast(msg)
  }
}

async function handleDelete(id) {
  const confirmed = await askConfirm('Sigur ștergi acest abonament?')
  if (!confirmed) return
  try {
    await subscriptionsApi.remove(id)
    await fetchAll()
  } catch (e) {
    showToast('Eroare la ștergere.')
  }
}

function handleEdit(item) {
  editingItem.value = item
  showForm.value = true
  window.scrollTo({ top: 0, behavior: 'smooth' })
}

function handleCancel() {
  editingItem.value = null
  showForm.value = false
}

onMounted(() => {
  if (isAuthenticated.value) fetchAll()
})
</script>

<template>
  <!-- Login screen -->
  <LoginView v-if="!isAuthenticated" @login="handleLogin" />

  <!-- Main app -->
  <div v-else class="min-h-screen bg-gray-950 text-white">
    <!-- Confirm modal -->
    <ConfirmModal
      v-if="confirmModal.visible"
      :message="confirmModal.message"
      confirm-label="Șterge"
      @confirm="onConfirm"
      @cancel="onCancel"
    />

    <!-- Toast notifications -->
    <div class="fixed bottom-4 right-4 z-40 flex flex-col gap-2 max-w-sm">
      <TransitionGroup name="toast">
        <div
          v-for="toast in toasts"
          :key="toast.id"
          class="bg-red-500/90 text-white text-sm px-4 py-3 rounded-xl shadow-lg"
        >
          {{ toast.message }}
        </div>
      </TransitionGroup>
    </div>

    <!-- Nav -->
    <header class="border-b border-gray-800 bg-gray-950/80 backdrop-blur sticky top-0 z-10">
      <div class="max-w-6xl mx-auto px-4 py-4 flex items-center justify-between">
        <div class="flex items-center gap-3">
          <div class="w-8 h-8 bg-indigo-600 rounded-lg flex items-center justify-center text-sm font-bold">$</div>
          <span class="font-semibold text-white">Expense Tracker</span>
        </div>
        <div class="flex items-center gap-2">
          <button
            @click="showForm = !showForm; editingItem = null"
            class="bg-indigo-600 hover:bg-indigo-500 text-white text-sm font-medium px-4 py-2 rounded-lg transition"
          >
            {{ showForm && !editingItem ? '✕ Închide' : '+ Abonament nou' }}
          </button>
          <span class="text-xs text-gray-500 hidden sm:inline">{{ currentUser }}</span>
          <button
            @click="handleLogout"
            title="Deconectare"
            class="text-xs text-gray-500 hover:text-gray-300 px-3 py-2 rounded-lg transition"
          >
            Ieșire
          </button>
        </div>
      </div>
    </header>

    <main class="max-w-6xl mx-auto px-4 py-8 space-y-8">

      <!-- Error -->
      <div v-if="error" class="bg-red-500/10 border border-red-500/30 text-red-400 rounded-xl p-4 text-sm">
        {{ error }}
      </div>

      <!-- Summary Cards (per currency) -->
      <div v-if="summary">
        <!-- Per-currency cards -->
        <div
          v-if="summary.byCurrency && summary.byCurrency.length"
          class="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-4 mb-4"
        >
          <div
            v-for="c in summary.byCurrency"
            :key="c.currency"
            class="bg-gray-900 border border-gray-800 rounded-2xl p-4"
          >
            <p class="text-xs text-gray-500 mb-1 font-medium uppercase tracking-wider">{{ c.currency }}</p>
            <div class="flex items-baseline gap-2">
              <span class="text-2xl font-bold text-indigo-400">{{ c.monthlyTotal.toFixed(2) }}</span>
              <span class="text-xs text-gray-500">/ lună</span>
            </div>
            <div class="flex items-baseline gap-2 mt-0.5">
              <span class="text-base font-semibold text-white">{{ c.yearlyTotal.toFixed(2) }}</span>
              <span class="text-xs text-gray-500">/ an</span>
            </div>
            <p class="text-xs text-gray-500 mt-1">{{ c.activeCount }} abonament{{ c.activeCount === 1 ? '' : 'e' }} active</p>
          </div>
        </div>

        <!-- Count cards -->
        <div class="grid grid-cols-2 gap-4">
          <div class="bg-gray-900 border border-gray-800 rounded-2xl p-4">
            <p class="text-xs text-gray-500 mb-1">Abonamente active</p>
            <p class="text-2xl font-bold text-emerald-400">{{ summary.activeSubscriptions }}</p>
          </div>
          <div class="bg-gray-900 border border-gray-800 rounded-2xl p-4">
            <p class="text-xs text-gray-500 mb-1">Total abonamente</p>
            <p class="text-2xl font-bold text-white">{{ summary.totalSubscriptions }}</p>
          </div>
        </div>
      </div>

      <!-- Form (toggle) -->
      <Transition name="slide">
        <SubscriptionForm
          v-if="showForm || editingItem"
          :editing-item="editingItem"
          @submit="handleSubmit"
          @cancel="handleCancel"
        />
      </Transition>

      <!-- List -->
      <div>
        <div class="flex items-center justify-between mb-4">
          <h2 class="text-sm font-medium text-gray-400 uppercase tracking-wider">Abonamente</h2>
          <button
            v-if="!loading"
            @click="fetchAll"
            class="text-xs text-gray-500 hover:text-gray-300 transition"
          >
            ↻ Reîncarcă
          </button>
        </div>

        <div v-if="loading" class="text-center text-gray-600 py-16 animate-pulse">
          Se încarcă...
        </div>

        <SubscriptionList
          v-else
          :subscriptions="subscriptions"
          @edit="handleEdit"
          @delete="handleDelete"
        />
      </div>
    </main>
  </div>
</template>

<style>
.slide-enter-active,
.slide-leave-active {
  transition: all 0.2s ease;
}
.slide-enter-from,
.slide-leave-to {
  opacity: 0;
  transform: translateY(-8px);
}

.toast-enter-active,
.toast-leave-active {
  transition: all 0.3s ease;
}
.toast-enter-from,
.toast-leave-to {
  opacity: 0;
  transform: translateX(100%);
}
</style>

