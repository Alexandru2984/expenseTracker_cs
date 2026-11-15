import axios from 'axios'

const api = axios.create({
  baseURL: import.meta.env.VITE_API_URL ?? '/api',
  timeout: 15_000,
  withCredentials: true // send/receive the httpOnly auth cookies
})

// Read a cookie value by name (used for the CSRF double-submit token).
function getCookie(name) {
  const escaped = name.replace(/([.*+?^${}()|[\]\\])/g, '\\$1')
  const match = document.cookie.match(new RegExp('(?:^|; )' + escaped + '=([^;]*)'))
  return match ? decodeURIComponent(match[1]) : null
}

// Attach the CSRF token header on state-changing requests.
api.interceptors.request.use(config => {
  const method = (config.method ?? 'get').toLowerCase()
  if (['post', 'put', 'patch', 'delete'].includes(method)) {
    const csrf = getCookie('csrf_token')
    if (csrf) config.headers['X-CSRF-Token'] = csrf
  }
  return config
})

// Single in-flight refresh shared by all concurrent 401s.
let refreshPromise = null
function refreshSession() {
  if (!refreshPromise) {
    const rotate = async () => {
      // A different tab may already have refreshed the shared cookies while we
      // waited for the lock. Recheck before rotating a single-use token again.
      try { await api.get('/auth/me', { _skipRefresh: true }); return }
      catch (error) { if (error.response?.status !== 401) throw error }
      await api.post('/auth/refresh')
    }
    const operation = navigator.locks?.request
      ? navigator.locks.request('expense-session-refresh', rotate)
      : api.post('/auth/refresh')
    refreshPromise = operation.finally(() => { refreshPromise = null })
  }
  return refreshPromise
}

// On 401 for a normal call, try one silent refresh then replay the request.
api.interceptors.response.use(
  res => res,
  async err => {
    const original = err.config
    const status = err.response?.status
    const url = original?.url ?? ''
    const isAuthCall = /\/auth\/(login|register|refresh|logout|verify-email|resend-code|forgot-password|reset-password)/.test(url)

    if (status === 401 && original && !original._retry && !original._skipRefresh && !isAuthCall) {
      original._retry = true
      try {
        await refreshSession()
        return api(original)
      } catch (e) {
        window.dispatchEvent(new Event('auth:logout'))
        return Promise.reject(e)
      }
    }

    if (status === 401 && !isAuthCall && !original?._skipRefresh) {
      window.dispatchEvent(new Event('auth:logout'))
    }
    return Promise.reject(err)
  }
)

export const authApi = {
  login: (data) => api.post('/auth/login', data),
  register: (data) => api.post('/auth/register', data),
  verifyEmail: (data) => api.post('/auth/verify-email', data),
  resendCode: (data) => api.post('/auth/resend-code', data),
  forgotPassword: (data) => api.post('/auth/forgot-password', data),
  resetPassword: (data) => api.post('/auth/reset-password', data),
  logout: () => api.post('/auth/logout'),
  me: () => api.get('/auth/me')
}

export const accountApi = {
  get: () => api.get('/account'),
  changePassword: (data) => api.post('/account/change-password', data),
  changeEmail: (data) => api.post('/account/change-email', data),
  confirmEmailChange: (data) => api.post('/account/confirm-email-change', data),
  cancelEmailChange: () => api.delete('/account/pending-email'),
  logoutAll: () => api.post('/account/logout-all'),
  sessions: () => api.get('/account/sessions'),
  revokeSession: (id) => api.delete(`/account/sessions/${encodeURIComponent(id)}`)
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
  setStatus: (id, isActive) => api.post(`/subscriptions/${encodeURIComponent(id)}/status`, { isActive }),
  exportCalendar: () => api.get('/subscriptions/calendar', { responseType: 'blob' }),
  exportCsv: () => api.get('/subscriptions/export', { responseType: 'blob' }),
  importCsv: (formData) => api.post('/subscriptions/import', formData, {
    headers: { 'Content-Type': 'multipart/form-data' }
  })
}

export default api
