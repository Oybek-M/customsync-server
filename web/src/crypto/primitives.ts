/**
 * CustomSync kriptografik ibtidoiylari (Web Crypto API asosida).
 *
 * Diqqat:
 * - Faqat standart brauzer API (crypto.subtle, Uint8Array, TextEncoder/Decoder, atob/btoa)
 * - src/ ichida Node.js 'Buffer' ishlatilmaydi
 * - Qoidalar test-vectors.json va tdesktop/server bilan 100% baytma-bayt mos
 */

export interface KeyWrapData {
  salt: string
  nonce: string
  wrapped_key: string
  iterations: number
  wrap_id?: string
  wrap_type?: string
  label?: string
}

export function hexToBytes(hex: string): Uint8Array {
  if (hex.length % 2 !== 0) {
    throw new Error('Noto\'g\'ri hex satr: uzunligi juft bo\'lishi shart')
  }
  const bytes = new Uint8Array(hex.length / 2)
  for (let i = 0; i < bytes.length; i++) {
    bytes[i] = parseInt(hex.substring(i * 2, i * 2 + 2), 16)
  }
  return bytes
}

export function bytesToHex(bytes: Uint8Array): string {
  let hex = ''
  for (let i = 0; i < bytes.length; i++) {
    hex += bytes[i].toString(16).padStart(2, '0')
  }
  return hex
}

export function base64ToBytes(b64: string): Uint8Array {
  const binary = atob(b64)
  const bytes = new Uint8Array(binary.length)
  for (let i = 0; i < binary.length; i++) {
    bytes[i] = binary.charCodeAt(i)
  }
  return bytes
}

export function bytesToBase64(bytes: Uint8Array): string {
  let binary = ''
  for (let i = 0; i < bytes.length; i++) {
    binary += String.fromCharCode(bytes[i])
  }
  return btoa(binary)
}

export async function sha256(data: Uint8Array): Promise<Uint8Array> {
  const digest = await crypto.subtle.digest('SHA-256', data as any)
  return new Uint8Array(digest)
}

export async function hmacSha256(key: Uint8Array, data: Uint8Array): Promise<Uint8Array> {
  const cryptoKey = await crypto.subtle.importKey(
    'raw',
    key as any,
    { name: 'HMAC', hash: 'SHA-256' },
    false,
    ['sign']
  )
  const sig = await crypto.subtle.sign('HMAC', cryptoKey, data as any)
  return new Uint8Array(sig)
}

export async function hkdf(
  masterKey: Uint8Array,
  info: string | Uint8Array
): Promise<Uint8Array> {
  const baseKey = await crypto.subtle.importKey(
    'raw',
    masterKey as any,
    'HKDF',
    false,
    ['deriveBits']
  )

  const infoBytes = typeof info === 'string' ? new TextEncoder().encode(info) : info
  const salt = new Uint8Array(32) // 32 bayt nol har doim aniq uzatiladi

  const bits = await crypto.subtle.deriveBits(
    {
      name: 'HKDF',
      hash: 'SHA-256',
      salt: salt as any,
      info: infoBytes as any,
    },
    baseKey,
    256 // 32 bayt = 256 bit
  )
  return new Uint8Array(bits)
}

export async function pbkdf2(
  password: string | Uint8Array,
  salt: Uint8Array,
  iterations: number,
  dkLen = 32
): Promise<Uint8Array> {
  const pwBytes = typeof password === 'string' ? new TextEncoder().encode(password) : password

  const keyMaterial = await crypto.subtle.importKey(
    'raw',
    pwBytes as any,
    'PBKDF2',
    false,
    ['deriveBits']
  )

  const bits = await crypto.subtle.deriveBits(
    {
      name: 'PBKDF2',
      salt: salt as any,
      iterations,
      hash: 'SHA-256',
    },
    keyMaterial,
    dkLen * 8
  )
  return new Uint8Array(bits)
}

export async function accountHash(accountKey: Uint8Array, accountId: string): Promise<string> {
  const data = new TextEncoder().encode(accountId)
  const hmac = await hmacSha256(accountKey, data)
  return bytesToHex(hmac.slice(0, 16))
}

export async function peerHash(peerKey: Uint8Array, peerId: string): Promise<string> {
  const data = new TextEncoder().encode(peerId)
  const hmac = await hmacSha256(peerKey, data)
  return bytesToHex(hmac.slice(0, 16))
}

export async function recordId(
  kind: string,
  accountHashVal: string,
  peerHashVal: string,
  msgId: number | string | bigint,
  occurredAt: number | string | bigint
): Promise<string> {
  const encoder = new TextEncoder()
  const kindBytes = encoder.encode(kind)
  const accBytes = encoder.encode(accountHashVal)
  const peerBytes = encoder.encode(peerHashVal)
  const msgBytes = encoder.encode(String(msgId))
  const occBytes = encoder.encode(String(occurredAt))

  // total len: 4 bo'luvchi (0x00) + maydonlar
  const totalLen =
    kindBytes.length + 1 +
    accBytes.length + 1 +
    peerBytes.length + 1 +
    msgBytes.length + 1 +
    occBytes.length

  const buffer = new Uint8Array(totalLen)
  let offset = 0

  buffer.set(kindBytes, offset)
  offset += kindBytes.length
  buffer[offset++] = 0x00

  buffer.set(accBytes, offset)
  offset += accBytes.length
  buffer[offset++] = 0x00

  buffer.set(peerBytes, offset)
  offset += peerBytes.length
  buffer[offset++] = 0x00

  buffer.set(msgBytes, offset)
  offset += msgBytes.length
  buffer[offset++] = 0x00

  buffer.set(occBytes, offset)

  const digest = await sha256(buffer)
  return bytesToHex(digest)
}

export async function discriminator(text: string): Promise<bigint> {
  const hash = await sha256(new TextEncoder().encode(text))
  const view = new DataView(hash.buffer, hash.byteOffset, 8)
  const raw = view.getBigUint64(0, false) // big-endian 64 bit
  return raw & 0x7fffffffffffffffn // top bit cleared (doim >= 0)
}

export async function sealGcm(
  key: Uint8Array,
  nonce: Uint8Array,
  plaintext: Uint8Array
): Promise<Uint8Array> {
  const cryptoKey = await crypto.subtle.importKey(
    'raw',
    key as any,
    { name: 'AES-GCM' },
    false,
    ['encrypt']
  )

  const ciphertextAndTag = await crypto.subtle.encrypt(
    {
      name: 'AES-GCM',
      iv: nonce as any,
      tagLength: 128, // 16 bayt tag
    },
    cryptoKey,
    plaintext as any
  )

  return new Uint8Array(ciphertextAndTag)
}

export async function openGcm(
  key: Uint8Array,
  nonce: Uint8Array,
  payload: Uint8Array
): Promise<Uint8Array | null> {
  if (payload.length < 16) return null
  try {
    const cryptoKey = await crypto.subtle.importKey(
      'raw',
      key as any,
      { name: 'AES-GCM' },
      false,
      ['decrypt']
    )

    const plaintext = await crypto.subtle.decrypt(
      {
        name: 'AES-GCM',
        iv: nonce as any,
        tagLength: 128,
      },
      cryptoKey,
      payload as any
    )

    return new Uint8Array(plaintext)
  } catch {
    return null
  }
}

export async function fingerprint(master: Uint8Array): Promise<string> {
  const prefix = new TextEncoder().encode('customsync-fingerprint-v1')
  const combined = new Uint8Array(prefix.length + master.length)
  combined.set(prefix, 0)
  combined.set(master, prefix.length)

  const hash = await sha256(combined)
  return bytesToHex(hash.slice(0, 8)) // 8 bayt = 16 ta hex belgi
}

export async function unwrapPassphrase(
  wrap: KeyWrapData,
  passphrase: string
): Promise<Uint8Array | null> {
  try {
    const saltBytes = base64ToBytes(wrap.salt)
    const nonceBytes = base64ToBytes(wrap.nonce)
    const wrappedBytes = base64ToBytes(wrap.wrapped_key)

    const kek = await pbkdf2(passphrase, saltBytes, wrap.iterations, 32)
    const master = await openGcm(kek, nonceBytes, wrappedBytes)

    if (!master || master.length !== 32) {
      return null
    }

    return master
  } catch {
    return null
  }
}
