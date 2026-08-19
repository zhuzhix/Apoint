import { createApp } from 'vue'
import { createPinia } from 'pinia'
import { VueQueryPlugin, QueryClient } from '@tanstack/vue-query'
import {
  Alert, Button, ConfigProvider, DatePicker, Descriptions, Drawer, Empty, Input, Layout,
  Menu, Pagination, Progress, Segmented, Select, Skeleton, Switch, Table, Tabs, Tag,
} from 'ant-design-vue'
import 'ant-design-vue/dist/reset.css'
import App from './App.vue'
import router from './router'
import './styles/theme.css'

const app = createApp(App)
const queryClient = new QueryClient({
  defaultOptions: {
    queries: { retry: 2, refetchOnWindowFocus: false, staleTime: 10_000 },
  },
})

app.use(createPinia())
app.use(VueQueryPlugin, { queryClient })
app.use(router)
app.use(Alert)
app.use(Button)
app.use(ConfigProvider)
app.use(DatePicker)
app.use(Descriptions)
app.use(Drawer)
app.use(Empty)
app.use(Input)
app.use(Layout)
app.use(Menu)
app.use(Pagination)
app.use(Progress)
app.use(Segmented)
app.use(Select)
app.use(Skeleton)
app.use(Switch)
app.use(Table)
app.use(Tabs)
app.use(Tag)
app.mount('#app')
