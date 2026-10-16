import { defineStore } from 'pinia'
import { ref, computed } from 'vue'
import {
  getStoredDeviceId,
  getStoredRefreshToken,
  setCredentials as clientSetCredentials,
  clearCredentials as clientClearCredentials,
  enroll as clientEnroll,
  onAuthChange,
} from '../api/client'
import { getActiveRouter } from '../router-instance'

export const useAuthStore = defineStore('auth', () => {
  const deviceId = ref<string | null>(getStoredDeviceId())
  const refreshToken = ref<string | null>(getStoredRefreshToken())

  const isEnrolled = computed(() => Boolean(deviceId.value && refreshToken.value))

  // client.ts dagi avtomatik o'zgarishlar (masalan, 401 refresh) bilan sinxronlash
  onAuthChange((enrolled) => {
    deviceId.value = getStoredDeviceId()
    refreshToken.value = getStoredRefreshToken()
    if (!enrolled) {
      const r = getActiveRouter()
      if (r && r.currentRoute && r.currentRoute.value.path !== '/enroll') {
        r.push('/enroll').catch(() => {})
      }
    }
  })

  function setCredentials(newDeviceId: string, newRefreshToken: string) {
    deviceId.value = newDeviceId
    refreshToken.value = newRefreshToken
    clientSetCredentials(newDeviceId, newRefreshToken)
  }

  function clearCredentials() {
    deviceId.value = null
    refreshToken.value = null
    clientClearCredentials()
    const r = getActiveRouter()
    if (r && r.currentRoute && r.currentRoute.value.path !== '/enroll') {
      r.push('/enroll').catch(() => {})
    }
  }

  async function enroll(code: string, name = 'Web panel') {
    await clientEnroll(code, name)
    deviceId.value = getStoredDeviceId()
    refreshToken.value = getStoredRefreshToken()
  }

  return {
    deviceId,
    refreshToken,
    isEnrolled,
    setCredentials,
    clearCredentials,
    enroll,
  }
})
