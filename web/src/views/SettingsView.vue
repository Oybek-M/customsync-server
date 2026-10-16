<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted } from 'vue'
import { getSettings, updateSetting } from '../api/settings'
import type { Setting } from '../api/types'

const settings = ref<Setting[]>([])
const loading = ref(false)
const globalError = ref<string | null>(null)

// Local form edit values: key -> string or boolean (for checkbox)
const editValues = ref<Record<string, any>>({})
const errors = ref<Record<string, string>>({})
const saving = ref<Record<string, boolean>>({})
const savedSuccess = ref<Record<string, boolean>>({})

let abortController: AbortController | null = null

const categories = computed(() => {
  const map: Record<string, Setting[]> = {}
  for (const s of settings.value) {
    const cat = s.category || 'other'
    if (!map[cat]) map[cat] = []
    map[cat].push(s)
  }
  return map
})

async function loadData() {
  abortController = new AbortController()
  loading.value = true
  globalError.value = null

  try {
    const data = await getSettings(abortController.signal)
    settings.value = data
    for (const s of data) {
      if (s.value_type === 'bool') {
        editValues.value[s.key] = s.value.toLowerCase() === 'true'
      } else {
        editValues.value[s.key] = s.value
      }
    }
  } catch (err: unknown) {
    if (err instanceof DOMException && err.name === 'AbortError') return
    globalError.value = 'Sozlamalarni yuklashda xatolik yuz berdi'
  } finally {
    loading.value = false
  }
}

onMounted(() => {
  loadData()
})

onUnmounted(() => {
  if (abortController) {
    abortController.abort()
  }
})

async function saveSetting(s: Setting) {
  errors.value[s.key] = ''
  savedSuccess.value[s.key] = false

  let valToSend = ''
  const currentVal = editValues.value[s.key]

  if (s.value_type === 'bool') {
    valToSend = currentVal ? 'true' : 'false'
  } else {
    valToSend = typeof currentVal === 'string' ? currentVal : String(currentVal ?? '')

    // Client-side validation
    if (s.value_type === 'int') {
      if (!/^-?\d+$/.test(valToSend.trim())) {
        errors.value[s.key] = 'Qiymat butun son (int) bo\'lishi kerak, noto\'g\'ri format'
        return
      }
    } else if (s.value_type === 'json') {
      try {
        JSON.parse(valToSend)
      } catch {
        errors.value[s.key] = 'JSON formati noto\'g\'ri'
        return
      }
    }
  }

  saving.value[s.key] = true
  try {
    await updateSetting(s.key, valToSend)
    s.value = valToSend
    savedSuccess.value[s.key] = true
    setTimeout(() => {
      savedSuccess.value[s.key] = false
    }, 2000)
  } catch (err: any) {
    errors.value[s.key] = 'Qiymat noto\'g\'ri yoki server qabul qilmadi'
  } finally {
    saving.value[s.key] = false
  }
}
</script>

<template>
  <div class="cs-settings-view">
    <div class="cs-header-row">
      <div>
        <h2>Sozlamalar</h2>
        <p class="cs-subtitle">Server parametrlari va boshqaruv konfiguratsiyasi.</p>
      </div>
    </div>

    <div v-if="globalError" class="cs-error-msg">
      {{ globalError }}
    </div>

    <div v-for="(items, category) in categories" :key="category" class="cs-category-section">
      <h3 class="cs-category-title">{{ category }}</h3>

      <div class="cs-settings-list">
        <div v-for="s in items" :key="s.key" class="cs-setting-item">
          <div class="cs-setting-info">
            <div class="cs-setting-key">{{ s.key }}</div>
            <div class="cs-setting-desc">{{ s.description }}</div>
            <div v-if="s.key.includes('quota')" class="cs-setting-hint">
              Maslahat: 0 = cheksiz
            </div>
          </div>

          <div class="cs-setting-editor">
            <!-- Bool Toggle/Checkbox -->
            <template v-if="s.value_type === 'bool'">
              <label class="cs-toggle-wrap">
                <input
                  v-model="editValues[s.key]"
                  type="checkbox"
                  class="cs-checkbox"
                  :data-key="s.key"
                  :disabled="saving[s.key]"
                />
                <span class="cs-toggle-label">{{ editValues[s.key] ? 'Yoqilgan' : 'O\'chirilgan' }}</span>
              </label>
            </template>

            <!-- JSON Editor (Textarea) -->
            <template v-else-if="s.value_type === 'json'">
              <textarea
                v-model="editValues[s.key]"
                class="cs-textarea"
                rows="3"
                :data-key="s.key"
                :disabled="saving[s.key]"
              ></textarea>
            </template>

            <!-- Int Editor -->
            <template v-else-if="s.value_type === 'int'">
              <input
                v-model="editValues[s.key]"
                type="number"
                class="cs-input"
                :data-key="s.key"
                :disabled="saving[s.key]"
              />
            </template>

            <!-- Other/String Editor (Input) -->
            <template v-else>
              <input
                v-model="editValues[s.key]"
                type="text"
                class="cs-input"
                :data-key="s.key"
                :disabled="saving[s.key]"
              />
            </template>

            <button
              class="cs-btn cs-btn-sm cs-btn-primary"
              :data-save="s.key"
              :disabled="saving[s.key]"
              @click="saveSetting(s)"
            >
              {{ saving[s.key] ? 'Saqlanmoqda...' : 'Saqlash' }}
            </button>
          </div>

          <div v-if="errors[s.key]" class="cs-setting-error" :data-key="s.key">
            {{ errors[s.key] }}
          </div>
          <div v-else-if="savedSuccess[s.key]" class="cs-setting-success">
            Saqlandi!
          </div>
        </div>
      </div>
    </div>
  </div>
</template>

<style scoped>
.cs-header-row {
  margin-bottom: 1.5rem;
}

.cs-subtitle {
  color: var(--text-color-secondary, #6b7280);
  font-size: 0.875rem;
  margin-top: 0.25rem;
}

.cs-category-section {
  margin-bottom: 2rem;
  background: var(--surface-card, #ffffff);
  border: 1px solid var(--surface-border, #e5e7eb);
  border-radius: 6px;
  padding: 1.25rem;
}

.cs-category-title {
  text-transform: uppercase;
  font-size: 0.8125rem;
  letter-spacing: 0.05em;
  color: var(--text-color-secondary, #6b7280);
  border-bottom: 1px solid var(--surface-border, #e5e7eb);
  padding-bottom: 0.5rem;
  margin-bottom: 1rem;
}

.cs-settings-list {
  display: flex;
  flex-direction: column;
  gap: 1.25rem;
}

.cs-setting-item {
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
  padding-bottom: 1rem;
  border-bottom: 1px dashed var(--surface-border, #f3f4f6);
}

.cs-setting-item:last-child {
  border-bottom: none;
  padding-bottom: 0;
}

.cs-setting-key {
  font-family: monospace;
  font-weight: 600;
  font-size: 0.875rem;
}

.cs-setting-desc {
  font-size: 0.8125rem;
  color: var(--text-color-secondary, #6b7280);
  margin-top: 0.125rem;
}

.cs-setting-hint {
  font-size: 0.75rem;
  color: var(--primary-color, #3b82f6);
  margin-top: 0.25rem;
}

.cs-setting-editor {
  display: flex;
  align-items: flex-start;
  gap: 0.75rem;
  margin-top: 0.25rem;
}

.cs-input {
  flex: 1;
  max-width: 400px;
  padding: 0.375rem 0.5rem;
  border: 1px solid var(--surface-border, #d1d5db);
  border-radius: 4px;
}

.cs-textarea {
  flex: 1;
  max-width: 500px;
  font-family: monospace;
  font-size: 0.8125rem;
  padding: 0.375rem 0.5rem;
  border: 1px solid var(--surface-border, #d1d5db);
  border-radius: 4px;
}

.cs-toggle-wrap {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  cursor: pointer;
  height: 32px;
}

.cs-checkbox {
  width: 16px;
  height: 16px;
  cursor: pointer;
}

.cs-toggle-label {
  font-size: 0.8125rem;
}

.cs-setting-error {
  color: var(--red-500, #ef4444);
  font-size: 0.8125rem;
}

.cs-setting-success {
  color: #10b981;
  font-size: 0.8125rem;
}
</style>
