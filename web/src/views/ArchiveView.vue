<script setup lang="ts">
import { ref, onMounted, watch } from 'vue'
import { useVaultStore } from '../stores/vault'
import { getRecordsCount } from '../db/schema'
import { syncArchive } from '../db/sync'
import { loadIndexFromDatabase, cancelIndexLoading, isIndexLoading } from '../db/indexLoader'
import { useArchiveQuery } from '../composables/useArchiveQuery'
import RecordTable from '../components/RecordTable.vue'
import { getDevices } from '../api/devices'
import { groupMediaIndexRecords, type GroupedMediaFile } from '../utils/mediaGrouping'
import { formatBytes } from '../utils/format'

const vaultStore = useVaultStore()
const query = useArchiveQuery({ limit: 50 })

const cachedCount = ref(0)
const syncRunning = ref(false)
const syncStatusText = ref('')
const indexStatusText = ref('')
const indexLoading = ref(false)
const activeTab = ref<'records' | 'media'>('records')

const deviceMap = ref<Record<string, string>>({})
const mediaFiles = ref<GroupedMediaFile[]>([])
const mediaLoading = ref(false)

async function updateCachedCount() {
  try {
    cachedCount.value = await getRecordsCount()
  } catch {
    cachedCount.value = 0
  }
}

async function loadDevices() {
  try {
    const list = await getDevices()
    const map: Record<string, string> = {}
    for (const d of list) {
      if (d.device_id && d.name) {
        map[d.device_id] = d.name
      }
    }
    deviceMap.value = map
  } catch {
    // Ignore network error in test / offline
  }
}

async function loadMediaFiles() {
  if (vaultStore.locked) {
    mediaFiles.value = []
    return
  }
  mediaLoading.value = true
  try {
    const worker = vaultStore.getWorker()
    const mediaRecords = await worker.indexQuery({ kind: 'media_index', limit: 1000 })
    mediaFiles.value = groupMediaIndexRecords(mediaRecords)
  } catch {
    mediaFiles.value = []
  } finally {
    mediaLoading.value = false
  }
}

async function startLoadingIndex() {
  if (vaultStore.locked) return

  indexLoading.value = true
  try {
    await loadIndexFromDatabase({
      onProgress: (p) => {
        indexStatusText.value = p.text
        query.reset()
      },
    })
    await query.reset()
    if (activeTab.value === 'media') {
      await loadMediaFiles()
    }
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
      await query.checkNewer()
      if (activeTab.value === 'media') {
        await loadMediaFiles()
      }
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

function handleTabChange(tab: 'records' | 'media') {
  activeTab.value = tab
  if (tab === 'media') {
    loadMediaFiles()
  } else {
    query.reset()
  }
}

function handleOrderToggle() {
  query.order.value = query.order.value === 'desc' ? 'asc' : 'desc'
  query.reset()
}

function handleKindChange(event: Event) {
  const target = event.target as HTMLSelectElement
  query.kind.value = target.value
  query.reset()
}

function handlePeerInput(event: Event) {
  const target = event.target as HTMLInputElement
  query.peerHash.value = target.value.trim()
  query.reset()
}

async function handleRefreshBanner() {
  await query.reset()
}

function formatDevice(deviceId: string): string {
  if (deviceMap.value[deviceId]) {
    return deviceMap.value[deviceId]
  }
  return deviceId.slice(0, 8)
}

watch(
  () => vaultStore.locked,
  async (locked) => {
    await updateCachedCount()
    if (!locked) {
      await loadDevices()
      await startLoadingIndex()
    } else {
      query.clear()
      mediaFiles.value = []
      indexStatusText.value = ''
    }
  }
)

onMounted(async () => {
  await updateCachedCount()
  if (!vaultStore.locked) {
    await loadDevices()
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
        <!-- Rejim tanlash tabi -->
        <div v-if="!vaultStore.locked" class="cs-tabs">
          <button
            class="cs-btn cs-tab-btn"
            :class="{ 'is-active': activeTab === 'records' }"
            @click="handleTabChange('records')"
          >
            Barcha yozuvlar
          </button>
          <button
            class="cs-btn cs-tab-btn"
            :class="{ 'is-active': activeTab === 'media' }"
            @click="handleTabChange('media')"
          >
            Media indeksi
          </button>
        </div>

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

      <!-- Yangi yozuvlar banneri -->
      <div
        v-if="query.newerCount.value > 0"
        class="cs-banner-newer"
        @click="handleRefreshBanner"
      >
        <i class="pi pi-arrow-up"></i>
        <span>{{ query.newerCount.value }} ta yangi yozuv — yangilash</span>
      </div>

      <!-- TAB 1: Barcha yozuvlar -->
      <div v-if="activeTab === 'records'" class="cs-records-view">
        <!-- Boshqaruv va filterlar paneli -->
        <div class="cs-controls-bar">
          <div class="cs-control-group">
            <button
              class="cs-btn cs-btn-secondary cs-order-btn"
              @click="handleOrderToggle"
              :title="query.order.value === 'desc' ? 'Hozir: Eng yangi oldin' : 'Hozir: Eng eski oldin'"
            >
              <i :class="query.order.value === 'desc' ? 'pi pi-sort-amount-down' : 'pi pi-sort-amount-up-alt'"></i>
              <span>{{ query.order.value === 'desc' ? 'Yangi oldin' : 'Eski oldin' }}</span>
            </button>
          </div>

          <div class="cs-control-group">
            <label class="cs-label">Turi:</label>
            <select class="cs-select" :value="query.kind.value" @change="handleKindChange">
              <option value="">Barchasi</option>
              <option value="deleted">deleted</option>
              <option value="edited">edited</option>
              <option value="activity">activity</option>
              <option value="media_index">media_index</option>
            </select>
          </div>

          <div class="cs-control-group cs-search-group">
            <label class="cs-label">Muxbir:</label>
            <input
              type="text"
              class="cs-input"
              placeholder="Peer hash prefiksi..."
              :value="query.peerHash.value"
              @input="handlePeerInput"
            />
          </div>
        </div>

        <div v-if="query.error.value" class="cs-error-msg" data-testid="archive-query-error">
          Arxivni o'qib bo'lmadi: {{ query.error.value }}
        </div>

        <!-- Virtual scroll jadvali -->
        <RecordTable
          :rows="query.rows.value"
          :loading="query.loading.value"
          :device-names="deviceMap"
          @lazy-load="query.loadMore"
        />
      </div>

      <!-- TAB 2: Media indeksi -->
      <div v-else class="cs-media-view">
        <div class="cs-media-header">
          <p class="cs-text-muted">
            Fayllarning qurilmalardagi holati (LWW — eng so'nggi holat bo'yicha).
          </p>
        </div>

        <div class="cs-table-wrap">
          <table class="cs-table">
            <thead>
              <tr>
                <th>Fayl nomi</th>
                <th>Hajmi</th>
                <th>Qurilmalardagi holati</th>
              </tr>
            </thead>
            <tbody>
              <tr v-if="mediaFiles.length === 0">
                <td colspan="3" class="cs-empty-row">
                  {{ mediaLoading ? 'Media indeksi yuklanmoqda...' : 'Media yozuvlari topilmadi' }}
                </td>
              </tr>
              <tr
                v-for="file in mediaFiles"
                :key="file.fileKey"
                :class="{ 'cs-row-conflict': file.hasConflict }"
              >
                <td>
                  <span class="cs-file-name">{{ file.fileName }}</span>
                  <div v-if="file.sha256" class="cs-file-hash">
                    <code>{{ file.sha256.slice(0, 12) }}…</code>
                  </div>
                </td>
                <td>{{ formatBytes(file.size) }}</td>
                <td>
                  <div class="cs-device-statuses">
                    <span
                      v-for="dev in file.devices"
                      :key="dev.device_id"
                      class="cs-device-status-badge"
                      :class="dev.status === 'present' ? 'cs-status-present' : 'cs-status-pending'"
                    >
                      <i :class="dev.status === 'present' ? 'pi pi-check-circle' : 'pi pi-clock'"></i>
                      <span>{{ formatDevice(dev.device_id) }}</span>
                      <span v-if="dev.reason" class="cs-reason">({{ dev.reason }})</span>
                    </span>
                  </div>
                </td>
              </tr>
            </tbody>
          </table>
        </div>
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

.cs-actions {
  display: flex;
  align-items: center;
  gap: 0.75rem;
}

.cs-tabs {
  display: flex;
  gap: 0.25rem;
  background: var(--surface-ground, #f3f4f6);
  padding: 0.125rem;
  border-radius: 4px;
}

.cs-tab-btn {
  border: none;
  background: transparent;
  padding: 0.375rem 0.75rem;
  font-size: 0.8125rem;
  cursor: pointer;
  border-radius: 3px;
  color: var(--text-color-secondary, #6b7280);
}

.cs-tab-btn.is-active {
  background: var(--surface-card, #ffffff);
  color: var(--text-color, #111827);
  font-weight: 500;
  box-shadow: 0 1px 2px rgba(0, 0, 0, 0.05);
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

.cs-banner-newer {
  background: #eff6ff;
  border: 1px solid #bfdbfe;
  color: #1d4ed8;
  padding: 0.5rem 1rem;
  border-radius: 4px;
  margin-bottom: 1rem;
  display: flex;
  align-items: center;
  gap: 0.5rem;
  cursor: pointer;
  font-size: 0.875rem;
  font-weight: 500;
  transition: background 120ms;
}

.cs-banner-newer:hover {
  background: #dbeafe;
}

.cs-controls-bar {
  display: flex;
  align-items: center;
  gap: 1rem;
  margin-bottom: 1rem;
  padding: 0.5rem 0.75rem;
  background: var(--surface-ground, #f9fafb);
  border: 1px solid var(--surface-border, #e5e7eb);
  border-radius: 4px;
}

.cs-control-group {
  display: flex;
  align-items: center;
  gap: 0.5rem;
}

.cs-label {
  font-size: 0.8125rem;
  color: var(--text-color-secondary, #6b7280);
}

.cs-select,
.cs-input {
  padding: 0.25rem 0.5rem;
  border: 1px solid var(--surface-border, #e5e7eb);
  border-radius: 3px;
  font-size: 0.8125rem;
  background: var(--surface-card, #ffffff);
}

.cs-search-group {
  flex: 1;
}

.cs-search-group .cs-input {
  width: 100%;
}

.cs-order-btn {
  display: flex;
  align-items: center;
  gap: 0.375rem;
  font-size: 0.8125rem;
}

.cs-media-header {
  margin-bottom: 0.5rem;
}

.cs-file-name {
  font-weight: 500;
  font-size: 0.8125rem;
}

.cs-file-hash {
  font-size: 0.75rem;
  color: var(--text-color-secondary, #6b7280);
}

.cs-device-statuses {
  display: flex;
  flex-wrap: wrap;
  gap: 0.5rem;
}

.cs-device-status-badge {
  display: inline-flex;
  align-items: center;
  gap: 0.25rem;
  padding: 0.125rem 0.375rem;
  border-radius: 3px;
  font-size: 0.75rem;
}

.cs-status-present {
  background: #ecfdf5;
  color: #065f46;
  border: 1px solid #a7f3d0;
}

.cs-status-pending {
  background: #fffbeb;
  color: #92400e;
  border: 1px solid #fde68a;
}

.cs-row-conflict {
  background: #fffdf5;
}

.cs-reason {
  opacity: 0.8;
  margin-left: 0.125rem;
}

.cs-empty-row {
  text-align: center;
  color: var(--text-color-secondary, #6b7280);
  padding: 2rem;
}
</style>
