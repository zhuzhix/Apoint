import { api, queryString } from './client'
import type {
  MarketBar, NotificationChangePage, NotificationTask, PageResponse, PairTrendCapabilities,
  PairTrendDetail, PairTrendIntradayStatus, PairTrendStockGroupPage,
  PairTrendTimelineEventPage, PairTrendViewContext,
} from '@/types/market'

export interface NotificationFilters {
  page?: number
  pageSize?: number
  taskType?: string
  symbol?: string
  businessStatus?: string
  userStatus?: string
  isRead?: boolean
  isStarred?: boolean
}

export interface PairTrendGroupedFilters {
  page?: number
  pageSize?: number
  keyword?: string
  pivotType?: string
  stageAtEnd?: string
  frequency?: string
  activeAtEnd?: boolean
  dateFrom?: string
  dateTo?: string
}

export const marketApi = {
  pairCapabilities: () =>
    api.get<PairTrendCapabilities>('/api/pair-trends/capabilities'),
  pairIntradayStatus: () =>
    api.get<PairTrendIntradayStatus>('/api/pair-trends/intraday/status'),
  pairIntradayGroups: (filters: PairTrendGroupedFilters = {}) =>
    api.get<PairTrendStockGroupPage>(`/api/pair-trends/intraday/stock-groups${queryString(filters)}`),
  pairIntradayGroupEvents: (symbol: string, filters: PairTrendGroupedFilters = {}) =>
    api.get<PairTrendTimelineEventPage>(`/api/pair-trends/intraday/stock-groups/${encodeURIComponent(symbol)}/events${queryString(filters)}`),
  pairHistoricalDataGroups: (filters: PairTrendGroupedFilters = {}) =>
    api.get<PairTrendStockGroupPage>(`/api/pair-trends/data/stock-groups${queryString(filters)}`),
  pairHistoricalDataGroupEvents: (symbol: string, filters: PairTrendGroupedFilters = {}) =>
    api.get<PairTrendTimelineEventPage>(`/api/pair-trends/data/stock-groups/${encodeURIComponent(symbol)}/events${queryString(filters)}`),
  pairHistoricalDataEvents: (filters: PairTrendGroupedFilters = {}) =>
    api.get<PairTrendTimelineEventPage>(`/api/pair-trends/data/events${queryString(filters)}`),
  notifications: (filters: NotificationFilters = {}) =>
    api.get<PageResponse<NotificationTask>>(`/api/notifications${queryString(filters)}`),
  notificationChanges: (afterId: number, limit = 200) =>
    api.get<NotificationChangePage>(`/api/notifications/changes${queryString({ afterId, limit })}`),
  updateNotification: (id: number, state: { isRead?: boolean; isStarred?: boolean; userStatus?: string }) =>
    api.patch<NotificationTask>(`/api/notifications/${id}/state`, state),
  readAll: (taskType?: string) =>
    api.post<{ affected: number }>(`/api/notifications/read-all${queryString({ taskType })}`),
  pairDetailForContext: (context: PairTrendViewContext, id: number, hitPage = 1, hitPageSize = 200) =>
    api.get<PairTrendDetail>(context === 'intraday'
      ? `/api/pair-trends/intraday/events/${id}${queryString({ hitPage, hitPageSize })}`
      : `/api/pair-trends/live/events/${id}${queryString({ hitPage, hitPageSize })}`),
  bars: (symbol: string, frequency: string, days = 30) => {
    const to = new Date()
    const from = new Date(to.getTime() - days * 86_400_000)
    return api.get<MarketBar[]>(`/api/market/bars${queryString({
      symbol, frequency, from: from.toISOString(), to: to.toISOString(), limit: 3000,
    })}`)
  },
  barsRange: (symbol: string, frequency: string, from: string, to: string) =>
    api.get<MarketBar[]>(`/api/market/bars${queryString({ symbol, frequency, from, to, limit: 3000 })}`),
}
