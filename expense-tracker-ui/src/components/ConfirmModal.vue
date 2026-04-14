<script setup>
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
</script>

<template>
  <Teleport to="body">
    <div
      class="fixed inset-0 z-50 flex items-center justify-center p-4 bg-black/60 backdrop-blur-sm"
      @mousedown.self="emit('cancel')"
    >
      <div class="bg-gray-900 border border-gray-800 rounded-2xl p-6 w-full max-w-sm shadow-2xl">
        <p class="text-white text-base mb-6">{{ message }}</p>
        <div class="flex gap-3">
          <button
            @click="emit('cancel')"
            class="flex-1 bg-gray-700 hover:bg-gray-600 text-white font-medium py-2 rounded-lg transition"
          >
            Anulează
          </button>
          <button
            @click="emit('confirm')"
            class="flex-1 bg-red-600 hover:bg-red-500 text-white font-medium py-2 rounded-lg transition"
          >
            {{ confirmLabel }}
          </button>
        </div>
      </div>
    </div>
  </Teleport>
</template>
