<script setup lang="ts">
import { ref, onMounted } from 'vue'
import { useVaultStore } from '../stores/vault'
import { getKeyWraps, deleteKeyWrap, createKeyWrap } from '../api/keys'
import type { KeyWrapSummary } from '../api/types'
import { ApiError } from '../api/client'

const vaultStore = useVaultStore()

const wraps = ref<KeyWrapSummary[]>([])
const loading = ref(false)
const errorMessage = ref<string | null>(null)

// Delete confirm modal
const deletingWrap = ref<KeyWrapSummary | null>(null)
const deleteLoading = ref(false)

// New wrap modal
const showNewWrapModal = ref(false)
const newLabel = ref('')
const newPassphrase = ref('')
const newConfirmPassphrase = ref('')
const newWrapError = ref<string | null>(null)
const creatingWrap = ref(false)

async function loadWraps() {
  loading.value = true
  errorMessage.value = null
  try {
    wraps.value = await getKeyWraps()
  } catch (err: any) {
    if (err instanceof ApiError && err.status === 0) {
      errorMessage.value = "Tarmoq xatosi. Server bilan aloqa yo'q."
    } else {
      errorMessage.value = "O'ramlarni yuklashda xatolik yuz berdi."
    }
  } finally {
    loading.value = false
  }
}

function openDeleteModal(wrap: KeyWrapSummary) {
  if (wraps.value.length <= 1) return
  deletingWrap.value = wrap
}

function closeDeleteModal() {
  deletingWrap.value = null
}

async function handleConfirmDelete() {
  if (!deletingWrap.value) return
  deleteLoading.value = true
  try {
    await deleteKeyWrap(deletingWrap.value.wrap_id)
    deletingWrap.value = null
    await loadWraps()
  } catch (err: any) {
    if (err instanceof ApiError && err.status === 409) {
      deletingWrap.value = null
      await loadWraps()
      errorMessage.value = "Bu oxirgi o'ram. O'chirsangiz arxivga kirish butunlay yo'qoladi."
    } else {
      errorMessage.value = "O'ramni o'chirishda xatolik yuz berdi."
    }
  } finally {
    deleteLoading.value = false
  }
}

function openNewWrapModal() {
  newLabel.value = ''
  newPassphrase.value = ''
  newConfirmPassphrase.value = ''
  newWrapError.value = null
  showNewWrapModal.value = true
}

function closeNewWrapModal() {
  showNewWrapModal.value = false
}

async function handleCreateWrap() {
  newWrapError.value = null

  if (!newLabel.value.trim()) {
    newWrapError.value = "Yorliq (nom) kiritilishi shart."
    return
  }

  if (newPassphrase.value.length < 12) {
    newWrapError.value = "Parol kamida 12 belgidan iborat bo'lishi kerak."
    return
  }

  if (newPassphrase.value !== newConfirmPassphrase.value) {
    newWrapError.value = "Parollar mos kelmadi."
    return
  }

  creatingWrap.value = true
  try {
    const worker = vaultStore.getWorker()
    const wrapRes = await worker.createWrap(newPassphrase.value, newLabel.value)

    if (!wrapRes.ok || !wrapRes.salt || !wrapRes.nonce || !wrapRes.wrapped_key || !wrapRes.iterations) {
      newWrapError.value = "O'ramni shifrlashda xatolik yuz berdi."
      return
    }

    await createKeyWrap({
      wrap_type: 'passphrase',
      label: newLabel.value.trim(),
      salt: wrapRes.salt,
      nonce: wrapRes.nonce,
      wrapped_key: wrapRes.wrapped_key,
      iterations: wrapRes.iterations,
    })

    showNewWrapModal.value = false
    await loadWraps()
  } catch (err: any) {
    newWrapError.value = err.message || "Yangi o'ram yaratishda xatolik yuz berdi."
  } finally {
    creatingWrap.value = false
  }
}

function formatDate(iso: string | null): string {
  if (!iso) return '-'
  try {
    const d = new Date(iso)
    return isNaN(d.getTime()) ? iso : d.toLocaleString()
  } catch {
    return iso
  }
}

onMounted(() => {
  loadWraps()
})
</script>

<template>
  <div class="cs-card">
    <div class="cs-header-row">
      <div>
        <h2>Kalitlar</h2>
        <p class="cs-subtitle">Master kalitning shifrlangan o'ramlari va kalit boshqaruvi.</p>
      </div>
      <div>
        <button
          v-if="!vaultStore.locked"
          class="cs-btn cs-btn-primary cs-btn-new-wrap"
          @click="openNewWrapModal"
        >
          Yangi o'ram
        </button>
      </div>
    </div>

    <div v-if="errorMessage" class="cs-alert cs-alert-error">
      {{ errorMessage }}
    </div>

    <div v-if="wraps.length === 1" class="cs-alert cs-alert-warning">
      Bu oxirgi o'ram. O'chirsangiz arxivga kirish butunlay yo'qoladi.
    </div>

    <div class="cs-table-wrap">
      <table class="cs-table">
        <thead>
          <tr>
            <th>Turi</th>
            <th>Yorliq</th>
            <th>Yaratilgan</th>
            <th>Oxirgi ishlatilgan</th>
            <th style="text-align: right;">Amallar</th>
          </tr>
        </thead>
        <tbody>
          <tr v-if="loading && wraps.length === 0">
            <td colspan="5" class="cs-empty-row">Yuklanmoqda...</td>
          </tr>
          <tr v-else-if="wraps.length === 0">
            <td colspan="5" class="cs-empty-row">O'ramlar topilmadi</td>
          </tr>
          <tr v-for="w in wraps" :key="w.wrap_id">
            <td>
              <span class="cs-badge">{{ w.wrap_type }}</span>
            </td>
            <td><strong>{{ w.label }}</strong></td>
            <td>{{ formatDate(w.created_at) }}</td>
            <td>{{ formatDate(w.last_used_at) }}</td>
            <td style="text-align: right;">
              <button
                class="cs-btn cs-btn-danger cs-btn-delete"
                :disabled="wraps.length <= 1"
                @click="openDeleteModal(w)"
              >
                O'chirish
              </button>
            </td>
          </tr>
        </tbody>
      </table>
    </div>

    <!-- O'chirishni tasdiqlash modali -->
    <div v-if="deletingWrap" class="cs-modal-backdrop" @click.self="closeDeleteModal">
      <div class="cs-modal-card">
        <div class="cs-modal-header">
          <h3>O'ramni o'chirish</h3>
          <button class="cs-btn-icon" aria-label="Yopish" @click="closeDeleteModal">
            <i class="pi pi-times"></i>
          </button>
        </div>
        <p>
          Haqiqatan ham <strong>"{{ deletingWrap.label }}"</strong> o'ramini o'chirmoqchimisiz?
        </p>
        <div class="cs-modal-actions">
          <button class="cs-btn cs-btn-cancel" :disabled="deleteLoading" @click="closeDeleteModal">
            Bekor qilish
          </button>
          <button
            class="cs-btn cs-btn-danger cs-btn-confirm-delete"
            :disabled="deleteLoading"
            @click="handleConfirmDelete"
          >
            {{ deleteLoading ? "O'chirilmoqda..." : "O'chirish" }}
          </button>
        </div>
      </div>
    </div>

    <!-- Yangi o'ram yaratish modali -->
    <div v-if="showNewWrapModal" class="cs-modal-backdrop" @click.self="closeNewWrapModal">
      <div class="cs-modal-card">
        <div class="cs-modal-header">
          <h3>Yangi kalit o'rami</h3>
          <button class="cs-btn-icon" aria-label="Yopish" @click="closeNewWrapModal">
            <i class="pi pi-times"></i>
          </button>
        </div>

        <div v-if="newWrapError" class="cs-alert cs-alert-error">
          {{ newWrapError }}
        </div>

        <form @submit.prevent="handleCreateWrap">
          <div class="cs-form-group">
            <label for="new-wrap-label">Yorliq (nom):</label>
            <input
              id="new-wrap-label"
              v-model="newLabel"
              name="label"
              type="text"
              class="cs-input"
              placeholder="Masalan: Asosiy arxiv paroli 2026"
              :disabled="creatingWrap"
            />
          </div>

          <div class="cs-form-group">
            <label for="new-wrap-passphrase">Yangi parol (kamida 12 belgi):</label>
            <input
              id="new-wrap-passphrase"
              v-model="newPassphrase"
              name="passphrase"
              type="password"
              class="cs-input"
              placeholder="Kamida 12 belgidan iborat kuchli parol"
              :disabled="creatingWrap"
            />
          </div>

          <div class="cs-form-group">
            <label for="new-wrap-confirm">Parolni tasdiqlang:</label>
            <input
              id="new-wrap-confirm"
              v-model="newConfirmPassphrase"
              name="confirmPassphrase"
              type="password"
              class="cs-input"
              placeholder="Parolni qayta kiriting"
              :disabled="creatingWrap"
            />
          </div>

          <div class="cs-modal-actions">
            <button
              type="button"
              class="cs-btn cs-btn-cancel"
              :disabled="creatingWrap"
              @click="closeNewWrapModal"
            >
              Bekor qilish
            </button>
            <button
              type="submit"
              class="cs-btn cs-btn-primary cs-btn-submit-wrap"
              :disabled="creatingWrap"
              @click.prevent="handleCreateWrap"
            >
              {{ creatingWrap ? 'Yaratilmoqda...' : 'Yaratish' }}
            </button>
          </div>
        </form>
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
  padding: 0.625rem 0.75rem;
  border-radius: 4px;
  margin-bottom: 1rem;
  font-size: 0.8125rem;
}

.cs-alert-error {
  background: #fef2f2;
  color: #b91c1c;
  border: 1px solid #fecaca;
}

.cs-alert-warning {
  background: #fffbeb;
  color: #b45309;
  border: 1px solid #fde68a;
}

.cs-badge {
  display: inline-block;
  padding: 0.125rem 0.375rem;
  border-radius: 3px;
  background: var(--surface-ground, #f3f4f6);
  font-size: 0.75rem;
  font-family: monospace;
}

.cs-empty-row {
  text-align: center;
  color: var(--text-color-secondary, #6b7280);
  padding: 1.5rem;
}

.cs-modal-backdrop {
  position: fixed;
  inset: 0;
  background: rgba(0, 0, 0, 0.45);
  display: flex;
  align-items: center;
  justify-content: center;
  z-index: 1000;
  padding: 1rem;
}

.cs-modal-card {
  background: var(--surface-card, #ffffff);
  border: 1px solid var(--surface-border, #e5e7eb);
  border-radius: 6px;
  padding: 1.25rem;
  width: 100%;
  max-width: 460px;
}

.cs-modal-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 1rem;
}

.cs-modal-header h3 {
  margin: 0;
  font-size: 1.125rem;
  font-weight: 600;
}

.cs-form-group {
  margin-bottom: 1rem;
}

.cs-form-group label {
  display: block;
  font-size: 0.8125rem;
  font-weight: 500;
  margin-bottom: 0.25rem;
}

.cs-input {
  width: 100%;
  padding: 0.375rem 0.5rem;
  border: 1px solid var(--surface-border, #d1d5db);
  border-radius: 4px;
  background: var(--surface-ground, #ffffff);
  color: var(--text-color, #111827);
  font-size: 0.8125rem;
  box-sizing: border-box;
}

.cs-input:focus {
  outline: none;
  border-color: var(--primary-color, #3b82f6);
}

.cs-modal-actions {
  display: flex;
  justify-content: flex-end;
  gap: 0.5rem;
  margin-top: 1.25rem;
}
</style>
