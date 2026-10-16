<script setup lang="ts">
import { ref, computed, watch, onMounted } from 'vue'
import { useVaultStore } from '../stores/vault'
import { getKeyWraps, getKeyWrap } from '../api/keys'
import type { KeyWrapSummary } from '../api/types'
import { ApiError } from '../api/client'

const props = defineProps<{
  visible?: boolean
}>()

const emit = defineEmits<{
  (e: 'update:visible', val: boolean): void
  (e: 'close'): void
}>()

const vaultStore = useVaultStore()

const wraps = ref<KeyWrapSummary[]>([])
const selectedWrapId = ref<string>('')
const passphrase = ref<string>('')
const loading = ref<boolean>(false)
const errorMessage = ref<string | null>(null)
const successFp = ref<string | null>(null)

const isVisible = computed(() => props.visible || vaultStore.showUnlockDialog)

const selectedWrap = computed(() => {
  return wraps.value.find((w) => w.wrap_id === selectedWrapId.value)
})

function formatFingerprint(fp: string): string {
  const clean = fp.replace(/\s+/g, '')
  return clean.match(/.{1,4}/g)?.join(' ') ?? clean
}

async function loadWraps() {
  try {
    const list = await getKeyWraps()
    wraps.value = list
    if (list.length > 0) {
      // Avtomatik birinchi mos passphrase o'ramini tanlaymiz
      const currentExists = list.some((w) => w.wrap_id === selectedWrapId.value)
      if (!currentExists) {
        const firstPass = list.find((w) => w.wrap_type === 'passphrase')
        selectedWrapId.value = firstPass ? firstPass.wrap_id : list[0].wrap_id
      }
    }
  } catch (err: any) {
    if (err instanceof ApiError && err.status === 0) {
      errorMessage.value = "Tarmoq xatosi. Server bilan aloqa yo'q."
    }
  }
}

watch(
  isVisible,
  (val) => {
    if (val) {
      errorMessage.value = null
      successFp.value = null
      passphrase.value = ''
      loadWraps()
    }
  },
  { immediate: true }
)

onMounted(() => {
  if (isVisible.value) {
    loadWraps()
  }
})

function handleClose() {
  emit('update:visible', false)
  emit('close')
  vaultStore.closeUnlockDialog()
}

async function handleUnlock() {
  const pw = passphrase.value
  // Har bir urinishdan keyin parol maydoni tozalanadi
  passphrase.value = ''

  if (!selectedWrapId.value || !pw) return

  if (selectedWrap.value && selectedWrap.value.wrap_type !== 'passphrase') {
    errorMessage.value = "Ushbu o'ram turi web panelda hali qo'llab-quvvatlanmaydi."
    return
  }

  loading.value = true
  errorMessage.value = null

  try {
    const wrapDetail = await getKeyWrap(selectedWrapId.value)
    const worker = vaultStore.getWorker()

    const res = await worker.unlock(pw, wrapDetail)
    if (!res.ok || !res.fingerprint) {
      errorMessage.value = "Parol noto'g'ri."
      vaultStore.lock()
      return
    }

    successFp.value = res.fingerprint
    vaultStore.unlock(res.fingerprint)
  } catch (err: any) {
    if (err instanceof ApiError) {
      if (err.status === 429) {
        errorMessage.value = "Urinishlar soni juda ko'p. Iltimos, biroz kuting."
      } else if (err.status === 404) {
        errorMessage.value = "O'ram topilmadi (ehtimol o'chirilgan). Ro'yxatni yangilang."
        await loadWraps()
      } else if (err.status === 0) {
        errorMessage.value = "Tarmoq xatosi. Server bilan aloqa yo'q."
      } else {
        errorMessage.value = `Server xatosi (${err.status}): ${err.code}`
      }
    } else {
      errorMessage.value = "Kutilmagan xatolik yuz berdi."
    }
  } finally {
    loading.value = false
  }
}
</script>

<template>
  <div v-if="isVisible" class="cs-modal-backdrop" @click.self="handleClose">
    <div class="cs-modal-card">
      <div class="cs-modal-header">
        <h3>Omborni ochish</h3>
        <button class="cs-btn-icon cs-btn-close" aria-label="Yopish" @click="handleClose">
          <i class="pi pi-times"></i>
        </button>
      </div>

      <div v-if="successFp" class="cs-unlock-success">
        <div class="cs-alert cs-alert-success">
          <i class="pi pi-check-circle"></i>
          <span>Ombor muvaffaqiyatli ochildi!</span>
        </div>
        <div class="cs-fp-box">
          <div class="cs-fp-label">Kalit barmoq izi (FP):</div>
          <div class="cs-fp-value">{{ formatFingerprint(successFp) }}</div>
          <div class="cs-fp-hint">Ushbu qiymatni tdesktop ilovasidagi "Kalit barmoq izi (FP)" bilan solishtiring.</div>
        </div>
        <div class="cs-modal-actions">
          <button class="cs-btn cs-btn-primary" @click="handleClose">Yopish</button>
        </div>
      </div>

      <form v-else @submit.prevent="handleUnlock">
        <div v-if="errorMessage" class="cs-alert cs-alert-error">
          {{ errorMessage }}
        </div>

        <div class="cs-form-group">
          <label for="wrap-select">Kalit o'rami:</label>
          <select id="wrap-select" v-model="selectedWrapId" class="cs-select" :disabled="loading">
            <option
              v-for="w in wraps"
              :key="w.wrap_id"
              :value="w.wrap_id"
            >
              {{ w.label }} ({{ w.wrap_type }}){{ w.wrap_type !== 'passphrase' ? ' — web panelda hali qo\'llab-quvvatlanmaydi' : '' }}
            </option>
          </select>
          <div v-if="selectedWrap && selectedWrap.wrap_type !== 'passphrase'" class="cs-warning-hint">
            web panelda hali qo'llab-quvvatlanmaydi
          </div>
        </div>

        <div class="cs-form-group">
          <label for="unlock-passphrase">Parol:</label>
          <input
            id="unlock-passphrase"
            v-model="passphrase"
            type="password"
            class="cs-input"
            placeholder="O'ram parolini kiriting"
            autocomplete="current-password"
            :disabled="loading || (selectedWrap && selectedWrap.wrap_type !== 'passphrase')"
          />
        </div>

        <div class="cs-modal-actions">
          <button type="button" class="cs-btn cs-btn-cancel" :disabled="loading" @click="handleClose">
            Bekor qilish
          </button>
          <button
            type="submit"
            class="cs-btn cs-btn-primary cs-btn-unlock"
            :disabled="loading || !passphrase || (selectedWrap && selectedWrap.wrap_type !== 'passphrase')"
          >
            {{ loading ? 'Ochilmoqda...' : 'Ochish' }}
          </button>
        </div>
      </form>
    </div>
  </div>
</template>

<style scoped>
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
  color: var(--text-color, #374151);
}

.cs-select,
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

.cs-select:focus,
.cs-input:focus {
  outline: none;
  border-color: var(--primary-color, #3b82f6);
}

.cs-warning-hint {
  font-size: 0.75rem;
  color: #d97706;
  margin-top: 0.25rem;
}

.cs-alert {
  padding: 0.625rem 0.75rem;
  border-radius: 4px;
  margin-bottom: 1rem;
  font-size: 0.8125rem;
  display: flex;
  align-items: center;
  gap: 0.5rem;
}

.cs-alert-error {
  background: #fef2f2;
  color: #b91c1c;
  border: 1px solid #fecaca;
}

.cs-alert-success {
  background: #f0fdf4;
  color: #15803d;
  border: 1px solid #bbf7d0;
}

.cs-fp-box {
  background: var(--surface-ground, #f9fafb);
  border: 1px solid var(--surface-border, #e5e7eb);
  border-radius: 4px;
  padding: 0.875rem;
  margin: 1rem 0;
  text-align: center;
}

.cs-fp-label {
  font-size: 0.75rem;
  color: var(--text-color-secondary, #6b7280);
  margin-bottom: 0.25rem;
}

.cs-fp-value {
  font-family: monospace;
  font-size: 1.125rem;
  font-weight: 700;
  letter-spacing: 0.05em;
  color: var(--primary-color, #2563eb);
}

.cs-fp-hint {
  font-size: 0.75rem;
  color: var(--text-color-secondary, #6b7280);
  margin-top: 0.5rem;
}

.cs-modal-actions {
  display: flex;
  justify-content: flex-end;
  gap: 0.5rem;
  margin-top: 1.25rem;
}
</style>
