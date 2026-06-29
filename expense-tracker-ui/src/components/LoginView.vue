<script setup>
import { ref, computed } from 'vue'
import { authApi } from '../api.js'

const emit = defineEmits(['login'])

// 'login' | 'register' | 'verify' | 'forgot' | 'reset'
const mode = ref('login')

const username = ref('')
const email = ref('')
const password = ref('')
const code = ref('')
const newPassword = ref('')

const verifyUsername = ref('') // account awaiting email verification
const resetEmail = ref('')     // account awaiting password reset

const error = ref('')
const info = ref('')
const loading = ref(false)

const titles = {
  login: 'Conectează-te pentru a continua.',
  register: 'Creează un cont nou.',
  verify: 'Verifică-ți adresa de email.',
  forgot: 'Resetare parolă.',
  reset: 'Introdu codul și noua parolă.'
}
const subtitle = computed(() => titles[mode.value])

function go(next) {
  error.value = ''
  info.value = ''
  mode.value = next
}

async function submit() {
  error.value = ''
  info.value = ''
  loading.value = true
  try {
    if (mode.value === 'login') return await doLogin()
    if (mode.value === 'register') return await doRegister()
    if (mode.value === 'verify') return await doVerify()
    if (mode.value === 'forgot') return await doForgot()
    if (mode.value === 'reset') return await doReset()
  } catch (e) {
    error.value = apiError(e)
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
  error.value = ''
  info.value = ''
  loading.value = true
  try {
    await authApi.resendCode({ username: verifyUsername.value })
    info.value = 'Ți-am trimis un cod nou (dacă există un cont neverificat).'
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
  await authApi.forgotPassword({ email: email.value.trim() })
  resetEmail.value = email.value.trim()
  code.value = ''
  newPassword.value = ''
  info.value = 'Dacă există un cont cu acest email, ți-am trimis un cod de resetare.'
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
    reset: 'Resetează parola'
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

      <div v-if="error" class="bg-red-500/10 border border-red-500/30 text-red-500 rounded-xl p-3 text-xs mb-4 font-medium">
        {{ error }}
      </div>
      <div v-if="info" class="bg-indigo-500/10 border border-indigo-500/30 text-indigo-600 dark:text-indigo-300 rounded-xl p-3 text-xs mb-4 font-medium">
        {{ info }}
      </div>

      <form @submit.prevent="submit" class="space-y-4">
        <!-- Username (login / register) -->
        <div v-if="mode === 'login' || mode === 'register'">
          <label class="block text-xs font-bold text-gray-400 uppercase mb-1.5 ml-1">Username</label>
          <input v-model="username" type="text" autocomplete="username" placeholder="Nume utilizator"
            class="w-full bg-gray-50 dark:bg-gray-800 border-none rounded-xl px-4 py-3 dark:text-white placeholder-gray-400 outline-none focus:ring-2 ring-indigo-500/20 transition" />
        </div>

        <!-- Email (register / forgot) -->
        <div v-if="mode === 'register' || mode === 'forgot'">
          <label class="block text-xs font-bold text-gray-400 uppercase mb-1.5 ml-1">Email</label>
          <input v-model="email" type="email" autocomplete="email" placeholder="adresa@email.com"
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
          <label class="block text-xs font-bold text-gray-400 uppercase mb-1.5 ml-1">Cod (6 cifre)</label>
          <input v-model="code" inputmode="numeric" maxlength="6" autocomplete="one-time-code" placeholder="••••••"
            class="w-full bg-gray-50 dark:bg-gray-800 border-none rounded-xl px-4 py-3 text-center text-lg tracking-[0.5em] dark:text-white placeholder-gray-400 outline-none focus:ring-2 ring-indigo-500/20 transition" />
        </div>

        <!-- Password (login / register) -->
        <div v-if="mode === 'login' || mode === 'register'">
          <label class="block text-xs font-bold text-gray-400 uppercase mb-1.5 ml-1">Parolă</label>
          <input v-model="password" type="password" :autocomplete="mode === 'register' ? 'new-password' : 'current-password'"
            :placeholder="mode === 'register' ? 'Minim 8 caractere' : 'Parola ta'"
            class="w-full bg-gray-50 dark:bg-gray-800 border-none rounded-xl px-4 py-3 dark:text-white placeholder-gray-400 outline-none focus:ring-2 ring-indigo-500/20 transition" />
        </div>

        <!-- New password (reset) -->
        <div v-if="mode === 'reset'">
          <label class="block text-xs font-bold text-gray-400 uppercase mb-1.5 ml-1">Parolă nouă</label>
          <input v-model="newPassword" type="password" autocomplete="new-password" placeholder="Minim 8 caractere"
            class="w-full bg-gray-50 dark:bg-gray-800 border-none rounded-xl px-4 py-3 dark:text-white placeholder-gray-400 outline-none focus:ring-2 ring-indigo-500/20 transition" />
        </div>

        <button type="submit" :disabled="loading"
          class="w-full bg-indigo-600 hover:bg-indigo-500 disabled:opacity-50 text-white font-bold py-3 rounded-xl shadow-lg shadow-indigo-500/20 transition-all active:scale-[0.98]">
          {{ submitLabel }}
        </button>
      </form>

      <!-- Contextual links -->
      <div class="mt-6 text-center space-y-2">
        <template v-if="mode === 'login'">
          <button @click="go('register')" class="block w-full text-xs font-bold text-indigo-500 hover:text-indigo-400 transition uppercase tracking-wider">
            Prima dată? Creează un cont
          </button>
          <button @click="go('forgot')" class="block w-full text-xs text-gray-400 hover:text-gray-600 dark:hover:text-gray-300 transition">
            Ai uitat parola?
          </button>
        </template>

        <template v-else-if="mode === 'register'">
          <button @click="go('login')" class="text-xs font-bold text-indigo-500 hover:text-indigo-400 transition uppercase tracking-wider">
            Ai deja cont? Conectează-te
          </button>
        </template>

        <template v-else-if="mode === 'verify'">
          <button @click="resend" :disabled="loading" class="block w-full text-xs font-bold text-indigo-500 hover:text-indigo-400 transition uppercase tracking-wider disabled:opacity-50">
            Retrimite codul
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
