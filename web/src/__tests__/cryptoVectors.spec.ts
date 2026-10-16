// @vitest-environment node
import { describe, it, expect } from 'vitest'
import * as fs from 'node:fs'
import { Buffer } from 'node:buffer'
import {
  hkdf,
  pbkdf2,
  accountHash,
  peerHash,
  recordId,
  discriminator,
  sealGcm,
  openGcm,
  unwrapPassphrase,
  fingerprint,
  hexToBytes,
  bytesToHex,
} from '../crypto/primitives'
import { handleMessage, createInitialWorkerState } from '../crypto/worker'

export function resolveTestVectorsPath(envOverride?: string): string {
  const envPath = envOverride !== undefined ? envOverride : process.env.CUSTOMSYNC_TEST_VECTORS
  if (envPath !== undefined && envPath !== '') {
    if (fs.existsSync(envPath)) {
      return envPath
    }
    throw new Error(
      `test-vectors.json topilmadi: ${envPath}. CUSTOMSYNC_TEST_VECTORS muhit o'zgaruvchisiga to'liq yo'lni bering.`
    )
  }

  const defaultPaths = [
    'C:\\TBuild\\tdesktop\\docs\\sync-protocol\\test-vectors.json',
    'D:\\TBuild\\tdesktop\\docs\\sync-protocol\\test-vectors.json',
  ]

  for (const p of defaultPaths) {
    if (fs.existsSync(p)) {
      return p
    }
  }

  throw new Error(
    'test-vectors.json topilmadi. CUSTOMSYNC_TEST_VECTORS muhit o\'zgaruvchisiga to\'liq yo\'lni bering, masalan: set CUSTOMSYNC_TEST_VECTORS=C:\\TBuild\\tdesktop\\docs\\sync-protocol\\test-vectors.json'
  )
}

export function loadTestVectors(envOverride?: string): any {
  const p = resolveTestVectorsPath(envOverride)
  const content = fs.readFileSync(p, 'utf-8')
    // Preserves precision for 64-bit numbers exceeding 2^53 (e.g. discriminator value and msg_id)
    .replace(/"(value|msg_id)":\s*(-?\d+)/g, '"$1": "$2"')
  return JSON.parse(content)
}

describe('test-vectors.json loader contract', () => {
  it('throws clear error when vector file is not found', () => {
    expect(() => resolveTestVectorsPath('C:\\non_existent_path\\bogus_vectors.json')).toThrow(
      /CUSTOMSYNC_TEST_VECTORS/
    )
  })

  it('loads real vectors with all required sections and cases', () => {
    const vectors = loadTestVectors()
    expect(vectors.version).toBe(1)
    expect(vectors.hkdf).toBeDefined()
    expect(vectors.account_hash.cases.length).toBeGreaterThan(0)
    expect(vectors.peer_hash.cases.length).toBeGreaterThan(0)
    expect(vectors.record_id.cases.length).toBeGreaterThan(0)
    expect(vectors.discriminator.cases.length).toBeGreaterThan(0)
    expect(vectors.pbkdf2.cases.length).toBeGreaterThan(0)
    expect(vectors.aes_gcm.cases.length).toBeGreaterThan(0)
    expect(vectors.key_wrap.cases.length).toBeGreaterThan(0)
    expect(vectors.fingerprint.cases.length).toBeGreaterThan(0)
  })
})

describe('HKDF-SHA256 (32 zero bytes salt)', () => {
  it('reproduces all derived keys from master key', async () => {
    const vectors = loadTestVectors()
    const masterKey = hexToBytes(vectors.hkdf.master_key_hex)
    const expected = vectors.hkdf.derived

    const expectedKeys = Object.keys(expected)
    expect(expectedKeys.length).toBe(4)

    for (const info of expectedKeys) {
      const derived = await hkdf(masterKey, info)
      expect(bytesToHex(derived)).toBe(expected[info])
    }
  })
})

describe('account_hash HMAC-SHA256', () => {
  it('reproduces all account_hash cases with decimal string account_id', async () => {
    const vectors = loadTestVectors()
    const masterKey = hexToBytes(vectors.hkdf.master_key_hex)
    const accountKey = await hkdf(masterKey, 'customsync-account-v1')

    const cases = vectors.account_hash.cases
    expect(cases.length).toBeGreaterThan(0)

    for (const c of cases) {
      const hash = await accountHash(accountKey, c.account_id)
      expect(hash).toBe(c.account_hash)
    }
  })
})

describe('peer_hash HMAC-SHA256', () => {
  it('reproduces all peer_hash cases with decimal string peer_id', async () => {
    const vectors = loadTestVectors()
    const masterKey = hexToBytes(vectors.hkdf.master_key_hex)
    const peerKey = await hkdf(masterKey, 'customsync-peer-v1')

    const cases = vectors.peer_hash.cases
    expect(cases.length).toBeGreaterThan(0)

    for (const c of cases) {
      const hash = await peerHash(peerKey, c.peer_id)
      expect(hash).toBe(c.peer_hash)
    }
  })
})

describe('record_id SHA-256', () => {
  it('reproduces all record_id cases, preserving negative signs and empty activity account_hash', async () => {
    const vectors = loadTestVectors()
    const cases = vectors.record_id.cases
    expect(cases.length).toBeGreaterThan(0)

    let hasNegativeMsgId = false
    let hasActivityEmptyAccount = false

    for (const c of cases) {
      if (BigInt(c.msg_id) < 0n) {
        hasNegativeMsgId = true
      }
      if (c.kind === 'activity' && c.account_hash === '') {
        hasActivityEmptyAccount = true
      }

      const id = await recordId(
        c.kind,
        c.account_hash,
        c.peer_hash,
        c.msg_id,
        c.occurred_at
      )
      expect(id).toBe(c.record_id)
    }

    expect(hasNegativeMsgId).toBe(true)
    expect(hasActivityEmptyAccount).toBe(true)
  })
})

describe('discriminator', () => {
  it('reproduces big-endian int64 with top bit cleared as BigInt', async () => {
    const vectors = loadTestVectors()
    const cases = vectors.discriminator.cases
    expect(cases.length).toBeGreaterThan(0)

    for (const c of cases) {
      const disc = await discriminator(c.text)
      expect(typeof disc).toBe('bigint')
      expect(disc >= 0n).toBe(true)
      expect(disc.toString()).toBe(c.value.toString())
    }
  })
})

describe('PBKDF2-HMAC-SHA256', () => {
  it(
    'reproduces all PBKDF2 keys including 600k and 2M iterations',
    async () => {
      const vectors = loadTestVectors()
      const cases = vectors.pbkdf2.cases
      expect(cases.length).toBeGreaterThan(0)

      for (const c of cases) {
        const salt = hexToBytes(c.salt_hex)
        const kek = await pbkdf2(c.password, salt, c.iterations)
        expect(bytesToHex(kek)).toBe(c.kek_hex)
      }
    },
    60_000 // 60s timeout for 2M iterations
  )
})

describe('AES-256-GCM', () => {
  it('reproduces seal and open for all test cases', async () => {
    const vectors = loadTestVectors()
    const cases = vectors.aes_gcm.cases
    expect(cases.length).toBeGreaterThan(0)

    for (const c of cases) {
      const key = hexToBytes(c.key_hex)
      const nonce = hexToBytes(c.nonce_hex)
      const plaintext = hexToBytes(c.plaintext_hex)
      const expectedPayloadHex = c.ciphertext_hex + c.tag_hex

      // 1. Seal
      const sealed = await sealGcm(key, nonce, plaintext)
      expect(bytesToHex(sealed)).toBe(expectedPayloadHex)

      // 2. Open
      const opened = await openGcm(key, nonce, sealed)
      expect(opened).not.toBeNull()
      expect(bytesToHex(opened!)).toBe(c.plaintext_hex)
    }
  })

  it('openGcm returns null on flipped tag byte (never throws)', async () => {
    const vectors = loadTestVectors()
    const c = vectors.aes_gcm.cases[1] // sample with payload
    const key = hexToBytes(c.key_hex)
    const nonce = hexToBytes(c.nonce_hex)
    const payload = hexToBytes(c.ciphertext_hex + c.tag_hex)

    // Flip the last byte of tag
    payload[payload.length - 1] ^= 0x01

    const result = await openGcm(key, nonce, payload)
    expect(result).toBeNull()
  })
})

describe('fingerprint', () => {
  it('reproduces 16 hex char fingerprint for all cases', async () => {
    const vectors = loadTestVectors()
    const cases = vectors.fingerprint.cases
    expect(cases.length).toBeGreaterThan(0)

    for (const c of cases) {
      const master = hexToBytes(c.master_hex)
      const fp = await fingerprint(master)
      expect(fp).toBe(c.fingerprint)
    }
  })
})

describe('key_wrap (unwrapPassphrase)', () => {
  it('unwraps valid passphrase and fails wrong_passphrase with null', async () => {
    const vectors = loadTestVectors()
    const cases = vectors.key_wrap.cases
    expect(cases.length).toBeGreaterThan(0)

    for (const c of cases) {
      const wrap = {
        salt: c.salt,
        nonce: c.nonce,
        wrapped_key: c.wrapped_key,
        iterations: c.iterations,
      }

      // 1. Valid passphrase
      const master = await unwrapPassphrase(wrap, c.passphrase)
      expect(master).not.toBeNull()
      expect(bytesToHex(master!)).toBe(c.master_hex)

      // Fingerprint check
      const fp = await fingerprint(master!)
      expect(fp).toBe(c.fingerprint)

      // 2. Wrong passphrase returns null
      const wrongMaster = await unwrapPassphrase(wrap, c.wrong_passphrase)
      expect(wrongMaster).toBeNull()
    }
  }, 60_000)
})

describe('Worker message handler', () => {
  it('unlocks with valid wrap, rejects wrong_passphrase, handles locked states, opens records, and locks', async () => {
    const vectors = loadTestVectors()
    const wrapCase = vectors.key_wrap.cases[0]
    const wrap = {
      salt: wrapCase.salt,
      nonce: wrapCase.nonce,
      wrapped_key: wrapCase.wrapped_key,
      iterations: wrapCase.iterations,
    }

    const state = createInitialWorkerState()
    const replies: any[] = []

    // 1. Request while locked
    const lockedRes = await handleMessage(state, {
      id: 1,
      type: 'openRecords',
      records: [],
    })
    expect(lockedRes).toEqual({ id: 1, ok: false, error: 'locked' })
    replies.push(lockedRes)

    // 2. Wrong passphrase unlock
    const wrongRes = await handleMessage(state, {
      id: 2,
      type: 'unlock',
      passphrase: wrapCase.wrong_passphrase,
      wrap,
    })
    expect(wrongRes).toEqual({ id: 2, ok: false, error: 'wrong_passphrase' })
    replies.push(wrongRes)

    // 3. Valid unlock
    const unlockRes = await handleMessage(state, {
      id: 3,
      type: 'unlock',
      passphrase: wrapCase.passphrase,
      wrap,
    })
    expect(unlockRes).toEqual({
      id: 3,
      ok: true,
      fingerprint: wrapCase.fingerprint,
    })
    replies.push(unlockRes)

    // 4. peerHash request
    const peerRes = await handleMessage(state, {
      id: 4,
      type: 'peerHash',
      peer_id: vectors.peer_hash.cases[0].peer_id,
    })
    expect(peerRes.ok).toBe(true)
    const masterBytes = hexToBytes(wrapCase.master_hex)
    const wrapPeerKey = await hkdf(masterBytes, 'customsync-peer-v1')
    const expectedPeerHash = await peerHash(wrapPeerKey, vectors.peer_hash.cases[0].peer_id)
    expect(peerRes.peer_hash).toBe(expectedPeerHash)
    replies.push(peerRes)

    // 5. openRecords: create a valid record and a corrupted one
    // We encrypt a sample JSON with content key
    const contentKey = await hkdf(masterBytes, 'customsync-content-v1')
    const sampleNonce = new Uint8Array(12).fill(7)
    const sampleData = { text: 'salom dunyo', num: 42 }
    const samplePlaintext = new TextEncoder().encode(JSON.stringify(sampleData))
    const sealed = await sealGcm(contentKey, sampleNonce, samplePlaintext)

    const corruptedPayload = new Uint8Array(sealed)
    corruptedPayload[corruptedPayload.length - 1] ^= 0xff

    const openRecordsRes = await handleMessage(state, {
      id: 5,
      type: 'openRecords',
      records: [
        {
          record_id: 'rec-valid-01',
          nonce: Buffer.from(sampleNonce).toString('base64'),
          payload: Buffer.from(sealed).toString('base64'),
        },
        {
          record_id: 'rec-corrupted-02',
          nonce: Buffer.from(sampleNonce).toString('base64'),
          payload: Buffer.from(corruptedPayload).toString('base64'),
        },
      ],
    })

    expect(openRecordsRes.id).toBe(5)
    expect(openRecordsRes.ok).toBe(true)
    expect(openRecordsRes.results).toEqual([
      { record_id: 'rec-valid-01', json: sampleData },
      { record_id: 'rec-corrupted-02', corrupt: true },
    ])
    replies.push(openRecordsRes)

    // 6. Lock
    const lockRes = await handleMessage(state, { id: 6, type: 'lock' })
    expect(lockRes).toEqual({ id: 6, ok: true })
    replies.push(lockRes)

    // 7. Request after lock -> locked
    const afterLockRes = await handleMessage(state, {
      id: 7,
      type: 'openRecords',
      records: [],
    })
    expect(afterLockRes).toEqual({ id: 7, ok: false, error: 'locked' })
    replies.push(afterLockRes)

    // 8. Assert no reply object ever contains a key or master
    const allSerialized = JSON.stringify(replies)
    expect(allSerialized).not.toContain(wrapCase.master_hex)
    expect(allSerialized).not.toContain(bytesToHex(contentKey))
  }, 60_000)
})
