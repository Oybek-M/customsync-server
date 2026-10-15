import { defineStore } from 'pinia'
import { ref } from 'vue'

export const useVaultStore = defineStore('vault', () => {
  const locked = ref(true)

  function setLocked(value: boolean) {
    locked.value = value
  }

  function unlock() {
    locked.value = false
  }

  function lock() {
    locked.value = true
  }

  return {
    locked,
    setLocked,
    unlock,
    lock,
  }
})
