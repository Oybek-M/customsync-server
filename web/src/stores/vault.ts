import { defineStore } from 'pinia'
import { ref } from 'vue'
import { CryptoWorkerClient } from '../crypto/worker-api'

export const useVaultStore = defineStore('vault', () => {
  const locked = ref(true)
  const fingerprint = ref<string | null>(null)
  const showUnlockDialog = ref(false)
  let customWorkerClient: CryptoWorkerClient | null = null

  function getWorker(): CryptoWorkerClient {
    if (customWorkerClient) {
      return customWorkerClient
    }
    customWorkerClient = new CryptoWorkerClient()
    return customWorkerClient
  }

  function setWorkerClient(client: CryptoWorkerClient | null) {
    customWorkerClient = client
  }

  function setLocked(value: boolean) {
    locked.value = value
    if (value) {
      fingerprint.value = null
    }
  }

  function unlock(fp?: string) {
    locked.value = false
    if (fp) {
      fingerprint.value = fp
    }
    showUnlockDialog.value = false
  }

  function lock() {
    locked.value = true
    fingerprint.value = null
    if (customWorkerClient) {
      customWorkerClient.lock().catch(() => {})
    }
  }

  function openUnlockDialog() {
    showUnlockDialog.value = true
  }

  function closeUnlockDialog() {
    showUnlockDialog.value = false
  }

  return {
    locked,
    fingerprint,
    showUnlockDialog,
    getWorker,
    setWorkerClient,
    setLocked,
    unlock,
    lock,
    openUnlockDialog,
    closeUnlockDialog,
  }
})
