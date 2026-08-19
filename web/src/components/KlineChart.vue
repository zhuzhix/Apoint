<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, watch } from 'vue'
import * as echarts from 'echarts/core'
import { CandlestickChart, BarChart } from 'echarts/charts'
import { GridComponent, TooltipComponent, DataZoomComponent, AxisPointerComponent, MarkPointComponent } from 'echarts/components'
import { CanvasRenderer } from 'echarts/renderers'
import type { MarketBar, PairChartMarker } from '@/types/market'

echarts.use([CandlestickChart, BarChart, GridComponent, TooltipComponent, DataZoomComponent, AxisPointerComponent, MarkPointComponent, CanvasRenderer])
const props = defineProps<{ bars: MarketBar[]; loading?: boolean; markers?: PairChartMarker[] }>()
const element = ref<HTMLDivElement>()
let chart: echarts.ECharts | undefined
let observer: ResizeObserver | undefined

function render() {
  if (!element.value) return
  chart ??= echarts.init(element.value, undefined, { renderer: 'canvas' })
  const markerData = (props.markers ?? []).flatMap((marker) => {
    if (props.bars.length === 0) return []
    const target = new Date(marker.time).getTime()
    let index = 0
    let distance = Number.POSITIVE_INFINITY
    props.bars.forEach((bar, candidate) => {
      const delta = Math.abs(new Date(bar.eob).getTime() - target)
      if (delta < distance) { distance = delta; index = candidate }
    })
    const status = marker.status.toUpperCase()
    const color = marker.selected ? '#a798ff'
      : status === 'CONFIRMED' ? '#35c69a'
      : status === 'INVALIDATED' || status === 'RETRACTED' ? '#65758e'
      : marker.pivotType === 'TOP' ? '#f0a43a' : '#31b7d5'
    const suffix = marker.pairKind === 'ROUND_00' ? '.00' : `.${String(marker.pairCode).padStart(2, '0')}`
    return [{
      name: `${marker.pivotType === 'TOP' ? '顶部' : '底部'} ${suffix}`,
      coord: [index, marker.price],
      value: `${suffix} ${status}`,
      symbol: status === 'INVALIDATED' || status === 'RETRACTED' ? 'path://M-8,-6 L-6,-8 L0,-2 L6,-8 L8,-6 L2,0 L8,6 L6,8 L0,2 L-6,8 L-8,6 L-2,0 Z' : 'triangle',
      symbolRotate: marker.pivotType === 'TOP' ? 180 : 0,
      symbolSize: marker.selected ? 23 : 16,
      itemStyle: { color, borderColor: marker.selected ? '#e3deff' : color, borderWidth: marker.selected ? 2 : 1 },
      label: { show: true, position: marker.pivotType === 'TOP' ? 'top' : 'bottom', color, fontSize: 10, formatter: suffix },
    }]
  })
  chart.setOption({
    animation: false,
    backgroundColor: 'transparent',
    axisPointer: { link: [{ xAxisIndex: 'all' }], label: { backgroundColor: '#26344a' } },
    tooltip: { trigger: 'axis', axisPointer: { type: 'cross' }, borderColor: '#26344a', backgroundColor: '#111a2b', textStyle: { color: '#e7ecf4' } },
    grid: [{ left: 56, right: 18, top: 24, height: '64%' }, { left: 56, right: 18, top: '75%', height: '14%' }],
    xAxis: [0, 1].map((index) => ({
      type: 'category', gridIndex: index, data: props.bars.map((x) => new Date(x.bob).toLocaleString('zh-CN', { month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', hour12: false })),
      boundaryGap: false, axisLine: { lineStyle: { color: '#26344a' } }, axisLabel: { color: '#7e8ea6', show: index === 1 }, splitLine: { show: false },
    })),
    yAxis: [
      { scale: true, gridIndex: 0, axisLabel: { color: '#7e8ea6' }, splitLine: { lineStyle: { color: '#1c2940' } } },
      { scale: true, gridIndex: 1, axisLabel: { color: '#7e8ea6' }, splitLine: { show: false } },
    ],
    dataZoom: [{ type: 'inside', xAxisIndex: [0, 1], start: Math.max(0, 100 - 12000 / Math.max(props.bars.length, 1)), end: 100 }, { show: true, xAxisIndex: [0, 1], bottom: 3, height: 18, borderColor: '#26344a', fillerColor: 'rgba(124,108,242,.16)', textStyle: { color: '#7e8ea6' } }],
    series: [
      { name: 'K线', type: 'candlestick', data: props.bars.map((x) => [x.openPrice, x.closePrice, x.lowPrice, x.highPrice]), itemStyle: { color: '#f04455', color0: '#19a974', borderColor: '#f04455', borderColor0: '#19a974' }, markPoint: { silent: false, data: markerData } },
      { name: '成交量', type: 'bar', xAxisIndex: 1, yAxisIndex: 1, data: props.bars.map((x) => ({ value: x.volume, itemStyle: { color: x.closePrice >= x.openPrice ? 'rgba(240,68,85,.55)' : 'rgba(25,169,116,.55)' } })) },
    ],
  }, true)
  props.loading ? chart.showLoading({ color: '#7c6cf2', maskColor: 'rgba(11,18,32,.65)' }) : chart.hideLoading()
}

onMounted(() => { render(); observer = new ResizeObserver(() => chart?.resize()); if (element.value) observer.observe(element.value) })
watch(() => [props.bars, props.loading, props.markers], render, { deep: true })
onBeforeUnmount(() => { observer?.disconnect(); chart?.dispose() })
</script>
<template><div ref="element" class="kline-chart" /></template>
