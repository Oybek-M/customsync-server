import 'fake-indexeddb/auto'
import { describe, it, expect, beforeEach, afterEach } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import { useVaultStore } from '../stores/vault'
import { CryptoWorkerClient } from '../crypto/worker-api'
import { handleMessage, createInitialWorkerState } from '../crypto/worker'
import { loadIndexFromDatabase } from '../db/indexLoader'
import {
  closeDatabase,
  wipeDatabase,
  putRecordsBatch,
  getAllRecords,
} from '../db/schema'
import { getContentKey, createEncryptedRecord } from './syncTestHelpers'
import { loadTestVectors } from './testVectorsHelper'

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

describe('db/indexLoader.ts', () => {
  let contentKey: Uint8Array
  const vectors = loadTestVectors()
  const wrapCase = vectors.key_wrap.cases.find(
    (c: any) => c.name.includes('UTF-8') || c.iterations === 1000
  ) || vectors.key_wrap.cases[0]

  beforeEach(async () => {
    setActivePinia(createPinia())
    contentKey = await getContentKey()
    await wipeDatabase()
  })

  afterEach(async () => {
    closeDatabase()
    await wipeDatabase()
  })

  it('loads records from IDB in pages, reports progress, and partial results are queryable', async () => {
    const directClient = createDirectWorkerClient()
    const vaultStore = useVaultStore()
    vaultStore.setWorkerClient(directClient.client)

    // Unlock
    await directClient.client.unlock(wrapCase.passphrase, wrapCase)
    await directClient.waitForWorker()
    vaultStore.locked = false

    // Put 10 records in IDB with secret plaintext messages
    const secretMessages = [
      'Secret message number 1',
      'Secret message number 2',
      'Secret message number 3',
    ]
    const records = await Promise.all([
      createEncryptedRecord(contentKey, { seq: 1, record_id: 'rec-1', json: { text: secretMessages[0] } }),
      createEncryptedRecord(contentKey, { seq: 2, record_id: 'rec-2', json: { text: secretMessages[1] } }),
      createEncryptedRecord(contentKey, { seq: 3, record_id: 'rec-3', json: { text: secretMessages[2] } }),
    ])

    await putRecordsBatch(records, 3)

    const progressReports: string[] = []
    await loadIndexFromDatabase({
      pageSize: 2, // 2 per page to test pagination
      onProgress: (p) => progressReports.push(p.text),
    })

    // Progress reported
    expect(progressReports.length).toBeGreaterThanOrEqual(2)
    expect(progressReports[progressReports.length - 1]).toContain('3 / 3')

    // Query worker index
    const stats = await directClient.client.indexStats()
    expect(stats.count).toBe(3)

    const query = await directClient.client.indexQuery({ limit: 10 })
    expect(query.length).toBe(3)
    expect(query.map((r: any) => r.json.text)).toContain('Secret message number 1')

    // SECURITY CHECK: Scan entire IndexedDB to prove NO plaintext exists in IDB!
    const allStored = await getAllRecords()
    expect(allStored.length).toBe(3)

    for (const stored of allStored) {
      const serialized = JSON.stringify(stored)
      for (const secret of secretMessages) {
        expect(
          serialized.includes(secret),
          `Plaintext secret "${secret}" was found inside IndexedDB stored record ${stored.record_id}!`
        ).toBe(false)
      }
    }
  })

  it('cancel stops loading', async () => {
    const directClient = createDirectWorkerClient()
    const vaultStore = useVaultStore()
    vaultStore.setWorkerClient(directClient.client)
    await directClient.client.unlock(wrapCase.passphrase, wrapCase)
    await directClient.waitForWorker()
    vaultStore.locked = false

    // Populate 6 records
    const records = await Promise.all([
      createEncryptedRecord(contentKey, { seq: 1, record_id: 'rec-1' }),
      createEncryptedRecord(contentKey, { seq: 2, record_id: 'rec-2' }),
      createEncryptedRecord(contentKey, { seq: 3, record_id: 'rec-3' }),
      createEncryptedRecord(contentKey, { seq: 4, record_id: 'rec-4' }),
      createEncryptedRecord(contentKey, { seq: 5, record_id: 'rec-5' }),
      createEncryptedRecord(contentKey, { seq: 6, record_id: 'rec-6' }),
    ])
    await putRecordsBatch(records, 6)

    const controller = new AbortController()

    let pagesLoaded = 0
    const loadPromise = loadIndexFromDatabase({
      pageSize: 2,
      signal: controller.signal,
      onProgress: () => {
        pagesLoaded++
        if (pagesLoaded === 1) {
          controller.abort()
        }
      },
    })

    await loadPromise

    // Stopped after first page
    const stats = await directClient.client.indexStats()
    expect(stats.count).toBe(2)
  })
})
