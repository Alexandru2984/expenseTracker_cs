// Only random transition markers are shared. Tokens and identity data stay out
// of localStorage and BroadcastChannel.
const storageKey = 'expense.auth.boundary.v1'
const channelName = 'expense-auth-boundary-v1'
const lockName = 'expense-session-refresh'
const valid = value => value && /^[a-f0-9-]{36}$/i.test(value.id) && ['changing', 'ready'].includes(value.phase)
function read() {
  try { const value = JSON.parse(localStorage.getItem(storageKey)); return valid(value) ? value : null } catch { return null }
}
const signature = value => value ? `${value.id}:${value.phase}` : ''
let seen = signature(read()), channel = null, localTail = Promise.resolve()
try { if (typeof BroadcastChannel !== 'undefined') channel = new BroadcastChannel(channelName) } catch { /* Storage events remain available. */ }

function receive(value) {
  if (!valid(value) || signature(value) === seen) return false
  seen = signature(value)
  window.dispatchEvent(new CustomEvent('auth:boundary', { detail: { phase: value.phase } }))
  return true
}
export function syncSessionBoundary() { return receive(read()) }
export function publishSessionBoundary(phase, id = crypto.randomUUID()) {
  const value = { id, phase }
  seen = signature(value)
  try { localStorage.setItem(storageKey, JSON.stringify(value)) } catch { /* No credential fallback to storage. */ }
  try { channel?.postMessage(value) } catch { /* Resume checks also compare storage. */ }
  return id
}
export function recoverSessionBoundary() {
  // Called while holding the session lock. A previous writer may have closed
  // without publishing ready; the released lock permits authoritative discovery.
  const value = read()
  if (value?.phase === 'changing') publishSessionBoundary('ready', value.id)
}
export function notifyAccountContextChanged() {
  publishSessionBoundary('ready')
  window.dispatchEvent(new CustomEvent('auth:boundary', { detail: { phase: 'ready' } }))
}
export function withSessionLock(action, mode = 'exclusive') {
  if (navigator.locks?.request) return navigator.locks.request(lockName, { mode }, action)
  const operation = localTail.then(action, action)
  localTail = operation.catch(() => {})
  return operation
}
if (channel) channel.onmessage = event => {
  // Storage is authoritative when available; it prevents a delayed message
  // from rolling the observed marker back to an earlier transition.
  receive(read() ?? event.data)
}
window.addEventListener('storage', event => { if (event.key === storageKey) syncSessionBoundary() })
window.addEventListener('focus', syncSessionBoundary)
window.addEventListener('pageshow', syncSessionBoundary)
document.addEventListener('visibilitychange', () => { if (document.visibilityState === 'visible') syncSessionBoundary() })
