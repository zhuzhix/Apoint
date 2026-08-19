import { createRouter, createWebHistory } from 'vue-router'

export default createRouter({
  history: createWebHistory(),
  routes: [
    { path: '/', redirect: { name: 'pair-trends-intraday' } },
    {
      path: '/pair-trends',
      redirect: (to) => ({
        name: 'pair-trends-history-data',
        query: { ...to.query, source: undefined, replayDisabled: to.query.source === 'history' ? '1' : undefined },
      }),
    },
    {
      path: '/pair-trends/intraday',
      name: 'pair-trends-intraday',
      component: () => import('@/pages/PairTrendsPage.vue'),
      meta: { title: '盘中实时', pairContext: 'intraday' },
    },
    {
      path: '/pair-trends/history-data',
      name: 'pair-trends-history-data',
      component: () => import('@/pages/PairTrendsPage.vue'),
      meta: { title: '历史数据', pairContext: 'history-data' },
    },
    {
      path: '/pair-trends/replay',
      redirect: { name: 'pair-trends-history-data', query: { replayDisabled: '1' } },
    },
    {
      path: '/pair-trends/intraday/:id',
      name: 'pair-trend-intraday-detail',
      component: () => import('@/pages/PairTrendDetailPage.vue'),
      meta: { title: '盘中对子详情', pairContext: 'intraday' },
    },
    {
      path: '/pair-trends/history-data/:id',
      name: 'pair-trend-history-data-detail',
      component: () => import('@/pages/PairTrendDetailPage.vue'),
      meta: { title: '历史对子详情', pairContext: 'history-data' },
    },
    {
      path: '/pair-trends/:source(live|history)/:id',
      redirect: (to) => to.params.source === 'live'
        ? { name: 'pair-trend-history-data-detail', params: { id: to.params.id } }
        : { name: 'pair-trends-history-data', query: { replayDisabled: '1' } },
    },
    {
      path: '/pair-trends/live',
      redirect: { name: 'pair-trends-history-data' },
    },
    {
      path: '/pair-trends/history',
      redirect: { name: 'pair-trends-history-data', query: { replayDisabled: '1' } },
    },
    {
      path: '/pair-trends/replay/:id',
      redirect: { name: 'pair-trends-history-data', query: { replayDisabled: '1' } },
    },
    { path: '/operations', name: 'operations', component: () => import('@/pages/OperationsPage.vue'), meta: { title: '运维中心' } },
    { path: '/stocks/:symbol', name: 'stock', component: () => import('@/pages/StockDetailPage.vue'), meta: { title: '个股详情' } },
    { path: '/:pathMatch(.*)*', redirect: { name: 'pair-trends-intraday' } },
  ],
})
