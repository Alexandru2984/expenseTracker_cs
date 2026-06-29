<script setup>
import { ref, onMounted } from 'vue'
import { accountApi, authApi } from '../api.js'
import { X, ShieldCheck, ShieldAlert, LogOut } from 'lucide-vue-next'

const emit = defineEmits(['close', 'logout', 'toast'])

const account = ref(null)
const loading = ref(true)

// change password
const currentPassword = ref('')
const newPassword = ref('')
const pwBusy = ref(false)

// change email
const newEmail = ref('')
const emailBusy = ref(false)
const awaitingCode = ref(false)
const code = ref('')

const logoutBusy = ref(false)

async function load() {
  try {
    const res = await accountApi.get()
    account.value = res.data
  } catch { /* handled globally */ }
  finally { loading.value = false }
}
onMounted(load)

function err(e, fallback) {
  return e.response?.data?.detail ?? e.response?.data?.title ?? fallback
}

async function submitPassword() {
  if (newPassword.value.length < 8) { emit('toast', 'Parola nouă: minim 8 caractere.'); return }
  pwBusy.value = true
  try {
    await accountApi.changePassword({ currentPassword: currentPassword.value, newPassword: newPassword.value })
    currentPassword.value = ''
    newPassword.value = ''
    emit('toast', 'Parola a fost schimbată.', 'success')
  } catch (e) {
    emit('toast', err(e, 'Eroare la schimbarea parolei.'))
  } finally {
    pwBusy.value = false
  }
}

async function submitEmail() {
  if (!newEmail.value.trim()) { emit('toast', 'Introdu o adresă de email.'); return }
  emailBusy.value = true
  try {
    await accountApi.changeEmail({ email: newEmail.value.trim() })
    awaitingCode.value = true
    emit('toast', 'Ți-am trimis un cod la noua adresă.', 'success')
    await load()
  } catch (e) {
    emit('toast', err(e, 'Eroare la schimbarea emailului.'))
  } finally {
    emailBusy.value = false
  }
}

async function confirmEmail() {
  if (code.value.trim().length !== 6) { emit('toast', 'Codul are 6 cifre.'); return }
  emailBusy.value = true
  try {
    await authApi.verifyEmail({ username: account.value.username, code: code.value.trim() })
    awaitingCode.value = false
    code.value = ''
    newEmail.value = ''
    emit('toast', 'Email verificat.', 'success')
    await load()
  } catch (e) {
    emit('toast', err(e, 'Cod invalid.'))
  } finally {
    emailBusy.value = false
  }
}

async function doLogoutAll() {
  logoutBusy.value = true
  try {
    await accountApi.logoutAll()
    emit('logout')
  } catch (e) {
    emit('toast', err(e, 'Eroare.'))
  } finally {
    logoutBusy.value = false
  }
}

const inputCls = 'w-full bg-gray-50 dark:bg-gray-800 border-none rounded-xl px-4 py-2.5 text-sm dark:text-white placeholder-gray-400 outline-none focus:ring-2 ring-indigo-500/20 transition'
</script>

<template>
  <Teleport to="body">
    <div class="fixed inset-0 z-50 flex items-end sm:items-center justify-center p-0 sm:p-4 bg-black/60 backdrop-blur-sm" @mousedown.self="emit('close')">
      <div class="bg-white dark:bg-gray-900 border border-gray-200 dark:border-gray-800 w-full max-w-md rounded-t-3xl sm:rounded-3xl p-6 shadow-2xl max-h-[90vh] overflow-y-auto">
        <div class="flex items-center justify-between mb-5">
          <h2 class="text-lg font-bold dark:text-white">Setări cont</h2>
          <button @click="emit('close')" class="p-2 rounded-lg hover:bg-gray-100 dark:hover:bg-gray-800 transition" aria-label="Închide">
            <X :size="18" />
          </button>
        </div>

        <div v-if="loading" class="py-8 text-center text-gray-400 text-sm animate-pulse">Se încarcă...</div>

        <div v-else-if="account" class="space-y-6">
          <!-- Account summary -->
          <div class="bg-gray-50 dark:bg-gray-800/50 rounded-2xl p-4">
            <p class="text-xs text-gray-400 uppercase font-bold tracking-wider mb-1">Cont</p>
            <p class="font-bold dark:text-white">{{ account.username }}</p>
            <div class="flex items-center gap-2 mt-2 text-sm">
              <span class="text-gray-500 dark:text-gray-400">{{ account.email ?? 'fără email' }}</span>
              <span v-if="account.email && account.emailVerified" class="inline-flex items-center gap-1 text-[10px] font-bold text-emerald-600 dark:text-emerald-400 bg-emerald-500/10 px-2 py-0.5 rounded-md">
                <ShieldCheck :size="12" /> Verificat
              </span>
              <span v-else-if="account.email" class="inline-flex items-center gap-1 text-[10px] font-bold text-amber-600 dark:text-amber-400 bg-amber-500/10 px-2 py-0.5 rounded-md">
                <ShieldAlert :size="12" /> Neverificat
              </span>
            </div>
          </div>

          <!-- Change password -->
          <form @submit.prevent="submitPassword" class="space-y-3">
            <p class="text-xs text-gray-400 uppercase font-bold tracking-wider">Schimbă parola</p>
            <input v-model="currentPassword" type="password" autocomplete="current-password" placeholder="Parola curentă" :class="inputCls" />
            <input v-model="newPassword" type="password" autocomplete="new-password" placeholder="Parolă nouă (min 8)" :class="inputCls" />
            <button type="submit" :disabled="pwBusy" class="w-full bg-indigo-600 hover:bg-indigo-500 disabled:opacity-50 text-white font-bold py-2.5 rounded-xl text-sm transition active:scale-[0.98]">
              {{ pwBusy ? 'Se salvează...' : 'Schimbă parola' }}
            </button>
          </form>

          <!-- Change email -->
          <form @submit.prevent="awaitingCode ? confirmEmail() : submitEmail()" class="space-y-3">
            <p class="text-xs text-gray-400 uppercase font-bold tracking-wider">Schimbă emailul</p>
            <input v-model="newEmail" type="email" autocomplete="email" placeholder="adresa@email.com" :class="inputCls" />
            <input v-if="awaitingCode" v-model="code" inputmode="numeric" maxlength="6" placeholder="Cod (6 cifre)" :class="[inputCls, 'text-center tracking-[0.4em]']" />
            <button type="submit" :disabled="emailBusy" class="w-full bg-gray-100 dark:bg-gray-800 hover:bg-gray-200 dark:hover:bg-gray-700 disabled:opacity-50 text-gray-700 dark:text-gray-200 font-bold py-2.5 rounded-xl text-sm transition active:scale-[0.98]">
              {{ emailBusy ? 'Se procesează...' : (awaitingCode ? 'Confirmă codul' : 'Trimite codul') }}
            </button>
          </form>

          <!-- Logout everywhere -->
          <div class="pt-2 border-t border-gray-100 dark:border-gray-800">
            <button @click="doLogoutAll" :disabled="logoutBusy" class="w-full flex items-center justify-center gap-2 text-red-500 hover:bg-red-500/10 disabled:opacity-50 font-bold py-2.5 rounded-xl text-sm transition">
              <LogOut :size="16" /> Deconectează toate dispozitivele
            </button>
          </div>
        </div>
      </div>
    </div>
  </Teleport>
</template>
