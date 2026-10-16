export function formatBytes(bytes: number | null | undefined): string {
  if (bytes === null || bytes === undefined || isNaN(bytes)) return '-'
  if (bytes === 0) return '0 B'
  const k = 1000
  const sizes = ['B', 'KB', 'MB', 'GB', 'TB']
  const i = Math.floor(Math.log(bytes) / Math.log(k))
  if (i <= 0) return `${bytes} B`
  const val = (bytes / Math.pow(k, i)).toFixed(1)
  return `${val} ${sizes[i]}`
}

export function formatNumber(num: number | null | undefined): string {
  if (num === null || num === undefined || isNaN(num)) return '-'
  return num.toLocaleString('en-US')
}

export function formatDate(val: string | number | null | undefined): string {
  if (!val) return '-'
  let date: Date
  if (typeof val === 'number') {
    // Treat values below 1e12 as seconds, otherwise ms
    const ms = val < 1e12 ? val * 1000 : val
    date = new Date(ms)
  } else {
    date = new Date(val)
  }

  if (isNaN(date.getTime())) return String(val)

  // Format as YYYY-MM-DD HH:mm:ss
  const pad = (n: number) => n.toString().padStart(2, '0')
  const y = date.getUTCFullYear()
  const m = pad(date.getUTCMonth() + 1)
  const d = pad(date.getUTCDate())
  const h = pad(date.getUTCHours())
  const min = pad(date.getUTCMinutes())
  const s = pad(date.getUTCSeconds())
  return `${y}-${m}-${d} ${h}:${min}:${s}`
}

export function shortenHash(hash: string | null | undefined, len = 8): string {
  if (!hash) return '-'
  return hash.slice(0, len)
}
