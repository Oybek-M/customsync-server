import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import UnlockDialog from '../components/UnlockDialog.vue'
import { useVaultStore } from '../stores/vault'
import { useAuthStore } from '../stores/auth'
import { clearTokens, setAccessTokenForTest } from '../api/client'
import { loadTestVectors } from './testVectorsHelper'
import { CryptoWorkerClient } from '../crypto/worker-api'
import { handleMessage, createInitialWorkerState } from '../crypto/worker'

// Direct in-memory worker for integration testing without browser WebWorker
function createDirectWorkerClient() {
  const state = createInitialWorkerState()
  let pendingPromise: Promise<void> = Promise.resolve()
  const mockWorker = {
    onmessage: null as any,
    onerror: null as any,
    postMessage: (data: any) => {
      pendingPromise = handleMessage(state, data).then((res) => {
        if (mockWorker.onmessage) {
          mockWorker.onmessage({ data: res } as MessageEvent)
        }
      })
    },
    terminate: () => {},
  }
  const client = new CryptoWorkerClient(mockWorker as any)
  return {
    client,
    waitForWorker: () => pendingPromise,
  }
}

describe('UnlockDialog.vue', () => {
  let originalFetch: any
  const vectors = loadTestVectors()
  const wrapCase = vectors.key_wrap.cases.find(
    (c: any) => c.name.includes('UTF-8') || c.iterations === 1000
  ) || vectors.key_wrap.cases[0]

  beforeEach(() => {
    setActivePinia(createPinia())
    localStorage.clear()
    clearTokens()
    const authStore = useAuthStore()
    authStore.setCredentials('dev-test-web', 'mock-refresh-token')
    setAccessTokenForTest('mock-access-token')
    originalFetch = globalThis.fetch
  })

  afterEach(() => {
    globalThis.fetch = originalFetch
    localStorage.clear()
    clearTokens()
    vi.restoreAllMocks()
  })

  const sampleWrapsSummary = [
    {
      wrap_id: 'wrap-pass-1',
      wrap_type: 'passphrase',
      label: 'Asosiy arxiv paroli',
      created_at: '2026-10-10T12:00:00Z',
      last_used_at: null,
    },
    {
      wrap_id: 'wrap-hw-1',
      wrap_type: 'yubikey',
      label: 'YubiKey apparat kaliti',
      created_at: '2026-10-11T12:00:00Z',
      last_used_at: null,
    },
  ]

  const sampleWrapDetail = {
    wrap_id: 'wrap-pass-1',
    wrap_type: wrapCase.wrap_type,
    label: 'Asosiy arxiv paroli',
    salt: wrapCase.salt,
    nonce: wrapCase.nonce,
    wrapped_key: wrapCase.wrapped_key,
    iterations: wrapCase.iterations,
  }

  it('lists wraps and disables non-passphrase wraps with notice', async () => {
    globalThis.fetch = vi.fn().mockImplementation(async (url: string) => {
      if (url.includes('/api/v1/keys/wraps')) {
        return {
          ok: true,
          status: 200,
          json: async () => sampleWrapsSummary,
        }
      }
      return { ok: false, status: 404, json: async () => ({}) }
    })

    const wrapper = mount(UnlockDialog, {
      props: { visible: true },
    })

    await flushPromises()

    const text = wrapper.text()
    expect(text).toContain('Asosiy arxiv paroli')
    expect(text).toContain('YubiKey apparat kaliti')
    expect(text).toContain("web panelda hali qo'llab-quvvatlanmaydi")
  })

  it('unlocks with vector passphrase, shows grouped fingerprint and clears passphrase input', async () => {
    const directClient = createDirectWorkerClient()
    const vaultStore = useVaultStore()
    vaultStore.setWorkerClient(directClient.client)

    globalThis.fetch = vi.fn().mockImplementation(async (url: string) => {
      if (url.endsWith('/api/v1/keys/wraps')) {
        return {
          ok: true,
          status: 200,
          json: async () => [sampleWrapsSummary[0]],
        }
      }
      if (url.includes('/api/v1/keys/wraps/wrap-pass-1')) {
        return {
          ok: true,
          status: 200,
          json: async () => sampleWrapDetail,
        }
      }
      return { ok: false, status: 404, json: async () => ({}) }
    })

    const wrapper = mount(UnlockDialog, {
      props: { visible: true },
    })
    await flushPromises()

    const input = wrapper.find('input[type="password"]')
    expect(input.exists()).toBe(true)

    // Type vector passphrase
    await input.setValue(wrapCase.passphrase)

    const form = wrapper.find('form')
    if (form.exists()) {
      await form.trigger('submit')
    } else {
      await wrapper.find('.cs-btn-unlock').trigger('click')
    }

    await flushPromises()
    await directClient.waitForWorker()
    await flushPromises()

    // Passphrase input must be cleared / replaced by success screen
    const pwInput = wrapper.find('input[type="password"]')
    if (pwInput.exists()) {
      expect((pwInput.element as HTMLInputElement).value).toBe('')
    }

    // Vault store is unlocked
    expect(vaultStore.locked).toBe(false)

    // Fingerprint shown grouped: 5c7f b6d9 68d4 b5a5
    const expectedFpGrouped = '5c7f b6d9 68d4 b5a5'
    expect(wrapper.text()).toContain(expectedFpGrouped)
    expect(vaultStore.fingerprint).toBe(wrapCase.fingerprint)
  })

  it('wrong passphrase shows distinct Uzbek error, clears field, and leaves vault locked', async () => {
    const directClient = createDirectWorkerClient()
    const vaultStore = useVaultStore()
    vaultStore.setWorkerClient(directClient.client)

    globalThis.fetch = vi.fn().mockImplementation(async (url: string) => {
      if (url.endsWith('/api/v1/keys/wraps')) {
        return {
          ok: true,
          status: 200,
          json: async () => [sampleWrapsSummary[0]],
        }
      }
      if (url.includes('/api/v1/keys/wraps/wrap-pass-1')) {
        return {
          ok: true,
          status: 200,
          json: async () => sampleWrapDetail,
        }
      }
      return { ok: false, status: 404, json: async () => ({}) }
    })

    const wrapper = mount(UnlockDialog, {
      props: { visible: true },
    })
    await flushPromises()

    const input = wrapper.find('input[type="password"]')
    await input.setValue(wrapCase.wrong_passphrase)

    const form = wrapper.find('form')
    if (form.exists()) {
      await form.trigger('submit')
    } else {
      await wrapper.find('.cs-btn-unlock').trigger('click')
    }

    await flushPromises()
    await directClient.waitForWorker()
    await flushPromises()

    // Passphrase input must be cleared
    expect((wrapper.find('input[type="password"]').element as HTMLInputElement).value).toBe('')

    // Vault remains locked
    expect(vaultStore.locked).toBe(true)

    // Error message displayed
    expect(wrapper.text().toLowerCase()).toMatch(/noto'g'ri|xato/)
  })

  it('handles 429 rate limit with distinct Uzbek message and clears field', async () => {
    globalThis.fetch = vi.fn().mockImplementation(async (url: string) => {
      if (url.endsWith('/api/v1/keys/wraps')) {
        return {
          ok: true,
          status: 200,
          json: async () => [sampleWrapsSummary[0]],
        }
      }
      if (url.includes('/api/v1/keys/wraps/wrap-pass-1')) {
        return {
          ok: false,
          status: 429,
          json: async () => ({ error: 'rate_limited' }),
        }
      }
      return { ok: false, status: 404, json: async () => ({}) }
    })

    const wrapper = mount(UnlockDialog, {
      props: { visible: true },
    })
    await flushPromises()

    const input = wrapper.find('input[type="password"]')
    await input.setValue('any-password-test')

    const form = wrapper.find('form')
    if (form.exists()) {
      await form.trigger('submit')
    } else {
      await wrapper.find('.cs-btn-unlock').trigger('click')
    }

    await flushPromises()

    expect((wrapper.find('input[type="password"]').element as HTMLInputElement).value).toBe('')
    expect(wrapper.text().toLowerCase()).toMatch(/urinish|kuting/)
  })

  it('handles 404 deleted wrap with distinct Uzbek message and clears field', async () => {
    globalThis.fetch = vi.fn().mockImplementation(async (url: string) => {
      if (url.endsWith('/api/v1/keys/wraps')) {
        return {
          ok: true,
          status: 200,
          json: async () => [sampleWrapsSummary[0]],
        }
      }
      if (url.includes('/api/v1/keys/wraps/wrap-pass-1')) {
        return {
          ok: false,
          status: 404,
          json: async () => ({ error: 'not_found' }),
        }
      }
      return { ok: false, status: 404, json: async () => ({}) }
    })

    const wrapper = mount(UnlockDialog, {
      props: { visible: true },
    })
    await flushPromises()

    const input = wrapper.find('input[type="password"]')
    await input.setValue('any-password-test')

    const form = wrapper.find('form')
    if (form.exists()) {
      await form.trigger('submit')
    } else {
      await wrapper.find('.cs-btn-unlock').trigger('click')
    }

    await flushPromises()

    expect((wrapper.find('input[type="password"]').element as HTMLInputElement).value).toBe('')
    expect(wrapper.text().toLowerCase()).toMatch(/topilmadi|o'chirilgan/)
  })
})
