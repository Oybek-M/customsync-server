export interface EncryptedRecord {
  seq: number
  record_id: string
  kind: string
  account_hash: string
  peer_hash: string
  msg_id: number
  occurred_at: number
  observed_at: number
  device_id: string
  nonce: string
  payload: string
  target_record_id: string | null
  media_hashes: string[] | null
}

export interface MetaRecord {
  key: string
  value: any
}

export const DB_NAME = 'customsync'
export const DB_VERSION = 1

let cachedDb: IDBDatabase | null = null

export function openDatabase(): Promise<IDBDatabase> {
  if (cachedDb) {
    return Promise.resolve(cachedDb)
  }

  return new Promise((resolve, reject) => {
    const req = indexedDB.open(DB_NAME, DB_VERSION)

    req.onupgradeneeded = (e) => {
      const db = (e.target as IDBOpenDBRequest).result

      // 1. records store
      if (!db.objectStoreNames.contains('records')) {
        const recordsStore = db.createObjectStore('records', { keyPath: 'record_id' })
        recordsStore.createIndex('bySeq', 'seq', { unique: false })
        recordsStore.createIndex('byTime', ['occurred_at', 'seq'], { unique: false })
        recordsStore.createIndex('byPeerTime', ['peer_hash', 'occurred_at', 'seq'], { unique: false })
        recordsStore.createIndex('byKindTime', ['kind', 'occurred_at', 'seq'], { unique: false })
      }

      // 2. meta store
      if (!db.objectStoreNames.contains('meta')) {
        db.createObjectStore('meta', { keyPath: 'key' })
      }
    }

    req.onsuccess = () => {
      cachedDb = req.result
      cachedDb.onversionchange = () => {
        closeDatabase()
      }
      resolve(cachedDb)
    }

    req.onerror = () => {
      reject(req.error)
    }
  })
}

export function closeDatabase(): void {
  if (cachedDb) {
    try {
      cachedDb.close()
    } catch {
      // ignore
    }
    cachedDb = null
  }
}

export async function wipeDatabase(): Promise<void> {
  closeDatabase()
  return new Promise((resolve, reject) => {
    const req = indexedDB.deleteDatabase(DB_NAME)
    req.onsuccess = () => resolve()
    req.onerror = () => reject(req.error)
    req.onblocked = () => resolve()
  })
}

export async function getMeta(key: string): Promise<any> {
  const db = await openDatabase()
  return new Promise((resolve, reject) => {
    const tx = db.transaction('meta', 'readonly')
    const store = tx.objectStore('meta')
    const req = store.get(key)
    req.onsuccess = () => resolve(req.result ? req.result.value : null)
    req.onerror = () => reject(req.error)
  })
}

export async function setMeta(key: string, value: any): Promise<void> {
  const db = await openDatabase()
  return new Promise((resolve, reject) => {
    const tx = db.transaction('meta', 'readwrite')
    const store = tx.objectStore('meta')
    const req = store.put({ key, value })
    req.onsuccess = () => resolve()
    req.onerror = () => reject(req.error)
  })
}

export async function getCursor(): Promise<number> {
  const val = await getMeta('cursor')
  return typeof val === 'number' ? val : 0
}

export async function setCursor(cursor: number): Promise<void> {
  await setMeta('cursor', cursor)
}

export async function getStoredDeviceId(): Promise<string | null> {
  const val = await getMeta('deviceId')
  return typeof val === 'string' ? val : null
}

export async function ensureDevice(currentDeviceId: string): Promise<void> {
  const storedId = await getStoredDeviceId()
  if (storedId && storedId !== currentDeviceId) {
    // Qurilma o'zgardi -> butun bazani tozalaymiz
    await wipeDatabase()
    await setMeta('deviceId', currentDeviceId)
    await setCursor(0)
  } else if (!storedId) {
    await setMeta('deviceId', currentDeviceId)
    await setCursor(0)
  }
}

export async function getRecord(recordId: string): Promise<EncryptedRecord | undefined> {
  const db = await openDatabase()
  return new Promise((resolve, reject) => {
    const tx = db.transaction('records', 'readonly')
    const store = tx.objectStore('records')
    const req = store.get(recordId)
    req.onsuccess = () => resolve(req.result)
    req.onerror = () => reject(req.error)
  })
}

export async function getAllRecords(): Promise<EncryptedRecord[]> {
  const db = await openDatabase()
  return new Promise((resolve, reject) => {
    const tx = db.transaction('records', 'readonly')
    const store = tx.objectStore('records')
    const req = store.getAll()
    req.onsuccess = () => resolve(req.result || [])
    req.onerror = () => reject(req.error)
  })
}

export async function getRecordsCount(): Promise<number> {
  const db = await openDatabase()
  return new Promise((resolve, reject) => {
    const tx = db.transaction('records', 'readonly')
    const store = tx.objectStore('records')
    const req = store.count()
    req.onsuccess = () => resolve(req.result || 0)
    req.onerror = () => reject(req.error)
  })
}

export async function putRecordsBatch(
  records: EncryptedRecord[],
  nextSince: number
): Promise<void> {
  const db = await openDatabase()
  return new Promise((resolve, reject) => {
    const tx = db.transaction(['records', 'meta'], 'readwrite')
    const recordsStore = tx.objectStore('records')
    const metaStore = tx.objectStore('meta')

    tx.oncomplete = () => resolve()
    tx.onerror = () => reject(tx.error)
    tx.onabort = () => reject(tx.error || new Error('Transaction aborted'))

    for (const r of records) {
      if (r.target_record_id) {
        recordsStore.delete(r.target_record_id)
      }
      if (r.kind !== 'tombstone') {
        recordsStore.put(r)
      }
    }

    metaStore.put({ key: 'cursor', value: nextSince })
  })
}

export async function getRecordsPageBySeq(
  sinceSeq: number,
  limit: number
): Promise<EncryptedRecord[]> {
  const db = await openDatabase()
  return new Promise((resolve, reject) => {
    const tx = db.transaction('records', 'readonly')
    const store = tx.objectStore('records')
    const index = store.index('bySeq')
    const range = IDBKeyRange.lowerBound(sinceSeq, true) // seq > sinceSeq
    const req = index.openCursor(range)
    const results: EncryptedRecord[] = []

    req.onsuccess = (e) => {
      const cursor = (e.target as IDBRequest<IDBCursorWithValue>).result
      if (cursor && results.length < limit) {
        results.push(cursor.value)
        cursor.continue()
      } else {
        resolve(results)
      }
    }

    req.onerror = () => reject(req.error)
  })
}
