import { onMounted, onUnmounted, nextTick } from 'vue'

export function useDialog(root, close) {
  let previousFocus, previousOverflow
  function onKey(event) {
    if (event.key === 'Escape') { event.preventDefault(); close(); return }
    if (event.key !== 'Tab') return
    const elements = [...(root.value?.querySelectorAll('button, a[href], input, select, textarea, [tabindex="0"]') ?? [])]
      .filter(element => !element.disabled && element.getClientRects().length > 0)
    if (!elements.length) { event.preventDefault(); root.value?.focus(); return }
    const first = elements[0], last = elements.at(-1)
    if (event.shiftKey && (document.activeElement === first || !root.value.contains(document.activeElement))) {
      event.preventDefault(); last.focus()
    } else if (!event.shiftKey && (document.activeElement === last || !root.value.contains(document.activeElement))) {
      event.preventDefault(); first.focus()
    }
  }
  onMounted(async () => {
    previousFocus = document.activeElement; previousOverflow = document.body.style.overflow
    document.body.style.overflow = 'hidden'
    document.addEventListener('keydown', onKey)
    await nextTick()
    root.value?.querySelector('button')?.focus()
  })
  onUnmounted(() => {
    document.removeEventListener('keydown', onKey)
    document.body.style.overflow = previousOverflow
    if (previousFocus?.isConnected) previousFocus.focus({ preventScroll: true })
  })
}
