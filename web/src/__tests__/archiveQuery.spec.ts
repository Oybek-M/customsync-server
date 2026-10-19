import { describe, it, expect, beforeEach, vi } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import { useArchiveQuery } from '../composables/useArchiveQuery'
import { useVaultStore } from '../stores/vault'
import type { CryptoWorkerClient, DecryptedIndexedRecord } from '../crypto/worker-api'

function createDeferredPromise<T>() {
  let resolve!: (value: T) => void
  let reject!: (reason?: any) => void
  const promise = new Promise<T>((res, rej) => {
    resolve = res
    reject = rej
  })
  return { promise, resolve, reject }
}

function createMockRecord(id: string, seq = 1): DecryptedIndexedRecord {
  return {
    record_id: id,
    kind: 'deleted',
    peer_hash: 'peer-hash-1234',
    msg_id: seq,
    occurred_at: 1700000000,
    seq,
    json: { text: `Text for ${id}` },
  }
}

describe('useArchiveQuery composable', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
  })

  it('stale generation: starting a reset while a page is in flight drops the late page', async () => {
    const deferred1 = createDeferredPromise<DecryptedIndexedRecord[]>()
    const deferred2 = createDeferredPromise<DecryptedIndexedRecord[]>()

    let queryCallCount = 0
    const mockWorker = {
      indexStats: vi.fn().mockResolvedValue({
        count: 10,
        corrupt: 0,
        max_seq: 10,
        newer: 0,
      }),
      indexQuery: vi.fn().mockImplementation(() => {
        queryCallCount++
        if (queryCallCount === 1) {
          return deferred1.promise
        }
        return deferred2.promise
      }),
    } as unknown as CryptoWorkerClient

    const query = useArchiveQuery({
      getWorker: () => mockWorker,
      limit: 10,
    })

    // 1. First reset starts (generation 1)
    const reset1Promise = query.reset()
    await Promise.resolve()
    await Promise.resolve()
    expect(query.loading.value).toBe(true)
    expect(query.generation.value).toBe(1)
    expect(queryCallCount).toBe(1)

    // 2. While first query is still in flight, second reset starts (generation 2)
    const reset2Promise = query.reset()
    await Promise.resolve()
    await Promise.resolve()
    expect(query.generation.value).toBe(2)
    expect(queryCallCount).toBe(2)

    // 3. First (stale) query resolves
    deferred1.resolve([createMockRecord('rec-stale-gen1', 1)])
    await reset1Promise

    // Rows must NOT have rec-stale-gen1
    expect(query.rows.value).toEqual([])

    // 4. Second query resolves
    deferred2.resolve([createMockRecord('rec-fresh-gen2', 2)])
    await reset2Promise

    // Rows must contain only gen2
    expect(query.rows.value.map((r) => r.record_id)).toEqual(['rec-fresh-gen2'])
  })

  it('a loadMore page that resolves after a reset is dropped', async () => {
    const late = createDeferredPromise<DecryptedIndexedRecord[]>()
    let call = 0
    const mockWorker = {
      indexStats: vi.fn().mockResolvedValue({ count: 3, corrupt: 0, max_seq: 3, newer: 0 }),
      indexQuery: vi.fn().mockImplementation(() => {
        call++
        if (call === 1) return Promise.resolve([createMockRecord('first', 1)])
        if (call === 2) return late.promise
        return Promise.resolve([createMockRecord('after-reset', 3)])
      }),
    } as unknown as CryptoWorkerClient
    const query = useArchiveQuery({ getWorker: () => mockWorker, limit: 1 })

    await query.reset()
    const more = query.loadMore()
    await Promise.resolve()
    await query.reset()
    late.resolve([createMockRecord('stale-more', 2)])
    await more

    expect(query.rows.value.map((r) => r.record_id)).toEqual(['after-reset'])
  })

  it('a failed query is reported instead of looking like an empty archive', async () => {
    let fail = true
    const mockWorker = {
      indexStats: vi.fn().mockResolvedValue({ count: 3, corrupt: 0, max_seq: 3, newer: 0 }),
      indexQuery: vi.fn().mockImplementation(() =>
        fail ? Promise.reject(new Error('worker timeout')) : Promise.resolve([createMockRecord('ok', 1)])
      ),
    } as unknown as CryptoWorkerClient
    const query = useArchiveQuery({ getWorker: () => mockWorker, limit: 1 })

    await query.reset()
    expect(query.error.value).toContain('worker timeout')

    fail = false
    await query.reset()
    expect(query.error.value).toBe('')

    fail = true
    await query.loadMore()
    expect(query.error.value).toContain('worker timeout')
    expect(query.rows.value.map((r) => r.record_id)).toEqual(['ok'])
  })

  it('loadMore while loading is a no-op and exhausted stops further calls', async () => {
    const deferred = createDeferredPromise<DecryptedIndexedRecord[]>()
    const queryCalls: any[] = []

    const mockWorker = {
      indexStats: vi.fn().mockResolvedValue({
        count: 5,
        corrupt: 0,
        max_seq: 5,
        newer: 0,
      }),
      indexQuery: vi.fn().mockImplementation((params) => {
        queryCalls.push(params)
        return deferred.promise
      }),
    } as unknown as CryptoWorkerClient

    const query = useArchiveQuery({
      getWorker: () => mockWorker,
      limit: 10,
    })

    // 1. Call reset -> loading becomes true
    const resetPromise = query.reset()
    await Promise.resolve()
    await Promise.resolve()
    expect(query.loading.value).toBe(true)
    expect(queryCalls.length).toBe(1)

    // 2. loadMore while loading is a no-op
    await query.loadMore()
    expect(queryCalls.length).toBe(1)

    // 3. Resolve page with 3 items (< limit of 10)
    deferred.resolve([
      createMockRecord('rec-1', 1),
      createMockRecord('rec-2', 2),
      createMockRecord('rec-3', 3),
    ])
    await resetPromise

    expect(query.loading.value).toBe(false)
    expect(query.exhausted.value).toBe(true)
    expect(query.rows.value.length).toBe(3)

    // 4. loadMore when exhausted is true is a no-op
    await query.loadMore()
    expect(queryCalls.length).toBe(1)
  })

  it('vault lock clears rows and state', async () => {
    const vaultStore = useVaultStore()
    vaultStore.locked = false

    const mockWorker = {
      indexStats: vi.fn().mockResolvedValue({
        count: 2,
        corrupt: 0,
        max_seq: 2,
        newer: 0,
      }),
      indexQuery: vi.fn().mockResolvedValue([
        createMockRecord('rec-1', 1),
        createMockRecord('rec-2', 2),
      ]),
    } as unknown as CryptoWorkerClient

    const query = useArchiveQuery({
      getWorker: () => mockWorker,
      limit: 10,
    })

    await query.reset()
    expect(query.rows.value.length).toBe(2)

    // Lock vault
    vaultStore.lock()

    // Watcher must have cleared rows immediately
    expect(query.rows.value).toEqual([])
    expect(query.snapshot.value).toBeNull()
  })
})
