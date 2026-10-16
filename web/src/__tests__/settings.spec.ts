import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import SettingsView from '../views/SettingsView.vue'
import { mockSettings } from './fixtures'
import { clearTokens, setAccessTokenForTest } from '../api/client'
import { useAuthStore } from '../stores/auth'

describe('SettingsView.vue', () => {
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

  it('renders settings grouped by category and provides editor per type', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValueOnce(
      new Response(JSON.stringify(mockSettings), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      })
    )

    const wrapper = mount(SettingsView)
    await flushPromises()

    // Categories rendered
    expect(wrapper.text()).toContain('auth')
    expect(wrapper.text()).toContain('storage')
    expect(wrapper.text()).toContain('sync')

    // Quota hints (REVIZIYA)
    expect(wrapper.text()).toContain('0 = cheksiz')

    // Int editor (input type number)
    const intInput = wrapper.find('input[data-key="auth.enroll_code_minutes"]')
    expect(intInput.exists()).toBe(true)
    expect(intInput.attributes('type')).toBe('number')

    // Bool editor (checkbox/toggle)
    const boolInput = wrapper.find('input[data-key="sync.allow_registration"]')
    expect(boolInput.exists()).toBe(true)
    expect(boolInput.attributes('type')).toBe('checkbox')

    // Json editor (textarea)
    const jsonInput = wrapper.find('textarea[data-key="sync.custom_rules"]')
    expect(jsonInput.exists()).toBe(true)
  })

  it('bool toggle sends "true" or "false" on save', async () => {
    let sentValue = ''
    vi.spyOn(globalThis, 'fetch').mockImplementation(async (url, init) => {
      const u = url.toString()
      if (u === '/api/v1/settings') {
        return new Response(JSON.stringify(mockSettings), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        })
      }
      if (u === '/api/v1/settings/sync.allow_registration' && init?.method === 'PUT') {
        const body = JSON.parse(init.body as string)
        sentValue = body.value
        return new Response(null, { status: 204 })
      }
      return new Response('Not found', { status: 404 })
    })

    const wrapper = mount(SettingsView)
    await flushPromises()

    const checkbox = wrapper.find('input[data-key="sync.allow_registration"]')
    // Initially true -> toggle to false
    await checkbox.setValue(false)

    const saveBtn = wrapper.find('button[data-save="sync.allow_registration"]')
    await saveBtn.trigger('click')
    await flushPromises()

    expect(sentValue).toBe('false')
  })

  it('400 keeps user input and shows inline error without losing edit', async () => {
    vi.spyOn(globalThis, 'fetch').mockImplementation(async (url, init) => {
      const u = url.toString()
      if (u === '/api/v1/settings') {
        return new Response(JSON.stringify(mockSettings), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        })
      }
      if (u === '/api/v1/settings/auth.enroll_code_minutes' && init?.method === 'PUT') {
        return new Response(JSON.stringify({ error: 'invalid_value' }), {
          status: 400,
          headers: { 'Content-Type': 'application/json' },
        })
      }
      return new Response('Not found', { status: 404 })
    })

    const wrapper = mount(SettingsView)
    await flushPromises()

    const input = wrapper.find('input[data-key="auth.enroll_code_minutes"]')
    await input.setValue('99999')

    const saveBtn = wrapper.find('button[data-save="auth.enroll_code_minutes"]')
    await saveBtn.trigger('click')
    await flushPromises()

    // Value still in input (not reset)
    expect((input.element as HTMLInputElement).value).toBe('99999')

    // Inline error shown
    const errorEl = wrapper.find('.cs-setting-error[data-key="auth.enroll_code_minutes"]')
    expect(errorEl.exists()).toBe(true)
    expect(errorEl.text().toLowerCase()).toContain('noto\'g\'ri')
  })

  it('bad int and bad json are blocked client-side before sending', async () => {
    let putCalls = 0
    vi.spyOn(globalThis, 'fetch').mockImplementation(async (url, init) => {
      const u = url.toString()
      if (u === '/api/v1/settings') {
        return new Response(JSON.stringify(mockSettings), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        })
      }
      if (init?.method === 'PUT') {
        putCalls++
        return new Response(null, { status: 204 })
      }
      return new Response('Not found', { status: 404 })
    })

    const wrapper = mount(SettingsView)
    await flushPromises()

    // 1. Bad int
    const intInput = wrapper.find('input[data-key="auth.enroll_code_minutes"]')
    await intInput.setValue('not-a-number')
    const intSaveBtn = wrapper.find('button[data-save="auth.enroll_code_minutes"]')
    await intSaveBtn.trigger('click')
    await flushPromises()

    expect(putCalls).toBe(0)
    expect(wrapper.find('.cs-setting-error[data-key="auth.enroll_code_minutes"]').exists()).toBe(true)

    // 2. Bad json
    const jsonInput = wrapper.find('textarea[data-key="sync.custom_rules"]')
    await jsonInput.setValue('{ bad json syntax: }')
    const jsonSaveBtn = wrapper.find('button[data-save="sync.custom_rules"]')
    await jsonSaveBtn.trigger('click')
    await flushPromises()

    expect(putCalls).toBe(0)
    expect(wrapper.find('.cs-setting-error[data-key="sync.custom_rules"]').exists()).toBe(true)
  })

  it('leaving view aborts in-flight request and shows no error', async () => {
    let aborted = false
    vi.spyOn(globalThis, 'fetch').mockImplementation((_url, init) => {
      init?.signal?.addEventListener('abort', () => {
        aborted = true
      })
      return new Promise(() => {})
    })

    const wrapper = mount(SettingsView)
    wrapper.unmount()
    await flushPromises()

    expect(aborted).toBe(true)
  })
})
