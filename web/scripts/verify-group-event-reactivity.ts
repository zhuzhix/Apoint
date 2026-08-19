import { nextTick, reactive, watch } from 'vue'
import { ensureGroupEventState } from '../src/composables/groupEventState'
import type { PairTrendTimelineEvent } from '../src/types/market'

const store = reactive({}) as Record<string, ReturnType<typeof ensureGroupEventState>>
const symbol = 'SHSE.600000'
let observedLength = -1

watch(
  () => store[symbol]?.items.length ?? 0,
  (length) => { observedLength = length },
  { flush: 'sync' },
)

const state = ensureGroupEventState(store, symbol)
if (state !== store[symbol]) throw new Error('ensureGroupEventState did not return the reactive proxy')

state.items = [{ id: 1 }] as PairTrendTimelineEvent[]
await nextTick()

if (observedLength !== 1) {
  throw new Error(`reactive group event update was not observed: ${observedLength}`)
}

console.log('PASS: first group-event response updates the rendered reactive state')
