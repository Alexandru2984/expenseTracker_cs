<script setup>
import { ref, computed } from 'vue'
import { authApi } from '../api.js'
import { useCooldown } from '../useCooldown.js'

const emit = defineEmits(['login'])

// 'login' | 'register' | 'verify' | 'forgot' | 'reset'
const mode = ref('login')

const username = ref('')
const email = ref('')
const password = ref('')
const code = ref('')
const newPassword = ref('')
const mfaChallenge = ref(''), mfaCode = ref(''), recoveryMode = ref(false)

const verifyUsername = ref('') // account awaiting email verification
const resetEmail = ref('')     // account awaiting password reset

const error = ref('')
const info = ref('')
const loading = ref(false)
const cooldown = useCooldown()
const retrySeconds = cooldown.remaining

const titles = {
  login: 'Conectează-te pentru a continua.',
  register: 'Creează un cont nou.',
  verify: 'Verifică-ți adresa de email.',
  forgot: 'Resetare parolă.',
  reset: 'Introdu codul și noua parolă.',
  mfa: 'Verificare în doi pași.'
}
const subtitle = computed(() => titles[mode.value])

function go(next) {
  if (loading.value) return
  if (mode.value === 'mfa' && mfaChallenge.value) authApi.cancelMfa({ challengeToken: mfaChallenge.value }).catch(() => {})
  error.value = ''
  info.value = ''
  mode.value = next
  if (next !== 'mfa') { mfaChallenge.value = ''; mfaCode.value = ''; recoveryMode.value = false }
}

async function submit() {
  if (loading.value) return
  error.value = ''
  info.value = ''
  loading.value = true
  try {
    if (mode.value === 'login') return await doLogin()
    if (mode.value === 'register') return await doRegister()
    if (mode.value === 'verify') return await doVerify()
    if (mode.value === 'forgot') return await doForgot()
    if (mode.value === 'reset') return await doReset()
    if (mode.value === 'mfa') {
      const response = await authApi.mfaLogin({ challengeToken: mfaChallenge.value, code: mfaCode.value.trim() })
      mfaCode.value = ''; mfaChallenge.value = ''
      emit('login', response.data)
    }
  } catch (e) {
    error.value = apiError(e)
    if (mode.value === 'mfa' && e.response?.data?.code === 'mfa_restart_required') {
      mode.value = 'login'; mfaChallenge.value = ''; mfaCode.value = ''; password.value = ''
    }
  } finally {
    loading.value = false
  }
}

function apiError(e) {
  return e.response?.data?.detail ?? e.response?.data?.title ?? 'A apărut o eroare. Încearcă din nou.'
}

async function doLogin() {
  if (!username.value.trim() || !password.value) {
    error.value = 'Completează username și parolă.'
    return
  }
  try {
    const res = await authApi.login({ username: username.value.trim(), password: password.value })
    if (res.data.requiresTwoFactor) {
      password.value = ''; mfaChallenge.value = res.data.challengeToken; mfaCode.value = ''
      recoveryMode.value = false; mode.value = 'mfa'
      info.value = 'Introdu codul curent din autentificator. Verificarea este valabilă cinci minute.'
      return
    }
    emit('login', res.data)
  } catch (e) {
    // Account exists but email not verified → jump to the verify screen.
    if (e.response?.status === 403 && e.response.data?.emailVerificationRequired) {
      verifyUsername.value = e.response.data.username ?? username.value.trim()
      code.value = ''
      info.value = 'Contul nu este verificat. Introdu codul primit pe email sau cere unul nou.'
      mode.value = 'verify'
      return
    }
    throw e
  }
}

async function doRegister() {
  if (!username.value.trim() || !email.value.trim() || !password.value) {
    error.value = 'Completează toate câmpurile.'
    return
  }
  if (password.value.length < 8) {
    error.value = 'Parola trebuie să aibă minim 8 caractere.'
    return
  }
  const res = await authApi.register({
    username: username.value.trim(),
    email: email.value.trim(),
    password: password.value
  })
  verifyUsername.value = res.data.username ?? username.value.trim()
  cooldown.start(res.data.retryAfterSeconds ?? 60)
  code.value = ''
  info.value = res.data.message ?? 'Ți-am trimis un cod de verificare pe email.'
  mode.value = 'verify'
}

async function doVerify() {
  if (code.value.trim().length !== 6) {
    error.value = 'Codul are 6 cifre.'
    return
  }
  const res = await authApi.verifyEmail({ username: verifyUsername.value, code: code.value.trim() })
  emit('login', res.data)
}

async function resend() {
  if (loading.value || retrySeconds.value > 0) return
  error.value = ''
  info.value = ''
  loading.value = true
  try {
    const response = await authApi.resendCode({ username: verifyUsername.value })
    cooldown.start(response.data.retryAfterSeconds ?? 60)
    info.value = response.data.message ?? 'Codurile pot fi trimise cel mult o dată pe minut.'
  } catch (e) {
    error.value = apiError(e)
  } finally {
    loading.value = false
  }
}

async function doForgot() {
  if (!email.value.trim()) {
    error.value = 'Introdu adresa de email.'
    return
  }
  const response = await authApi.forgotPassword({ email: email.value.trim() })
  cooldown.start(response.data.retryAfterSeconds ?? 60)
  resetEmail.value = email.value.trim()
  code.value = ''
  newPassword.value = ''
  info.value = response.data.message ?? 'Dacă există un cont cu acest email, codul poate fi trimis cel mult o dată pe minut.'
  mode.value = 'reset'
}

async function doReset() {
  if (code.value.trim().length !== 6) {
    error.value = 'Codul are 6 cifre.'
    return
  }
  if (newPassword.value.length < 8) {
    error.value = 'Parola trebuie să aibă minim 8 caractere.'
    return
  }
  await authApi.resetPassword({
    email: resetEmail.value,
    code: code.value.trim(),
    newPassword: newPassword.value
  })
  password.value = ''
  newPassword.value = ''
  info.value = 'Parola a fost resetată. Acum te poți conecta.'
  mode.value = 'login'
}

const submitLabel = computed(() => {
  if (loading.value) return 'Se procesează...'
  return {
    login: 'Conectare',
    register: 'Creează cont',
    verify: 'Verifică și intră',
    forgot: 'Trimite codul',
    reset: 'Resetează parola',
    mfa: 'Verifică MFA și intră'
  }[mode.value]
})
</script>

<template>
  <div class="min-h-screen bg-gray-50 dark:bg-gray-950 flex items-center justify-center p-4 transition-colors">
    <div class="bg-white dark:bg-gray-900 border border-gray-200 dark:border-gray-800 rounded-3xl p-6 sm:p-8 w-full max-w-sm shadow-xl">
      <div class="flex items-center gap-3 mb-6">
        <div class="w-10 h-10 bg-indigo-600 rounded-xl flex items-center justify-center text-sm font-bold text-white shadow-lg shadow-indigo-500/20">$</div>
        <h1 class="text-xl font-bold dark:text-white">Expense Tracker</h1>
      </div>

      <p class="text-sm text-gray-500 dark:text-gray-400 mb-6">{{ subtitle }}</p>

      <div v-if="error" role="alert" class="bg-red-500/10 border border-red-500/30 text-red-500 rounded-xl p-3 text-xs mb-4 font-medium">
        {{ error }}
      </div>
      <div v-if="info" role="status" class="bg-indigo-500/10 border border-indigo-500/30 text-indigo-600 dark:text-indigo-300 rounded-xl p-3 text-xs mb-4 font-medium">
        {{ info }}
      </div>

      <form @submit.prevent="submit" class="space-y-4">
        <div v-if="mode === 'mfa'" class="space-y-3">
          <label for="login-mfa-code" class="block text-xs font-bold text-gray-600 dark:text-gray-300">{{ recoveryMode ? 'Cod de recuperare' : 'Cod din autentificator' }}</label>
          <input id="login-mfa-code" v-model="mfaCode" :disabled="loading" required :maxlength="recoveryMode ? 30 : 6" :pattern="recoveryMode ? undefined : '[0-9]{6}'" :inputmode="recoveryMode ? 'text' : 'numeric'" autocomplete="one-time-code" autocapitalize="off" spellcheck="false" class="w-full min-w-0 bg-gray-50 dark:bg-gray-800 rounded-xl px-4 py-3 text-gray-900 dark:text-white text-center" />
          <button type="button" @click="recoveryMode = !recoveryMode; mfaCode = ''" :disabled="loading" class="text-xs underline text-indigo-700 dark:text-indigo-300">{{ recoveryMode ? 'Folosește aplicația de autentificare' : 'Folosește un cod de recuperare' }}</button>
          <p class="text-xs text-gray-600 dark:text-gray-300">Codurile de recuperare sunt de unică folosință. Dacă ai folosit deja codul din aplicație, așteaptă următorul cod.</p>
        </div>
        <!-- Username (login / register) -->
        <div v-if="mode === 'login' || mode === 'register'">
          <label for="login-username" class="block text-xs font-bold text-gray-400 uppercase mb-1.5 ml-1">Username</label>
          <input id="login-username" :disabled="loading" required v-model="username" maxlength="50" type="text" autocomplete="username" placeholder="Nume utilizator"
            class="w-full bg-gray-50 dark:bg-gray-800 border-none rounded-xl px-4 py-3 dark:text-white placeholder-gray-400 outline-none focus:ring-2 ring-indigo-500/20 transition" />
        </div>

        <!-- Email (register / forgot) -->
        <div v-if="mode === 'register' || mode === 'forgot'">
          <label for="login-email" class="block text-xs font-bold text-gray-400 uppercase mb-1.5 ml-1">Email</label>
          <input id="login-email" :disabled="loading" required v-model="email" maxlength="256" type="email" autocomplete="email" placeholder="adresa@email.com"
            class="w-full bg-gray-50 dark:bg-gray-800 border-none rounded-xl px-4 py-3 dark:text-white placeholder-gray-400 outline-none focus:ring-2 ring-indigo-500/20 transition" />
        </div>

        <!-- Account being verified / reset (read-only context) -->
        <div v-if="mode === 'verify'" class="text-xs text-gray-500 dark:text-gray-400 -mt-1">
          Cont: <span class="font-bold text-gray-700 dark:text-gray-200">{{ verifyUsername }}</span>
        </div>
        <div v-if="mode === 'reset'" class="text-xs text-gray-500 dark:text-gray-400 -mt-1">
          Email: <span class="font-bold text-gray-700 dark:text-gray-200">{{ resetEmail }}</span>
        </div>

        <!-- Code (verify / reset) -->
        <div v-if="mode === 'verify' || mode === 'reset'">
          <label for="login-code" class="block text-xs font-bold text-gray-400 uppercase mb-1.5 ml-1">Cod (6 cifre)</label>
          <input id="login-code" :disabled="loading" required v-model="code" inputmode="numeric" maxlength="6" pattern="[0-9]{6}" autocomplete="one-time-code" placeholder="••••••"
            class="w-full bg-gray-50 dark:bg-gray-800 border-none rounded-xl px-4 py-3 text-center text-lg tracking-[0.5em] dark:text-white placeholder-gray-400 outline-none focus:ring-2 ring-indigo-500/20 transition" />
        </div>

        <!-- Password (login / register) -->
        <div v-if="mode === 'login' || mode === 'register'">
          <label for="login-password" class="block text-xs font-bold text-gray-400 uppercase mb-1.5 ml-1">Parolă</label>
          <input id="login-password" :disabled="loading" required v-model="password" maxlength="128" type="password" :autocomplete="mode === 'register' ? 'new-password' : 'current-password'"
            :placeholder="mode === 'register' ? 'Minim 8 caractere' : 'Parola ta'"
            class="w-full bg-gray-50 dark:bg-gray-800 border-none rounded-xl px-4 py-3 dark:text-white placeholder-gray-400 outline-none focus:ring-2 ring-indigo-500/20 transition" />
        </div>

        <!-- New password (reset) -->
        <div v-if="mode === 'reset'">
          <label for="login-new-password" class="block text-xs font-bold text-gray-400 uppercase mb-1.5 ml-1">Parolă nouă</label>
          <input id="login-new-password" :disabled="loading" required v-model="newPassword" minlength="8" maxlength="100" type="password" autocomplete="new-password" placeholder="Minim 8 caractere"
            class="w-full bg-gray-50 dark:bg-gray-800 border-none rounded-xl px-4 py-3 dark:text-white placeholder-gray-400 outline-none focus:ring-2 ring-indigo-500/20 transition" />
        </div>

        <button type="submit" :disabled="loading"
          class="w-full bg-indigo-600 hover:bg-indigo-700 disabled:opacity-50 text-white font-bold py-3 rounded-xl shadow-lg shadow-indigo-500/20 transition-all active:scale-[0.98]">
          {{ submitLabel }}
        </button>
      </form>

      <!-- Contextual links -->
      <div class="mt-6 text-center space-y-2">
        <template v-if="mode === 'login'">
          <button @click="go('register')" class="block w-full text-xs font-bold text-indigo-700 dark:text-indigo-300 hover:text-indigo-800 dark:hover:text-indigo-200 transition uppercase tracking-wider">
            Prima dată? Creează un cont
          </button>
          <button @click="go('forgot')" class="block w-full text-xs text-gray-400 hover:text-gray-600 dark:hover:text-gray-300 transition">
            Ai uitat parola?
          </button>
        </template>

        <template v-else-if="mode === 'register'">
          <button @click="go('login')" class="text-xs font-bold text-indigo-700 dark:text-indigo-300 hover:text-indigo-800 dark:hover:text-indigo-200 transition uppercase tracking-wider">
            Ai deja cont? Conectează-te
          </button>
        </template>

        <template v-else-if="mode === 'verify'">
          <button @click="resend" :disabled="loading || retrySeconds > 0" class="block w-full text-xs font-bold text-indigo-700 dark:text-indigo-300 hover:text-indigo-800 dark:hover:text-indigo-200 transition uppercase tracking-wider disabled:opacity-50">
            {{ retrySeconds > 0 ? `Retrimite codul în ${retrySeconds}s` : 'Retrimite codul' }}
          </button>
          <button @click="go('login')" class="block w-full text-xs text-gray-400 hover:text-gray-600 dark:hover:text-gray-300 transition">
            Înapoi la conectare
          </button>
        </template>

        <template v-else>
          <button @click="go('login')" class="text-xs text-gray-400 hover:text-gray-600 dark:hover:text-gray-300 transition">
            Înapoi la conectare
          </button>
        </template>
      </div>
    </div>
  </div>
</template>
