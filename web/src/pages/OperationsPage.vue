<script setup lang="ts">
import { computed } from 'vue'
import { useQuery } from '@tanstack/vue-query'
import {
  ApiOutlined,
  CheckCircleFilled,
  ClockCircleOutlined,
  CloudServerOutlined,
  DatabaseOutlined,
  ExclamationCircleFilled,
  GlobalOutlined,
  ReloadOutlined,
} from '@ant-design/icons-vue'
import { operationsApi } from '@/api/operations'
import type { OperationsHealthStatus } from '@/types/operations'
import { formatRelativeTime, formatTime } from '@/utils/format'

const query = useQuery({
  queryKey: ['operations-status'],
  queryFn: operationsApi.status,
  refetchInterval: 5_000,
  refetchIntervalInBackground: true,
  retry: 1,
})

const status = computed(() => query.data.value)
const collector = computed(() => status.value?.collector)
const primaryCollector = computed(() => status.value?.collectors[0])
const processCapacity = computed(() => {
  const value = collector.value
  if (!value?.processesExpected) return 0
  return Math.min(100, Math.round((value.processesRunning / value.processesExpected) * 100))
})
const errorMessage = computed(() => {
  const error = query.error.value
  return error instanceof Error ? error.message : '无法取得运维状态。'
})

function statusLevel(value?: OperationsHealthStatus | string) {
  switch (value?.toLowerCase()) {
    case 'healthy':
    case 'online':
    case 'running':
    case 'ok':
      return 'healthy'
    case 'degraded':
    case 'warning':
    case 'retrying':
    case 'starting':
      return 'degraded'
    case 'unhealthy':
    case 'offline':
    case 'stopped':
    case 'failed':
      return 'unhealthy'
    default:
      return 'unknown'
  }
}

function statusLabel(value?: OperationsHealthStatus | string) {
  switch (statusLevel(value)) {
    case 'healthy': return '正常'
    case 'degraded': return '需关注'
    case 'unhealthy': return '异常'
    default: return '未知'
  }
}

function processStatusLabel(value?: string) {
  const labels: Record<string, string> = {
    healthy: '运行中', online: '运行中', running: '运行中', idle: '空闲',
    retrying: '重试中', starting: '启动中', degraded: '需关注',
    stopped: '已停止', offline: '离线', failed: '失败', unhealthy: '异常',
  }
  return value ? labels[value.toLowerCase()] ?? value : '未知'
}

function duration(seconds?: number) {
  if (seconds === undefined || seconds < 0) return '—'
  const days = Math.floor(seconds / 86_400)
  const hours = Math.floor((seconds % 86_400) / 3_600)
  const minutes = Math.floor((seconds % 3_600) / 60)
  if (days > 0) return `${days}天 ${hours}小时`
  if (hours > 0) return `${hours}小时 ${minutes}分钟`
  return `${minutes}分钟`
}

function latency(value?: number) {
  return value === undefined ? '—' : `${Math.round(value)} ms`
}
</script>

<template>
  <section class="operations-page">
    <div class="operations-toolbar">
      <div>
        <span class="eyebrow">OPERATIONS CENTER</span>
        <h2>运行状态总览</h2>
        <p>每 5 秒自动刷新，集中检查采集端、接口服务和网站。</p>
      </div>
      <div class="toolbar-actions">
        <span v-if="status" class="overall-status" :class="statusLevel(status.overallStatus)">
          <i />系统{{ statusLabel(status.overallStatus) }}
        </span>
        <span v-if="status" class="last-check"><ClockCircleOutlined />检查于 {{ formatTime(status.checkedAt) }}</span>
        <a-button :loading="query.isFetching.value" @click="query.refetch()"><ReloadOutlined />立即刷新</a-button>
      </div>
    </div>

    <a-alert
      v-if="query.isError.value"
      type="error"
      show-icon
      message="运维接口暂时不可用"
      :description="`${errorMessage} 页面会继续自动重试。`"
      class="operations-alert"
    />

    <a-skeleton v-if="query.isLoading.value" active :paragraph="{ rows: 10 }" />
    <template v-else-if="status">
      <div class="service-grid">
        <article class="service-card" :class="statusLevel(status.collector.status)">
          <div class="service-icon"><CloudServerOutlined /></div>
          <div class="service-main">
            <span>采集端</span>
            <strong>{{ statusLabel(status.collector.status) }}</strong>
            <small>{{ primaryCollector?.hostName || primaryCollector?.collectorId || '本地采集主机' }}</small>
          </div>
          <div class="service-detail">
            <span>进程</span><strong>{{ status.collector.processesRunning }} / {{ status.collector.processesExpected }}</strong>
            <small>心跳 {{ formatRelativeTime(status.collector.lastHeartbeatAt) }}</small>
          </div>
          <CheckCircleFilled v-if="statusLevel(status.collector.status) === 'healthy'" class="health-mark" />
          <ExclamationCircleFilled v-else class="health-mark" />
        </article>

        <article class="service-card" :class="statusLevel(status.api.status)">
          <div class="service-icon"><ApiOutlined /></div>
          <div class="service-main">
            <span>WebAPI</span>
            <strong>{{ statusLabel(status.api.status) }}</strong>
            <small>运行 {{ duration(status.api.uptimeSeconds) }}</small>
          </div>
          <div class="service-detail">
            <span>响应时间</span><strong>{{ latency(status.api.responseTimeMs) }}</strong>
            <small>版本 {{ status.api.version }}</small>
          </div>
          <CheckCircleFilled v-if="statusLevel(status.api.status) === 'healthy'" class="health-mark" />
          <ExclamationCircleFilled v-else class="health-mark" />
        </article>

        <article class="service-card" :class="statusLevel(status.website.status)">
          <div class="service-icon"><GlobalOutlined /></div>
          <div class="service-main">
            <span>网站</span>
            <strong>{{ statusLabel(status.website.status) }}</strong>
            <small :title="status.website.url">{{ status.website.url || '当前站点' }}</small>
          </div>
          <div class="service-detail">
            <span>响应时间</span><strong>{{ latency(status.website.responseTimeMs) }}</strong>
            <small>{{ status.website.staticAssetCount }} 个静态资源</small>
          </div>
          <CheckCircleFilled v-if="statusLevel(status.website.status) === 'healthy'" class="health-mark" />
          <ExclamationCircleFilled v-else class="health-mark" />
        </article>

        <article class="service-card" :class="statusLevel(status.database.status)">
          <div class="service-icon"><DatabaseOutlined /></div>
          <div class="service-main">
            <span>MySQL</span>
            <strong>{{ statusLabel(status.database.status) }}</strong>
            <small>业务数据与运维心跳</small>
          </div>
          <div class="service-detail">
            <span>响应时间</span><strong>{{ latency(status.database.responseTimeMs) }}</strong>
            <small>最近检查 {{ formatRelativeTime(status.checkedAt) }}</small>
          </div>
          <CheckCircleFilled v-if="statusLevel(status.database.status) === 'healthy'" class="health-mark" />
          <ExclamationCircleFilled v-else class="health-mark" />
        </article>
      </div>

      <div v-if="status.recentErrors.length" class="health-errors">
        <a-alert type="warning" show-icon message="检测到最近错误">
          <template #description>
            <div v-for="item in status.recentErrors.slice(0, 3)" :key="`${item.source}-${item.occurredAt}`">
              {{ item.source }}：{{ item.message }}（{{ formatRelativeTime(item.occurredAt) }}）
            </div>
          </template>
        </a-alert>
      </div>

      <div class="section-heading compact">
        <div><span class="eyebrow">COLLECTION QUEUE</span><h2>今日采集队列</h2><p>失败任务最多重试 3 次，超过后加入一天黑名单。</p></div>
        <div class="capacity-block">
          <span>进程可用率</span>
          <a-progress :percent="processCapacity" :show-info="false" :stroke-color="processCapacity === 100 ? '#31d092' : '#f0a43a'" />
          <strong>{{ status.collector.processesRunning }} / {{ status.collector.processesExpected }}</strong>
        </div>
      </div>

      <div class="queue-grid">
        <article><span>运行中</span><strong>{{ status.collector.activeJobs }}</strong><small>当前处理任务</small></article>
        <article><span>等待队列</span><strong>{{ status.collector.queuedJobs }}</strong><small>等待分配股票</small></article>
        <article><span>今日成功</span><strong class="positive">{{ status.collector.succeededJobs }}</strong><small>已完成股票</small></article>
        <article><span>正在重试</span><strong class="warning">{{ status.collector.retryingJobs }}</strong><small>失败后重新排队</small></article>
        <article><span>今日黑名单</span><strong class="negative">{{ status.blacklist.activeSymbols }}</strong><small>24 小时后自动解除</small></article>
        <article><span>最近采集</span><strong class="time-value">{{ formatRelativeTime(status.collection.lastCompletedAt) }}</strong><small>{{ formatTime(status.collection.lastCompletedAt) }}</small></article>
      </div>

      <div class="section-heading compact">
        <div><span class="eyebrow">PROCESS HEARTBEATS</span><h2>采集进程</h2><p>每个进程最多负责 200 只股票，其余股票进入共享队列。</p></div>
      </div>
      <div class="ops-table-wrap">
        <table class="ops-table">
          <thead>
            <tr>
              <th>进程</th><th>状态</th><th>负责</th><th>已完成</th><th>失败</th><th>当前股票</th><th>最近心跳</th><th>最近错误</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="process in status.collector.processes" :key="process.workerId">
              <td><strong>{{ process.workerId }}</strong><small v-if="process.pid">PID {{ process.pid }}</small></td>
              <td><span class="status-badge" :class="statusLevel(process.status)"><i />{{ processStatusLabel(process.status) }}</span></td>
              <td class="number-cell">{{ process.assignedSymbols }}</td>
              <td class="number-cell positive-text">{{ process.completedSymbols }}</td>
              <td class="number-cell negative-text">{{ process.failedSymbols }}</td>
              <td class="message-column"><span>{{ process.currentSymbol || '—' }}</span></td>
              <td><span>{{ formatRelativeTime(status.collector.lastHeartbeatAt) }}</span><small>{{ formatTime(status.collector.lastHeartbeatAt) }}</small></td>
              <td class="message-column"><span>{{ process.lastError || '—' }}</span></td>
            </tr>
            <tr v-if="status.collector.processes.length === 0">
              <td colspan="8" class="empty-row">尚未收到采集进程心跳</td>
            </tr>
          </tbody>
        </table>
      </div>

      <template v-if="status.blacklist.recent.length">
        <div class="section-heading compact">
          <div><span class="eyebrow">DAILY BLACKLIST</span><h2>今日黑名单明细</h2><p>到期后自动返回采集队列。</p></div>
        </div>
        <div class="blacklist-grid">
          <article v-for="item in status.blacklist.recent" :key="item.symbol">
            <div><strong>{{ item.symbol }}</strong></div>
            <div><span>失败次数</span><strong>{{ item.failureCount }}</strong></div>
            <div><span>解除时间</span><strong>{{ formatTime(item.expiresAt) }}</strong></div>
            <p>{{ item.reason || '连续采集失败' }}</p>
          </article>
        </div>
      </template>
    </template>
  </section>
</template>

<style scoped>
.operations-page { color: #cbd5e1; }
.operations-toolbar { margin-bottom: 18px; display: flex; align-items: flex-end; justify-content: space-between; gap: 24px; }
.operations-toolbar h2 { margin: 3px 0; color: #eef2f8; font-size: 22px; }
.operations-toolbar p { margin: 0; color: #72829a; font-size: 12px; }
.toolbar-actions { display: flex; align-items: center; gap: 12px; }
.overall-status { display: inline-flex; align-items: center; gap: 6px; color: #8191a8; font-size: 11px; }
.overall-status i { width: 7px; height: 7px; border-radius: 50%; background: currentColor; box-shadow: 0 0 0 4px rgba(129, 145, 168, .1); }
.overall-status.healthy { color: #31d092; }.overall-status.degraded { color: #f0a43a; }.overall-status.unhealthy { color: #f04455; }
.last-check { color: #75869e; font-size: 11px; }
.last-check :deep(svg) { margin-right: 5px; }
.operations-alert { margin-bottom: 16px; }
.service-grid { display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); gap: 14px; }
.service-card { position: relative; min-height: 144px; padding: 20px; display: grid; grid-template-columns: 42px minmax(0, 1fr) auto; align-items: center; gap: 14px; overflow: hidden; border: 1px solid #26344a; border-radius: 11px; background: #111a2b; }
.service-card::before { content: ''; position: absolute; inset: 0 auto 0 0; width: 3px; background: #65758e; }
.service-card.healthy::before { background: #31d092; box-shadow: 0 0 16px rgba(49, 208, 146, .4); }
.service-card.degraded::before { background: #f0a43a; }.service-card.unhealthy::before { background: #f04455; }
.service-icon { width: 42px; height: 42px; display: grid; place-items: center; color: #a89df8; font-size: 21px; border: 1px solid #30405a; border-radius: 9px; background: #0d1626; }
.service-main span, .service-main strong, .service-main small, .service-detail span, .service-detail strong, .service-detail small { display: block; }
.service-main span, .service-detail span { color: #71829a; font-size: 10px; }
.service-main strong { margin: 3px 0; color: #eef2f8; font-size: 22px; }
.service-main small { max-width: 180px; overflow: hidden; color: #71829a; font-size: 10px; text-overflow: ellipsis; white-space: nowrap; }
.service-detail { min-width: 112px; padding-left: 15px; border-left: 1px solid #26344a; }
.service-detail strong { margin: 4px 0; color: #dce4ef; font-size: 16px; font-variant-numeric: tabular-nums; }
.service-detail small { color: #6f8098; font-size: 9px; }
.health-mark { position: absolute; right: 12px; top: 11px; color: #65758e; font-size: 11px; }
.service-card.healthy .health-mark { color: #31d092; }.service-card.degraded .health-mark { color: #f0a43a; }.service-card.unhealthy .health-mark { color: #f04455; }
.health-errors { margin-top: 14px; }
.capacity-block { min-width: 240px; display: grid; grid-template-columns: 1fr 130px auto; align-items: center; gap: 9px; color: #73849b; font-size: 10px; }
.capacity-block strong { color: #dce4ef; font-size: 12px; font-variant-numeric: tabular-nums; }
.capacity-block :deep(.ant-progress) { line-height: 1; }
.queue-grid { display: grid; grid-template-columns: repeat(6, minmax(0, 1fr)); gap: 10px; }
.queue-grid article { min-height: 96px; padding: 14px; border: 1px solid #233149; border-radius: 9px; background: #111a2b; }
.queue-grid span, .queue-grid strong, .queue-grid small { display: block; }
.queue-grid span { color: #71829a; font-size: 10px; }.queue-grid strong { margin: 6px 0 2px; color: #e1e8f1; font-size: 24px; line-height: 1; font-variant-numeric: tabular-nums; }.queue-grid small { color: #5f7089; font-size: 9px; }
.queue-grid strong.positive { color: #31d092; }.queue-grid strong.warning { color: #f0a43a; }.queue-grid strong.negative { color: #f04455; }.queue-grid strong.time-value { font-size: 18px; }
.ops-table-wrap { overflow: hidden; border: 1px solid #233149; border-radius: 10px; background: #111a2b; }
.ops-table { width: 100%; border-collapse: collapse; table-layout: fixed; }
.ops-table th { padding: 11px 10px; color: #7f90a8; font-size: 10px; font-weight: 500; text-align: left; background: #0f1929; border-bottom: 1px solid #243248; }
.ops-table td { height: 57px; padding: 9px 10px; color: #cdd6e2; font-size: 11px; border-bottom: 1px solid #1f2c40; }
.ops-table tbody tr:last-child td { border-bottom: 0; }.ops-table tbody tr:hover td { background: #151f32; }
.ops-table th:nth-child(1) { width: 110px; }.ops-table th:nth-child(2) { width: 95px; }.ops-table th:nth-child(n+3):nth-child(-n+5) { width: 72px; }.ops-table th:nth-child(6) { width: 110px; }.ops-table th:nth-child(7) { width: 125px; }
.ops-table td strong, .ops-table td small { display: block; }.ops-table td small { margin-top: 3px; color: #687991; font-size: 9px; }
.number-cell { font-size: 13px !important; font-variant-numeric: tabular-nums; }.positive-text { color: #31d092 !important; }.warning-text { color: #f0a43a !important; }.negative-text { color: #f04455 !important; }
.status-badge { display: inline-flex; align-items: center; gap: 6px; color: #8191a8; }.status-badge i { width: 6px; height: 6px; border-radius: 50%; background: currentColor; }.status-badge.healthy { color: #31d092; }.status-badge.degraded { color: #f0a43a; }.status-badge.unhealthy { color: #f04455; }
.message-column span { display: block; overflow: hidden; color: #8191a8; font-size: 10px; text-overflow: ellipsis; white-space: nowrap; }.empty-row { height: 90px !important; color: #71829a !important; text-align: center; }
.blacklist-grid { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 10px; }
.blacklist-grid article { padding: 13px; display: grid; grid-template-columns: 1fr 90px 130px; gap: 12px; border: 1px solid #342d3c; border-radius: 9px; background: #161725; }
.blacklist-grid div > span, .blacklist-grid div > strong { display: block; }.blacklist-grid div > span { color: #76869c; font-size: 9px; }.blacklist-grid div > strong { margin-top: 3px; color: #d7dee8; font-size: 12px; }.blacklist-grid > article > div:first-child strong { font-size: 14px; }.blacklist-grid p { grid-column: 1 / -1; margin: 0; color: #9a7180; font-size: 10px; }
@media (max-width: 1420px) { .queue-grid { grid-template-columns: repeat(3, minmax(0, 1fr)); }.service-grid { grid-template-columns: repeat(2, minmax(0, 1fr)); }.blacklist-grid { grid-template-columns: repeat(2, minmax(0, 1fr)); } }
</style>
