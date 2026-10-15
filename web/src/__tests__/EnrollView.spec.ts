import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createMemoryHistory, createRouter } from 'vue-router'
import EnrollView from '../views/EnrollView.vue'
import { routes, setupAuthGuard } from '../router'
import { clearTokens } from '../api/client'

describe('EnrollView.vue', () => {
  let router: ReturnType<typeof createRouter>

  beforeEach(async () => {
    setActivePinia(createPinia())
    localStorage.clear()
    clearTokens()
    vi.restoreAllMocks()

    router = createRouter({
      history: createMemoryHistory(),
      routes,
    })
    setupAuthGuard(router)
    router.push('/enroll')
    await router.isReady()
  })

  afterEach(() => {
    localStorage.clear()
    clearTokens()
  })

  it('renders visible text input for code with autocomplete="off" and spellcheck="false"', () => {
    const wrapper = mount(EnrollView, {
      global: { plugins: [router] },
    })

    const codeInput = wrapper.find('input[data-test="code-input"]')
    expect(codeInput.exists()).toBe(true)
    expect(codeInput.attributes('type')).toBe('text') // NOT password!
    expect(codeInput.attributes('autocomplete')).toBe('off')
    expect(codeInput.attributes('spellcheck')).toBe('false')

    const nameInput = wrapper.find('input[data-test="name-input"]')
    expect(nameInput.exists()).toBe(true)
    expect((nameInput.element as HTMLInputElement).value).toBe('Web panel')
  })

  it('"Ulash" button is disabled when code is empty and while pending', async () => {
    const wrapper = mount(EnrollView, {
      global: { plugins: [router] },
    })

    const submitBtn = wrapper.find('button[type="submit"]')
    expect(submitBtn.exists()).toBe(true)

    // Code empty initially -> disabled
    expect(submitBtn.attributes('disabled')).toBeDefined()

    // Enter only whitespace -> still disabled
    const codeInput = wrapper.find('input[data-test="code-input"]')
    await codeInput.setValue('   ')
    expect(submitBtn.attributes('disabled')).toBeDefined()

    // Enter non-empty code -> enabled
    await codeInput.setValue('ABC-123')
    expect(submitBtn.attributes('disabled')).toBeUndefined()
  })

  it('submitting valid code calls enroll with trimmed code and navigates to dashboard', async () => {
    const fetchSpy = vi.spyOn(globalThis, 'fetch').mockResolvedValueOnce(
      new Response(
        JSON.stringify({
          deviceId: 'dev-web-1',
          refreshToken: 'ref-token-1',
          accessToken: 'acc-token-1',
          expiresAt: new Date(Date.now() + 3600_000).toISOString(),
        }),
        { status: 200, headers: { 'Content-Type': 'application/json' } }
      )
    )

    const wrapper = mount(EnrollView, {
      global: { plugins: [router] },
    })

    await wrapper.find('input[data-test="code-input"]').setValue('  VALID-CODE-123  ')
    await wrapper.find('input[data-test="name-input"]').setValue('Admin Browser')

    await wrapper.find('form').trigger('submit')
    await flushPromises()
    await new Promise((r) => setTimeout(r, 50))
    await flushPromises()

    expect(fetchSpy).toHaveBeenCalledTimes(1)
    const [, init] = fetchSpy.mock.calls[0]
    expect(JSON.parse(init?.body as string)).toEqual({
      code: 'VALID-CODE-123',
      name: 'Admin Browser',
      platform: 'web',
    })

    // Navigates to dashboard
    expect(router.currentRoute.value.path).toBe('/')
  })

  it('enroll 400 shows bad code message in Uzbek', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValueOnce(
      new Response(JSON.stringify({ error: 'invalid_or_used_code' }), {
        status: 400,
        headers: { 'Content-Type': 'application/json' },
      })
    )

    const wrapper = mount(EnrollView, {
      global: { plugins: [router] },
    })

    await wrapper.find('input[data-test="code-input"]').setValue('BAD-CODE')
    await wrapper.find('form').trigger('submit')
    await flushPromises()

    const errorEl = wrapper.find('.cs-error-msg')
    expect(errorEl.exists()).toBe(true)
    // Bad code error message in Uzbek
    expect(errorEl.text().toLowerCase()).toMatch(/kod.*(noto'g'ri|ishlatilgan)/)
  })

  it('network failure shows unreachable message in Uzbek', async () => {
    vi.spyOn(globalThis, 'fetch').mockRejectedValueOnce(new TypeError('Failed to fetch'))

    const wrapper = mount(EnrollView, {
      global: { plugins: [router] },
    })

    await wrapper.find('input[data-test="code-input"]').setValue('ANY-CODE')
    await wrapper.find('form').trigger('submit')
    await flushPromises()

    const errorEl = wrapper.find('.cs-error-msg')
    expect(errorEl.exists()).toBe(true)
    // Unreachable error message in Uzbek
    expect(errorEl.text().toLowerCase()).toMatch(/server.*(ulanib|ishlamayapti|aloqa)/)
  })
})
