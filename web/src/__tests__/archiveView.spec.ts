import 'fake-indexeddb/auto'
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import ArchiveView from '../views/ArchiveView.vue'
import { useVaultStore } from '../stores/vault'
import { useAuthStore } from '../stores/auth'
import { clearTokens, setAccessTokenForTest } from '../api/client'
import { wipeDatabase, closeDatabase, putRecordsBatch } from '../db/schema'
import { getContentKey, createEncryptedRecord } from './syncTestHelpers'
import { CryptoWorkerClient } from '../crypto/worker-api'
import { handleMessage, createInitialWorkerState } from '../crypto/worker'
import { loadTestVectors } from './testVectorsHelper'

if (typeof globalThis.ResizeObserver === 'undefined') {
  globalThis.ResizeObserver = class ResizeObserver {
    observe() {}
    unobserve() {}
    disconnect() {}
  } as any
}

Object.defineProperty(HTMLElement.prototype, 'clientHeight', { configurable: true, value: 600 })
Object.defineProperty(HTMLElement.prototype, 'offsetHeight', { configurable: true, value: 600 })
Object.defineProperty(HTMLElement.prototype, 'scrollHeight', { configurable: true, value: 600 })
Object.defineProperty(HTMLElement.prototype, 'clientWidth', { configurable: true, value: 800 })
Object.defineProperty(HTMLElement.prototype, 'offsetWidth', { configurable: true, value: 800 })
Object.defineProperty(HTMLElement.prototype, 'scrollWidth', { configurable: true, value: 800 })
Object.defineProperty(HTMLElement.prototype, 'offsetParent', {
  configurable: true,
  get() {
    return document.body
  },
})

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
    state,
    waitForWorker: () => pendingPromise,
  }
}

describe('ArchiveView.vue', () => {
  let contentKey: Uint8Array
  let originalFetch: any
  const vectors = loadTestVectors()
  const wrapCase = vectors.key_wrap.cases.find(
    (c: any) => c.name.includes('UTF-8') || c.iterations === 1000
  ) || vectors.key_wrap.cases[0]

  beforeEach(async () => {
    setActivePinia(createPinia())
    localStorage.clear()
    clearTokens()
    const authStore = useAuthStore()
    authStore.setCredentials('dev-test', 'mock-refresh')
    setAccessTokenForTest('mock-access')
    contentKey = await getContentKey()
    await wipeDatabase()
    originalFetch = globalThis.fetch
  })

  afterEach(async () => {
    closeDatabase()
    await wipeDatabase()
    globalThis.fetch = originalFetch
    localStorage.clear()
    clearTokens()
    vi.restoreAllMocks()
  })

  it('while locked shows lock warning and count of encrypted records cached in IDB', async () => {
    const vaultStore = useVaultStore()
    vaultStore.locked = true

    // Store 5 records in IDB
    const records = await Promise.all([
      createEncryptedRecord(contentKey, { seq: 1, record_id: 'rec-1' }),
      createEncryptedRecord(contentKey, { seq: 2, record_id: 'rec-2' }),
      createEncryptedRecord(contentKey, { seq: 3, record_id: 'rec-3' }),
      createEncryptedRecord(contentKey, { seq: 4, record_id: 'rec-4' }),
      createEncryptedRecord(contentKey, { seq: 5, record_id: 'rec-5' }),
    ])
    await putRecordsBatch(records, 5)

    const wrapper = mount(ArchiveView)
    await flushPromises()

    expect(wrapper.text()).toContain('Arxiv qulflangan — ochish uchun parolni kiriting')
    expect(wrapper.text()).toContain('5')
  })

  it('when unlocked shows progress, cancel button, and first page from indexQuery', async () => {
    const direct = createDirectWorkerClient()
    const vaultStore = useVaultStore()
    vaultStore.setWorkerClient(direct.client)

    await direct.client.unlock(wrapCase.passphrase, wrapCase)
    await direct.waitForWorker()
    vaultStore.locked = false

    const rec1 = await createEncryptedRecord(contentKey, {
      seq: 1,
      record_id: 'rec-view-1',
      json: { text: 'Arxivdagi birinchi xabar' },
    })
    await putRecordsBatch([rec1], 1)

    // Mock sync returning empty
    globalThis.fetch = vi.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => ({ records: [], next_since: 1, has_more: false }),
    } as any)

    const wrapper = mount(ArchiveView)
    for (let i = 0; i < 20; i++) {
      await flushPromises()
      await direct.waitForWorker()
      await new Promise((r) => setTimeout(r, 10))
    }

    // Row rendered
    expect(wrapper.text()).toContain('rec-view-1')
    expect(wrapper.text()).toContain('Arxivdagi birinchi xabar')

    // Cancel button exists
    expect(wrapper.find('.cs-btn-cancel-index').exists()).toBe(true)
  })

  it('shows a query failure instead of an empty table', async () => {
    const direct = createDirectWorkerClient()
    const vaultStore = useVaultStore()
    vaultStore.setWorkerClient(direct.client)
    await direct.client.unlock(wrapCase.passphrase, wrapCase)
    await direct.waitForWorker()
    vaultStore.locked = false
    vi.spyOn(direct.client, 'indexQuery').mockRejectedValue(new Error('worker timeout'))

    globalThis.fetch = vi.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => ({ records: [], next_since: 0, has_more: false }),
    } as any)

    const wrapper = mount(ArchiveView)
    for (let i = 0; i < 20; i++) {
      await flushPromises()
      await direct.waitForWorker()
      await new Promise((r) => setTimeout(r, 10))
    }

    const err = wrapper.find('[data-testid="archive-query-error"]')
    expect(err.exists()).toBe(true)
    expect(err.text()).toContain('worker timeout')
  })

  it('Yangilash button triggers sync', async () => {
    let syncCalled = false
    globalThis.fetch = vi.fn().mockImplementation(async (url: string) => {
      if (url.includes('/api/v1/sync/pull')) {
        syncCalled = true
        return {
          ok: true,
          status: 200,
          json: async () => ({ records: [], next_since: 0, has_more: false }),
        }
      }
      return { ok: false, status: 404, json: async () => ({}) }
    })

    const wrapper = mount(ArchiveView)
    await flushPromises()

    const syncBtn = wrapper.find('.cs-btn-sync')
    await syncBtn.trigger('click')
    for (let i = 0; i < 10; i++) {
      await flushPromises()
      await new Promise((r) => setTimeout(r, 10))
    }

    expect(syncCalled).toBe(true)
  })
})
