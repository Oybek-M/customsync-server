import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { defineComponent } from 'vue'
import { createPinia, setActivePinia } from 'pinia'
import { useVaultStore } from '../stores/vault'
import { useAuthStore } from '../stores/auth'
import { clearTokens, setAccessTokenForTest } from '../api/client'
import { useAutoLock } from '../composables/useAutoLock'

describe('useAutoLock composable', () => {
  let originalFetch: any

  beforeEach(() => {
    setActivePinia(createPinia())
    localStorage.clear()
    clearTokens()
    const authStore = useAuthStore()
    authStore.setCredentials('dev-test', 'mock-ref-token')
    setAccessTokenForTest('mock-acc-token')
    vi.useFakeTimers()
    originalFetch = globalThis.fetch
  })

  afterEach(() => {
    vi.useRealTimers()
    globalThis.fetch = originalFetch
    localStorage.clear()
    clearTokens()
    vi.restoreAllMocks()
  })

  it('locks vault after N minutes idle from settings', async () => {
    // Mock settings returning web.autolock_minutes = 5
    globalThis.fetch = vi.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => [
        { key: 'web.autolock_minutes', value: '5', value_type: 'int', category: 'web', description: '', updated_at: '' }
      ],
    } as any)

    const vaultStore = useVaultStore()
    vaultStore.locked = false

    const TestComponent = defineComponent({
      setup() {
        useAutoLock()
        return {}
      },
      template: '<div>test</div>',
    })

    const wrapper = mount(TestComponent)
    // Wait for settings fetch
    await flushPromises()

    expect(vaultStore.locked).toBe(false)

    // Advance 4 minutes -> still unlocked
    vi.advanceTimersByTime(4 * 60 * 1000)
    expect(vaultStore.locked).toBe(false)

    // Advance 1 more minute (total 5) -> locked
    vi.advanceTimersByTime(1 * 60 * 1000)
    expect(vaultStore.locked).toBe(true)

    wrapper.unmount()
  })

  it('falls back to 15 minutes when settings returns 403 or error', async () => {
    // Mock settings returning 403 Forbidden
    globalThis.fetch = vi.fn().mockResolvedValue({
      ok: false,
      status: 403,
      json: async () => ({ error: 'forbidden' }),
    } as any)

    const vaultStore = useVaultStore()
    vaultStore.locked = false

    const TestComponent = defineComponent({
      setup() {
        useAutoLock()
        return {}
      },
      template: '<div>test</div>',
    })

    const wrapper = mount(TestComponent)
    await flushPromises()

    expect(vaultStore.locked).toBe(false)

    // Advance 14 minutes -> still unlocked
    vi.advanceTimersByTime(14 * 60 * 1000)
    expect(vaultStore.locked).toBe(false)

    // Advance to 15 minutes -> locked
    vi.advanceTimersByTime(1 * 60 * 1000)
    expect(vaultStore.locked).toBe(true)

    wrapper.unmount()
  })

  it('resets idle timer on user activity events', async () => {
    globalThis.fetch = vi.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => [
        { key: 'web.autolock_minutes', value: '2', value_type: 'int', category: 'web', description: '', updated_at: '' }
      ],
    } as any)

    const vaultStore = useVaultStore()
    vaultStore.locked = false

    const TestComponent = defineComponent({
      setup() {
        useAutoLock()
        return {}
      },
      template: '<div>test</div>',
    })

    const wrapper = mount(TestComponent)
    await flushPromises()

    // Advance 90 seconds (1.5 min out of 2 min)
    vi.advanceTimersByTime(90 * 1000)
    expect(vaultStore.locked).toBe(false)

    // Trigger activity (e.g. keydown)
    window.dispatchEvent(new Event('keydown'))

    // Advance another 90 seconds (total 180s from start, but 90s from reset)
    vi.advanceTimersByTime(90 * 1000)
    expect(vaultStore.locked).toBe(false)

    // Advance remaining 30s of new 2m window -> locked
    vi.advanceTimersByTime(30 * 1000)
    expect(vaultStore.locked).toBe(true)

    wrapper.unmount()
  })

  it('removes event listeners on unmount so no lock triggers after unmount', async () => {
    globalThis.fetch = vi.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => [
        { key: 'web.autolock_minutes', value: '1', value_type: 'int', category: 'web', description: '', updated_at: '' }
      ],
    } as any)

    const vaultStore = useVaultStore()
    vaultStore.locked = false

    const TestComponent = defineComponent({
      setup() {
        useAutoLock()
        return {}
      },
      template: '<div>test</div>',
    })

    const wrapper = mount(TestComponent)
    await flushPromises()

    // Unmount before timeout
    wrapper.unmount()

    // Advance timer past timeout
    vi.advanceTimersByTime(2 * 60 * 1000)

    // Should not have locked because unmounted component cleared its timer
    expect(vaultStore.locked).toBe(false)
  })
})
