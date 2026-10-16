<script setup lang="ts">
import { ref, onMounted, watch } from 'vue'
import { useVaultStore } from '../stores/vault'
import { getRecordsCount } from '../db/schema'
import { syncArchive } from '../db/sync'
import { loadIndexFromDatabase, cancelIndexLoading, isIndexLoading } from '../db/indexLoader'
import type { DecryptedIndexedRecord } from '../crypto/worker-api'

const vaultStore = useVaultStore()

const cachedCount = ref(0)
const syncRunning = ref(false)
const syncStatusText = ref('')
const indexStatusText = ref('')
const indexLoading = ref(false)
const firstPageRows = ref<DecryptedIndexedRecord[]>([])

async function updateCachedCount() {
  try {
    cachedCount.value = await getRecordsCount()
  } catch {
    cachedCount.value = 0
  }
}

async function refreshFirstPage() {
  if (vaultStore.locked) {
    firstPageRows.value = []
    return
  }
  try {
    const worker = vaultStore.getWorker()
    firstPageRows.value = await worker.indexQuery({ limit: 50 })
  } catch {
    firstPageRows.value = []
  }
}

async function startLoadingIndex() {
  if (vaultStore.locked) return

  indexLoading.value = true
  try {
    await loadIndexFromDatabase({
      onProgress: (p) => {
        indexStatusText.value = p.text
        refreshFirstPage()
      },
    })
    await refreshFirstPage()
  } finally {
    indexLoading.value = isIndexLoading()
  }
}

async function triggerSync() {
  if (syncRunning.value) return
  syncRunning.value = true
  syncStatusText.value = "Sinxronizatsiya boshlandi..."

  try {
    await syncArchive(undefined, (p) => {
      syncStatusText.value = `${p.fetched} ta yozuv yuklandi`
    })
    syncStatusText.value = "Sinxronizatsiya yakunlandi"
    await updateCachedCount()
    if (!vaultStore.locked) {
      await refreshFirstPage()
    }
  } catch {
    syncStatusText.value = "Sinxronizatsiyada xatolik yuz berdi"
  } finally {
    syncRunning.value = false
  }
}

function handleCancelIndex() {
  cancelIndexLoading()
  indexLoading.value = false
}

function formatDate(seconds: number): string {
  try {
    return new Date(seconds * 1000).toLocaleString()
  } catch {
    return String(seconds)
  }
}

function formatJsonSnippet(json: any): string {
  if (!json) return '-'
  if (typeof json === 'string') return json
  if (json.text) return String(json.text)
  return JSON.stringify(json)
}

watch(
  () => vaultStore.locked,
  async (locked) => {
    await updateCachedCount()
    if (!locked) {
      await startLoadingIndex()
    } else {
      firstPageRows.value = []
      indexStatusText.value = ''
    }
  }
)

onMounted(async () => {
  await updateCachedCount()
  if (!vaultStore.locked) {
    await startLoadingIndex()
  }
})
</script>

<template>
  <div class="cs-card">
    <div class="cs-header-row">
      <div>
        <h2>Arxiv</h2>
        <p class="cs-subtitle">Telegram xabarlari va media arxivi.</p>
      </div>
      <div class="cs-actions">
        <button
          class="cs-btn cs-btn-primary cs-btn-sync"
          :disabled="syncRunning"
          @click="triggerSync"
        >
          <i class="pi pi-sync" :class="{ 'pi-spin': syncRunning }"></i>
          <span>{{ syncRunning ? 'Yuklanmoqda...' : 'Yangilash' }}</span>
        </button>
      </div>
    </div>

    <!-- Agar ombor qulflangan bo'lsa -->
    <div v-if="vaultStore.locked" class="cs-locked-view">
      <div class="cs-alert cs-alert-warning">
        <i class="pi pi-lock"></i>
        <span>Arxiv qulflangan — ochish uchun parolni kiriting</span>
      </div>

      <div class="cs-locked-stats">
        <p>
          Brauzer xotirasida <strong>{{ cachedCount }}</strong> ta shifrlangan yozuv saqlanmoqda.
        </p>
        <p class="cs-text-muted">
          Yozuvlar faqat ombor paroli kiritilgandan so'ng worker xotirasida ochiladi.
        </p>
        <button
          class="cs-btn cs-btn-primary cs-btn-open-vault"
          @click="vaultStore.openUnlockDialog"
        >
          Omborni ochish
        </button>
      </div>
    </div>

    <!-- Agar ombor ochiq bo'lsa -->
    <div v-else class="cs-unlocked-view">
      <!-- Status paneli -->
      <div class="cs-progress-bar-row">
        <div class="cs-status-texts">
          <div v-if="syncStatusText" class="cs-status-line">
            <i class="pi pi-cloud-download"></i>
            <span>{{ syncStatusText }}</span>
          </div>
          <div v-if="indexStatusText" class="cs-status-line">
            <i class="pi pi-database"></i>
            <span>{{ indexStatusText }}</span>
          </div>
        </div>
        <div class="cs-progress-actions">
          <button
            class="cs-btn cs-btn-secondary cs-btn-cancel-index"
            :disabled="!indexLoading"
            @click="handleCancelIndex"
          >
            Bekor qilish
          </button>
        </div>
      </div>

      <!-- Birinchi sahifa yozuvlar ro'yxati -->
      <div class="cs-table-wrap">
        <table class="cs-table">
          <thead>
            <tr>
              <th>ID / Seq</th>
              <th>Turi</th>
              <th>Muxbir (peer)</th>
              <th>Sana</th>
              <th>Xabar / Ma'lumot</th>
            </tr>
          </thead>
          <tbody>
            <tr v-if="firstPageRows.length === 0">
              <td colspan="5" class="cs-empty-row">
                {{ indexLoading ? 'Yozuvlar indekslanmoqda...' : 'Yozuvlar topilmadi' }}
              </td>
            </tr>
            <tr v-for="row in firstPageRows" :key="row.record_id">
              <td>
                <code>{{ row.record_id }}</code>
                <span class="cs-seq-badge">#{{ row.seq }}</span>
              </td>
              <td>
                <span class="cs-badge">{{ row.kind }}</span>
              </td>
              <td>
                <code class="cs-peer-hash">{{ row.peer_hash.slice(0, 8) }}…</code>
              </td>
              <td>{{ formatDate(row.occurred_at) }}</td>
              <td class="cs-json-snippet">
                {{ formatJsonSnippet(row.json) }}
              </td>
            </tr>
          </tbody>
        </table>
      </div>
    </div>
  </div>
</template>

<style scoped>
.cs-header-row {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 1rem;
}

.cs-subtitle {
  color: var(--text-color-secondary, #6b7280);
  font-size: 0.8125rem;
  margin-top: 0.25rem;
}

.cs-alert {
  padding: 0.75rem 1rem;
  border-radius: 4px;
  margin-bottom: 1rem;
  font-size: 0.875rem;
  display: flex;
  align-items: center;
  gap: 0.5rem;
}

.cs-alert-warning {
  background: #fffbeb;
  color: #b45309;
  border: 1px solid #fde68a;
}

.cs-locked-stats {
  padding: 1.5rem;
  text-align: center;
  background: var(--surface-ground, #f9fafb);
  border: 1px dashed var(--surface-border, #e5e7eb);
  border-radius: 6px;
  margin-top: 1rem;
}

.cs-text-muted {
  color: var(--text-color-secondary, #6b7280);
  font-size: 0.8125rem;
  margin: 0.5rem 0 1rem 0;
}

.cs-progress-bar-row {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 0.75rem;
  background: var(--surface-ground, #f9fafb);
  border: 1px solid var(--surface-border, #e5e7eb);
  border-radius: 4px;
  margin-bottom: 1rem;
}

.cs-status-texts {
  display: flex;
  flex-direction: column;
  gap: 0.25rem;
  font-size: 0.8125rem;
}

.cs-status-line {
  display: flex;
  align-items: center;
  gap: 0.5rem;
}

.cs-badge {
  display: inline-block;
  padding: 0.125rem 0.375rem;
  border-radius: 3px;
  background: var(--surface-ground, #f3f4f6);
  font-size: 0.75rem;
  font-family: monospace;
}

.cs-seq-badge {
  margin-left: 0.375rem;
  font-size: 0.75rem;
  color: var(--text-color-secondary, #6b7280);
}

.cs-peer-hash {
  font-size: 0.75rem;
}

.cs-json-snippet {
  max-width: 300px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.cs-empty-row {
  text-align: center;
  color: var(--text-color-secondary, #6b7280);
  padding: 2rem;
}
</style>
