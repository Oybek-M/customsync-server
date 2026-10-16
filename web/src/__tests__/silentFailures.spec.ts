import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createMemoryHistory, createRouter } from 'vue-router'
import DevicesView from '../views/DevicesView.vue'
import AuditView from '../views/AuditView.vue'
import { routes, setupAuthGuard } from '../router'
import { useAuthStore } from '../stores/auth'
import { clearTokens, setAccessTokenForTest } from '../api/client'
import { mockDevices, mockAuditPage1 } from './fixtures'

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })

// Muvaffaqiyatsiz amal jim qolmasligi kerak: foydalanuvchi bekor qilindi yoki
// hammasi yuklandi deb o'ylab qoladi.
describe('failures are shown, not swallowed', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    localStorage.clear()
    clearTokens()
    vi.restoreAllMocks()
    useAuthStore().setCredentials('dev-own-web', 'mock-refresh-token')
    setAccessTokenForTest('mock-access-token')
  })

  afterEach(() => {
    localStorage.clear()
    clearTokens()
  })

  it('a failed revoke shows an error and leaves the device active', async () => {
    vi.spyOn(globalThis, 'fetch').mockImplementation(async (url, init) => {
      if (init?.method === 'DELETE') return json({ error: 'server_error' }, 500)
      if (url.toString() === '/api/v1/devices') return json(mockDevices)
      return json({}, 404)
    })
    const router = createRouter({ history: createMemoryHistory(), routes })
    setupAuthGuard(router)
    const wrapper = mount(DevicesView, { global: { plugins: [router] } })
    await flushPromises()

    await wrapper.findAll('tbody tr')[1].find('.cs-btn-revoke').trigger('click')
    await wrapper.find('.cs-btn-confirm').trigger('click')
    await flushPromises()

    expect(wrapper.find('.cs-confirm-dialog .cs-error-msg').exists()).toBe(true)
    expect(wrapper.findAll('tbody tr')[1].find('.cs-btn-revoke').exists()).toBe(true)
  })

  it('a failed "load more" shows an error and keeps the button', async () => {
    let call = 0
    vi.spyOn(globalThis, 'fetch').mockImplementation(async () =>
      ++call === 1 ? json(mockAuditPage1) : json({ error: 'server_error' }, 500))
    const wrapper = mount(AuditView)
    await flushPromises()

    await wrapper.find('.cs-btn-load-more').trigger('click')
    await flushPromises()

    expect(wrapper.find('.cs-error-msg').exists()).toBe(true)
    expect(wrapper.find('.cs-btn-load-more').exists()).toBe(true)
  })
})
