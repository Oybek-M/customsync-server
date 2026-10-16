import { ref, watch } from 'vue'
import type {
  CryptoWorkerClient,
  DecryptedIndexedRecord,
  IndexQueryParams,
} from '../crypto/worker-api'
import { useVaultStore } from '../stores/vault'

export interface ArchiveQueryOptions {
  getWorker?: () => CryptoWorkerClient
  limit?: number
  initialOrder?: 'desc' | 'asc'
}

export function useArchiveQuery(options: ArchiveQueryOptions = {}) {
  const vaultStore = useVaultStore()
  const getWorker = options.getWorker ?? (() => vaultStore.getWorker())
  const limit = options.limit ?? 50

  const order = ref<'desc' | 'asc'>(options.initialOrder ?? 'desc')
  const kind = ref<string>('')
  const peerHash = ref<string>('')

  const rows = ref<DecryptedIndexedRecord[]>([])
  const afterKey = ref<[number, number] | null>(null)
  const snapshot = ref<number | null>(null)
  const loading = ref<boolean>(false)
  const exhausted = ref<boolean>(false)
  const generation = ref<number>(0)
  const newerCount = ref<number>(0)

  async function reset() {
    const currentGen = ++generation.value
    rows.value = []
    afterKey.value = null
    exhausted.value = false
    loading.value = true

    try {
      const worker = getWorker()
      const stats = await worker.indexStats()
      if (generation.value !== currentGen) return

      snapshot.value = stats.max_seq ?? 0
      newerCount.value = 0

      const queryParams: IndexQueryParams = {
        order: order.value,
        max_seq: snapshot.value,
        limit,
      }
      if (kind.value) queryParams.kind = kind.value
      if (peerHash.value) queryParams.peer_hash = peerHash.value

      const page = await worker.indexQuery(queryParams)
      if (generation.value !== currentGen) return

      rows.value = page
      if (page.length > 0) {
        const last = page[page.length - 1]
        afterKey.value = [last.occurred_at, last.seq]
      } else {
        afterKey.value = null
      }
      exhausted.value = page.length < limit
    } catch {
      if (generation.value === currentGen) {
        rows.value = []
        exhausted.value = true
      }
    } finally {
      if (generation.value === currentGen) {
        loading.value = false
      }
    }
  }

  async function loadMore() {
    if (loading.value || exhausted.value) return
    if (snapshot.value === null) {
      return reset()
    }

    loading.value = true
    const currentGen = generation.value

    try {
      const worker = getWorker()
      const queryParams: IndexQueryParams = {
        order: order.value,
        max_seq: snapshot.value,
        limit,
      }
      if (afterKey.value) {
        queryParams.after = afterKey.value
      }
      if (kind.value) queryParams.kind = kind.value
      if (peerHash.value) queryParams.peer_hash = peerHash.value

      const page = await worker.indexQuery(queryParams)
      if (generation.value !== currentGen) return

      rows.value.push(...page)
      if (page.length > 0) {
        const last = page[page.length - 1]
        afterKey.value = [last.occurred_at, last.seq]
      }
      exhausted.value = page.length < limit
    } catch {
      if (generation.value === currentGen) {
        exhausted.value = true
      }
    } finally {
      if (generation.value === currentGen) {
        loading.value = false
      }
    }
  }

  async function checkNewer(): Promise<number> {
    if (snapshot.value === null) return 0
    try {
      const worker = getWorker()
      const stats = await worker.indexStats({ since_seq: snapshot.value })
      newerCount.value = stats.newer ?? 0
      return newerCount.value
    } catch {
      return 0
    }
  }

  function clear() {
    generation.value++
    rows.value = []
    afterKey.value = null
    snapshot.value = null
    loading.value = false
    exhausted.value = false
    newerCount.value = 0
  }

  // Ombor qulflanganda ochiq matnlar xotiradan darhol tashlanadi
  watch(
    () => vaultStore.locked,
    (locked) => {
      if (locked) {
        clear()
      }
    },
    { flush: 'sync' }
  )

  return {
    order,
    kind,
    peerHash,
    rows,
    afterKey,
    snapshot,
    loading,
    exhausted,
    generation,
    newerCount,
    loadMore,
    reset,
    checkNewer,
    clear,
  }
}
