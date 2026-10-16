import 'fake-indexeddb/auto'
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import { useAuthStore } from '../stores/auth'
import { useVaultStore } from '../stores/vault'
import { clearTokens, setAccessTokenForTest } from '../api/client'
import {
  closeDatabase,
  wipeDatabase,
  getCursor,
  getAllRecords,
  getRecord,
  ensureDevice,
} from '../db/schema'
import { syncArchive } from '../db/sync'
import { getContentKey, createEncryptedRecord } from './syncTestHelpers'

describe('db/sync.ts & schema.ts', () => {
  let contentKey: Uint8Array
  let originalFetch: any

  beforeEach(async () => {
    setActivePinia(createPinia())
    localStorage.clear()
    clearTokens()
    const authStore = useAuthStore()
    authStore.setCredentials('dev-device-1', 'mock-refresh-token')
    setAccessTokenForTest('mock-access-token')
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

  it('syncs two pages (has_more true then false) and updates cursor to last next_since', async () => {
    const rec1 = await createEncryptedRecord(contentKey, { seq: 1, record_id: 'rec-1' })
    const rec2 = await createEncryptedRecord(contentKey, { seq: 2, record_id: 'rec-2' })
    const rec3 = await createEncryptedRecord(contentKey, { seq: 3, record_id: 'rec-3' })

    const calls: string[] = []
    globalThis.fetch = vi.fn().mockImplementation(async (url: string) => {
      calls.push(url)
      if (url.includes('since=0')) {
        return {
          ok: true,
          status: 200,
          json: async () => ({
            records: [rec1, rec2],
            next_since: 2,
            has_more: true,
          }),
        }
      }
      if (url.includes('since=2')) {
        return {
          ok: true,
          status: 200,
          json: async () => ({
            records: [rec3],
            next_since: 3,
            has_more: false,
          }),
        }
      }
      return { ok: false, status: 400, json: async () => ({}) }
    })

    const result = await syncArchive()
    expect(result.fetched).toBe(3)
    expect(result.cursor).toBe(3)

    const storedCursor = await getCursor()
    expect(storedCursor).toBe(3)

    const storedRecords = await getAllRecords()
    expect(storedRecords.length).toBe(3)
    expect(storedRecords.map((r) => r.record_id)).toEqual(['rec-1', 'rec-2', 'rec-3'])
  })

  it('keeps cursor at page 1 if page 2 fails, re-run fetches page 2 without duplicates', async () => {
    const rec1 = await createEncryptedRecord(contentKey, { seq: 1, record_id: 'rec-1' })
    const rec2 = await createEncryptedRecord(contentKey, { seq: 2, record_id: 'rec-2' })
    const rec3 = await createEncryptedRecord(contentKey, { seq: 3, record_id: 'rec-3' })

    let attempt = 0
    globalThis.fetch = vi.fn().mockImplementation(async (url: string) => {
      if (url.includes('since=0')) {
        return {
          ok: true,
          status: 200,
          json: async () => ({
            records: [rec1, rec2],
            next_since: 2,
            has_more: true,
          }),
        }
      }
      if (url.includes('since=2')) {
        attempt++
        if (attempt === 1) {
          // Failure on page 2 write or network
          throw new Error('Network failure while fetching page 2')
        }
        return {
          ok: true,
          status: 200,
          json: async () => ({
            records: [rec3],
            next_since: 3,
            has_more: false,
          }),
        }
      }
      return { ok: false, status: 400, json: async () => ({}) }
    })

    // 1st run: fails on page 2
    await expect(syncArchive()).rejects.toThrow(/Network failure|network_error/)

    // Cursor must still be at page 1's next_since (2)
    expect(await getCursor()).toBe(2)
    let records = await getAllRecords()
    expect(records.length).toBe(2)

    // 2nd run: resumes from cursor 2, fetches page 2, no duplicates
    const resumeResult = await syncArchive()
    expect(resumeResult.cursor).toBe(3)
    expect(await getCursor()).toBe(3)

    records = await getAllRecords()
    expect(records.length).toBe(3)
    expect(records.map((r) => r.record_id)).toEqual(['rec-1', 'rec-2', 'rec-3'])
  })

  it('completes via has_more even when server clamps limit', async () => {
    const rec1 = await createEncryptedRecord(contentKey, { seq: 1, record_id: 'rec-1' })
    const rec2 = await createEncryptedRecord(contentKey, { seq: 2, record_id: 'rec-2' })

    globalThis.fetch = vi.fn().mockImplementation(async (url: string) => {
      // Requested limit might be 500, but server returns only 1 item per page
      if (url.includes('since=0')) {
        return {
          ok: true,
          status: 200,
          json: async () => ({
            records: [rec1],
            next_since: 1,
            has_more: true,
          }),
        }
      }
      if (url.includes('since=1')) {
        return {
          ok: true,
          status: 200,
          json: async () => ({
            records: [rec2],
            next_since: 2,
            has_more: false,
          }),
        }
      }
      return { ok: false, status: 400, json: async () => ({}) }
    })

    const res = await syncArchive()
    expect(res.fetched).toBe(2)
    expect(res.cursor).toBe(2)
    expect(await getCursor()).toBe(2)
  })

  it('removes target record when tombstone arrives and does not store tombstone itself', async () => {
    const rec1 = await createEncryptedRecord(contentKey, { seq: 1, record_id: 'rec-target-1' })
    const rec2 = await createEncryptedRecord(contentKey, { seq: 2, record_id: 'rec-survivor' })
    const tombstone = await createEncryptedRecord(contentKey, {
      seq: 3,
      record_id: 'rec-tombstone-1',
      kind: 'tombstone',
      target_record_id: 'rec-target-1',
    })

    globalThis.fetch = vi.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => ({
        records: [rec1, rec2, tombstone],
        next_since: 3,
        has_more: false,
      }),
    } as any)

    await syncArchive()

    const records = await getAllRecords()
    // rec-target-1 removed, tombstone not stored
    expect(records.length).toBe(1)
    expect(records[0].record_id).toBe('rec-survivor')

    expect(await getRecord('rec-target-1')).toBeUndefined()
    expect(await getRecord('rec-tombstone-1')).toBeUndefined()
  })

  it('aborts after current batch when signal is triggered', async () => {
    const rec1 = await createEncryptedRecord(contentKey, { seq: 1, record_id: 'rec-1' })
    const rec2 = await createEncryptedRecord(contentKey, { seq: 2, record_id: 'rec-2' })

    const controller = new AbortController()
    let batchCount = 0

    globalThis.fetch = vi.fn().mockImplementation(async (_url: string) => {
      batchCount++
      if (batchCount === 1) {
        controller.abort()
        return {
          ok: true,
          status: 200,
          json: async () => ({
            records: [rec1],
            next_since: 1,
            has_more: true,
          }),
        }
      }
      return {
        ok: true,
        status: 200,
        json: async () => ({
          records: [rec2],
          next_since: 2,
          has_more: false,
        }),
      }
    })

    const res = await syncArchive(controller.signal)
    // Aborted after first batch
    expect(res.cursor).toBe(1)
    expect(batchCount).toBe(1)
    expect(await getCursor()).toBe(1)
    expect((await getAllRecords()).length).toBe(1)
  })

  it('runs successfully while vault is locked (no key needed)', async () => {
    const vaultStore = useVaultStore()
    vaultStore.locked = true

    const rec1 = await createEncryptedRecord(contentKey, { seq: 1, record_id: 'rec-locked-1' })

    globalThis.fetch = vi.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => ({
        records: [rec1],
        next_since: 1,
        has_more: false,
      }),
    } as any)

    const res = await syncArchive()
    expect(res.cursor).toBe(1)
    expect(await getCursor()).toBe(1)
    expect(vaultStore.locked).toBe(true)
  })

  it('wipes database when deviceId changes', async () => {
    const rec1 = await createEncryptedRecord(contentKey, { seq: 1, record_id: 'rec-1' })

    globalThis.fetch = vi.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => ({
        records: [rec1],
        next_since: 1,
        has_more: false,
      }),
    } as any)

    await syncArchive()
    expect((await getAllRecords()).length).toBe(1)
    expect(await getCursor()).toBe(1)

    // Device changes
    await ensureDevice('different-device-id')

    // DB should have been wiped
    expect(await getCursor()).toBe(0)
    expect((await getAllRecords()).length).toBe(0)
  })
})
