import { ref, onScopeDispose, getCurrentScope, getCurrentInstance, onUnmounted } from 'vue'

export type HealthStatus = 'ok' | 'down' | 'unknown'

const TIMEOUT_MS = 5000
const POLL_INTERVAL_MS = 30000

export function useServerHealth() {
  const status = ref<HealthStatus>('unknown')
  let timerId: ReturnType<typeof setInterval> | null = null
  let currentController: AbortController | null = null
  let isStopped = false

  async function checkHealth() {
    if (isStopped) return

    if (currentController) {
      currentController.abort()
      currentController = null
    }

    const controller = new AbortController()
    currentController = controller

    let timeoutId: ReturnType<typeof setTimeout> | null = null

    try {
      timeoutId = setTimeout(() => {
        controller.abort()
      }, TIMEOUT_MS)

      const response = await fetch('/api/v1/health', {
        signal: controller.signal,
      })

      if (!isStopped) {
        if (response.status === 200) {
          status.value = 'ok'
        } else {
          status.value = 'down'
        }
      }
    } catch {
      if (!isStopped) {
        status.value = 'down'
      }
    } finally {
      if (timeoutId !== null) {
        clearTimeout(timeoutId)
      }
      if (currentController === controller) {
        currentController = null
      }
    }
  }

  function stop() {
    isStopped = true
    if (timerId !== null) {
      clearInterval(timerId)
      timerId = null
    }
    if (currentController) {
      currentController.abort()
      currentController = null
    }
  }

  // Initial poll
  checkHealth()

  // Schedule periodic polling
  timerId = setInterval(checkHealth, POLL_INTERVAL_MS)

  // Clean up on component unmount or scope dispose
  if (getCurrentScope()) {
    onScopeDispose(stop)
  }
  if (getCurrentInstance()) {
    onUnmounted(stop)
  }

  return {
    status,
    poll: checkHealth,
    stop,
  }
}
