<script setup>
import { ref, onMounted, computed } from 'vue'
import FinanceIntegrationPanel from './FinanceIntegrationPanel.vue'
import SessionPanel from './SessionPanel.vue'
import NotificationPanel from './NotificationPanel.vue'
import MfaPanel from './MfaPanel.vue'
import AccountDataPanel from './AccountDataPanel.vue'
import { useDialog } from '../useDialog.js'
import { useCooldown } from '../useCooldown.js'
import { accountApi, authApi } from '../api.js'
import { X, ShieldCheck, ShieldAlert, LogOut } from 'lucide-vue-next'

const emit = defineEmits(['close', 'logout', 'toast'])
const dialog = ref(null)
useDialog(dialog, () => emit('close'))

const account = ref(null)
const loading = ref(true)
const sessionRevision = ref(0)
function mfaChanged() { sessionRevision.value++; load() }

// change password
const currentPassword = ref('')
const newPassword = ref('')
const pwBusy = ref(false)

// change email
const newEmail = ref('')
const emailPassword = ref('')
const emailBusy = ref(false)
const code = ref('')
const currentEmailCode = ref('')
const cooldown = useCooldown()
const retrySeconds = cooldown.remaining
const pendingCodeValid = computed(() => account.value?.pendingEmailExpiresAt
  && new Date(account.value.pendingEmailExpiresAt).getTime() > cooldown.now.value)

const logoutBusy = ref(false)

async function load() {
  try {
    const res = await accountApi.get()
    account.value = res.data
    cooldown.start(res.data.emailRetryAfterSeconds)
    if (res.data.pendingEmail) newEmail.value = res.data.pendingEmail
  } catch { /* handled globally */ }
  finally { loading.value = false }
}
onMounted(load)

function err(e, fallback) {
  return e.response?.data?.detail ?? e.response?.data?.title ?? fallback
}

async function submitPassword() {
  if (pwBusy.value) return
  if (newPassword.value.length < 8) { emit('toast', 'Parola nouă: minim 8 caractere.'); return }
  pwBusy.value = true
  try {
    await accountApi.changePassword({ currentPassword: currentPassword.value, newPassword: newPassword.value })
    currentPassword.value = ''
    newPassword.value = ''
    code.value = ''
    await load()
    emit('toast', 'Parola a fost schimbată.', 'success')
  } catch (e) {
    emit('toast', err(e, 'Eroare la schimbarea parolei.'))
  } finally {
    pwBusy.value = false
  }
}

async function submitEmail() {
  if (emailBusy.value || retrySeconds.value > 0) return
  if (!newEmail.value.trim()) { emit('toast', 'Introdu o adresă de email.'); return }
  emailBusy.value = true
  try {
    const response = await accountApi.changeEmail({ email: newEmail.value.trim(), currentPassword: emailPassword.value })
    emailPassword.value = ''
    cooldown.start(response.data.retryAfterSeconds)
    code.value = ''
    emit('toast', 'Cod trimis. Emailul curent rămâne valabil până la confirmare.', 'success')
    await load()
  } catch (e) {
    if (e.response?.status === 429) cooldown.start(e.response.headers?.['retry-after'] ?? 60)
    emit('toast', err(e, 'Eroare la schimbarea emailului.'))
  } finally {
    emailBusy.value = false
  }
}

async function confirmEmail() {
  if (emailBusy.value) return
  if (code.value.trim().length !== 6) { emit('toast', 'Codul are 6 cifre.'); return }
  emailBusy.value = true
  try {
    await accountApi.confirmEmailChange({ code: code.value.trim() })
    code.value = ''
    newEmail.value = ''
    emit('toast', 'Email confirmat. Celelalte sesiuni și tokenurile Finance au fost revocate.', 'success')
    await load()
  } catch (e) {
    emit('toast', err(e, 'Cod invalid.'))
  } finally {
    emailBusy.value = false
  }
}

async function cancelEmail() {
  if (emailBusy.value) return
  emailBusy.value = true
  try {
    await accountApi.cancelEmailChange()
    code.value = ''; newEmail.value = ''; emailPassword.value = ''
    await load()
    emit('toast', 'Cerere anulată. Adresa curentă a fost păstrată.', 'success')
  } catch (e) { emit('toast', err(e, 'Cererea nu a putut fi anulată.')) }
  finally { emailBusy.value = false }
}

async function verifyCurrentEmail() {
  if (emailBusy.value) return
  emailBusy.value = true
  try {
    await authApi.verifyEmail({ username: account.value.username, code: currentEmailCode.value.trim() })
    currentEmailCode.value = ''; await load()
    emit('toast', 'Adresa curentă a fost verificată.', 'success')
  } catch (e) { emit('toast', err(e, 'Cod invalid.')) }
  finally { emailBusy.value = false }
}

async function doLogoutAll() {
  if (logoutBusy.value) return
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
      <div ref="dialog" role="dialog" aria-modal="true" aria-labelledby="account-settings-title" tabindex="-1" class="bg-white dark:bg-gray-900 border border-gray-200 dark:border-gray-800 w-full max-w-md rounded-t-3xl sm:rounded-3xl p-6 shadow-2xl max-h-[90vh] overflow-y-auto">
        <div class="flex items-center justify-between mb-5">
          <h2 id="account-settings-title" class="text-lg font-bold dark:text-white">Setări cont</h2>
          <button @click="emit('close')" class="p-2 rounded-lg hover:bg-gray-100 dark:hover:bg-gray-800 transition" aria-label="Închide">
            <X :size="18" />
          </button>
        </div>

        <div v-if="loading" class="py-8 text-center text-gray-400 text-sm animate-pulse">Se încarcă...</div>

        <div v-else-if="account" class="space-y-6">
          <!-- Account summary -->
          <div class="bg-gray-50 dark:bg-gray-800/50 rounded-2xl p-4">
            <p class="text-xs text-gray-400 uppercase font-bold tracking-wider mb-1">Cont</p>
            <p class="font-bold dark:text-white break-words">{{ account.username }}</p>
            <div class="flex flex-wrap items-center gap-2 mt-2 text-sm">
              <span class="min-w-0 break-all text-gray-500 dark:text-gray-400">{{ account.email ?? 'fără email' }}</span>
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
            <input v-model="currentPassword" type="password" autocomplete="current-password" aria-label="Parola curentă pentru schimbarea parolei" required placeholder="Parola curentă" :class="inputCls" />
            <input v-model="newPassword" type="password" autocomplete="new-password" aria-label="Parola nouă" required minlength="8" maxlength="100" placeholder="Parolă nouă (min 8)" :class="inputCls" />
            <button type="submit" :disabled="pwBusy" class="w-full bg-indigo-600 hover:bg-indigo-700 disabled:opacity-50 text-white font-bold py-2.5 rounded-xl text-sm transition active:scale-[0.98]">
              {{ pwBusy ? 'Se salvează...' : 'Schimbă parola' }}
            </button>
          </form>

          <!-- Change email -->
          <form @submit.prevent="submitEmail" class="space-y-3">
            <p class="text-xs text-gray-400 uppercase font-bold tracking-wider">Schimbă emailul</p>
            <p class="text-xs text-gray-500 dark:text-gray-400">Adresa curentă rămâne valabilă până când confirmi noua adresă. Poți anula oricând cererea.</p>
            <input v-model="newEmail" type="email" autocomplete="email" aria-label="Noua adresă de email" required maxlength="256" placeholder="adresa@email.com" :class="inputCls" :disabled="emailBusy" />
            <input v-model="emailPassword" type="password" autocomplete="current-password" aria-label="Parola pentru schimbarea emailului" required maxlength="128" placeholder="Confirmă parola curentă" :class="inputCls" :disabled="emailBusy" />
            <button type="submit" :disabled="emailBusy || retrySeconds > 0" class="w-full bg-gray-100 dark:bg-gray-800 hover:bg-gray-200 dark:hover:bg-gray-700 disabled:opacity-50 text-gray-700 dark:text-gray-200 font-bold py-2.5 rounded-xl text-sm transition active:scale-[0.98]">
              {{ emailBusy ? 'Se procesează…' : retrySeconds > 0 ? `Cod nou disponibil în ${retrySeconds}s` : account.pendingEmail ? 'Retrimite codul' : 'Trimite codul' }}
            </button>
          </form>

          <form v-if="account.pendingEmail" @submit.prevent="confirmEmail" class="rounded-2xl border border-indigo-200 dark:border-indigo-500/40 p-4 space-y-3" aria-labelledby="pending-email-title">
            <h3 id="pending-email-title" class="font-bold text-sm dark:text-white">Confirmă noua adresă</h3>
            <p class="text-sm text-gray-600 dark:text-gray-300 break-all">{{ account.pendingEmail }}</p>
            <p class="text-xs text-gray-500 dark:text-gray-400">{{ pendingCodeValid ? 'Introdu codul primit la noua adresă. Este valabil 15 minute.' : 'Codul a expirat sau nu mai este valabil. Cere unul nou folosind parola.' }}</p>
            <input v-model="code" aria-label="Codul pentru noua adresă de email" autocomplete="one-time-code" inputmode="numeric" maxlength="6" required pattern="[0-9]{6}" placeholder="Cod (6 cifre)" :disabled="emailBusy || !pendingCodeValid" :class="[inputCls, 'text-center tracking-[0.4em]']" />
            <button type="submit" :disabled="emailBusy || !pendingCodeValid" class="w-full bg-indigo-600 hover:bg-indigo-700 disabled:opacity-50 text-white font-bold py-2.5 rounded-xl text-sm">Confirmă noul email</button>
            <button type="button" @click="cancelEmail" :disabled="emailBusy" class="w-full text-sm text-gray-600 dark:text-gray-300 underline py-2">Anulează schimbarea emailului</button>
          </form>

          <form v-if="account.email && !account.emailVerified" @submit.prevent="verifyCurrentEmail" class="space-y-3">
            <p class="text-xs text-amber-800 dark:text-amber-300">Adresa curentă nu este încă verificată.</p>
            <input v-model="currentEmailCode" aria-label="Codul pentru adresa curentă" autocomplete="one-time-code" inputmode="numeric" maxlength="6" required pattern="[0-9]{6}" :class="inputCls" />
            <button :disabled="emailBusy" class="text-sm font-semibold text-indigo-700 dark:text-indigo-300">Verifică adresa curentă</button>
          </form>

          <MfaPanel @toast="(...args) => emit('toast', ...args)" @changed="mfaChanged" />
          <NotificationPanel :email="account.email ?? ''" :verified="account.emailVerified" @toast="(...args) => emit('toast', ...args)" />
          <FinanceIntegrationPanel @toast="(...args) => emit('toast', ...args)" />
          <SessionPanel :key="sessionRevision" @toast="(...args) => emit('toast', ...args)" @logout="emit('logout')" />

          <AccountDataPanel @toast="(...args) => emit('toast', ...args)" />

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
