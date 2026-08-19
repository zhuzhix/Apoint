<script setup lang="ts">
import { computed } from 'vue'
import { useMutation, useQueryClient } from '@tanstack/vue-query'
import { EyeOutlined, StarFilled, StarOutlined, CheckOutlined, ClockCircleOutlined } from '@ant-design/icons-vue'
import { marketApi } from '@/api/market'
import type { NotificationTask, PairPayload, StrategyPayload } from '@/types/market'
import { parsePayload } from '@/types/market'
import { formatRelativeTime, formatTime, label, price, score } from '@/utils/format'

const props = defineProps<{ task: NotificationTask }>()
const emit = defineEmits<{ open: [task: NotificationTask] }>()
const queryClient = useQueryClient()
const isStrategy = computed(() => props.task.taskType === 'strategy_opportunity')
const strategy = computed(() => isStrategy.value ? parsePayload<StrategyPayload>(props.task) : undefined)
const pair = computed(() => !isStrategy.value ? parsePayload<PairPayload>(props.task) : undefined)
const mutation = useMutation({
  mutationFn: (state: { isRead?: boolean; isStarred?: boolean; userStatus?: string }) => marketApi.updateNotification(props.task.id, state),
  onSuccess: () => queryClient.invalidateQueries({ queryKey: ['notifications'] }),
})

function open() {
  emit('open', props.task)
  if (!props.task.isRead) mutation.mutate({ isRead: true })
}
</script>

<template>
  <article class="task-card" :class="[`severity-${task.severity}`, { unread: !task.isRead }]" @click="open">
    <div class="task-accent" />
    <header>
      <div class="task-symbol"><strong>{{ task.symbolName || task.symbol }}</strong><span>{{ task.symbol }}</span></div>
      <div class="task-actions" @click.stop>
        <a-button type="text" size="small" aria-label="收藏" @click="mutation.mutate({ isStarred: !task.isStarred })">
          <StarFilled v-if="task.isStarred" class="starred" /><StarOutlined v-else />
        </a-button>
        <a-button type="text" size="small" aria-label="处理" @click="mutation.mutate({ userStatus: 'handled', isRead: true })"><CheckOutlined /></a-button>
      </div>
    </header>

    <template v-if="isStrategy">
      <div class="tag-line">
        <a-tag :color="task.severity === 'focus' ? 'purple' : task.severity === 'candidate' ? 'blue' : 'default'">{{ label(task.severity) }}</a-tag>
        <a-tag>{{ strategy?.primaryStrategyName || strategy?.primaryStrategyCode }}</a-tag>
        <a-tag>{{ label(strategy?.eventType) }}</a-tag>
      </div>
      <div class="metric-row">
        <div><span>最高分</span><strong>{{ score(strategy?.highestScore) }}</strong></div>
        <div><span>命中策略</span><strong>{{ strategy?.strategyCount ?? '—' }}</strong></div>
        <div><span>命中价</span><strong>{{ price(strategy?.hitPrice) }}</strong></div>
      </div>
    </template>
    <template v-else>
      <div class="tag-line">
        <a-tag :class="pair?.pivotType === 'TOP' ? 'pair-top' : 'pair-bottom'">{{ pair?.pivotType === 'TOP' ? '对子顶部' : '对子底部' }}</a-tag>
        <a-tag>{{ pair?.latestPairKind === 'ROUND_00' ? '.00' : `.${String(pair?.latestPairCode ?? '').padStart(2, '0')}` }}</a-tag>
        <a-tag :color="task.severity === 'level1' ? 'red' : task.severity === 'critical' ? 'purple' : task.severity === 'observe' ? 'orange' : 'default'">{{ label(pair?.stage || task.businessStatus) }}</a-tag>
      </div>
      <div class="metric-row">
        <div><span>对子价</span><strong>{{ price(pair?.latestPairPrice) }}</strong></div>
        <div><span>当前阶段</span><strong>{{ label(pair?.stage) }}</strong></div>
        <div><span>事件代次</span><strong>{{ pair?.generation ?? 1 }}</strong></div>
      </div>
      <div class="frequency-line"><span v-for="frequency in pair?.frequencies" :key="frequency">{{ frequency }}</span></div>
    </template>

    <footer>
      <span><ClockCircleOutlined /> {{ formatRelativeTime(task.lastSeenAt) }}</span>
      <span :title="formatTime(task.firstSeenAt)">首次 {{ formatTime(task.firstSeenAt) }}</span>
      <a-button type="link" size="small"><EyeOutlined />详情</a-button>
    </footer>
  </article>
</template>
