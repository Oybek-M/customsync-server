import { getRecordsCount, getRecordsPageBySeq } from './schema'
import { useVaultStore } from '../stores/vault'

export interface IndexProgress {
  indexed: number
  total: number
  text: string
  isComplete: boolean
}

export interface LoadIndexOptions {
  pageSize?: number
  signal?: AbortSignal
  onProgress?: (progress: IndexProgress) => void
}

let activeAbortController: AbortController | null = null
let loading = false

export function isIndexLoading(): boolean {
  return loading
}

export function cancelIndexLoading(): void {
  if (activeAbortController) {
    activeAbortController.abort()
  }
}

export function formatNumber(n: number): string {
  return n.toString().replace(/\B(?=(\d{3})+(?!\d))/g, ' ')
}

export async function loadIndexFromDatabase(
  options: LoadIndexOptions = {}
): Promise<{ indexed: number; total: number }> {
  const vaultStore = useVaultStore()
  if (vaultStore.locked) {
    return { indexed: 0, total: 0 }
  }

  if (loading) {
    return { indexed: 0, total: 0 }
  }

  loading = true
  activeAbortController = new AbortController()

  const isAborted = () => {
    return Boolean(options.signal?.aborted || activeAbortController?.signal.aborted)
  }

  try {
    const worker = vaultStore.getWorker()
    await worker.indexBegin()

    const total = await getRecordsCount()
    if (total === 0) {
      options.onProgress?.({
        indexed: 0,
        total: 0,
        text: '0 / 0 yozuv indekslandi',
        isComplete: true,
      })
      return { indexed: 0, total: 0 }
    }

    const pageSize = options.pageSize ?? 500
    let lastSeq = 0
    let indexed = 0

    while (!isAborted()) {
      const page = await getRecordsPageBySeq(lastSeq, pageSize)
      if (page.length === 0) {
        break
      }

      await worker.indexAdd(page)
      lastSeq = page[page.length - 1].seq
      indexed += page.length

      const text = `${formatNumber(indexed)} / ${formatNumber(total)} yozuv indekslandi`
      options.onProgress?.({
        indexed,
        total,
        text,
        isComplete: indexed >= total,
      })

      if (page.length < pageSize) {
        break
      }
    }

    return { indexed, total }
  } finally {
    loading = false
    activeAbortController = null
  }
}
