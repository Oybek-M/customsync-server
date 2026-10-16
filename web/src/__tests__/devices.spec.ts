import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createMemoryHistory, createRouter } from 'vue-router'
import DevicesView from '../views/DevicesView.vue'
import { routes, setupAuthGuard } from '../router'
import { useAuthStore } from '../stores/auth'
import { clearTokens, setAccessTokenForTest } from '../api/client'
import { mockDevices, mockSettings } from './fixtures'

describe('DevicesView.vue', () => {
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

  it('renders rows from fixture with active and revoked statuses', async () => {
    vi.spyOn(globalThis, 'fetch').mockImplementation(async (url) => {
      const u = url.toString()
      if (u === '/api/v1/devices') {
        return new Response(JSON.stringify(mockDevices), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        })
      }
      return new Response('Not found', { status: 404 })
    })

    const wrapper = mount(DevicesView, {
      global: { plugins: [router] },
    })
    await flushPromises()

    const rows = wrapper.findAll('tbody tr')
    expect(rows.length).toBe(mockDevices.length)

    expect(wrapper.text()).toContain('Web Panel (My Browser)')
    expect(wrapper.text()).toContain('Desktop PC')
    expect(wrapper.text()).toContain('Old Android Phone')

    // Revoked row has no revoke button
    const revokedRow = rows[2] // dev-phone-old
    expect(revokedRow.text()).toContain('Bekor qilingan')
    expect(revokedRow.find('.cs-btn-revoke').exists()).toBe(false)

    // Non-revoked rows have revoke button
    const activeRow = rows[1] // dev-desktop-01
    expect(activeRow.text()).toContain('Faol')
    expect(activeRow.find('.cs-btn-revoke').exists()).toBe(true)
  })

  it('revoke shows confirm dialog; cancelled confirm sends nothing; confirmed sends DELETE', async () => {
    let deleteCalls = 0
    vi.spyOn(globalThis, 'fetch').mockImplementation(async (url, init) => {
      const u = url.toString()
      if (u === '/api/v1/devices') {
        return new Response(JSON.stringify(mockDevices), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        })
      }
      if (u === '/api/v1/devices/dev-desktop-01' && init?.method === 'DELETE') {
        deleteCalls++
        return new Response(null, { status: 204 })
      }
      return new Response('Not found', { status: 404 })
    })

    const wrapper = mount(DevicesView, {
      global: { plugins: [router] },
    })
    await flushPromises()

    const rows = wrapper.findAll('tbody tr')
    const desktopRow = rows[1]
    const revokeBtn = desktopRow.find('.cs-btn-revoke')
    await revokeBtn.trigger('click')

    // Confirm dialog is open and names the device
    const dialog = wrapper.find('.cs-confirm-dialog')
    expect(dialog.exists()).toBe(true)
    expect(dialog.text()).toContain('Desktop PC')

    // Cancel first
    const cancelBtn = dialog.find('.cs-btn-cancel')
    await cancelBtn.trigger('click')
    await flushPromises()

    expect(deleteCalls).toBe(0)
    expect(wrapper.find('.cs-confirm-dialog').exists()).toBe(false)

    // Click revoke again and confirm
    await revokeBtn.trigger('click')
    const confirmBtn = wrapper.find('.cs-btn-confirm')
    await confirmBtn.trigger('click')
    await flushPromises()

    expect(deleteCalls).toBe(1)
  })

  it('own-device revoke shows extra warning and navigates to /enroll after confirmation', async () => {
    vi.spyOn(globalThis, 'fetch').mockImplementation(async (url, init) => {
      const u = url.toString()
      if (u === '/api/v1/devices') {
        return new Response(JSON.stringify(mockDevices), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        })
      }
      if (u === '/api/v1/devices/dev-own-web' && init?.method === 'DELETE') {
        return new Response(null, { status: 204 })
      }
      return new Response('Not found', { status: 404 })
    })

    const wrapper = mount(DevicesView, {
      global: { plugins: [router] },
    })
    await flushPromises()

    const rows = wrapper.findAll('tbody tr')
    const ownRow = rows[0] // dev-own-web
    const revokeBtn = ownRow.find('.cs-btn-revoke')
    await revokeBtn.trigger('click')

    const dialog = wrapper.find('.cs-confirm-dialog')
    expect(dialog.exists()).toBe(true)
    // Extra warning for current panel device
    expect(dialog.find('.cs-own-device-warning').exists()).toBe(true)

    const confirmBtn = dialog.find('.cs-btn-confirm')
    await confirmBtn.trigger('click')
    await flushPromises()

    // Auth cleared and lands on /enroll
    const authStore = useAuthStore()
    expect(authStore.isEnrolled).toBe(false)
    expect(router.currentRoute.value.path).toBe('/enroll')
  })

  it('new code modal shows code + countdown and expires with fake timers', async () => {
    vi.useFakeTimers()

    vi.spyOn(globalThis, 'fetch').mockImplementation(async (url, init) => {
      const u = url.toString()
      if (u === '/api/v1/devices') {
        return new Response(JSON.stringify(mockDevices), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        })
      }
      if (u === '/api/v1/settings') {
        return new Response(JSON.stringify(mockSettings), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        })
      }
      if (u === '/api/v1/devices/codes' && init?.method === 'POST') {
        return new Response(JSON.stringify({ code: 'ENROLL-CODE-9876' }), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        })
      }
      return new Response('Not found', { status: 404 })
    })

    const wrapper = mount(DevicesView, {
      global: { plugins: [router] },
    })
    await flushPromises()

    const newCodeBtn = wrapper.find('.cs-btn-new-code')
    expect(newCodeBtn.exists()).toBe(true)
    await newCodeBtn.trigger('click')
    await flushPromises()

    // Modal is open
    const modal = wrapper.find('.cs-code-modal')
    expect(modal.exists()).toBe(true)

    // Submit generation
    const generateBtn = modal.find('.cs-btn-generate')
    await generateBtn.trigger('click')
    await flushPromises()

    // Code is displayed in monospace and not stored/logged
    const codeDisplay = modal.find('.cs-code-display')
    expect(codeDisplay.exists()).toBe(true)
    expect(codeDisplay.text()).toBe('ENROLL-CODE-9876')
    expect(localStorage.getItem('ENROLL-CODE-9876')).toBeNull()

    // Initial countdown shows time remaining (e.g. 15:00)
    expect(modal.text()).toMatch(/14:\d\d|15:00/)

    // Advance timers by 15 minutes + 1 second
    await vi.advanceTimersByTimeAsync(15 * 60 * 1000 + 1000)
    await flushPromises()

    // Code is now expired and greyed out
    expect(modal.find('.is-expired').exists()).toBe(true)
    expect(modal.text().toLowerCase()).toContain('muddati tugadi')

    vi.useRealTimers()
  })

  it('leaving view aborts in-flight request and shows no error', async () => {
    let aborted = false
    vi.spyOn(globalThis, 'fetch').mockImplementation((_url, init) => {
      init?.signal?.addEventListener('abort', () => {
        aborted = true
      })
      return new Promise(() => {}) // never resolves
    })

    const wrapper = mount(DevicesView, {
      global: { plugins: [router] },
    })

    // Unmount before response
    wrapper.unmount()
    await flushPromises()

    expect(aborted).toBe(true)
    // No error element displayed
    expect(wrapper.find('.cs-error-msg').exists()).toBe(false)
  })
})
