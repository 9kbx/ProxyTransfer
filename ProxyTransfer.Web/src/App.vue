<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'

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

type UpstreamProxyRecord = {
  id: string
  poolId: string
  proxy: string
  proxyDisplay: string
  status: string
  failureCount: number
  createdAt: string
  lastCheckedAt: string | null
  lastSuccessAt: string | null
  lastFailureAt: string | null
  disabledUntil: string | null
  lastError: string | null
}

type UpstreamPoolRecord = {
  poolId: string
  note: string | null
  totalCount: number
  healthyCount: number
  createdAt: string
  updatedAt: string
}

type UpstreamPoolDetails = UpstreamPoolRecord & {
  items: UpstreamProxyRecord[]
}

type ImportUpstreamPoolResponse = {
  poolId: string
  importedCount: number
  totalCount: number
  items: UpstreamProxyRecord[]
}

type FixedProxyRecord = {
  id: string
  poolId: string
  note: string | null
  downstreamProtocol: string
  listenAddress: string
  publicHost: string
  requestedListenPort: number
  activeListenPort: number
  forwardedProxy: string | null
  selectionPolicy: string
  stickyMinutes: number
  totalUpstreamCount: number
  healthyUpstreamCount: number
  lastSelectedUpstream: string | null
  lastSelectedUpstreamDisplay: string | null
  status: string
  createdAt: string
  startedAt: string | null
  stoppedAt: string | null
  lastError: string | null
}

type ViewMode = 'classic' | 'fixed'

const apiBaseUrl = (import.meta.env.VITE_API_BASE_URL as string | undefined)?.replace(/\/$/, '') ?? ''

const activeView = ref<ViewMode>('classic')

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

const upstreamPoolForm = reactive({
  proxyText: '',
  poolId: '',
  note: '',
})

const fixedProxyForm = reactive({
  poolId: '',
  downstreamProtocol: 'http',
  note: '',
  listenAddress: '0.0.0.0',
  publicHost: '',
  listenPort: '',
  stickyMinutes: '10',
  autoStart: true,
})

const tunnels = ref<TunnelRecord[]>([])
const batches = ref<BatchSummary[]>([])
const upstreamPools = ref<UpstreamPoolRecord[]>([])
const selectedPoolId = ref('')
const selectedPool = ref<UpstreamPoolDetails | null>(null)
const fixedProxies = ref<FixedProxyRecord[]>([])
const busy = ref(false)
const statusMessage = ref('')
const errorMessage = ref('')

const runningCount = computed(() => tunnels.value.filter((item) => item.status === 'Running').length)
const stoppedCount = computed(() => tunnels.value.filter((item) => item.status === 'Stopped').length)
const errorCount = computed(() => tunnels.value.filter((item) => item.status === 'Error').length)
const runningForwardedProxies = computed(() =>
  tunnels.value
    .filter((item) => item.status === 'Running' && item.forwardedProxy)
    .map((item) => item.forwardedProxy as string),
)

const fixedRunningCount = computed(() => fixedProxies.value.filter((item) => item.status === 'Running').length)
const fixedErrorCount = computed(() => fixedProxies.value.filter((item) => item.status === 'Error').length)
const totalHealthyUpstreams = computed(() => upstreamPools.value.reduce((sum, item) => sum + item.healthyCount, 0))
const selectedPoolItems = computed(() => selectedPool.value?.items ?? [])
const runningFixedProxies = computed(() =>
  fixedProxies.value
    .filter((item) => item.status === 'Running' && item.forwardedProxy)
    .map((item) => item.forwardedProxy as string),
)

const manualDownstreamHint = computed(() => {
  if (!manualForm.proxy.trim()) {
    return '上游支持 HTTP 和 SOCKS5；下游可以选择 HTTP 或 SOCKS5。'
  }

  if (manualForm.downstreamProtocol === 'socks5') {
    return '当前会创建无认证 SOCKS5 下游出口；上游可以是 HTTP 或 SOCKS5。'
  }

  return '当前会创建无认证 HTTP 下游出口；上游可以是 HTTP 或 SOCKS5。'
})

const importDownstreamHint = computed(() => {
  if (!importForm.proxyText.trim()) {
    return '批量导入支持 HTTP 和 SOCKS5 上游；下游可以统一选择 HTTP 或 SOCKS5。'
  }

  if (importForm.downstreamProtocol === 'socks5') {
    return '当前批次会统一创建无认证 SOCKS5 下游出口；每一行上游可以是 HTTP 或 SOCKS5。'
  }

  return '当前批次会统一创建无认证 HTTP 下游出口；每一行上游可以是 HTTP 或 SOCKS5。'
})

const fixedProxyHint = computed(() => {
  if (!fixedProxyForm.poolId) {
    return '先导入上游池，再把固定入口绑定到某个池。'
  }

  return `固定入口地址保持不变，系统会从池 ${fixedProxyForm.poolId} 中按粘性会话动态切换健康上游。`
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

async function withBusy(work: () => Promise<void>) {
  busy.value = true
  errorMessage.value = ''

  try {
    await work()
  } catch (error) {
    errorMessage.value = (error as Error).message
  } finally {
    busy.value = false
  }
}

async function loadClassicData() {
  const [tunnelData, batchData] = await Promise.all([
    apiFetch<TunnelRecord[]>('/api/tunnels'),
    apiFetch<BatchSummary[]>('/api/batches'),
  ])

  tunnels.value = tunnelData
  batches.value = batchData
}

async function loadFixedData() {
  const [poolData, fixedData] = await Promise.all([
    apiFetch<UpstreamPoolRecord[]>('/api/upstream-pools'),
    apiFetch<FixedProxyRecord[]>('/api/fixed-proxies'),
  ])

  upstreamPools.value = poolData
  fixedProxies.value = fixedData

  if (!selectedPoolId.value && poolData.length > 0) {
    selectedPoolId.value = poolData[0].poolId
  }

  if (selectedPoolId.value) {
    selectedPool.value = await apiFetch<UpstreamPoolDetails>(`/api/upstream-pools/${selectedPoolId.value}`)
  } else {
    selectedPool.value = null
  }

  if (!fixedProxyForm.poolId && poolData.length > 0) {
    fixedProxyForm.poolId = poolData[0].poolId
  }
}

async function refreshActiveView() {
  if (activeView.value === 'classic') {
    await loadClassicData()
    return
  }

  await loadFixedData()
}

async function changeView(view: ViewMode) {
  activeView.value = view
  await withBusy(async () => {
    await refreshActiveView()
  })
}

async function importBatch() {
  if (!importForm.proxyText.trim()) {
    errorMessage.value = '请先粘贴 proxy.txt 内容。'
    return
  }

  await withBusy(async () => {
    const response = await apiFetch<ImportResponse>('/api/tunnels/import', {
      method: 'POST',
      body: JSON.stringify({
        proxyText: importForm.proxyText,
        downstreamProtocol: importForm.downstreamProtocol,
        batchId: importForm.batchId || null,
        note: importForm.note || null,
        listenAddress: importForm.listenAddress || null,
        publicHost: importForm.publicHost || null,
        firstListenPort: importForm.firstListenPort ? Number(importForm.firstListenPort) : null,
        autoStart: importForm.autoStart,
      }),
    })

    importForm.proxyText = ''
    statusMessage.value = `批次 ${response.batchId} 已导入 ${response.importedCount} 个代理。`
    await loadClassicData()
  })
}

async function addManualProxy() {
  if (!manualForm.proxy.trim()) {
    errorMessage.value = '请填写一个 HTTP 或 SOCKS5 代理。'
    return
  }

  await withBusy(async () => {
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
    await loadClassicData()
  })
}

async function stopTunnel(id: string) {
  await withBusy(async () => {
    await apiFetch<TunnelRecord>(`/api/tunnels/${id}/stop`, { method: 'POST' })
    statusMessage.value = '代理已停止。'
    await loadClassicData()
  })
}

async function startTunnel(item: TunnelRecord) {
  await withBusy(async () => {
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
    await loadClassicData()
  })
}

async function stopBatch(batchId: string) {
  await withBusy(async () => {
    const response = await apiFetch<{ batchId: string; stoppedCount: number }>('/api/tunnels/stop-batch', {
      method: 'POST',
      body: JSON.stringify({ batchId }),
    })

    statusMessage.value = `批次 ${response.batchId} 已停止 ${response.stoppedCount} 个代理。`
    await loadClassicData()
  })
}

async function importUpstreamPool() {
  if (!upstreamPoolForm.proxyText.trim()) {
    errorMessage.value = '请先粘贴一批上游代理。'
    return
  }

  await withBusy(async () => {
    const response = await apiFetch<ImportUpstreamPoolResponse>('/api/upstream-pools/import', {
      method: 'POST',
      body: JSON.stringify({
        proxyText: upstreamPoolForm.proxyText,
        poolId: upstreamPoolForm.poolId || null,
        note: upstreamPoolForm.note || null,
      }),
    })

    upstreamPoolForm.proxyText = ''
    selectedPoolId.value = response.poolId
    fixedProxyForm.poolId = response.poolId
    statusMessage.value = `上游池 ${response.poolId} 已新增 ${response.importedCount} 条代理，当前共 ${response.totalCount} 条。`
    await loadFixedData()
  })
}

async function selectPool(poolId: string) {
  selectedPoolId.value = poolId
  await withBusy(async () => {
    selectedPool.value = await apiFetch<UpstreamPoolDetails>(`/api/upstream-pools/${poolId}`)
  })
}

async function addFixedProxy() {
  if (!fixedProxyForm.poolId) {
    errorMessage.value = '请先选择一个上游池。'
    return
  }

  await withBusy(async () => {
    await apiFetch<FixedProxyRecord>('/api/fixed-proxies', {
      method: 'POST',
      body: JSON.stringify({
        poolId: fixedProxyForm.poolId,
        downstreamProtocol: fixedProxyForm.downstreamProtocol,
        note: fixedProxyForm.note || null,
        listenAddress: fixedProxyForm.listenAddress || null,
        publicHost: fixedProxyForm.publicHost || null,
        listenPort: fixedProxyForm.listenPort ? Number(fixedProxyForm.listenPort) : null,
        stickyMinutes: fixedProxyForm.stickyMinutes ? Number(fixedProxyForm.stickyMinutes) : null,
        autoStart: fixedProxyForm.autoStart,
      }),
    })

    fixedProxyForm.note = ''
    fixedProxyForm.listenPort = ''
    statusMessage.value = '固定下游代理入口已创建。'
    await loadFixedData()
  })
}

async function startFixedProxy(id: string) {
  await withBusy(async () => {
    await apiFetch<FixedProxyRecord>(`/api/fixed-proxies/${id}/start`, { method: 'POST' })
    statusMessage.value = '固定入口已启动。'
    await loadFixedData()
  })
}

async function stopFixedProxy(id: string) {
  await withBusy(async () => {
    await apiFetch<FixedProxyRecord>(`/api/fixed-proxies/${id}/stop`, { method: 'POST' })
    statusMessage.value = '固定入口已停止。'
    await loadFixedData()
  })
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

async function copyRunningProxies(values: string[], label: string) {
  if (!values.length) {
    return
  }

  try {
    await navigator.clipboard.writeText(values.join('\n'))
    statusMessage.value = `已复制 ${values.length} 个${label}。`
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

onMounted(async () => {
  await withBusy(async () => {
    await loadClassicData()
  })
})
</script>

<template>
  <div class="shell">
    <header class="hero">
      <div>
        <p class="eyebrow">Proxy Relay Console</p>
        <h2>把带账号密码的 HTTP 或 SOCKS5 代理转成可直接交付的下游代理入口，并支持固定入口动态切换上游。</h2>
        <p class="summary">
          单代理转发适合手工交付端口；固定入口池模式适合给客户一个长期不变的代理地址，由服务端自动在健康上游之间做粘性切换。
        </p>
      </div>
      <div class="metrics">
        <article>
          <strong>{{ activeView === 'classic' ? tunnels.length : upstreamPools.length }}</strong>
          <span>{{ activeView === 'classic' ? '已登记代理' : '上游池数量' }}</span>
        </article>
        <article>
          <strong>{{ activeView === 'classic' ? runningCount : fixedRunningCount }}</strong>
          <span>{{ activeView === 'classic' ? '运行中' : '固定入口运行中' }}</span>
        </article>
        <article>
          <strong>{{ activeView === 'classic' ? stoppedCount : totalHealthyUpstreams }}</strong>
          <span>{{ activeView === 'classic' ? '已停止' : '健康上游总数' }}</span>
        </article>
        <article>
          <strong>{{ activeView === 'classic' ? errorCount : fixedErrorCount }}</strong>
          <span>{{ activeView === 'classic' ? '异常' : '固定入口异常' }}</span>
        </article>
      </div>
    </header>

    <section class="view-switcher">
      <button
        class="ghost"
        :class="{ active: activeView === 'classic' }"
        :disabled="busy && activeView === 'classic'"
        @click="changeView('classic')"
      >
        单代理转发
      </button>
      <button
        class="ghost"
        :class="{ active: activeView === 'fixed' }"
        :disabled="busy && activeView === 'fixed'"
        @click="changeView('fixed')"
      >
        固定入口池模式
      </button>
    </section>

    <section class="notice-bar">
      <p v-if="statusMessage" class="notice success">{{ statusMessage }}</p>
      <p v-if="errorMessage" class="notice danger">{{ errorMessage }}</p>
      <button class="ghost" :disabled="busy" @click="withBusy(refreshActiveView)">{{ busy ? '刷新中...' : '刷新列表' }}</button>
    </section>

    <template v-if="activeView === 'classic'">
      <main class="grid">
      <section class="panel">
        <div class="panel-head">
          <h2>批量导入 proxy.txt</h2>
          <p>每行一个代理，支持 http://user:pass@host:port、socks5://user:pass@host:port；如果省略 scheme，系统会按 HTTP 上游处理，但下游仍可选 HTTP 或 SOCKS5。</p>
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
              <option value="socks5">SOCKS5</option>
            </select>
            <small class="field-help">{{ importDownstreamHint }}</small>
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
            <small class="field-help">{{ manualDownstreamHint }}</small>
          </label>
          <label>
            <span>下游出口协议</span>
            <select v-model="manualForm.downstreamProtocol">
              <option value="http">HTTP</option>
              <option value="socks5">SOCKS5</option>
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
        <button class="ghost" :disabled="!runningForwardedProxies.length" @click="copyRunningProxies(runningForwardedProxies, '运行中代理')">
          复制运行中代理
        </button>
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
    </template>

    <template v-else>
      <main class="grid">
        <section class="panel">
          <div class="panel-head">
            <h2>导入上游池</h2>
            <p>每行一个上游代理，导入到同一个池中。固定入口创建后会从池里按粘性策略选择健康上游。</p>
          </div>

          <textarea
            v-model="upstreamPoolForm.proxyText"
            rows="10"
            placeholder="socks5://user:pass@2.2.2.2:4321"
          />

          <div class="form-grid">
            <label>
              <span>上游池 ID</span>
              <input v-model="upstreamPoolForm.poolId" placeholder="留空则自动生成" />
            </label>
            <label>
              <span>池备注</span>
              <input v-model="upstreamPoolForm.note" placeholder="例如：住宅代理池 A" />
            </label>
          </div>

          <button class="primary" :disabled="busy" @click="importUpstreamPool">导入上游池</button>
        </section>

        <section class="panel">
          <div class="panel-head">
            <h2>创建固定下游代理入口</h2>
            <p>客户端始终使用这个固定地址；服务端会在上游池内按粘性会话复用健康代理，故障时自动切换。</p>
          </div>

          <div class="form-grid">
            <label>
              <span>上游池</span>
              <select v-model="fixedProxyForm.poolId">
                <option disabled value="">请选择一个上游池</option>
                <option v-for="pool in upstreamPools" :key="pool.poolId" :value="pool.poolId">
                  {{ pool.poolId }} ({{ pool.healthyCount }}/{{ pool.totalCount }})
                </option>
              </select>
              <small class="field-help">{{ fixedProxyHint }}</small>
            </label>
            <label>
              <span>下游协议</span>
              <select v-model="fixedProxyForm.downstreamProtocol">
                <option value="http">HTTP</option>
                <option value="socks5">SOCKS5</option>
              </select>
            </label>
            <label>
              <span>监听地址</span>
              <input v-model="fixedProxyForm.listenAddress" placeholder="0.0.0.0" />
            </label>
            <label>
              <span>公网主机/IP</span>
              <input v-model="fixedProxyForm.publicHost" placeholder="例如 1.1.1.1" />
            </label>
            <label>
              <span>固定端口</span>
              <input v-model="fixedProxyForm.listenPort" type="number" min="1" max="65535" placeholder="例如 1234" />
            </label>
            <label>
              <span>粘性分钟数</span>
              <input v-model="fixedProxyForm.stickyMinutes" type="number" min="1" max="1440" />
            </label>
            <label>
              <span>备注</span>
              <input v-model="fixedProxyForm.note" placeholder="例如：客户 A 固定入口" />
            </label>
            <label class="checkbox">
              <input v-model="fixedProxyForm.autoStart" type="checkbox" />
              <span>创建后立即启动</span>
            </label>
          </div>

          <button class="primary secondary" :disabled="busy" @click="addFixedProxy">创建固定入口</button>
        </section>
      </main>

      <section class="panel wide">
        <div class="panel-head row-between">
          <div>
            <h2>上游池概览</h2>
            <p>点击卡片查看池内健康状态、失败次数和最近探活结果。</p>
          </div>
        </div>

        <div v-if="upstreamPools.length" class="batch-grid">
          <article
            v-for="pool in upstreamPools"
            :key="pool.poolId"
            class="batch-card clickable"
            :class="{ selected: selectedPoolId === pool.poolId }"
            @click="selectPool(pool.poolId)"
          >
            <div>
              <p class="card-label">{{ pool.poolId }}</p>
              <strong>{{ pool.healthyCount }} / {{ pool.totalCount }}</strong>
              <span>健康上游 / 总数</span>
              <p>{{ pool.note ?? '无备注' }}</p>
            </div>
          </article>
        </div>
        <p v-else class="empty">当前还没有任何上游池。</p>
      </section>

      <section class="panel wide">
        <div class="panel-head row-between">
          <div>
            <h2>池内健康状态</h2>
            <p>当前展示 {{ selectedPoolId || '未选择' }} 的上游代理健康情况。</p>
          </div>
        </div>

        <div v-if="selectedPoolItems.length" class="table-wrap">
          <table>
            <thead>
              <tr>
                <th>状态</th>
                <th>上游代理</th>
                <th>失败信息</th>
                <th>时间</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="item in selectedPoolItems" :key="item.id">
                <td>
                  <span class="badge" :class="item.status.toLowerCase()">{{ item.status }}</span>
                </td>
                <td>
                  <code>{{ item.proxyDisplay }}</code>
                </td>
                <td>
                  <p>失败次数: {{ item.failureCount }}</p>
                  <p>恢复时间: {{ formatTime(item.disabledUntil) }}</p>
                  <p v-if="item.lastError" class="inline-error">{{ item.lastError }}</p>
                </td>
                <td>
                  <p>探测: {{ formatTime(item.lastCheckedAt) }}</p>
                  <p>成功: {{ formatTime(item.lastSuccessAt) }}</p>
                  <p>失败: {{ formatTime(item.lastFailureAt) }}</p>
                </td>
              </tr>
            </tbody>
          </table>
        </div>
        <p v-else class="empty">请选择一个上游池查看健康状态。</p>
      </section>

      <section class="panel wide">
        <div class="panel-head row-between">
          <div>
            <h2>固定下游代理入口</h2>
            <p>这些地址可以直接给客户端长期使用；真实出口 IP 会在池内动态变化。</p>
          </div>
          <button class="ghost" :disabled="!runningFixedProxies.length" @click="copyRunningProxies(runningFixedProxies, '固定入口')">
            复制运行中固定入口
          </button>
        </div>

        <div v-if="fixedProxies.length" class="table-wrap">
          <table>
            <thead>
              <tr>
                <th>状态</th>
                <th>绑定池</th>
                <th>固定入口</th>
                <th>粘性与上游</th>
                <th>监听配置</th>
                <th>时间</th>
                <th>操作</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="item in fixedProxies" :key="item.id">
                <td>
                  <span class="badge" :class="item.status.toLowerCase()">{{ item.status }}</span>
                  <p v-if="item.lastError" class="inline-error">{{ item.lastError }}</p>
                </td>
                <td>
                  <strong>{{ item.poolId }}</strong>
                  <p>{{ item.note ?? '无备注' }}</p>
                  <p>{{ item.healthyUpstreamCount }} / {{ item.totalUpstreamCount }} 健康</p>
                </td>
                <td>
                  <div class="copy-row">
                    <code>{{ item.forwardedProxy ?? '尚未启动' }}</code>
                    <button class="ghost small" :disabled="!item.forwardedProxy" @click="copyProxy(item.forwardedProxy)">复制</button>
                  </div>
                  <p>下游协议: {{ item.downstreamProtocol.toUpperCase() }}</p>
                </td>
                <td>
                  <p>策略: {{ item.selectionPolicy }}</p>
                  <p>粘性: {{ item.stickyMinutes }} 分钟</p>
                  <p>最近上游: {{ item.lastSelectedUpstreamDisplay ?? '尚未选择' }}</p>
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
                    <button v-if="item.status !== 'Running'" class="ghost" :disabled="busy" @click="startFixedProxy(item.id)">启动</button>
                    <button v-else class="ghost danger" :disabled="busy" @click="stopFixedProxy(item.id)">停止</button>
                  </div>
                </td>
              </tr>
            </tbody>
          </table>
        </div>
        <p v-else class="empty">当前还没有任何固定下游代理入口。</p>
      </section>
    </template>
  </div>
</template>
