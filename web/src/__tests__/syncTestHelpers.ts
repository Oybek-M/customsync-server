import { hkdf, sealGcm, hexToBytes, bytesToBase64 } from '../crypto/primitives'
import { loadTestVectors } from './testVectorsHelper'
import type { EncryptedRecord } from '../db/schema'

export async function getContentKey(): Promise<Uint8Array> {
  const vectors = loadTestVectors()
  const wrapCase = vectors.key_wrap.cases.find(
    (c: any) => c.name.includes('UTF-8') || c.iterations === 1000
  ) || vectors.key_wrap.cases[0]

  const master = hexToBytes(wrapCase.master_hex)
  return hkdf(master, 'customsync-content-v1')
}

export async function createEncryptedRecord(
  contentKey: Uint8Array,
  fields: {
    seq: number
    record_id: string
    kind?: string
    account_hash?: string
    peer_hash?: string
    msg_id?: number
    occurred_at?: number
    observed_at?: number
    device_id?: string
    json?: any
    target_record_id?: string | null
    corruptPayload?: boolean
  }
): Promise<EncryptedRecord> {
  const nonce = crypto.getRandomValues(new Uint8Array(12))
  const json = fields.json ?? { text: `Message for ${fields.record_id}`, msg_id: fields.msg_id ?? fields.seq }
  const plaintext = new TextEncoder().encode(JSON.stringify(json))

  let payloadBytes = await sealGcm(contentKey, nonce, plaintext)
  if (fields.corruptPayload) {
    // Tamper with tag/ciphertext to produce corrupt decryption
    payloadBytes = new Uint8Array(payloadBytes)
    payloadBytes[0] ^= 0xff
  }

  return {
    seq: fields.seq,
    record_id: fields.record_id,
    kind: fields.kind ?? 'deleted',
    account_hash: fields.account_hash ?? 'acc-hash-1',
    peer_hash: fields.peer_hash ?? 'peer-hash-1',
    msg_id: fields.msg_id ?? fields.seq,
    occurred_at: fields.occurred_at ?? (1700000000 + fields.seq),
    observed_at: fields.observed_at ?? (1700000000 + fields.seq),
    device_id: fields.device_id ?? 'dev-test',
    nonce: bytesToBase64(nonce),
    payload: bytesToBase64(payloadBytes),
    target_record_id: fields.target_record_id ?? null,
    media_hashes: null,
  }
}
