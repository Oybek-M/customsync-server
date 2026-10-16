import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import AuditView from '../views/AuditView.vue'
import { mockAuditPage1, mockAuditPage2 } from './fixtures'
import { clearTokens, setAccessTokenForTest } from '../api/client'
import { useAuthStore } from '../stores/auth'

describe('AuditView.vue', () => {
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

  it('renders table columns, parses detail defensively and renders HTML as text', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValueOnce(
      new Response(JSON.stringify(mockAuditPage1), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      })
    )

    const wrapper = mount(AuditView)
    await flushPromises()

    const rows = wrapper.findAll('tbody tr')
    expect(rows.length).toBe(3)

    // Action and device rendered
    expect(wrapper.text()).toContain('device.enrolled')
    expect(wrapper.text()).toContain('settings.changed')
    expect(wrapper.text()).toContain('device.revoked')

    // Defensive detail: HTML is NOT rendered as HTML elements (never v-html)
    const scriptEl = wrapper.find('script')
    expect(scriptEl.exists()).toBe(false)
    expect(wrapper.html()).not.toContain('<script>') // textContent escaped
    expect(wrapper.text()).toContain('<script>alert("xss")</script>')

    // Parsed JSON detail formatting
    expect(wrapper.text()).toContain('Desktop PC')
  })

  it('second page requests before=<next_before> and button hides when next_before is null', async () => {
    const urlsCalled: string[] = []
    vi.spyOn(globalThis, 'fetch').mockImplementation(async (url) => {
      const u = url.toString()
      urlsCalled.push(u)
      if (u.includes('before=103')) {
        return new Response(JSON.stringify(mockAuditPage2), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        })
      }
      return new Response(JSON.stringify(mockAuditPage1), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      })
    })

    const wrapper = mount(AuditView)
    await flushPromises()

    expect(wrapper.findAll('tbody tr').length).toBe(3)

    // "Ko'proq yuklash" button is visible
    const loadMoreBtn = wrapper.find('.cs-btn-load-more')
    expect(loadMoreBtn.exists()).toBe(true)

    // Click load more
    await loadMoreBtn.trigger('click')
    await flushPromises()

    // Second call requested before=103 (keyset pagination)
    expect(urlsCalled.some((u) => u.includes('before=103'))).toBe(true)
    // Rows combined (3 + 1 = 4)
    expect(wrapper.findAll('tbody tr').length).toBe(4)

    // Now next_before is null -> button hidden
    expect(wrapper.find('.cs-btn-load-more').exists()).toBe(false)
  })

  it('leaving view aborts in-flight request and shows no error', async () => {
    let aborted = false
    vi.spyOn(globalThis, 'fetch').mockImplementation((_url, init) => {
      init?.signal?.addEventListener('abort', () => {
        aborted = true
      })
      return new Promise(() => {})
    })

    const wrapper = mount(AuditView)
    wrapper.unmount()
    await flushPromises()

    expect(aborted).toBe(true)
  })
})
