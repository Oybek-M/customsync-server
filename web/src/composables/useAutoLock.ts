import { onMounted, onUnmounted, watch } from 'vue'
import { useVaultStore } from '../stores/vault'
import { getSettings } from '../api/settings'

export function useAutoLock() {
  const vaultStore = useVaultStore()
  let timer: any = null
  let timeoutMinutes = 15 // Sukut bo'yicha 15 daqiqa
  let lastActivity = 0

  function stopTimer() {
    if (timer) {
      clearTimeout(timer)
      timer = null
    }
  }

  function startTimer() {
    stopTimer()
    if (vaultStore.locked) return

    const ms = timeoutMinutes * 60 * 1000
    timer = setTimeout(() => {
      // Vaqt tugaganda omborni qulflaymiz
      vaultStore.lock()
    }, ms)
  }

  function onActivity() {
    const now = Date.now()
    // Har bir harakatda emas, 500ms drossellash (throttling) bilan yangilanadi
    if (now - lastActivity < 500) return
    lastActivity = now

    if (!vaultStore.locked) {
      startTimer()
    }
  }

  // web.autolock_minutes sozlamasini bir marta o'qish
  async function initSettings() {
    try {
      const settings = await getSettings()
      const found = settings.find((s) => s.key === 'web.autolock_minutes')
      if (found) {
        const val = parseInt(found.value, 10)
        if (!isNaN(val) && val > 0) {
          timeoutMinutes = val
        }
      }
    } catch {
      // web.autolock_minutes o'qishda xatolik (masalan 403) bo'lsa, sukut bo'yicha 15 daqiqa olinadi
    }

    if (!vaultStore.locked) {
      startTimer()
    }
  }

  const events = ['pointerdown', 'keydown', 'wheel', 'touchstart']

  onMounted(() => {
    initSettings()

    if (typeof window !== 'undefined') {
      for (const ev of events) {
        window.addEventListener(ev, onActivity, { passive: true })
      }
    }
  })

  watch(
    () => vaultStore.locked,
    (isLocked) => {
      if (isLocked) {
        stopTimer()
      } else {
        startTimer()
      }
    }
  )

  onUnmounted(() => {
    stopTimer()
    if (typeof window !== 'undefined') {
      for (const ev of events) {
        window.removeEventListener(ev, onActivity)
      }
    }
  })

  return {
    getTimeoutMinutes: () => timeoutMinutes,
    resetTimer: onActivity,
  }
}
