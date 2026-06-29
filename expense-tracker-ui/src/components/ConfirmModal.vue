<script setup>
import { onMounted, onUnmounted, ref, nextTick } from 'vue'

defineProps({
  message: {
    type: String,
    default: 'Ești sigur?'
  },
  confirmLabel: {
    type: String,
    default: 'Confirmă'
  }
})

const emit = defineEmits(['confirm', 'cancel'])
const confirmBtn = ref(null)

function onKey(e) {
  if (e.key === 'Escape') emit('cancel')
  if (e.key === 'Enter') emit('confirm')
}

onMounted(async () => {
  window.addEventListener('keydown', onKey)
  await nextTick()
  confirmBtn.value?.focus()
})
onUnmounted(() => window.removeEventListener('keydown', onKey))
</script>

<template>
  <Teleport to="body">
    <div
      class="fixed inset-0 z-50 flex items-center justify-center p-4 bg-black/60 backdrop-blur-sm"
      role="dialog"
      aria-modal="true"
      @mousedown.self="emit('cancel')"
    >
      <div class="bg-white dark:bg-gray-900 border border-gray-200 dark:border-gray-800 rounded-2xl p-6 w-full max-w-sm shadow-2xl">
        <p class="text-gray-900 dark:text-white text-base mb-6">{{ message }}</p>
        <div class="flex gap-3">
          <button
            @click="emit('cancel')"
            class="flex-1 bg-gray-100 dark:bg-gray-700 hover:bg-gray-200 dark:hover:bg-gray-600 text-gray-700 dark:text-white font-medium py-2 rounded-lg transition focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-gray-400"
          >
            Anulează
          </button>
          <button
            ref="confirmBtn"
            @click="emit('confirm')"
            class="flex-1 bg-red-600 hover:bg-red-500 text-white font-medium py-2 rounded-lg transition focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-red-400"
          >
            {{ confirmLabel }}
          </button>
        </div>
      </div>
    </div>
  </Teleport>
</template>
