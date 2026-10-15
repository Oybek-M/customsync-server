import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest'
import { flushPromises } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createMemoryHistory, createRouter } from 'vue-router'
import { useAuthStore } from '../stores/auth'
import { clearTokens } from '../api/client'
import { routes, setupAuthGuard } from '../router'

describe('Router auth guard', () => {
  let router: ReturnType<typeof createRouter>

  beforeEach(() => {
    setActivePinia(createPinia())
    localStorage.clear()
    clearTokens()
    vi.restoreAllMocks()

    router = createRouter({
      history: createMemoryHistory(),
      routes,
    })
    setupAuthGuard(router)
  })

  afterEach(() => {
    localStorage.clear()
    clearTokens()
  })

  it('not enrolled -> every route redirects to /enroll', async () => {
    const authStore = useAuthStore()
    expect(authStore.isEnrolled).toBe(false)

    // Try navigating to '/' (dashboard)
    router.push('/')
    await router.isReady()
    expect(router.currentRoute.value.path).toBe('/enroll')

    // Try navigating to '/devices'
    await router.push('/devices')
    expect(router.currentRoute.value.path).toBe('/enroll')

    // Try navigating to '/settings'
    await router.push('/settings')
    expect(router.currentRoute.value.path).toBe('/enroll')
  })

  it('enrolled user opening /enroll -> redirects to dashboard', async () => {
    const authStore = useAuthStore()
    authStore.setCredentials('dev-123', 'ref-token-xyz')
    expect(authStore.isEnrolled).toBe(true)

    // Navigate to '/'
    router.push('/')
    await router.isReady()
    expect(router.currentRoute.value.path).toBe('/')

    // Now try opening '/enroll'
    await router.push('/enroll')
    expect(router.currentRoute.value.path).toBe('/')
  })

  it('when credentials are cleared (revoked device) -> redirects to /enroll', async () => {
    const authStore = useAuthStore()
    authStore.setCredentials('dev-123', 'ref-token-xyz')

    router.push('/devices')
    await router.isReady()
    expect(router.currentRoute.value.path).toBe('/devices')

    // Revocation triggers clearCredentials
    authStore.clearCredentials()
    expect(authStore.isEnrolled).toBe(false)

    // Wait for router push and dynamic component import to settle
    await flushPromises()
    await new Promise((r) => setTimeout(r, 50))
    await flushPromises()
    expect(router.currentRoute.value.path).toBe('/enroll')
  })
})
