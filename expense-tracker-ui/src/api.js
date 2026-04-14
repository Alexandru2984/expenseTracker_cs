import axios from 'axios'

const api = axios.create({
  baseURL: import.meta.env.VITE_API_URL ?? '/api'
})

// Attach Bearer token from localStorage on every request
api.interceptors.request.use(config => {
  const token = localStorage.getItem('api_token')
  if (token) config.headers['Authorization'] = `Bearer ${token}`
  return config
})

// On 401, clear stored token and notify app to show login screen
api.interceptors.response.use(
  res => res,
  err => {
    if (err.response?.status === 401) {
      localStorage.removeItem('api_token')
      window.dispatchEvent(new Event('auth:logout'))
    }
    return Promise.reject(err)
  }
)

export const subscriptionsApi = {
  getAll: (skip = 0, take = 200) => api.get(`/subscriptions?skip=${skip}&take=${take}`),
  getById: (id) => api.get(`/subscriptions/${id}`),
  create: (data) => api.post('/subscriptions', data),
  update: (id, data) => api.put(`/subscriptions/${id}`, data),
  remove: (id) => api.delete(`/subscriptions/${id}`),
  getSummary: () => api.get('/subscriptions/summary')
}

