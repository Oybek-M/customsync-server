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

describe('Storage security - no secrets persisted', () => {
  let originalFetch: any
  const vectors = loadTestVectors()
  const wrapCase = vectors.key_wrap.cases.find(
    (c: any) => c.name.includes('UTF-8') || c.iterations === 1000
  ) || vectors.key_wrap.cases[0]

  beforeEach(() => {
    setActivePinia(createPinia())
    localStorage.clear()
    sessionStorage.clear()
    clearTokens()
    const authStore = useAuthStore()
    authStore.setCredentials('dev-test-web', 'mock-refresh-token')
    setAccessTokenForTest('mock-access-token')
    originalFetch = globalThis.fetch
  })

  afterEach(() => {
    localStorage.clear()
    sessionStorage.clear()
    clearTokens()
    globalThis.fetch = originalFetch
    vi.restoreAllMocks()
  })

  it('ensures master key, derived keys, passphrase, and fingerprint never appear in localStorage or sessionStorage', async () => {
    const directClient = createDirectWorkerClient()
    const vaultStore = useVaultStore()
    vaultStore.setWorkerClient(directClient.client)

    globalThis.fetch = vi.fn().mockImplementation(async (url: string) => {
      if (url.endsWith('/api/v1/keys/wraps')) {
        return {
          ok: true,
          status: 200,
          json: async () => [
            {
              wrap_id: 'wrap-pass-1',
              wrap_type: 'passphrase',
              label: 'Test wrap',
              created_at: '2026-10-10T12:00:00Z',
              last_used_at: null,
            },
          ],
        }
      }
      if (url.includes('/api/v1/keys/wraps/wrap-pass-1')) {
        return {
          ok: true,
          status: 200,
          json: async () => ({
            wrap_id: 'wrap-pass-1',
            wrap_type: wrapCase.wrap_type,
            label: 'Test wrap',
            salt: wrapCase.salt,
            nonce: wrapCase.nonce,
            wrapped_key: wrapCase.wrapped_key,
            iterations: wrapCase.iterations,
          }),
        }
      }
      return { ok: false, status: 404, json: async () => ({}) }
    })

    const wrapper = mount(UnlockDialog, {
      props: { visible: true },
    })
    await flushPromises()

    const input = wrapper.find('input[type="password"]')
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

    expect(vaultStore.locked).toBe(false)
    expect(vaultStore.fingerprint).toBe(wrapCase.fingerprint)

    // Gather all storage keys and values
    const allStoredEntries: { store: string; key: string; value: string }[] = []

    for (let i = 0; i < localStorage.length; i++) {
      const key = localStorage.key(i)!
      allStoredEntries.push({ store: 'localStorage', key, value: localStorage.getItem(key) || '' })
    }

    for (let i = 0; i < sessionStorage.length; i++) {
      const key = sessionStorage.key(i)!
      allStoredEntries.push({ store: 'sessionStorage', key, value: sessionStorage.getItem(key) || '' })
    }

    // Secrets to verify are ABSENT
    const secretsToCheck = [
      wrapCase.passphrase,
      wrapCase.master_hex,
      wrapCase.fingerprint,
    ]

    for (const entry of allStoredEntries) {
      for (const secret of secretsToCheck) {
        expect(
          entry.value.includes(secret),
          `Secret found in ${entry.store} under key '${entry.key}'`
        ).toBe(false)
        expect(
          entry.key.includes(secret),
          `Secret found in ${entry.store} key name '${entry.key}'`
        ).toBe(false)
      }
    }
  })
})
