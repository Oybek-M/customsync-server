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
    expect(stats).toEqual({ id: 5, ok: true, count: 0, corrupt: 0 })
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
})
