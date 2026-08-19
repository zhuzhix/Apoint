import type { PairTrendTimelineEvent } from '@/types/market'

export interface GroupEventState {
  items: PairTrendTimelineEvent[]
  page: number
  total: number
  loading: boolean
  error: string
}

export function ensureGroupEventState(
  store: Record<string, GroupEventState>,
  symbol: string,
): GroupEventState {
  if (!store[symbol]) {
    store[symbol] = { items: [], page: 0, total: 0, loading: false, error: '' }
  }

  // reactive() 的赋值表达式会返回原始对象，不是容器 getter 生成的 Proxy。
  // 必须从 store 再读一次，否则首次接口响应会绕过 Vue 的更新通知。
  return store[symbol]
}
