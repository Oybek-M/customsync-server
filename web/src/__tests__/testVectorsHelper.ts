import * as fs from 'node:fs'

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
