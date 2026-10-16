import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import StorageView from '../views/StorageView.vue'
import { mockStorageStats, mockPeerStats, mockDeviceHealth } from './fixtures'
import { clearTokens, setAccessTokenForTest } from '../api/client'
import { useAuthStore } from '../stores/auth'

describe('StorageView.vue', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    localStorage.clear()
    clearTokens()
    vi.restoreAllMocks()

    const authStore = useAuthStore()
    authStore.setCredentials('dev-own-web', 'mock-refresh-token')
    setAccessTokenForTest('mock-access-token')
  })

  afterEach(() => {
    localStorage.clear()
    clearTokens()
  })

  it('renders storage totals, peers table with shortened hashes, and device health with stale marker', async () => {
    vi.spyOn(globalThis, 'fetch').mockImplementation(async (url) => {
      const u = url.toString()
      if (u === '/api/v1/stats/storage') {
        return new Response(JSON.stringify(mockStorageStats), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        })
      }
      if (u.startsWith('/api/v1/stats/peers')) {
        return new Response(JSON.stringify(mockPeerStats), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        })
      }
      if (u === '/api/v1/devices/health') {
        return new Response(JSON.stringify(mockDeviceHealth), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        })
      }
      return new Response('Not found', { status: 404 })
    })

    const wrapper = mount(StorageView)
    await flushPromises()

    // Storage totals rendered
    expect(wrapper.text()).toContain('5,420') // record_count
    expect(wrapper.text()).toContain('850') // media_count

    // Shortened peer hash rendered (e.g. a1b2c3d4...)
    expect(wrapper.text()).toContain('a1b2c3d4')
    expect(wrapper.text()).toContain('f0e1d2c3')

    // Device health table rendered
    expect(wrapper.text()).toContain('Desktop PC')
    expect(wrapper.text()).toContain('Old Android Phone')

    // Stale row marked
    const staleEl = wrapper.find('.is-stale')
    expect(staleEl.exists()).toBe(true)
  })

  it('leaving view aborts in-flight request and shows no error', async () => {
    let aborted = false
    vi.spyOn(globalThis, 'fetch').mockImplementation((_url, init) => {
      init?.signal?.addEventListener('abort', () => {
        aborted = true
      })
      return new Promise(() => {})
    })

    const wrapper = mount(StorageView)
    wrapper.unmount()
    await flushPromises()

    expect(aborted).toBe(true)
  })
})
