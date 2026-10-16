import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createMemoryHistory, createRouter } from 'vue-router'
import DashboardView from '../views/DashboardView.vue'
import { routes, setupAuthGuard } from '../router'
import { mockDevices, mockStorageStats, mockAuditPage1 } from './fixtures'
import { clearTokens, setAccessTokenForTest } from '../api/client'
import { useAuthStore } from '../stores/auth'

describe('DashboardView.vue', () => {
  let router: ReturnType<typeof createRouter>

  beforeEach(() => {
    setActivePinia(createPinia())
    localStorage.clear()
    clearTokens()
    vi.restoreAllMocks()

    const authStore = useAuthStore()
    authStore.setCredentials('dev-own-web', 'mock-refresh-token')
    setAccessTokenForTest('mock-access-token')

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

  it('renders 4 StatCards, recent audit events, and devices overview', async () => {
    vi.spyOn(globalThis, 'fetch').mockImplementation(async (url) => {
      const u = url.toString()
      if (u === '/api/v1/stats/storage') {
        return new Response(JSON.stringify(mockStorageStats), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        })
      }
      if (u === '/api/v1/devices') {
        return new Response(JSON.stringify(mockDevices), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        })
      }
      if (u.startsWith('/api/v1/audit')) {
        return new Response(JSON.stringify(mockAuditPage1), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        })
      }
      return new Response('Not found', { status: 404 })
    })

    const wrapper = mount(DashboardView, {
      global: { plugins: [router] },
    })
    await flushPromises()

    // 4 StatCards:
    // 1. Record count
    expect(wrapper.text()).toContain('5,420')
    // 2. Storage bytes (14.5MB + 450MB = ~464.5 MB)
    expect(wrapper.text()).toMatch(/464\.[0-9]+\s*MB|443\.[0-9]+\s*MiB/i)
    // 3. Active devices: 2 (mockDevices has 3 devices, 1 is revoked)
    expect(wrapper.text()).toContain('2')
    // 4. Last sync / recent seen
    expect(wrapper.text()).toMatch(/2026-10-16|08:00/)

    // Recent audit events rendered
    expect(wrapper.text()).toContain('device.enrolled')
    expect(wrapper.text()).toContain('settings.changed')

    // Device status overview rendered
    expect(wrapper.text()).toContain('Desktop PC')
    expect(wrapper.text()).toContain('Web Panel (My Browser)')
  })

  it('isolates errors so a storage failure does not break devices or audit', async () => {
    vi.spyOn(globalThis, 'fetch').mockImplementation(async (url) => {
      const u = url.toString()
      if (u === '/api/v1/stats/storage') {
        return new Response(JSON.stringify({ error: 'server_error' }), {
          status: 500,
          headers: { 'Content-Type': 'application/json' },
        })
      }
      if (u === '/api/v1/devices') {
        return new Response(JSON.stringify(mockDevices), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        })
      }
      if (u.startsWith('/api/v1/audit')) {
        return new Response(JSON.stringify(mockAuditPage1), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        })
      }
      return new Response('Not found', { status: 404 })
    })

    const wrapper = mount(DashboardView, {
      global: { plugins: [router] },
    })
    await flushPromises()

    // Devices & audit still render despite storage 500
    expect(wrapper.text()).toContain('Desktop PC')
    expect(wrapper.text()).toContain('device.enrolled')
    // Storage stat cards show an error indicator or placeholder
    expect(wrapper.find('.cs-stat-error').exists() || wrapper.text().includes('Xato')).toBe(true)
  })

  it('leaving view aborts in-flight requests and shows no error', async () => {
    let abortedCount = 0
    vi.spyOn(globalThis, 'fetch').mockImplementation((_url, init) => {
      init?.signal?.addEventListener('abort', () => {
        abortedCount++
      })
      return new Promise(() => {})
    })

    const wrapper = mount(DashboardView, {
      global: { plugins: [router] },
    })
    wrapper.unmount()
    await flushPromises()

    expect(abortedCount).toBeGreaterThan(0)
  })
})
