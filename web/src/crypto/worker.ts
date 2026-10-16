import {
  hkdf,
  openGcm,
  sealGcm,
  pbkdf2,
  peerHash,
  fingerprint,
  unwrapPassphrase,
  base64ToBytes,
  bytesToBase64,
  hexToBytes,
  type KeyWrapData,
} from './primitives'

export interface DerivedKeys {
  contentKey: Uint8Array
  mediaKey: Uint8Array
  peerKey: Uint8Array
  accountKey: Uint8Array
}

export interface DecryptedIndexedRecord {
  record_id: string
  kind: string
  peer_hash: string
  msg_id: number
  occurred_at: number
  seq: number
  json: any
  device_id?: string
  account_hash?: string
  observed_at?: number
}

export interface WorkerIndexState {
  recordsById: Map<string, DecryptedIndexedRecord>
  sortedRecords: DecryptedIndexedRecord[]
  corruptCount: number
  maxSeq: number
}

export function createInitialIndexState(): WorkerIndexState {
  return {
    recordsById: new Map(),
    sortedRecords: [],
    corruptCount: 0,
    maxSeq: 0,
  }
}

export interface WorkerState {
  locked: boolean
  masterKey: Uint8Array | null
  keys: DerivedKeys | null
  fingerprint: string | null
  index: WorkerIndexState | null
}

export function createInitialWorkerState(): WorkerState {
  return {
    locked: true,
    masterKey: null,
    keys: null,
    fingerprint: null,
    index: null,
  }
}

export function compareNewestFirst(a: DecryptedIndexedRecord, b: DecryptedIndexedRecord): number {
  if (b.occurred_at !== a.occurred_at) {
    return b.occurred_at - a.occurred_at
  }
  return b.seq - a.seq
}

export function compareKeyDesc(
  occA: number,
  seqA: number,
  occB: number,
  seqB: number
): number {
  if (occB !== occA) {
    return occB - occA
  }
  return seqB - seqA
}

export function findFirstOlderDesc(
  records: DecryptedIndexedRecord[],
  occ: number,
  seq: number
): number {
  let low = 0
  let high = records.length
  while (low < high) {
    const mid = (low + high) >>> 1
    if (compareKeyDesc(records[mid].occurred_at, records[mid].seq, occ, seq) > 0) {
      high = mid
    } else {
      low = mid + 1
    }
  }
  return low
}

export function findFirstNotNewerDesc(
  records: DecryptedIndexedRecord[],
  occ: number,
  seq: number
): number {
  let low = 0
  let high = records.length
  while (low < high) {
    const mid = (low + high) >>> 1
    if (compareKeyDesc(records[mid].occurred_at, records[mid].seq, occ, seq) >= 0) {
      high = mid
    } else {
      low = mid + 1
    }
  }
  return low
}

function parseBytesInput(val: Uint8Array | string): Uint8Array {
  if (val instanceof Uint8Array) return val
  // Base64 vs Hex aniqlash
  if (!/^[0-9a-fA-F]+$/.test(val) || val.length % 2 !== 0) {
    return base64ToBytes(val)
  }
  // 12-bayt nonce: hex = 24 chars, base64 = 16 chars
  if (val.length === 24) return hexToBytes(val)
  if (val.length === 16) return base64ToBytes(val)

  try {
    return base64ToBytes(val)
  } catch {
    return hexToBytes(val)
  }
}

export async function handleMessage(state: WorkerState, req: any): Promise<any> {
  const id = req?.id

  if (!req || typeof req !== 'object' || !req.type) {
    return { id, ok: false, error: 'unknown_request' }
  }

  switch (req.type) {
    case 'unlock': {
      const { passphrase, wrap } = req as { passphrase?: string; wrap?: KeyWrapData }
      if (!passphrase || !wrap) {
        return { id, ok: false, error: 'wrong_passphrase' }
      }

      const master = await unwrapPassphrase(wrap, passphrase)
      if (!master) {
        return { id, ok: false, error: 'wrong_passphrase' }
      }

      try {
        const fp = await fingerprint(master)
        const [contentKey, mediaKey, peerKey, accountKey] = await Promise.all([
          hkdf(master, 'customsync-content-v1'),
          hkdf(master, 'customsync-media-v1'),
          hkdf(master, 'customsync-peer-v1'),
          hkdf(master, 'customsync-account-v1'),
        ])

        // Eski master yoki kalitlar bo'lsa tozalaymiz
        if (state.masterKey) {
          state.masterKey.fill(0)
        }
        if (state.keys) {
          state.keys.contentKey.fill(0)
          state.keys.mediaKey.fill(0)
          state.keys.peerKey.fill(0)
          state.keys.accountKey.fill(0)
        }

        state.locked = false
        state.masterKey = master // ochiq turganda worker ichida saqlanadi
        state.keys = { contentKey, mediaKey, peerKey, accountKey }
        state.fingerprint = fp

        return { id, ok: true, fingerprint: fp }
      } catch {
        master.fill(0)
        return { id, ok: false, error: 'derivation_failed' }
      }
    }

    case 'lock': {
      if (state.masterKey) {
        state.masterKey.fill(0)
        state.masterKey = null
      }
      if (state.keys) {
        state.keys.contentKey.fill(0)
        state.keys.mediaKey.fill(0)
        state.keys.peerKey.fill(0)
        state.keys.accountKey.fill(0)
      }
      state.keys = null
      state.locked = true
      state.fingerprint = null

      // Barcha ochiq matnlar va indeks xotiradan to'liq o'chiriladi
      if (state.index) {
        state.index.recordsById.clear()
        state.index.sortedRecords = []
        state.index.corruptCount = 0
        state.index = null
      }

      return { id, ok: true }
    }

    case 'openRecords': {
      if (state.locked || !state.keys) {
        return { id, ok: false, error: 'locked' }
      }

      const records = req.records as Array<{
        record_id: string
        nonce: string | Uint8Array
        payload: string | Uint8Array
      }>

      if (!Array.isArray(records)) {
        return { id, ok: true, results: [] }
      }

      const results = await Promise.all(
        records.map(async (r) => {
          try {
            const nonceBytes = parseBytesInput(r.nonce)
            const payloadBytes = parseBytesInput(r.payload)

            const plaintext = await openGcm(state.keys!.contentKey, nonceBytes, payloadBytes)
            if (!plaintext) {
              return { record_id: r.record_id, corrupt: true }
            }

            const jsonStr = new TextDecoder().decode(plaintext)
            const json = JSON.parse(jsonStr)
            return { record_id: r.record_id, json }
          } catch {
            return { record_id: r.record_id, corrupt: true }
          }
        })
      )

      return { id, ok: true, results }
    }

    case 'peerHash': {
      if (state.locked || !state.keys) {
        return { id, ok: false, error: 'locked' }
      }

      const peerId = String(req.peer_id ?? '')
      const hash = await peerHash(state.keys.peerKey, peerId)
      return { id, ok: true, peer_hash: hash }
    }

    case 'createWrap': {
      if (state.locked || !state.masterKey) {
        return { id, ok: false, error: 'locked' }
      }

      const passphrase = req.passphrase
      if (!passphrase || typeof passphrase !== 'string') {
        return { id, ok: false, error: 'invalid_passphrase' }
      }

      try {
        const salt = crypto.getRandomValues(new Uint8Array(16))
        const nonce = crypto.getRandomValues(new Uint8Array(12))
        const iterations = 600000

        const kek = await pbkdf2(passphrase, salt, iterations, 32)
        const wrappedKeyBytes = await sealGcm(kek, nonce, state.masterKey)
        kek.fill(0)

        const saltB64 = bytesToBase64(salt)
        const nonceB64 = bytesToBase64(nonce)
        const wrappedKeyB64 = bytesToBase64(wrappedKeyBytes)

        // Qayta ochib barmoq izi mosligini tekshiramiz
        const reopenedMaster = await unwrapPassphrase(
          {
            salt: saltB64,
            nonce: nonceB64,
            wrapped_key: wrappedKeyB64,
            iterations,
          },
          passphrase
        )

        if (!reopenedMaster) {
          return { id, ok: false, error: 'wrap_verification_failed' }
        }

        const reopenedFp = await fingerprint(reopenedMaster)
        reopenedMaster.fill(0)

        if (reopenedFp !== state.fingerprint) {
          return { id, ok: false, error: 'wrap_fingerprint_mismatch' }
        }

        return {
          id,
          ok: true,
          salt: saltB64,
          nonce: nonceB64,
          wrapped_key: wrappedKeyB64,
          iterations,
        }
      } catch {
        return { id, ok: false, error: 'create_wrap_failed' }
      }
    }

    case 'indexBegin': {
      if (state.locked || !state.keys) {
        return { id, ok: false, error: 'locked' }
      }
      state.index = createInitialIndexState()
      return { id, ok: true }
    }

    case 'indexAdd': {
      if (state.locked || !state.keys) {
        return { id, ok: false, error: 'locked' }
      }
      if (!state.index) {
        state.index = createInitialIndexState()
      }

      const records = req.records as any[]
      if (!Array.isArray(records)) {
        return { id, ok: true, added: 0 }
      }

      let added = 0
      for (const r of records) {
        if (r.target_record_id) {
          state.index.recordsById.delete(r.target_record_id)
        }
        if (r.kind === 'tombstone') {
          continue
        }

        try {
          const nonceBytes = parseBytesInput(r.nonce)
          const payloadBytes = parseBytesInput(r.payload)
          const plaintext = await openGcm(state.keys.contentKey, nonceBytes, payloadBytes)

          if (!plaintext) {
            state.index.corruptCount++
            continue
          }

          const json = JSON.parse(new TextDecoder().decode(plaintext))
          state.index.recordsById.set(r.record_id, {
            record_id: r.record_id,
            kind: r.kind,
            peer_hash: r.peer_hash,
            msg_id: r.msg_id,
            occurred_at: r.occurred_at,
            seq: r.seq,
            device_id: r.device_id,
            account_hash: r.account_hash,
            observed_at: r.observed_at,
            json,
          })
          if (typeof r.seq === 'number' && r.seq > state.index.maxSeq) {
            state.index.maxSeq = r.seq
          }
          added++
        } catch {
          state.index.corruptCount++
        }
      }

      state.index.sortedRecords = Array.from(state.index.recordsById.values()).sort(compareNewestFirst)

      return { id, ok: true, added, corrupt: state.index.corruptCount }
    }

    case 'indexQuery': {
      if (state.locked || !state.keys) {
        return { id, ok: false, error: 'locked' }
      }
      if (!state.index || state.index.sortedRecords.length === 0) {
        return { id, ok: true, rows: [] }
      }

      const {
        kind,
        peer_hash,
        order = 'desc',
        after,
        before,
        max_seq,
        limit = 50,
      } = req as {
        kind?: string
        peer_hash?: string
        order?: 'desc' | 'asc'
        after?: [number, number]
        before?: [number, number]
        max_seq?: number
        limit?: number
      }

      const isAsc = order === 'asc'
      // before is an alias for after in desc order
      const effectiveAfter = after ?? (!isAsc ? before : undefined)
      const rows: DecryptedIndexedRecord[] = []
      const records = state.index.sortedRecords

      if (!isAsc) {
        // desc: walk forwards from binary search start position
        let startIdx = 0
        if (effectiveAfter && Array.isArray(effectiveAfter) && effectiveAfter.length === 2) {
          startIdx = findFirstOlderDesc(records, effectiveAfter[0], effectiveAfter[1])
        }

        for (let i = startIdx; i < records.length; i++) {
          if (rows.length >= limit) break
          const rec = records[i]
          if (max_seq !== undefined && rec.seq > max_seq) continue
          if (kind && rec.kind !== kind) continue
          if (peer_hash && !rec.peer_hash.startsWith(peer_hash)) continue
          rows.push(rec)
        }
      } else {
        // asc: walk backwards from binary search start position
        let startIdx = records.length - 1
        if (effectiveAfter && Array.isArray(effectiveAfter) && effectiveAfter.length === 2) {
          const k = findFirstNotNewerDesc(records, effectiveAfter[0], effectiveAfter[1])
          startIdx = k - 1
        }

        for (let i = startIdx; i >= 0; i--) {
          if (rows.length >= limit) break
          const rec = records[i]
          if (max_seq !== undefined && rec.seq > max_seq) continue
          if (kind && rec.kind !== kind) continue
          if (peer_hash && !rec.peer_hash.startsWith(peer_hash)) continue
          rows.push(rec)
        }
      }

      return { id, ok: true, rows }
    }

    case 'indexStats': {
      if (state.locked || !state.keys) {
        return { id, ok: false, error: 'locked' }
      }
      const count = state.index ? state.index.recordsById.size : 0
      const corrupt = state.index ? state.index.corruptCount : 0
      const max_seq = state.index ? state.index.maxSeq : 0
      let newer = 0

      if (state.index && typeof req.since_seq === 'number') {
        const since = req.since_seq
        for (const rec of state.index.recordsById.values()) {
          if (rec.seq > since) {
            newer++
          }
        }
      }

      return { id, ok: true, count, corrupt, max_seq, newer }
    }

    default:
      return { id, ok: false, error: 'unknown_request' }
  }
}

// Brauzer Web Worker muhitida bo'lsa o'zini ulaydi
if (typeof self !== 'undefined' && typeof (self as any).postMessage === 'function') {
  const globalWorkerState = createInitialWorkerState()
  self.onmessage = async (e: MessageEvent) => {
    const res = await handleMessage(globalWorkerState, e.data)
    self.postMessage(res)
  }
}
