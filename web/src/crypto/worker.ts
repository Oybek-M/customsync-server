import {
  hkdf,
  openGcm,
  peerHash,
  fingerprint,
  unwrapPassphrase,
  base64ToBytes,
  hexToBytes,
  type KeyWrapData,
} from './primitives'

export interface DerivedKeys {
  contentKey: Uint8Array
  mediaKey: Uint8Array
  peerKey: Uint8Array
  accountKey: Uint8Array
}

export interface WorkerState {
  locked: boolean
  keys: DerivedKeys | null
  fingerprint: string | null
}

export function createInitialWorkerState(): WorkerState {
  return {
    locked: true,
    keys: null,
    fingerprint: null,
  }
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

        // Master kalit xotiradan zudlik bilan o'chiriladi
        master.fill(0)

        // Eski kalitlar bo'lsa ularni ham tozalaymiz
        if (state.keys) {
          state.keys.contentKey.fill(0)
          state.keys.mediaKey.fill(0)
          state.keys.peerKey.fill(0)
          state.keys.accountKey.fill(0)
        }

        state.locked = false
        state.keys = { contentKey, mediaKey, peerKey, accountKey }
        state.fingerprint = fp

        return { id, ok: true, fingerprint: fp }
      } catch {
        master.fill(0)
        return { id, ok: false, error: 'derivation_failed' }
      }
    }

    case 'lock': {
      if (state.keys) {
        state.keys.contentKey.fill(0)
        state.keys.mediaKey.fill(0)
        state.keys.peerKey.fill(0)
        state.keys.accountKey.fill(0)
      }
      state.keys = null
      state.locked = true
      state.fingerprint = null
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
