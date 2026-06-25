<script setup lang="ts">
import { computed, nextTick, onMounted, reactive, ref } from 'vue'

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

type BatchTunnelTestResponse = {
  batchId: string
  totalCount: number
  testedCount: number
  successCount: number
  failureCount: number
  items: Array<{
    tunnelId: string
    proxyDisplay: string
    forwardedProxy: string | null
    status: string
    success: boolean
    errorMessage: string | null
    runId: string | null
    completedAt: string | null
  }>
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

type UpstreamProxyTestItem = {
  upstreamId: string
  proxyDisplay: string
  success: boolean
  exitIp: string | null
  elapsedMilliseconds: number | null
  errorMessage: string | null
  testedAt: string
}

type UpstreamPoolTestResponse = {
  runId: string
  completedAt: string
  poolId: string
  totalCount: number
  successCount: number
  failureCount: number
  items: UpstreamProxyTestItem[]
}

type UpstreamPoolRetestComparison = {
  previousRunId: string
  currentRunId: string
  recoveredUpstreamIds: string[]
  stillFailedUpstreamIds: string[]
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

type ProxyTestLogRecord = {
  timestamp: string
  level: string
  message: string
}

type ProxyTestSwitchSummary = {
  hasExitIpSwitch: boolean
  hasUpstreamSwitch: boolean
  exitIpSwitchCount: number
  upstreamSwitchCount: number
  uniqueExitIpCount: number
  uniqueUpstreamCount: number
  successfulObservationCount: number
}

type ProxyTestResult = {
  runId: string
  completedAt: string
  mode: string
  resourceId: string
  proxyDisplay: string
  forwardedProxy: string | null
  success: boolean
  successCount: number
  failureCount: number
  lastExitIp: string | null
  lastSelectedUpstreamDisplay: string | null
  switchSummary: ProxyTestSwitchSummary | null
  logs: ProxyTestLogRecord[]
}

type ViewMode = 'classic' | 'fixed'
type SelectionPolicy = 'sticky' | 'round-robin' | 'least-failures'

type FloatingNavItem = {
  id: string
  label: string
  shortLabel: string
}

const selectionPolicyOptions: Array<{
  value: SelectionPolicy
  label: string
  description: string
}> = [
  {
    value: 'sticky',
    label: '粘性会话',
    description: '在设定时间窗内尽量复用最近成功的上游。',
  },
  {
    value: 'round-robin',
    label: '轮询',
    description: '每个新连接在健康上游之间依次轮换。',
  },
  {
    value: 'least-failures',
    label: '最少失败优先',
    description: '优先选择当前失败次数更少的健康上游。',
  },
]

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

const appendUpstreamPoolForm = reactive({
  proxyText: '',
})

const deleteUpstreamPoolForm = reactive({
  proxyText: '',
})

const fixedProxyForm = reactive({
  poolId: '',
  downstreamProtocol: 'http',
  selectionPolicy: 'sticky' as SelectionPolicy,
  note: '',
  listenAddress: '0.0.0.0',
  publicHost: '',
  listenPort: '',
  stickyMinutes: '10',
  autoStart: true,
})

const fixedTestForm = reactive({
  iterationCount: '6',
  intervalSeconds: '5',
})

const tunnels = ref<TunnelRecord[]>([])
const batches = ref<BatchSummary[]>([])
const upstreamPools = ref<UpstreamPoolRecord[]>([])
const selectedPoolId = ref('')
const selectedPool = ref<UpstreamPoolDetails | null>(null)
const selectedUpstreamIds = ref<string[]>([])
const fixedProxies = ref<FixedProxyRecord[]>([])
const busy = ref(false)
const statusMessage = ref('')
const errorMessage = ref('')
const testHistory = ref<ProxyTestResult[]>([])
const upstreamPoolTestHistory = ref<UpstreamPoolTestResponse[]>([])
const selectedUpstreamPoolTestRunId = ref('')
const upstreamPoolRetestComparison = ref<UpstreamPoolRetestComparison | null>(null)
const selectedClassicTestRunId = ref('')
const selectedFixedTestRunId = ref('')
const selectedFixedHistoryResourceId = ref('')

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
const classicTestHistory = computed(() => testHistory.value.filter((item) => item.mode === 'single'))
const fixedTestHistory = computed(() => testHistory.value.filter((item) => item.mode === 'fixed'))
const selectedClassicTestResult = computed(() =>
  classicTestHistory.value.find((item) => item.runId === selectedClassicTestRunId.value) ?? null,
)
const selectedFixedTestResult = computed(() =>
  fixedTestHistory.value.find((item) => item.runId === selectedFixedTestRunId.value) ?? null,
)
const selectedUpstreamPoolTestRun = computed(() =>
  upstreamPoolTestHistory.value.find((item) => item.runId === selectedUpstreamPoolTestRunId.value) ?? null,
)
const selectedUpstreamPoolTestLookup = computed<Record<string, UpstreamProxyTestItem>>(() =>
  Object.fromEntries((selectedUpstreamPoolTestRun.value?.items ?? []).map((item) => [item.upstreamId, item])),
)
const selectedUpstreamPoolSuccessItems = computed(() =>
  (selectedUpstreamPoolTestRun.value?.items ?? []).filter((item) => item.success),
)
const selectedUpstreamPoolFailureItems = computed(() =>
  (selectedUpstreamPoolTestRun.value?.items ?? []).filter((item) => !item.success),
)
const selectedFailedPoolItems = computed(() =>
  selectedPoolItems.value.filter(
    (item) => item.status === 'Unhealthy' || item.failureCount > 0 || !!item.lastError,
  ),
)
const selectedImportedDeleteTargets = computed(() => parseProxyTargets(deleteUpstreamPoolForm.proxyText))
const selectedImportedDeleteMatchCount = computed(() => {
  if (!selectedImportedDeleteTargets.value.length) {
    return 0
  }

  const targetSet = new Set(selectedImportedDeleteTargets.value)
  return selectedPoolItems.value.filter((item) => targetSet.has(item.proxy)).length
})
const selectedUpstreamPoolRetestLookup = computed<Record<string, 'recovered' | 'still-failed'>>(() => {
  const comparison = upstreamPoolRetestComparison.value
  if (!comparison || comparison.currentRunId !== selectedUpstreamPoolTestRun.value?.runId) {
    return {}
  }

  return Object.fromEntries([
    ...comparison.recoveredUpstreamIds.map((id) => [id, 'recovered' as const]),
    ...comparison.stillFailedUpstreamIds.map((id) => [id, 'still-failed' as const]),
  ])
})

const floatingNavItems = computed<FloatingNavItem[]>(() => {
  if (activeView.value === 'classic') {
    return [
      { id: 'classic-import', label: '批量导入代理', shortLabel: '导' },
      { id: 'classic-manual', label: '手动添加代理', shortLabel: '手' },
      { id: 'classic-batches', label: '批次概览', shortLabel: '批' },
      { id: 'classic-tunnels', label: '转发实例', shortLabel: '转' },
      { id: 'classic-test-logs', label: '测试日志', shortLabel: '测' },
    ]
  }

  return [
    { id: 'fixed-import', label: '导入上游池', shortLabel: '池' },
    { id: 'fixed-create', label: '创建固定入口', shortLabel: '固' },
    { id: 'fixed-pools', label: '上游池概览', shortLabel: '览' },
    { id: 'fixed-health', label: '池内健康状态', shortLabel: '健' },
    { id: 'fixed-entries', label: '固定下游代理入口', shortLabel: '口' },
    { id: 'fixed-test-logs', label: '测试日志', shortLabel: '测' },
  ]
})

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

  if (fixedProxyForm.selectionPolicy === 'round-robin') {
    return `固定入口地址保持不变，系统会从池 ${fixedProxyForm.poolId} 中按轮询依次分配健康上游。`
  }

  if (fixedProxyForm.selectionPolicy === 'least-failures') {
    return `固定入口地址保持不变，系统会优先从池 ${fixedProxyForm.poolId} 中选择失败次数更少的健康上游。`
  }

  return `固定入口地址保持不变，系统会从池 ${fixedProxyForm.poolId} 中按粘性会话动态切换健康上游。`
})

function formatSelectionPolicy(policy: string | null | undefined): string {
  return selectionPolicyOptions.find((item) => item.value === policy)?.label ?? (policy || '未知')
}

function parseProxyTargets(proxyText: string): string[] {
  const values = new Set<string>()

  for (const rawLine of proxyText.split(/\r?\n/)) {
    const normalized = normalizeProxyTarget(rawLine)
    if (normalized) {
      values.add(normalized)
    }
  }

  return [...values]
}

function normalizeProxyTarget(rawValue: string): string | null {
  const trimmed = rawValue.trim()
  if (!trimmed || trimmed.startsWith('#')) {
    return null
  }

  const candidate = /^[a-z][a-z0-9+.-]*:\/\//i.test(trimmed) ? trimmed : `http://${trimmed}`

  try {
    const url = new URL(candidate)
    if (!url.hostname || !url.port) {
      return null
    }

    return `${url.protocol}//${url.hostname}:${url.port}`
  } catch {
    return null
  }
}

function confirmDeleteFromSelectedPool(targetLabel: string, count: number, detail?: string): boolean {
  if (!selectedPoolId.value) {
    errorMessage.value = '请先选择一个上游池。'
    return false
  }

  const summary = [`即将从上游池 ${selectedPoolId.value} 删除 ${count} 条${targetLabel}。`]
  if (detail) {
    summary.push(detail)
  }

  summary.push('删除后会立即影响后续新连接，是否继续？')
  return window.confirm(summary.join('\n'))
}

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
    selectedUpstreamIds.value = selectedUpstreamIds.value.filter((id) =>
      selectedPool.value?.items.some((item) => item.id === id),
    )
  } else {
    selectedPool.value = null
    selectedUpstreamIds.value = []
  }

  if (!fixedProxyForm.poolId && poolData.length > 0) {
    fixedProxyForm.poolId = poolData[0].poolId
  }

  if (!fixedData.some((item) => item.id === selectedFixedHistoryResourceId.value)) {
    selectedFixedHistoryResourceId.value = fixedData[0]?.id ?? ''
  }
}

async function loadUpstreamPoolTestHistory(poolId?: string | null) {
  const suffix = poolId ? `?poolId=${encodeURIComponent(poolId)}` : ''
  upstreamPoolTestHistory.value = await apiFetch<UpstreamPoolTestResponse[]>(`/api/upstream-pool-test-history${suffix}`)
  ensureSelectedUpstreamPoolTestRun()
}

async function loadTestHistory(mode?: 'single' | 'fixed', resourceId?: string | null) {
  const searchParams = new URLSearchParams()
  if (mode) {
    searchParams.set('mode', mode)
  }

  if (resourceId) {
    searchParams.set('resourceId', resourceId)
  }

  const suffix = searchParams.size > 0 ? `?${searchParams.toString()}` : ''
  testHistory.value = await apiFetch<ProxyTestResult[]>(`/api/test-history${suffix}`)
  ensureSelectedTestRuns()
}

async function refreshActiveView() {
  if (activeView.value === 'classic') {
    await Promise.all([loadClassicData(), loadTestHistory('single')])
    return
  }

  await loadFixedData()
  await Promise.all([
    loadTestHistory('fixed', selectedFixedHistoryResourceId.value || null),
    loadUpstreamPoolTestHistory(selectedPoolId.value || null),
  ])
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

async function testBatch(batchId: string) {
  await withBusy(async () => {
    const response = await apiFetch<BatchTunnelTestResponse>('/api/tunnels/test-batch', {
      method: 'POST',
      body: JSON.stringify({ batchId, runningOnly: true }),
    })

    await loadTestHistory('single')
    statusMessage.value = `批次 ${response.batchId} 测试完成，共测试 ${response.testedCount} 个，成功 ${response.successCount} 个，失败 ${response.failureCount} 个。`
    await nextTick()
    scrollToSection('classic-test-logs')
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
  selectedUpstreamIds.value = []
  await withBusy(async () => {
    selectedPool.value = await apiFetch<UpstreamPoolDetails>(`/api/upstream-pools/${poolId}`)
    await loadUpstreamPoolTestHistory(poolId)
  })
}

async function reloadSelectedPool() {
  if (!selectedPoolId.value) {
    selectedPool.value = null
    selectedUpstreamIds.value = []
    return
  }

  selectedPool.value = await apiFetch<UpstreamPoolDetails>(`/api/upstream-pools/${selectedPoolId.value}`)
  selectedUpstreamIds.value = selectedUpstreamIds.value.filter((id) =>
    selectedPool.value?.items.some((item) => item.id === id),
  )
}

async function appendToSelectedPool() {
  if (!selectedPoolId.value) {
    errorMessage.value = '请先选择一个上游池。'
    return
  }

  if (!appendUpstreamPoolForm.proxyText.trim()) {
    errorMessage.value = '请先粘贴要追加的上游代理。'
    return
  }

  await withBusy(async () => {
    const response = await apiFetch<ImportUpstreamPoolResponse>(`/api/upstream-pools/${selectedPoolId.value}/append`, {
      method: 'POST',
      body: JSON.stringify({
        proxyText: appendUpstreamPoolForm.proxyText,
        note: null,
      }),
    })

    appendUpstreamPoolForm.proxyText = ''
    await loadFixedData()
    statusMessage.value = `上游池 ${response.poolId} 已追加 ${response.importedCount} 条代理，当前共 ${response.totalCount} 条。`
  })
}

async function deleteFailedUpstreams() {
  if (!selectedPoolId.value) {
    errorMessage.value = '请先选择一个上游池。'
    return
  }

  if (!selectedFailedPoolItems.value.length) {
    errorMessage.value = '当前池里没有可删除的失败代理。'
    return
  }

  if (!confirmDeleteFromSelectedPool('失败代理', selectedFailedPoolItems.value.length)) {
    return
  }

  await withBusy(async () => {
    const response = await apiFetch<{ poolId: string; removedCount: number; remainingCount: number }>(`/api/upstream-pools/${selectedPoolId.value}/delete`, {
      method: 'POST',
      body: JSON.stringify({ removeFailed: true }),
    })

    selectedUpstreamIds.value = []
    upstreamPoolRetestComparison.value = null
    await loadFixedData()
    statusMessage.value = `已从上游池 ${response.poolId} 删除 ${response.removedCount} 条失败代理，剩余 ${response.remainingCount} 条。`
  })
}

async function deleteSelectedUpstreams() {
  if (!selectedPoolId.value) {
    errorMessage.value = '请先选择一个上游池。'
    return
  }

  if (!selectedUpstreamIds.value.length) {
    errorMessage.value = '请先勾选要删除的上游代理。'
    return
  }

  if (!confirmDeleteFromSelectedPool('勾选代理', selectedUpstreamIds.value.length)) {
    return
  }

  await withBusy(async () => {
    const response = await apiFetch<{ poolId: string; removedCount: number; remainingCount: number }>(`/api/upstream-pools/${selectedPoolId.value}/delete`, {
      method: 'POST',
      body: JSON.stringify({ upstreamIds: selectedUpstreamIds.value }),
    })

    selectedUpstreamIds.value = []
    upstreamPoolRetestComparison.value = null
    await loadFixedData()
    statusMessage.value = `已从上游池 ${response.poolId} 删除 ${response.removedCount} 条勾选代理，剩余 ${response.remainingCount} 条。`
  })
}

async function deleteImportedUpstreams() {
  if (!selectedPoolId.value) {
    errorMessage.value = '请先选择一个上游池。'
    return
  }

  if (!deleteUpstreamPoolForm.proxyText.trim()) {
    errorMessage.value = '请先粘贴要删除的代理列表。'
    return
  }

  if (!selectedImportedDeleteMatchCount.value) {
    errorMessage.value = selectedImportedDeleteTargets.value.length
      ? '当前导入列表与池内代理没有匹配项。'
      : '当前导入列表里没有可识别的代理。'
    return
  }

  if (
    !confirmDeleteFromSelectedPool(
      '导入列表匹配代理',
      selectedImportedDeleteMatchCount.value,
      `本次输入 ${selectedImportedDeleteTargets.value.length} 条，匹配到 ${selectedImportedDeleteMatchCount.value} 条。`,
    )
  ) {
    return
  }

  await withBusy(async () => {
    const response = await apiFetch<{ poolId: string; removedCount: number; remainingCount: number }>(`/api/upstream-pools/${selectedPoolId.value}/delete`, {
      method: 'POST',
      body: JSON.stringify({ proxyText: deleteUpstreamPoolForm.proxyText }),
    })

    deleteUpstreamPoolForm.proxyText = ''
    selectedUpstreamIds.value = []
    upstreamPoolRetestComparison.value = null
    await loadFixedData()
    statusMessage.value = `已从上游池 ${response.poolId} 删除 ${response.removedCount} 条导入代理，剩余 ${response.remainingCount} 条。`
  })
}

function toggleAllSelectedUpstreams(event: Event) {
  const checked = (event.target as HTMLInputElement).checked
  selectedUpstreamIds.value = checked ? selectedPoolItems.value.map((item) => item.id) : []
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
        selectionPolicy: fixedProxyForm.selectionPolicy,
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

async function testTunnel(item: TunnelRecord) {
  await withBusy(async () => {
    const result = await apiFetch<ProxyTestResult>(`/api/tunnels/${item.id}/test`, {
      method: 'POST',
      body: JSON.stringify({}),
    })

    rememberTestResult(result)
    statusMessage.value = `单个代理测试完成，成功 ${result.successCount} 次，失败 ${result.failureCount} 次。`
    await nextTick()
    scrollToSection('classic-test-logs')
  })
}

async function testFixedProxy(item: FixedProxyRecord) {
  selectedFixedHistoryResourceId.value = item.id

  await withBusy(async () => {
    const result = await apiFetch<ProxyTestResult>(`/api/fixed-proxies/${item.id}/test`, {
      method: 'POST',
      body: JSON.stringify({
        iterationCount: fixedTestForm.iterationCount ? Number(fixedTestForm.iterationCount) : null,
        intervalSeconds: fixedTestForm.intervalSeconds ? Number(fixedTestForm.intervalSeconds) : null,
      }),
    })

    rememberTestResult(result)
    statusMessage.value = `固定代理测试完成，成功 ${result.successCount} 次，失败 ${result.failureCount} 次。`
    await nextTick()
    scrollToSection('fixed-test-logs')
    await loadFixedData()
    await loadTestHistory('fixed', item.id)
  })
}

async function refreshTestHistory() {
  await withBusy(async () => {
    if (activeView.value === 'classic') {
      await loadTestHistory('single')
    } else {
      await loadTestHistory('fixed', selectedFixedHistoryResourceId.value || null)
    }

    statusMessage.value = '测试历史已刷新。'
  })
}

async function changeFixedHistoryResource() {
  selectedFixedTestRunId.value = ''

  await withBusy(async () => {
    await loadTestHistory('fixed', selectedFixedHistoryResourceId.value || null)
  })
}

async function viewFixedProxyHistory(item: FixedProxyRecord) {
  selectedFixedHistoryResourceId.value = item.id
  await nextTick()
  scrollToSection('fixed-test-logs')
  await changeFixedHistoryResource()
}

async function testSelectedPool() {
  if (!selectedPoolId.value) {
    errorMessage.value = '请先选择一个上游池。'
    return
  }

  await withBusy(async () => {
    const response = await apiFetch<UpstreamPoolTestResponse>(`/api/upstream-pools/${selectedPoolId.value}/test`, {
      method: 'POST',
      body: JSON.stringify({}),
    })

    upstreamPoolRetestComparison.value = null
    rememberUpstreamPoolTest(response)

    await reloadSelectedPool()
    statusMessage.value = `上游池 ${response.poolId} 测试完成，成功 ${response.successCount} 个，失败 ${response.failureCount} 个。`
  })
}

async function testUpstreamProxy(item: UpstreamProxyRecord) {
  await withBusy(async () => {
    const response = await apiFetch<UpstreamPoolTestResponse>(`/api/upstream-pools/${item.poolId}/test`, {
      method: 'POST',
      body: JSON.stringify({ upstreamId: item.id }),
    })

    const result = response.items[0]
    upstreamPoolRetestComparison.value = null
    rememberUpstreamPoolTest(response)
    await reloadSelectedPool()
    statusMessage.value = result?.success
      ? `上游代理测试成功，出口 IP ${result.exitIp ?? '未知'}。`
      : `上游代理测试失败：${result?.errorMessage ?? '未知错误'}`
  })
}

async function retestFailedUpstreamProxies() {
  if (!selectedPoolId.value || !selectedUpstreamPoolFailureItems.value.length) {
    errorMessage.value = '当前没有可重测的失败上游代理。'
    return
  }

  const previousRun = selectedUpstreamPoolTestRun.value
  const previousFailedIds = selectedUpstreamPoolFailureItems.value.map((item) => item.upstreamId)

  await withBusy(async () => {
    const response = await apiFetch<UpstreamPoolTestResponse>(`/api/upstream-pools/${selectedPoolId.value}/test`, {
      method: 'POST',
      body: JSON.stringify({ upstreamIds: selectedUpstreamPoolFailureItems.value.map((item) => item.upstreamId) }),
    })

    rememberUpstreamPoolTest(response)
    const stillFailedUpstreamIds = response.items.filter((item) => !item.success).map((item) => item.upstreamId)
    const recoveredUpstreamIds = previousFailedIds.filter((id) => !stillFailedUpstreamIds.includes(id))
    upstreamPoolRetestComparison.value = previousRun
      ? {
          previousRunId: previousRun.runId,
          currentRunId: response.runId,
          recoveredUpstreamIds,
          stillFailedUpstreamIds,
        }
      : null
    await reloadSelectedPool()
    statusMessage.value = `失败项重测完成，成功 ${response.successCount} 个，失败 ${response.failureCount} 个。`
  })
}

async function refreshUpstreamPoolTestHistory() {
  await withBusy(async () => {
    await loadUpstreamPoolTestHistory(selectedPoolId.value || null)
    statusMessage.value = '上游池测试历史已刷新。'
  })
}

async function deleteSelectedUpstreamPoolTestRun() {
  const selectedRun = selectedUpstreamPoolTestRun.value
  if (!selectedRun) {
    return
  }

  await withBusy(async () => {
    await apiFetch<{ runId: string; deleted: boolean }>(`/api/upstream-pool-test-history/${selectedRun.runId}`, {
      method: 'DELETE',
    })

    upstreamPoolTestHistory.value = upstreamPoolTestHistory.value.filter((item) => item.runId !== selectedRun.runId)
    upstreamPoolRetestComparison.value = null
    ensureSelectedUpstreamPoolTestRun()
    statusMessage.value = '已删除这次上游池测试记录。'
  })
}

async function clearCurrentPoolTestHistory() {
  if (!selectedPoolId.value) {
    errorMessage.value = '请先选择一个上游池。'
    return
  }

  await withBusy(async () => {
    const response = await apiFetch<{ poolId: string; removedCount: number }>('/api/upstream-pool-test-history/clear?' + new URLSearchParams({ poolId: selectedPoolId.value }).toString(), {
      method: 'POST',
    })

    upstreamPoolTestHistory.value = []
    selectedUpstreamPoolTestRunId.value = ''
    upstreamPoolRetestComparison.value = null
    statusMessage.value = `已清空上游池 ${response.poolId} 的 ${response.removedCount} 条测试历史。`
  })
}

function clearClassicTestSelection() {
  selectedClassicTestRunId.value = ''
}

function clearFixedTestSelection() {
  selectedFixedTestRunId.value = ''
}

function clearUpstreamPoolTestSelection() {
  selectedUpstreamPoolTestRunId.value = ''
  upstreamPoolRetestComparison.value = null
}

function rememberTestResult(result: ProxyTestResult) {
  testHistory.value = [result, ...testHistory.value.filter((item) => item.runId !== result.runId)]

  if (result.mode === 'single') {
    selectedClassicTestRunId.value = result.runId
    return
  }

  selectedFixedHistoryResourceId.value = result.resourceId
  selectedFixedTestRunId.value = result.runId
}

function rememberUpstreamPoolTest(result: UpstreamPoolTestResponse) {
  upstreamPoolTestHistory.value = [result, ...upstreamPoolTestHistory.value.filter((item) => item.runId !== result.runId)]
  selectedUpstreamPoolTestRunId.value = result.runId
}

function ensureSelectedTestRuns() {
  if (!classicTestHistory.value.some((item) => item.runId === selectedClassicTestRunId.value)) {
    selectedClassicTestRunId.value = classicTestHistory.value[0]?.runId ?? ''
  }

  if (!fixedTestHistory.value.some((item) => item.runId === selectedFixedTestRunId.value)) {
    selectedFixedTestRunId.value = fixedTestHistory.value[0]?.runId ?? ''
  }
}

function ensureSelectedUpstreamPoolTestRun() {
  if (!upstreamPoolTestHistory.value.some((item) => item.runId === selectedUpstreamPoolTestRunId.value)) {
    selectedUpstreamPoolTestRunId.value = upstreamPoolTestHistory.value[0]?.runId ?? ''
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

function scrollToSection(sectionId: string) {
  const element = document.getElementById(sectionId)
  if (!element) {
    return
  }

  element.scrollIntoView({ behavior: 'smooth', block: 'start' })
}

onMounted(async () => {
  await withBusy(async () => {
    await refreshActiveView()
  })
})
</script>

<template>
  <div class="shell">
    <aside class="floating-nav" aria-label="页面快捷导航">
      <div class="floating-nav__hint">快捷导航</div>
      <button
        v-for="item in floatingNavItems"
        :key="item.id"
        class="floating-nav__item"
        type="button"
        @click="scrollToSection(item.id)"
      >
        <span class="floating-nav__badge">{{ item.shortLabel }}</span>
        <span class="floating-nav__label">{{ item.label }}</span>
      </button>
    </aside>

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
      <section id="classic-import" class="panel scroll-section">
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

      <section id="classic-manual" class="panel scroll-section">
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

      <section id="classic-batches" class="panel wide scroll-section">
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
          <div class="action-stack">
            <button class="ghost" :disabled="busy || !batch.runningCount" @click="testBatch(batch.batchId)">测试整个批次</button>
            <button class="ghost danger" :disabled="busy" @click="stopBatch(batch.batchId)">停止整个批次</button>
          </div>
        </article>
      </div>
      <p v-else class="empty">还没有导入任何批次。</p>
      </section>

      <section id="classic-tunnels" class="panel wide scroll-section">
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
                  <button class="ghost" :disabled="busy || item.status !== 'Running'" @click="testTunnel(item)">测试</button>
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

      <section id="classic-test-logs" class="panel wide scroll-section">
        <div class="panel-head row-between">
          <div>
            <h2>测试日志</h2>
            <p>服务端会缓存最近几次单代理测试。刷新页面后仍可从这里回看。</p>
          </div>
          <div class="panel-tools">
            <button class="ghost" :disabled="busy" @click="refreshTestHistory">刷新历史</button>
            <button class="ghost" :disabled="!selectedClassicTestResult" @click="clearClassicTestSelection">取消选中</button>
          </div>
        </div>

        <div v-if="classicTestHistory.length" class="history-strip">
          <button
            v-for="item in classicTestHistory"
            :key="item.runId"
            class="history-card"
            :class="{ selected: item.runId === selectedClassicTestRunId }"
            @click="selectedClassicTestRunId = item.runId"
          >
            <strong>{{ formatTime(item.completedAt) }}</strong>
            <span>{{ item.success ? '成功' : '存在失败' }}</span>
            <p>{{ item.forwardedProxy ?? item.proxyDisplay }}</p>
          </button>
        </div>

        <div v-if="selectedClassicTestResult" class="test-console">
          <div class="test-console__summary">
            <p>时间: {{ formatTime(selectedClassicTestResult.completedAt) }}</p>
            <p>代理: {{ selectedClassicTestResult.forwardedProxy ?? selectedClassicTestResult.proxyDisplay }}</p>
            <p>出口 IP: {{ selectedClassicTestResult.lastExitIp ?? '未知' }}</p>
            <p>最近上游: {{ selectedClassicTestResult.lastSelectedUpstreamDisplay ?? '不适用' }}</p>
          </div>
          <div class="test-log-list">
            <article v-for="(log, index) in selectedClassicTestResult.logs" :key="`${log.timestamp}-${index}`" class="test-log-item" :class="`is-${log.level}`">
              <time>{{ formatTime(log.timestamp) }}</time>
              <strong>{{ log.level.toUpperCase() }}</strong>
              <p>{{ log.message }}</p>
            </article>
          </div>
        </div>
        <p v-else class="empty">还没有单代理测试历史。先在“转发实例”里点击某个运行中代理的“测试”。</p>
      </section>
    </template>

    <template v-else>
      <main class="grid">
        <section id="fixed-import" class="panel scroll-section">
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

        <section id="fixed-create" class="panel scroll-section">
          <div class="panel-head">
            <h2>创建固定下游代理入口</h2>
            <p>客户端始终使用这个固定地址；服务端会在上游池内按粘性会话、轮询或最少失败优先策略选择健康上游。</p>
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
              <span>上游选择策略</span>
              <select v-model="fixedProxyForm.selectionPolicy">
                <option v-for="item in selectionPolicyOptions" :key="item.value" :value="item.value">
                  {{ item.label }}
                </option>
              </select>
              <small class="field-help">
                {{ selectionPolicyOptions.find((item) => item.value === fixedProxyForm.selectionPolicy)?.description }}
              </small>
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
              <small class="field-help">仅粘性会话策略使用这个时间窗；其他策略会忽略它。</small>
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

      <section id="fixed-pools" class="panel wide scroll-section">
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

      <section id="fixed-health" class="panel wide scroll-section">
        <div class="panel-head row-between">
          <div>
            <h2>池内健康状态</h2>
            <p>当前展示 {{ selectedPoolId || '未选择' }} 的上游代理健康情况。</p>
          </div>
          <div class="panel-tools">
            <button class="ghost" :disabled="busy || !selectedPoolItems.length" @click="testSelectedPool">测试整个上游池</button>
            <button class="ghost" :disabled="busy || !selectedUpstreamPoolFailureItems.length" @click="retestFailedUpstreamProxies">仅重测失败项</button>
            <button class="ghost" :disabled="busy || !selectedPoolId" @click="refreshUpstreamPoolTestHistory">刷新历史</button>
            <button class="ghost danger" :disabled="busy || !selectedUpstreamPoolTestRun" @click="deleteSelectedUpstreamPoolTestRun">删除这次记录</button>
            <button class="ghost danger" :disabled="busy || !selectedPoolId || !upstreamPoolTestHistory.length" @click="clearCurrentPoolTestHistory">清空当前池历史</button>
            <button class="ghost" :disabled="!selectedUpstreamPoolTestRun" @click="clearUpstreamPoolTestSelection">取消选中</button>
          </div>
        </div>

        <div v-if="selectedPoolId" class="pool-manage-grid">
          <article class="pool-manage-card">
            <h3>追加一批代理</h3>
            <p>直接向当前上游池追加新代理；后续新连接会自动参与选择。</p>
            <textarea
              v-model="appendUpstreamPoolForm.proxyText"
              rows="5"
              placeholder="http://user:pass@2.2.2.2:1234"
            />
            <button class="primary" :disabled="busy" @click="appendToSelectedPool">追加到当前池</button>
          </article>

          <article class="pool-manage-card">
            <h3>删除代理</h3>
            <p>支持删除当前失败代理、表格勾选代理，或粘贴一批代理文本进行删除。</p>
            <div class="action-stack">
              <button class="ghost danger" :disabled="busy || !selectedFailedPoolItems.length" @click="deleteFailedUpstreams">删除失败代理</button>
              <button class="ghost danger" :disabled="busy || !selectedUpstreamIds.length" @click="deleteSelectedUpstreams">删除勾选代理</button>
            </div>
            <p class="field-help warning">
              当前预计删除：失败代理 {{ selectedFailedPoolItems.length }} 条，勾选代理 {{ selectedUpstreamIds.length }} 条，导入列表匹配 {{ selectedImportedDeleteMatchCount }} / {{ selectedImportedDeleteTargets.length }} 条。
            </p>
            <textarea
              v-model="deleteUpstreamPoolForm.proxyText"
              rows="5"
              placeholder="粘贴要删除的代理，每行一条"
            />
            <button class="ghost danger" :disabled="busy" @click="deleteImportedUpstreams">按导入列表删除</button>
            <p class="field-help warning">池内容更新后会立即影响后续新连接；固定入口地址不变，但候选上游会即时变化。</p>
          </article>
        </div>

        <div v-if="upstreamPoolTestHistory.length" class="history-strip">
          <button
            v-for="item in upstreamPoolTestHistory"
            :key="item.runId"
            class="history-card"
            :class="{ selected: item.runId === selectedUpstreamPoolTestRunId }"
            @click="selectedUpstreamPoolTestRunId = item.runId"
          >
            <strong>{{ formatTime(item.completedAt) }}</strong>
            <span>{{ item.successCount }} 成功 / {{ item.failureCount }} 失败</span>
            <p>{{ item.poolId }}</p>
          </button>
        </div>

        <div v-if="selectedUpstreamPoolTestRun" class="test-console">
          <div class="test-console__summary">
            <p>时间: {{ formatTime(selectedUpstreamPoolTestRun.completedAt) }}</p>
            <p>上游池: {{ selectedUpstreamPoolTestRun.poolId }}</p>
            <p>成功: {{ selectedUpstreamPoolTestRun.successCount }}</p>
            <p>失败: {{ selectedUpstreamPoolTestRun.failureCount }}</p>
          </div>
          <div class="switch-grid">
            <article class="switch-card is-active">
              <span>成功代理</span>
              <strong>{{ selectedUpstreamPoolSuccessItems.length }}</strong>
              <p>{{ selectedUpstreamPoolSuccessItems.map((item) => item.proxyDisplay).join(' / ') || '无' }}</p>
            </article>
            <article class="switch-card is-muted">
              <span>失败代理</span>
              <strong>{{ selectedUpstreamPoolFailureItems.length }}</strong>
              <p>{{ selectedUpstreamPoolFailureItems.map((item) => item.proxyDisplay).join(' / ') || '无' }}</p>
            </article>
            <article v-if="upstreamPoolRetestComparison && upstreamPoolRetestComparison.currentRunId === selectedUpstreamPoolTestRun.runId" class="switch-card is-active">
              <span>已恢复</span>
              <strong>{{ upstreamPoolRetestComparison.recoveredUpstreamIds.length }}</strong>
              <p>{{ selectedUpstreamPoolSuccessItems.filter((item) => upstreamPoolRetestComparison?.recoveredUpstreamIds.includes(item.upstreamId)).map((item) => item.proxyDisplay).join(' / ') || '无' }}</p>
            </article>
            <article v-if="upstreamPoolRetestComparison && upstreamPoolRetestComparison.currentRunId === selectedUpstreamPoolTestRun.runId" class="switch-card is-muted">
              <span>仍失败</span>
              <strong>{{ upstreamPoolRetestComparison.stillFailedUpstreamIds.length }}</strong>
              <p>{{ selectedUpstreamPoolFailureItems.filter((item) => upstreamPoolRetestComparison?.stillFailedUpstreamIds.includes(item.upstreamId)).map((item) => item.proxyDisplay).join(' / ') || '无' }}</p>
            </article>
          </div>
        </div>

        <div v-if="selectedPoolItems.length" class="table-wrap">
          <table>
            <thead>
              <tr>
                <th>
                  <input
                    type="checkbox"
                    class="row-check"
                    :checked="selectedPoolItems.length > 0 && selectedUpstreamIds.length === selectedPoolItems.length"
                    :disabled="!selectedPoolItems.length"
                    @change="toggleAllSelectedUpstreams"
                  />
                </th>
                <th>状态</th>
                <th>上游代理</th>
                <th>失败信息</th>
                <th>时间</th>
                <th>最近测试</th>
                <th>操作</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="item in selectedPoolItems" :key="item.id">
                <td>
                  <input v-model="selectedUpstreamIds" type="checkbox" class="row-check" :value="item.id" />
                </td>
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
                <td>
                  <template v-if="selectedUpstreamPoolTestLookup[item.id]">
                    <span class="badge" :class="selectedUpstreamPoolTestLookup[item.id].success ? 'healthy' : 'unhealthy'">
                      {{ selectedUpstreamPoolTestLookup[item.id].success ? '可用' : '失败' }}
                    </span>
                    <span v-if="selectedUpstreamPoolRetestLookup[item.id] === 'recovered'" class="badge healthy">已恢复</span>
                    <span v-else-if="selectedUpstreamPoolRetestLookup[item.id] === 'still-failed'" class="badge unhealthy">仍失败</span>
                    <p>时间: {{ formatTime(selectedUpstreamPoolTestLookup[item.id].testedAt) }}</p>
                    <p>出口: {{ selectedUpstreamPoolTestLookup[item.id].exitIp ?? '未获取' }}</p>
                    <p v-if="selectedUpstreamPoolTestLookup[item.id].elapsedMilliseconds !== null">耗时: {{ selectedUpstreamPoolTestLookup[item.id].elapsedMilliseconds }} ms</p>
                    <p v-if="selectedUpstreamPoolTestLookup[item.id].errorMessage" class="inline-error">{{ selectedUpstreamPoolTestLookup[item.id].errorMessage }}</p>
                  </template>
                  <p v-else class="empty">尚未测试</p>
                </td>
                <td>
                  <button class="ghost small" :disabled="busy" @click="testUpstreamProxy(item)">测试</button>
                </td>
              </tr>
            </tbody>
          </table>
        </div>
        <p v-else class="empty">请选择一个上游池查看健康状态。</p>
      </section>

      <section id="fixed-entries" class="panel wide scroll-section">
        <div class="panel-head row-between">
          <div>
            <h2>固定下游代理入口</h2>
            <p>这些地址可以直接给客户端长期使用；真实出口 IP 会在池内动态变化。</p>
          </div>
          <div class="panel-tools">
            <label class="compact-field">
              <span>轮数</span>
              <input v-model="fixedTestForm.iterationCount" type="number" min="1" max="30" />
            </label>
            <label class="compact-field">
              <span>间隔秒</span>
              <input v-model="fixedTestForm.intervalSeconds" type="number" min="0" max="120" />
            </label>
            <button class="ghost" :disabled="!runningFixedProxies.length" @click="copyRunningProxies(runningFixedProxies, '固定入口')">
              复制运行中固定入口
            </button>
          </div>
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
                  <p>策略: {{ formatSelectionPolicy(item.selectionPolicy) }}</p>
                  <p v-if="item.selectionPolicy === 'sticky'">粘性: {{ item.stickyMinutes }} 分钟</p>
                  <p v-else>粘性窗口: 不使用</p>
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
                    <button class="ghost" :disabled="busy || item.status !== 'Running'" @click="testFixedProxy(item)">测试</button>
                    <button class="ghost small" :disabled="busy" @click="viewFixedProxyHistory(item)">历史</button>
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

      <section id="fixed-test-logs" class="panel wide scroll-section">
        <div class="panel-head row-between">
          <div>
            <h2>测试日志</h2>
            <p>服务端会缓存最近几次固定入口测试，并把是否切换过出口或上游做成摘要统计。</p>
          </div>
          <div class="panel-tools">
            <label class="compact-field">
              <span>固定入口历史</span>
              <select v-model="selectedFixedHistoryResourceId" :disabled="busy || !fixedProxies.length" @change="changeFixedHistoryResource">
                <option disabled value="">请选择固定入口</option>
                <option v-for="item in fixedProxies" :key="item.id" :value="item.id">
                  {{ item.note || item.poolId }} / {{ item.forwardedProxy || `${item.publicHost}:${item.activeListenPort || item.requestedListenPort}` }}
                </option>
              </select>
            </label>
            <button class="ghost" :disabled="busy" @click="refreshTestHistory">刷新历史</button>
            <button class="ghost" :disabled="!selectedFixedTestResult" @click="clearFixedTestSelection">取消选中</button>
          </div>
        </div>

        <div v-if="fixedTestHistory.length" class="history-strip">
          <button
            v-for="item in fixedTestHistory"
            :key="item.runId"
            class="history-card"
            :class="{ selected: item.runId === selectedFixedTestRunId }"
            @click="selectedFixedTestRunId = item.runId"
          >
            <strong>{{ formatTime(item.completedAt) }}</strong>
            <span>{{ item.success ? '成功' : '存在失败' }}</span>
            <p>{{ item.forwardedProxy ?? item.proxyDisplay }}</p>
          </button>
        </div>

        <div v-if="selectedFixedTestResult" class="test-console">
          <div class="test-console__summary">
            <p>时间: {{ formatTime(selectedFixedTestResult.completedAt) }}</p>
            <p>代理: {{ selectedFixedTestResult.forwardedProxy ?? selectedFixedTestResult.proxyDisplay }}</p>
            <p>出口 IP: {{ selectedFixedTestResult.lastExitIp ?? '未知' }}</p>
            <p>最近上游: {{ selectedFixedTestResult.lastSelectedUpstreamDisplay ?? '未知' }}</p>
          </div>
          <div v-if="selectedFixedTestResult.switchSummary" class="switch-grid">
            <article class="switch-card" :class="selectedFixedTestResult.switchSummary.hasExitIpSwitch ? 'is-active' : 'is-muted'">
              <span>出口是否切换</span>
              <strong>{{ selectedFixedTestResult.switchSummary.hasExitIpSwitch ? '已发生' : '未发生' }}</strong>
            </article>
            <article class="switch-card" :class="selectedFixedTestResult.switchSummary.hasUpstreamSwitch ? 'is-active' : 'is-muted'">
              <span>上游是否切换</span>
              <strong>{{ selectedFixedTestResult.switchSummary.hasUpstreamSwitch ? '已发生' : '未发生' }}</strong>
            </article>
            <article class="switch-card">
              <span>出口切换次数</span>
              <strong>{{ selectedFixedTestResult.switchSummary.exitIpSwitchCount }}</strong>
            </article>
            <article class="switch-card">
              <span>上游切换次数</span>
              <strong>{{ selectedFixedTestResult.switchSummary.upstreamSwitchCount }}</strong>
            </article>
            <article class="switch-card">
              <span>观察到的出口数</span>
              <strong>{{ selectedFixedTestResult.switchSummary.uniqueExitIpCount }}</strong>
            </article>
            <article class="switch-card">
              <span>观察到的上游数</span>
              <strong>{{ selectedFixedTestResult.switchSummary.uniqueUpstreamCount }}</strong>
            </article>
          </div>
          <div class="test-log-list">
            <article v-for="(log, index) in selectedFixedTestResult.logs" :key="`${log.timestamp}-${index}`" class="test-log-item" :class="`is-${log.level}`">
              <time>{{ formatTime(log.timestamp) }}</time>
              <strong>{{ log.level.toUpperCase() }}</strong>
              <p>{{ log.message }}</p>
            </article>
          </div>
        </div>
        <p v-else class="empty">还没有固定代理测试历史。先在“固定下游代理入口”里点击某个运行中入口的“测试”。</p>
      </section>
    </template>
  </div>
</template>
