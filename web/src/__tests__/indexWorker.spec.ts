// @vitest-environment node
import { describe, it, expect, beforeEach } from 'vitest'
import { handleMessage, createInitialWorkerState } from '../crypto/worker'
import { loadTestVectors } from './testVectorsHelper'
import { getContentKey, createEncryptedRecord } from './syncTestHelpers'

describe('Worker in-memory decrypted index', () => {
  let contentKey: Uint8Array
  const vectors = loadTestVectors()
  const wrapCase = vectors.key_wrap.cases.find(
    (c: any) => c.name.includes('UTF-8') || c.iterations === 1000
  ) || vectors.key_wrap.cases[0]

  beforeEach(async () => {
    contentKey = await getContentKey()
  })

  async function unlockWorker(state: any) {
    const res = await handleMessage(state, {
      id: 1,
      type: 'unlock',
      passphrase: wrapCase.passphrase,
      wrap: wrapCase,
    })
    expect(res.ok).toBe(true)
  }

  it('rejects index operations while locked', async () => {
    const state = createInitialWorkerState()

    const beginRes = await handleMessage(state, { id: 1, type: 'indexBegin' })
    expect(beginRes).toEqual({ id: 1, ok: false, error: 'locked' })

    const addRes = await handleMessage(state, { id: 2, type: 'indexAdd', records: [] })
    expect(addRes).toEqual({ id: 2, ok: false, error: 'locked' })

    const queryRes = await handleMessage(state, { id: 3, type: 'indexQuery', limit: 10 })
    expect(queryRes).toEqual({ id: 3, ok: false, error: 'locked' })

    const statsRes = await handleMessage(state, { id: 4, type: 'indexStats' })
    expect(statsRes).toEqual({ id: 4, ok: false, error: 'locked' })
  })

  it('decrypts records, returns newest first, and performs keyset pagination with tie-breaking seq', async () => {
    const state = createInitialWorkerState()
    await unlockWorker(state)

    await handleMessage(state, { id: 2, type: 'indexBegin' })

    // Create 4 records:
    // rec1: occurred_at: 100, seq: 1
    // rec2: occurred_at: 100, seq: 2 (same timestamp, higher seq)
    // rec3: occurred_at: 90,  seq: 3
    // rec4: occurred_at: 110, seq: 4 (newest timestamp)
    const rec1 = await createEncryptedRecord(contentKey, {
      seq: 1,
      record_id: 'rec-1',
      occurred_at: 100,
      json: { text: 'Message 1' },
    })
    const rec2 = await createEncryptedRecord(contentKey, {
      seq: 2,
      record_id: 'rec-2',
      occurred_at: 100,
      json: { text: 'Message 2' },
    })
    const rec3 = await createEncryptedRecord(contentKey, {
      seq: 3,
      record_id: 'rec-3',
      occurred_at: 90,
      json: { text: 'Message 3' },
    })
    const rec4 = await createEncryptedRecord(contentKey, {
      seq: 4,
      record_id: 'rec-4',
      occurred_at: 110,
      json: { text: 'Message 4' },
    })

    const addRes = await handleMessage(state, {
      id: 3,
      type: 'indexAdd',
      records: [rec1, rec2, rec3, rec4],
    })
    expect(addRes.ok).toBe(true)

    // Expected order newest first:
    // 1. rec4 (110, seq 4)
    // 2. rec2 (100, seq 2)
    // 3. rec1 (100, seq 1)
    // 4. rec3 (90, seq 3)

    // Page 1: limit 2
    const page1 = await handleMessage(state, {
      id: 4,
      type: 'indexQuery',
      limit: 2,
    })
    expect(page1.ok).toBe(true)
    expect(page1.rows.length).toBe(2)
    expect(page1.rows[0].record_id).toBe('rec-4')
    expect(page1.rows[1].record_id).toBe('rec-2')
    expect(page1.rows[0].json).toEqual({ text: 'Message 4' })

    // Page 2: before = [100, 2] (the last item of page 1), limit 2
    const page2 = await handleMessage(state, {
      id: 5,
      type: 'indexQuery',
      before: [page1.rows[1].occurred_at, page1.rows[1].seq],
      limit: 2,
    })
    expect(page2.ok).toBe(true)
    expect(page2.rows.length).toBe(2)
    expect(page2.rows[0].record_id).toBe('rec-1')
    expect(page2.rows[1].record_id).toBe('rec-3')

    // Page 3: before = [90, 3] -> should be empty
    const page3 = await handleMessage(state, {
      id: 6,
      type: 'indexQuery',
      before: [page2.rows[1].occurred_at, page2.rows[1].seq],
      limit: 2,
    })
    expect(page3.ok).toBe(true)
    expect(page3.rows.length).toBe(0)
  })

  it('lock drops the decrypted index, so a later unlock starts empty', async () => {
    const state = createInitialWorkerState()
    await unlockWorker(state)
    await handleMessage(state, { id: 2, type: 'indexBegin' })
    const rec = await createEncryptedRecord(contentKey, {
      seq: 1,
      record_id: 'rec-secret',
      occurred_at: 100,
      json: { text: 'secret text' },
    })
    await handleMessage(state, { id: 3, type: 'indexAdd', records: [rec] })

    expect((await handleMessage(state, { id: 4, type: 'lock' })).ok).toBe(true)
    expect(JSON.stringify(state.index ?? null)).not.toContain('secret text')

    await unlockWorker(state)
    const stats = await handleMessage(state, { id: 5, type: 'indexStats' })
    expect(stats).toEqual({ id: 5, ok: true, count: 0, corrupt: 0, max_seq: 0, newer: 0 })
    const rows = await handleMessage(state, { id: 6, type: 'indexQuery', limit: 10 })
    expect(rows.rows).toEqual([])
  })

  it('filters by kind and peer_hash', async () => {
    const state = createInitialWorkerState()
    await unlockWorker(state)
    await handleMessage(state, { id: 2, type: 'indexBegin' })

    const recA = await createEncryptedRecord(contentKey, {
      seq: 1,
      record_id: 'rec-a',
      kind: 'deleted',
      peer_hash: 'peer-user-1',
    })
    const recB = await createEncryptedRecord(contentKey, {
      seq: 2,
      record_id: 'rec-b',
      kind: 'edited',
      peer_hash: 'peer-user-1',
    })
    const recC = await createEncryptedRecord(contentKey, {
      seq: 3,
      record_id: 'rec-c',
      kind: 'deleted',
      peer_hash: 'peer-user-2',
    })

    await handleMessage(state, {
      id: 3,
      type: 'indexAdd',
      records: [recA, recB, recC],
    })

    // Filter by kind: deleted
    const deletedRes = await handleMessage(state, {
      id: 4,
      type: 'indexQuery',
      kind: 'deleted',
      limit: 10,
    })
    expect(deletedRes.rows.map((r: any) => r.record_id)).toEqual(['rec-c', 'rec-a'])

    // Filter by peer_hash: peer-user-1
    const peerRes = await handleMessage(state, {
      id: 5,
      type: 'indexQuery',
      peer_hash: 'peer-user-1',
      limit: 10,
    })
    expect(peerRes.rows.map((r: any) => r.record_id)).toEqual(['rec-b', 'rec-a'])

    // Both filters
    const bothRes = await handleMessage(state, {
      id: 6,
      type: 'indexQuery',
      kind: 'deleted',
      peer_hash: 'peer-user-1',
      limit: 10,
    })
    expect(bothRes.rows.map((r: any) => r.record_id)).toEqual(['rec-a'])
  })

  it('counts corrupt records without throwing and handles tombstone removal in index', async () => {
    const state = createInitialWorkerState()
    await unlockWorker(state)
    await handleMessage(state, { id: 2, type: 'indexBegin' })

    const recValid = await createEncryptedRecord(contentKey, {
      seq: 1,
      record_id: 'rec-valid-1',
    })
    const recCorrupt = await createEncryptedRecord(contentKey, {
      seq: 2,
      record_id: 'rec-corrupt-1',
      corruptPayload: true,
    })

    const addRes = await handleMessage(state, {
      id: 3,
      type: 'indexAdd',
      records: [recValid, recCorrupt],
    })
    expect(addRes.ok).toBe(true)

    const statsRes = await handleMessage(state, { id: 4, type: 'indexStats' })
    expect(statsRes.ok).toBe(true)
    expect(statsRes.count).toBe(1)
    expect(statsRes.corrupt).toBe(1)

    // Tombstone arrives removing rec-valid-1
    const tombstone = await createEncryptedRecord(contentKey, {
      seq: 3,
      record_id: 'rec-tombstone',
      kind: 'tombstone',
      target_record_id: 'rec-valid-1',
    })

    await handleMessage(state, {
      id: 5,
      type: 'indexAdd',
      records: [tombstone],
    })

    const queryRes = await handleMessage(state, {
      id: 6,
      type: 'indexQuery',
      limit: 10,
    })
    expect(queryRes.rows.length).toBe(0)

    const statsAfterTombstone = await handleMessage(state, { id: 7, type: 'indexStats' })
    expect(statsAfterTombstone.count).toBe(0)
  })

  it('lock empties the index and drops all plaintext from memory', async () => {
    const state = createInitialWorkerState()
    await unlockWorker(state)
    await handleMessage(state, { id: 2, type: 'indexBegin' })

    const rec = await createEncryptedRecord(contentKey, {
      seq: 1,
      record_id: 'rec-secret-1',
      json: { text: 'Highly sensitive chat message' },
    })
    await handleMessage(state, { id: 3, type: 'indexAdd', records: [rec] })

    const queryBeforeLock = await handleMessage(state, { id: 4, type: 'indexQuery', limit: 10 })
    expect(queryBeforeLock.rows.length).toBe(1)

    // Lock
    const lockRes = await handleMessage(state, { id: 5, type: 'lock' })
    expect(lockRes.ok).toBe(true)

    // Query after lock must return locked
    const queryAfterLock = await handleMessage(state, { id: 6, type: 'indexQuery', limit: 10 })
    expect(queryAfterLock).toEqual({ id: 6, ok: false, error: 'locked' })
  })

  it('desc and asc paging over >= 1 000 rows with duplicate occurred_at values gives every row exactly once', async () => {
    const state = createInitialWorkerState()
    await unlockWorker(state)
    await handleMessage(state, { id: 2, type: 'indexBegin' })

    const TOTAL = 1050
    // Generate sample valid encrypted payload once
    const sample = await createEncryptedRecord(contentKey, {
      seq: 1,
      record_id: 'sample',
      occurred_at: 100,
      json: { text: 'test payload' },
    })

    // Generate 1050 records with duplicate occurred_at values: e.g. every 5 records share the same occurred_at
    const records = []
    for (let i = 0; i < TOTAL; i++) {
      const seq = i + 1
      const occurred_at = 1700000000 + Math.floor(i / 5) * 10
      records.push({
        seq,
        record_id: `rec-batch-${seq}`,
        kind: 'deleted',
        account_hash: 'acc-1',
        peer_hash: 'peer-1',
        msg_id: seq,
        occurred_at,
        observed_at: occurred_at,
        device_id: 'dev-1',
        nonce: sample.nonce,
        payload: sample.payload,
        target_record_id: null,
        media_hashes: null,
      })
    }
    // Add in batches to index
    const batchSize = 350
    for (let b = 0; b < records.length; b += batchSize) {
      await handleMessage(state, {
        id: 3,
        type: 'indexAdd',
        records: records.slice(b, b + batchSize),
      })
    }

    const stats = await handleMessage(state, { id: 4, type: 'indexStats' })
    expect(stats.count).toBe(TOTAL)
    expect(stats.max_seq).toBe(TOTAL)

    for (const pageSize of [1, 7, 50]) {
      // 1. Test DESC paging
      const descRows: any[] = []
      let lastAfter: [number, number] | undefined = undefined

      while (true) {
        const pageRes: any = await handleMessage(state, {
          id: 5,
          type: 'indexQuery',
          order: 'desc',
          after: lastAfter,
          limit: pageSize,
        })
        expect(pageRes.ok).toBe(true)
        if (pageRes.rows.length === 0) break
        descRows.push(...pageRes.rows)
        const last = pageRes.rows[pageRes.rows.length - 1]
        lastAfter = [last.occurred_at, last.seq]
      }

      expect(descRows.length).toBe(TOTAL)
      // Check every ID is present exactly once
      const descIds = new Set(descRows.map((r) => r.record_id))
      expect(descIds.size).toBe(TOTAL)

      // Check strictly descending
      for (let i = 1; i < descRows.length; i++) {
        const prev = descRows[i - 1]
        const curr = descRows[i]
        const isOlder =
          prev.occurred_at > curr.occurred_at ||
          (prev.occurred_at === curr.occurred_at && prev.seq > curr.seq)
        expect(isOlder).toBe(true)
      }

      // 2. Test ASC paging
      const ascRows: any[] = []
      lastAfter = undefined

      while (true) {
        const pageRes: any = await handleMessage(state, {
          id: 6,
          type: 'indexQuery',
          order: 'asc',
          after: lastAfter,
          limit: pageSize,
        })
        expect(pageRes.ok).toBe(true)
        if (pageRes.rows.length === 0) break
        ascRows.push(...pageRes.rows)
        const last = pageRes.rows[pageRes.rows.length - 1]
        lastAfter = [last.occurred_at, last.seq]
      }

      expect(ascRows.length).toBe(TOTAL)
      const ascIds = new Set(ascRows.map((r) => r.record_id))
      expect(ascIds.size).toBe(TOTAL)

      // Check strictly ascending
      for (let i = 1; i < ascRows.length; i++) {
        const prev = ascRows[i - 1]
        const curr = ascRows[i]
        const isNewer =
          curr.occurred_at > prev.occurred_at ||
          (curr.occurred_at === prev.occurred_at && curr.seq > prev.seq)
        expect(isNewer).toBe(true)
      }
    }
  }, 30000)

  it('max_seq snapshot excludes rows added after the snapshot and indexStats counts them', async () => {
    const state = createInitialWorkerState()
    await unlockWorker(state)
    await handleMessage(state, { id: 2, type: 'indexBegin' })

    const initial = await Promise.all([
      createEncryptedRecord(contentKey, { seq: 1, record_id: 'rec-1', occurred_at: 100 }),
      createEncryptedRecord(contentKey, { seq: 2, record_id: 'rec-2', occurred_at: 200 }),
      createEncryptedRecord(contentKey, { seq: 3, record_id: 'rec-3', occurred_at: 300 }),
      createEncryptedRecord(contentKey, { seq: 4, record_id: 'rec-4', occurred_at: 400 }),
      createEncryptedRecord(contentKey, { seq: 5, record_id: 'rec-5', occurred_at: 500 }),
    ])
    await handleMessage(state, { id: 3, type: 'indexAdd', records: initial })

    const stats1 = await handleMessage(state, { id: 4, type: 'indexStats' })
    expect(stats1.max_seq).toBe(5)
    const snapshotSeq = stats1.max_seq

    // Query page 1 with snapshot
    const page1 = await handleMessage(state, {
      id: 5,
      type: 'indexQuery',
      order: 'desc',
      max_seq: snapshotSeq,
      limit: 2,
    })
    expect(page1.rows.map((r: any) => r.record_id)).toEqual(['rec-5', 'rec-4'])

    // Later: 3 new records arrive with higher sequence and newer timestamps
    const later = await Promise.all([
      createEncryptedRecord(contentKey, { seq: 6, record_id: 'rec-6', occurred_at: 600 }),
      createEncryptedRecord(contentKey, { seq: 7, record_id: 'rec-7', occurred_at: 700 }),
      createEncryptedRecord(contentKey, { seq: 8, record_id: 'rec-8', occurred_at: 800 }),
    ])
    await handleMessage(state, { id: 6, type: 'indexAdd', records: later })

    // indexStats with since_seq: snapshotSeq counts new records
    const stats2 = await handleMessage(state, {
      id: 7,
      type: 'indexStats',
      since_seq: snapshotSeq,
    })
    expect(stats2.max_seq).toBe(8)
    expect(stats2.newer).toBe(3)

    // Query page 2 with original snapshot
    const lastRow = page1.rows[page1.rows.length - 1]
    const page2 = await handleMessage(state, {
      id: 8,
      type: 'indexQuery',
      order: 'desc',
      after: [lastRow.occurred_at, lastRow.seq],
      max_seq: snapshotSeq,
      limit: 10,
    })
    // rec-6, rec-7, rec-8 must NOT appear!
    expect(page2.rows.map((r: any) => r.record_id)).toEqual(['rec-3', 'rec-2', 'rec-1'])
  })

  it('before alias still behaves like Task 6', async () => {
    const state = createInitialWorkerState()
    await unlockWorker(state)
    await handleMessage(state, { id: 2, type: 'indexBegin' })

    const records = await Promise.all([
      createEncryptedRecord(contentKey, { seq: 1, record_id: 'rec-1', occurred_at: 100 }),
      createEncryptedRecord(contentKey, { seq: 2, record_id: 'rec-2', occurred_at: 200 }),
      createEncryptedRecord(contentKey, { seq: 3, record_id: 'rec-3', occurred_at: 300 }),
    ])
    await handleMessage(state, { id: 3, type: 'indexAdd', records })

    // using before: [200, 2] in Task 6 style
    const res = await handleMessage(state, {
      id: 4,
      type: 'indexQuery',
      before: [200, 2],
      limit: 10,
    })
    expect(res.ok).toBe(true)
    expect(res.rows.map((r: any) => r.record_id)).toEqual(['rec-1'])
  })

  it('binary-search start correctly tie-breaks when occurred_at is equal at seq boundary', async () => {
    const state = createInitialWorkerState()
    await unlockWorker(state)
    await handleMessage(state, { id: 2, type: 'indexBegin' })

    // 5 records all having the exact same occurred_at = 1000
    const records = await Promise.all([
      createEncryptedRecord(contentKey, { seq: 1, record_id: 'rec-tie-1', occurred_at: 1000 }),
      createEncryptedRecord(contentKey, { seq: 2, record_id: 'rec-tie-2', occurred_at: 1000 }),
      createEncryptedRecord(contentKey, { seq: 3, record_id: 'rec-tie-3', occurred_at: 1000 }),
      createEncryptedRecord(contentKey, { seq: 4, record_id: 'rec-tie-4', occurred_at: 1000 }),
      createEncryptedRecord(contentKey, { seq: 5, record_id: 'rec-tie-5', occurred_at: 1000 }),
    ])
    await handleMessage(state, { id: 3, type: 'indexAdd', records })

    // In DESC: sorted order is [rec-tie-5, rec-tie-4, rec-tie-3, rec-tie-2, rec-tie-1]
    // after: [1000, 3] must start strictly at rec-tie-2 (seq < 3)
    const descRes = await handleMessage(state, {
      id: 4,
      type: 'indexQuery',
      order: 'desc',
      after: [1000, 3],
      limit: 10,
    })
    expect(descRes.rows.map((r: any) => r.record_id)).toEqual(['rec-tie-2', 'rec-tie-1'])

    // In ASC: sorted order is [rec-tie-1, rec-tie-2, rec-tie-3, rec-tie-4, rec-tie-5]
    // after: [1000, 3] must start strictly at rec-tie-4 (seq > 3)
    const ascRes = await handleMessage(state, {
      id: 5,
      type: 'indexQuery',
      order: 'asc',
      after: [1000, 3],
      limit: 10,
    })
    expect(ascRes.rows.map((r: any) => r.record_id)).toEqual(['rec-tie-4', 'rec-tie-5'])
  })
})
