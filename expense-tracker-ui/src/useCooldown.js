import { computed, onMounted, onUnmounted, ref } from 'vue'

export function useCooldown() {
  const now = ref(Date.now()), deadline = ref(0)
  let timer
  onMounted(() => { timer = setInterval(() => { now.value = Date.now() }, 1000) })
  onUnmounted(() => clearInterval(timer))
  const remaining = computed(() => Math.max(0, Math.ceil((deadline.value - now.value) / 1000)))
  function start(seconds) {
    const value = Number(seconds)
    now.value = Date.now()
    deadline.value = now.value + (Number.isFinite(value) ? Math.min(600, Math.max(0, value)) : 0) * 1000
  }
  return { remaining, start, now }
}
