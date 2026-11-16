import axios from 'axios'
import { withSessionLock, syncSessionBoundary, publishSessionBoundary, recoverSessionBoundary, notifyAccountContextChanged } from './sessionCoordinator.js'

const api = axios.create({
  baseURL: import.meta.env.VITE_API_URL ?? '/api',
  timeout: 15_000,
  withCredentials: true // send/receive the httpOnly auth cookies
})

let sessionGeneration = 0
let accountContext = null
const validContext = value => typeof value === 'string' && /^[A-F0-9]{64}$/.test(value)
export function advanceApiSession(context = null) {
  if (context !== null && !validContext(context)) throw new Error('Identitatea sesiunii nu a putut fi verificată. Reîncarcă pagina.')
  sessionGeneration++; accountContext = context
}
window.addEventListener('auth:boundary', () => advanceApiSession())
function requireSession(generation) {
  if (generation !== sessionGeneration) throw new axios.CanceledError('Contextul de autentificare s-a schimbat.')
}

// Read a cookie value by name (used for the CSRF double-submit token).
function getCookie(name) {
  const escaped = name.replace(/([.*+?^${}()|[\]\\])/g, '\\$1')
  const match = document.cookie.match(new RegExp('(?:^|; )' + escaped + '=([^;]*)'))
  return match ? decodeURIComponent(match[1]) : null
}

// Attach the CSRF token header on state-changing requests.
api.interceptors.request.use(config => {
  config._sessionGeneration ??= sessionGeneration
  syncSessionBoundary()
  requireSession(config._sessionGeneration)
  const url = config.url ?? ''
  const business = /^\/(account|subscriptions|budgets|goals)(\/|$)/.test(url) && !/^\/subscriptions\/rates(?:\/|$)/.test(url)
  if (business && !accountContext) throw new axios.CanceledError('Verifică mai întâi identitatea contului.')
  if (accountContext && (business || /^\/auth\/(refresh|logout)$/.test(url))) config.headers['X-Account-Context'] = accountContext
  const method = (config.method ?? 'get').toLowerCase()
  if (['post', 'put', 'patch', 'delete'].includes(method)) {
    const csrf = getCookie('csrf_token')
    if (csrf) config.headers['X-CSRF-Token'] = csrf
  }
  return config
})

// Single in-flight refresh shared by all concurrent 401s.
let refreshState = null
function refreshSession(generation) {
  requireSession(generation)
  if (!refreshState || refreshState.generation !== generation) {
    const rotate = async () => {
      syncSessionBoundary()
      requireSession(generation)
      // A different tab may already have refreshed the shared cookies while we
      // waited for the lock. Recheck before rotating a single-use token again.
      try {
        const response = await api.get('/auth/me', { _skipRefresh: true })
        if (accountContext && response.data.accountContext !== accountContext) { notifyAccountContextChanged(); throw new axios.CanceledError() }
        return
      }
      catch (error) { if (error.response?.status !== 401) throw error }
      requireSession(generation)
      await api.post('/auth/refresh')
    }
    const operation = withSessionLock(rotate)
    const state = { generation, promise: null }
    state.promise = operation.finally(() => { if (refreshState === state) refreshState = null })
    refreshState = state
  }
  return refreshState.promise
}

// On 401 for a normal call, try one silent refresh then replay the request.
api.interceptors.response.use(
  res => { syncSessionBoundary(); requireSession(res.config._sessionGeneration); return res },
  async err => {
    const original = err.config
    syncSessionBoundary()
    if (original) requireSession(original._sessionGeneration)
    const status = err.response?.status
    let problem = err.response?.data
    if (status === 409 && problem instanceof Blob && problem.size <= 8192) {
      try { problem = JSON.parse(await problem.text()) } catch { /* Not a context problem. */ }
      if (original) requireSession(original._sessionGeneration)
    }
    if (status === 409 && problem?.code === 'account_context_changed') {
      notifyAccountContextChanged()
      return Promise.reject(new axios.CanceledError('Contul activ s-a schimbat.'))
    }
    const url = original?.url ?? ''
    const isAuthCall = /\/auth\/(login|register|refresh|logout|verify-email|resend-code|forgot-password|reset-password)/.test(url)

    if (status === 401 && original && !original._retry && !original._skipRefresh && !isAuthCall) {
      original._retry = true
      try {
        await refreshSession(original._sessionGeneration)
        requireSession(original._sessionGeneration)
        return api(original)
      } catch (e) {
        if (original._sessionGeneration === sessionGeneration && !axios.isCancel(e)) window.dispatchEvent(new Event('auth:logout'))
        return Promise.reject(e)
      }
    }

    if (status === 401 && !isAuthCall && !original?._skipRefresh) {
      window.dispatchEvent(new Event('auth:logout'))
    }
    return Promise.reject(err)
  }
)

async function discoverIdentity() {
  const read = () => withSessionLock(() => {
    recoverSessionBoundary()
    return api.get('/auth/me', { _skipRefresh: true })
  }, 'shared')
  try { return await read() }
  catch (error) {
    if (error.response?.status !== 401) throw error
    await refreshSession(sessionGeneration)
    return read()
  }
}

async function cookieMutation(method, url, data, { identity = false, clear = false } = {}) {
  const generation = sessionGeneration, expectedContext = accountContext
  syncSessionBoundary(); requireSession(generation)
  return withSessionLock(async () => {
    syncSessionBoundary(); requireSession(generation)
    if (identity) {
      if (!expectedContext) throw new axios.CanceledError('Identitate absentă.')
      let current
      try { current = await api.get('/auth/me', { _skipRefresh: true }) }
      catch (error) {
        if (error.response?.status !== 401) throw error
        await api.post('/auth/refresh')
        current = await api.get('/auth/me', { _skipRefresh: true })
      }
      if (current.data.accountContext !== expectedContext) { notifyAccountContextChanged(); throw new axios.CanceledError() }
    }
    requireSession(generation)
    const transition = publishSessionBoundary('changing')
    sessionGeneration++ // Cancel pending requests while retaining this operation's binding.
    try {
      const response = await api.request({ method, url, data, _skipRefresh: true })
      if (response.data?.accountContext) {
        if (!validContext(response.data.accountContext)) throw new Error('Răspuns de autentificare invalid.')
        accountContext = response.data.accountContext
      }
      if (clear) accountContext = null
      return response
    } finally { publishSessionBoundary('ready', transition) }
  }).catch(error => {
    if (identity && error.response?.status === 401 && error.config?._sessionGeneration === sessionGeneration) {
      window.dispatchEvent(new Event('auth:logout'))
    }
    throw error
  })
}

export const authApi = {
  login: (data) => cookieMutation('post', '/auth/login', data),
  register: (data) => api.post('/auth/register', data),
  verifyEmail: (data) => cookieMutation('post', '/auth/verify-email', data),
  resendCode: (data) => api.post('/auth/resend-code', data),
  forgotPassword: (data) => api.post('/auth/forgot-password', data),
  resetPassword: (data) => cookieMutation('post', '/auth/reset-password', data),
  logout: () => cookieMutation('post', '/auth/logout', undefined, { clear: true }),
  me: discoverIdentity
}

export const accountApi = {
  get: () => api.get('/account'),
  changePassword: (data) => cookieMutation('post', '/account/change-password', data, { identity: true }),
  changeEmail: (data) => api.post('/account/change-email', data),
  confirmEmailChange: (data) => cookieMutation('post', '/account/confirm-email-change', data, { identity: true }),
  cancelEmailChange: () => api.delete('/account/pending-email'),
  logoutAll: () => cookieMutation('post', '/account/logout-all', undefined, { identity: true, clear: true }),
  sessions: () => api.get('/account/sessions'),
  revokeSession: (id) => cookieMutation('delete', `/account/sessions/${encodeURIComponent(id)}`, undefined, { identity: true })
}

export const financeIntegrationApi = {
  list: () => api.get('/account/finance-tokens'),
  create: (data) => api.post('/account/finance-tokens', data),
  revoke: (id) => api.delete(`/account/finance-tokens/${encodeURIComponent(id)}`)
}

export const budgetsApi = {
  list: () => api.get('/budgets'),
  create: (data) => api.post('/budgets', data),
  update: (id, data) => api.put(`/budgets/${encodeURIComponent(id)}`, data),
  remove: (id, version) => api.delete(`/budgets/${encodeURIComponent(id)}`, { params: { version } })
}

export const goalsApi = {
  list: () => api.get('/goals'),
  create: data => api.post('/goals', data),
  update: (id, data) => api.put('/goals/' + encodeURIComponent(id), data),
  remove: (id, version) => api.delete('/goals/' + encodeURIComponent(id), { params: { version } }),
  history: (id, params = {}) => api.get('/goals/' + encodeURIComponent(id) + '/contributions', { params }),
  contribute: (id, data) => api.post('/goals/' + encodeURIComponent(id) + '/contributions', data),
  removeContribution: (id, entryId, version) => api.delete('/goals/' + encodeURIComponent(id) + '/contributions/' + encodeURIComponent(entryId), { params: { version } })
}

export const subscriptionsApi = {
  getAll: (params = {}) => api.get('/subscriptions', { params }),
  getById: (id) => api.get(`/subscriptions/${id}`),
  priceHistory: (id, params = {}, signal) => api.get(`/subscriptions/${encodeURIComponent(id)}/price-history`, { params, signal }),
  create: (data) => api.post('/subscriptions', data),
  update: (id, data) => api.put(`/subscriptions/${id}`, data),
  remove: (id) => api.delete(`/subscriptions/${id}`),
  getSummary: () => api.get('/subscriptions/summary'),
  getRates: () => api.get('/subscriptions/rates'),
  getRatesStatus: () => api.get('/subscriptions/rates/status'),
  getInsights: () => api.get('/subscriptions/insights'),
  duplicates: (params = {}, signal) => api.get('/subscriptions/duplicates', { params, signal }),
  setStatus: (id, isActive) => api.post(`/subscriptions/${encodeURIComponent(id)}/status`, { isActive }),
  exportCalendar: () => api.get('/subscriptions/calendar', { responseType: 'blob' }),
  exportCsv: (params = {}) => api.get('/subscriptions/export', { params, responseType: 'blob' }),
  importCsv: (formData) => api.post('/subscriptions/import', formData, {
    headers: { 'Content-Type': 'multipart/form-data' }
  })
}

export default api
