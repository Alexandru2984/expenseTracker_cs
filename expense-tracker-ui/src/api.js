import axios from 'axios'

const api = axios.create({
  baseURL: import.meta.env.VITE_API_URL ?? '/api',
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
    refreshPromise = api.post('/auth/refresh').finally(() => { refreshPromise = null })
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
    const isAuthCall = /\/auth\/(login|register|refresh|logout)/.test(url)

    if (status === 401 && original && !original._retry && !isAuthCall) {
      original._retry = true
      try {
        await refreshSession()
        return api(original)
      } catch (e) {
        window.dispatchEvent(new Event('auth:logout'))
        return Promise.reject(e)
      }
    }

    if (status === 401 && !isAuthCall) {
      window.dispatchEvent(new Event('auth:logout'))
    }
    return Promise.reject(err)
  }
)

export const authApi = {
  login: (data) => api.post('/auth/login', data),
  register: (data) => api.post('/auth/register', data),
  logout: () => api.post('/auth/logout'),
  me: () => api.get('/auth/me')
}

export const subscriptionsApi = {
  getAll: (params = {}) => api.get('/subscriptions', { params }),
  getById: (id) => api.get(`/subscriptions/${id}`),
  create: (data) => api.post('/subscriptions', data),
  update: (id, data) => api.put(`/subscriptions/${id}`, data),
  remove: (id) => api.delete(`/subscriptions/${id}`),
  getSummary: () => api.get('/subscriptions/summary'),
  getRates: () => api.get('/subscriptions/rates'),
  exportCsv: () => api.get('/subscriptions/export', { responseType: 'blob' })
}

export default api
