<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from 'vue'

type TunnelRecord = {
  id: string
  batchId: string | null
  note: string | null
  remoteProxy: string
  remoteProxyDisplay: string
  downstreamProtocol: string
  listenAddress: string
  publicHost: string
  requestedListenPort: number
  activeListenPort: number
  forwardedProxy: string | null
  status: string
  createdAt: string
  startedAt: string | null
  stoppedAt: string | null
  lastError: string | null
}

type BatchSummary = {
  batchId: string
  totalCount: number
  runningCount: number
}

type ImportResponse = {
  batchId: string
  importedCount: number
  items: TunnelRecord[]
}

const apiBaseUrl = (import.meta.env.VITE_API_BASE_URL as string | undefined)?.replace(/\/$/, '') ?? ''

const importForm = reactive({
  proxyText: '',
  downstreamProtocol: 'http',
  batchId: '',
  note: '',
  listenAddress: '0.0.0.0',
  publicHost: '',
  firstListenPort: '',
  autoStart: true,
})

const manualForm = reactive({
  proxy: '',
  downstreamProtocol: 'http',
  batchId: 'manual',
  note: '',
  listenAddress: '0.0.0.0',
  publicHost: '',
  listenPort: '',
  autoStart: true,
})

const tunnels = ref<TunnelRecord[]>([])
const batches = ref<BatchSummary[]>([])
const busy = ref(false)
const statusMessage = ref('')
const errorMessage = ref('')

const runningCount = computed(() => tunnels.value.filter((item) => item.status === 'Running').length)
const stoppedCount = computed(() => tunnels.value.filter((item) => item.status === 'Stopped').length)
const errorCount = computed(() => tunnels.value.filter((item) => item.status === 'Error').length)

function normalizeProxyLines(value: string) {
  return value
    .split(/\r?\n/)
    .map((line) => line.trim())
    .filter((line) => line && !line.startsWith('#'))
}

function detectProxyProtocol(value: string): 'http' | 'socks5' | 'unknown' {
  const trimmed = value.trim()
  if (!trimmed) {
    return 'unknown'
  }

  if (/^socks5:\/\//i.test(trimmed)) {
    return 'socks5'
  }

  if (/^http:\/\//i.test(trimmed)) {
    return 'http'
  }

  if (trimmed.includes('://')) {
    return 'unknown'
  }

  return 'http'
}

const manualUpstreamProtocol = computed(() => detectProxyProtocol(manualForm.proxy))

const importUpstreamProtocols = computed(() =>
  normalizeProxyLines(importForm.proxyText).map((line) => detectProxyProtocol(line)),
)

const manualSupportsSocks5Downstream = computed(() => manualUpstreamProtocol.value === 'socks5')

const importSupportsSocks5Downstream = computed(() => {
  const protocols = importUpstreamProtocols.value
  return protocols.length > 0 && protocols.every((protocol) => protocol === 'socks5')
})

const manualDownstreamHint = computed(() => {
  if (!manualForm.proxy.trim()) {
    return '只有上游为 SOCKS5 时，才能选择下游 SOCKS5。'
  }

  if (manualSupportsSocks5Downstream.value) {
    return '当前上游为 SOCKS5，可以选择下游 HTTP 或 SOCKS5。'
  }

  return '当前上游会按 HTTP 处理，因此下游只能选择 HTTP。'
})

const importDownstreamHint = computed(() => {
  const protocols = importUpstreamProtocols.value
  if (protocols.length === 0) {
    return '批量导入时，只有每一行上游都显式为 socks5://...，才能选择下游 SOCKS5。'
  }

  if (importSupportsSocks5Downstream.value) {
    return '当前批次的上游全部为 SOCKS5，可以选择下游 HTTP 或 SOCKS5。'
  }

  return '批次中只要包含 HTTP 上游，或未写 scheme 的代理行，系统就会限制为下游 HTTP。'
})

watch(manualSupportsSocks5Downstream, (supported) => {
  if (!supported && manualForm.downstreamProtocol === 'socks5') {
    manualForm.downstreamProtocol = 'http'
  }
})

watch(importSupportsSocks5Downstream, (supported) => {
  if (!supported && importForm.downstreamProtocol === 'socks5') {
    importForm.downstreamProtocol = 'http'
  }
})

async function apiFetch<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(`${apiBaseUrl}${path}`, {
    headers: {
      'Content-Type': 'application/json',
      ...(init?.headers ?? {}),
    },
    ...init,
  })

  if (!response.ok) {
    const payload = await response.json().catch(() => ({ message: response.statusText }))
    throw new Error(payload.message ?? '请求失败')
  }

  return response.json() as Promise<T>
}

async function loadData() {
  busy.value = true
  errorMessage.value = ''

  try {
    const [tunnelData, batchData] = await Promise.all([
      apiFetch<TunnelRecord[]>('/api/tunnels'),
      apiFetch<BatchSummary[]>('/api/batches'),
    ])

    tunnels.value = tunnelData
    batches.value = batchData
  } catch (error) {
    errorMessage.value = (error as Error).message
  } finally {
    busy.value = false
  }
}

async function importBatch() {
  if (!importForm.proxyText.trim()) {
    errorMessage.value = '请先粘贴 proxy.txt 内容。'
    return
  }

  busy.value = true
  errorMessage.value = ''

  try {
    const payload = {
      proxyText: importForm.proxyText,
      downstreamProtocol: importForm.downstreamProtocol,
      batchId: importForm.batchId || null,
      note: importForm.note || null,
      listenAddress: importForm.listenAddress || null,
      publicHost: importForm.publicHost || null,
      firstListenPort: importForm.firstListenPort ? Number(importForm.firstListenPort) : null,
      autoStart: importForm.autoStart,
    }

    const response = await apiFetch<ImportResponse>('/api/tunnels/import', {
      method: 'POST',
      body: JSON.stringify(payload),
    })

    importForm.proxyText = ''
    statusMessage.value = `批次 ${response.batchId} 已导入 ${response.importedCount} 个代理。`
    await loadData()
  } catch (error) {
    errorMessage.value = (error as Error).message
  } finally {
    busy.value = false
  }
}

async function addManualProxy() {
  if (!manualForm.proxy.trim()) {
    errorMessage.value = '请填写一个 HTTP 或 SOCKS5 代理。'
    return
  }

  busy.value = true
  errorMessage.value = ''

  try {
    await apiFetch<TunnelRecord>('/api/tunnels', {
      method: 'POST',
      body: JSON.stringify({
        proxy: manualForm.proxy,
        downstreamProtocol: manualForm.downstreamProtocol,
        batchId: manualForm.batchId || null,
        note: manualForm.note || null,
        listenAddress: manualForm.listenAddress || null,
        publicHost: manualForm.publicHost || null,
        listenPort: manualForm.listenPort ? Number(manualForm.listenPort) : null,
        autoStart: manualForm.autoStart,
      }),
    })

    manualForm.proxy = ''
    manualForm.note = ''
    manualForm.listenPort = ''
    statusMessage.value = '手动代理已添加。'
    await loadData()
  } catch (error) {
    errorMessage.value = (error as Error).message
  } finally {
    busy.value = false
  }
}

async function stopTunnel(id: string) {
  busy.value = true
  errorMessage.value = ''

  try {
    await apiFetch<TunnelRecord>(`/api/tunnels/${id}/stop`, { method: 'POST' })
    statusMessage.value = '代理已停止。'
    await loadData()
  } catch (error) {
    errorMessage.value = (error as Error).message
  } finally {
    busy.value = false
  }
}

async function startTunnel(item: TunnelRecord) {
  busy.value = true
  errorMessage.value = ''

  try {
    await apiFetch<TunnelRecord>(`/api/tunnels/${item.id}/start`, {
      method: 'POST',
      body: JSON.stringify({
        downstreamProtocol: item.downstreamProtocol,
        listenAddress: item.listenAddress,
        publicHost: item.publicHost,
        listenPort: item.requestedListenPort > 0 ? item.requestedListenPort : null,
      }),
    })

    statusMessage.value = '代理已启动。'
    await loadData()
  } catch (error) {
    errorMessage.value = (error as Error).message
  } finally {
    busy.value = false
  }
}

async function stopBatch(batchId: string) {
  busy.value = true
  errorMessage.value = ''

  try {
    const response = await apiFetch<{ batchId: string; stoppedCount: number }>('/api/tunnels/stop-batch', {
      method: 'POST',
      body: JSON.stringify({ batchId }),
    })

    statusMessage.value = `批次 ${response.batchId} 已停止 ${response.stoppedCount} 个代理。`
    await loadData()
  } catch (error) {
    errorMessage.value = (error as Error).message
  } finally {
    busy.value = false
  }
}

async function copyProxy(value: string | null) {
  if (!value) {
    return
  }

  try {
    await navigator.clipboard.writeText(value)
    statusMessage.value = `已复制 ${value}`
  } catch {
    errorMessage.value = '复制失败，请手动复制。'
  }
}

function formatTime(value: string | null) {
  if (!value) {
    return '未发生'
  }

  return new Date(value).toLocaleString()
}

onMounted(loadData)
</script>

<template>
  <div class="shell">
    <header class="hero">
      <div>
        <p class="eyebrow">Proxy Relay Console</p>
        <h2>把带账号密码的 HTTP 或 SOCKS5 代理转成可直接交付的 HTTP 或 SOCKS5 转发端口。</h2>
        <p class="summary">
          适合无法二次开发、且只接受无账号密码代理的客户端。先导入远端 HTTP 或 SOCKS5 代理，再按需要选择本机对外暴露的 HTTP 或 SOCKS5 地址给业务使用。
        </p>
      </div>
      <div class="metrics">
        <article>
          <strong>{{ tunnels.length }}</strong>
          <span>已登记代理</span>
        </article>
        <article>
          <strong>{{ runningCount }}</strong>
          <span>运行中</span>
        </article>
        <article>
          <strong>{{ stoppedCount }}</strong>
          <span>已停止</span>
        </article>
        <article>
          <strong>{{ errorCount }}</strong>
          <span>异常</span>
        </article>
      </div>
    </header>

    <section class="notice-bar">
      <p v-if="statusMessage" class="notice success">{{ statusMessage }}</p>
      <p v-if="errorMessage" class="notice danger">{{ errorMessage }}</p>
      <button class="ghost" :disabled="busy" @click="loadData">{{ busy ? '刷新中...' : '刷新列表' }}</button>
    </section>

    <main class="grid">
      <section class="panel">
        <div class="panel-head">
          <h2>批量导入 proxy.txt</h2>
          <p>每行一个代理，支持 http://user:pass@host:port、socks5://user:pass@host:port；如果省略 scheme，系统会按 HTTP 上游处理。</p>
        </div>

        <textarea
          v-model="importForm.proxyText"
          rows="10"
          placeholder="http://demo:secret@1.2.3.4:8080"
        />

        <div class="form-grid two-up">
          <label>
            <span>下游出口协议</span>
            <select v-model="importForm.downstreamProtocol">
              <option value="http">HTTP</option>
              <option :disabled="!importSupportsSocks5Downstream" value="socks5">SOCKS5</option>
            </select>
            <small class="field-help" :class="{ warning: !importSupportsSocks5Downstream }">{{ importDownstreamHint }}</small>
          </label>
          <label>
            <span>批次号</span>
            <input v-model="importForm.batchId" placeholder="留空则自动生成" />
          </label>
          <label>
            <span>导入备注</span>
            <input v-model="importForm.note" placeholder="例如：海外住宅组 A" />
          </label>
          <label>
            <span>监听地址</span>
            <input v-model="importForm.listenAddress" placeholder="0.0.0.0" />
          </label>
          <label>
            <span>公网主机/IP</span>
            <input v-model="importForm.publicHost" placeholder="例如 203.0.113.18" />
          </label>
          <label>
            <span>起始端口</span>
            <input v-model="importForm.firstListenPort" type="number" min="1" max="65535" placeholder="留空则随机" />
          </label>
          <label class="checkbox">
            <input v-model="importForm.autoStart" type="checkbox" />
            <span>导入后立即启动</span>
          </label>
        </div>

        <button class="primary" :disabled="busy" @click="importBatch">导入并创建批次</button>
      </section>

      <section class="panel">
        <div class="panel-head">
          <h2>手动添加代理</h2>
          <p>适合临时业务单独加几个代理。可以指定固定对外端口，并选择对外暴露为 HTTP 或 SOCKS5。</p>
        </div>

        <div class="form-grid">
          <label>
            <span>代理</span>
            <input v-model="manualForm.proxy" placeholder="http://user:pass@host:port" />
            <small class="field-help" :class="{ warning: manualUpstreamProtocol !== 'socks5' }">{{ manualDownstreamHint }}</small>
          </label>
          <label>
            <span>下游出口协议</span>
            <select v-model="manualForm.downstreamProtocol">
              <option value="http">HTTP</option>
              <option :disabled="!manualSupportsSocks5Downstream" value="socks5">SOCKS5</option>
            </select>
          </label>
          <label>
            <span>批次号</span>
            <input v-model="manualForm.batchId" placeholder="manual" />
          </label>
          <label>
            <span>备注</span>
            <input v-model="manualForm.note" placeholder="例如：VIP 客户专用" />
          </label>
          <label>
            <span>监听地址</span>
            <input v-model="manualForm.listenAddress" placeholder="0.0.0.0" />
          </label>
          <label>
            <span>公网主机/IP</span>
            <input v-model="manualForm.publicHost" placeholder="例如 relay.example.com" />
          </label>
          <label>
            <span>固定端口</span>
            <input v-model="manualForm.listenPort" type="number" min="1" max="65535" placeholder="留空则随机" />
          </label>
          <label class="checkbox">
            <input v-model="manualForm.autoStart" type="checkbox" />
            <span>添加后立即启动</span>
          </label>
        </div>

        <button class="primary secondary" :disabled="busy" @click="addManualProxy">添加单个代理</button>
      </section>
    </main>

    <section class="panel wide">
      <div class="panel-head row-between">
        <div>
          <h2>批次概览</h2>
          <p>可以按导入批次统一停止，避免逐个操作。</p>
        </div>
      </div>

      <div v-if="batches.length" class="batch-grid">
        <article v-for="batch in batches" :key="batch.batchId" class="batch-card">
          <div>
            <p class="card-label">{{ batch.batchId }}</p>
            <strong>{{ batch.runningCount }} / {{ batch.totalCount }}</strong>
            <span>运行中 / 总数</span>
          </div>
          <button class="ghost danger" :disabled="busy" @click="stopBatch(batch.batchId)">停止整个批次</button>
        </article>
      </div>
      <p v-else class="empty">还没有导入任何批次。</p>
    </section>

    <section class="panel wide">
      <div class="panel-head row-between">
        <div>
          <h2>转发实例</h2>
          <p>运行中的 forwarded proxy 就是可以复制给客户端的无账号密码 HTTP 或 SOCKS5 代理。</p>
        </div>
      </div>

      <div v-if="tunnels.length" class="table-wrap">
        <table>
          <thead>
            <tr>
              <th>状态</th>
              <th>批次</th>
              <th>远端上游代理</th>
              <th>转发出口</th>
              <th>下游协议</th>
              <th>监听配置</th>
              <th>时间</th>
              <th>操作</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="item in tunnels" :key="item.id">
              <td>
                <span class="badge" :class="item.status.toLowerCase()">{{ item.status }}</span>
                <p v-if="item.lastError" class="inline-error">{{ item.lastError }}</p>
              </td>
              <td>
                <strong>{{ item.batchId ?? '未分组' }}</strong>
                <p>{{ item.note ?? '无备注' }}</p>
              </td>
              <td>
                <code>{{ item.remoteProxyDisplay }}</code>
              </td>
              <td>
                <div class="copy-row">
                  <code>{{ item.forwardedProxy ?? '尚未启动' }}</code>
                  <button class="ghost small" :disabled="!item.forwardedProxy" @click="copyProxy(item.forwardedProxy)">复制</button>
                </div>
              </td>
              <td>
                <strong>{{ item.downstreamProtocol.toUpperCase() }}</strong>
              </td>
              <td>
                <p>{{ item.listenAddress }}</p>
                <p>固定端口: {{ item.requestedListenPort > 0 ? item.requestedListenPort : '随机' }}</p>
                <p>公网主机: {{ item.publicHost }}</p>
              </td>
              <td>
                <p>创建: {{ formatTime(item.createdAt) }}</p>
                <p>启动: {{ formatTime(item.startedAt) }}</p>
                <p>停止: {{ formatTime(item.stoppedAt) }}</p>
              </td>
              <td>
                <div class="action-stack">
                  <button v-if="item.status !== 'Running'" class="ghost" :disabled="busy" @click="startTunnel(item)">启动</button>
                  <button v-else class="ghost danger" :disabled="busy" @click="stopTunnel(item.id)">停止</button>
                </div>
              </td>
            </tr>
          </tbody>
        </table>
      </div>
      <p v-else class="empty">当前还没有任何转发实例。</p>
    </section>
  </div>
</template>
