<script setup lang="ts">
import { computed } from 'vue'
import { theme } from 'ant-design-vue'
import { useRoute, useRouter } from 'vue-router'
import {
  StockOutlined, SafetyCertificateOutlined, ControlOutlined,
} from '@ant-design/icons-vue'

const route = useRoute()
const router = useRouter()
const darkAlgorithm = theme.darkAlgorithm
const selectedKeys = computed(() => [String(route.name ?? '').startsWith('pair-trend') ? 'pair-trends' : String(route.name ?? 'pair-trends')])
const title = computed(() => String(route.meta.title ?? 'A股监控程序'))

</script>

<template>
  <a-config-provider :theme="{ token: { colorPrimary: '#7c6cf2', borderRadius: 8 }, algorithm: darkAlgorithm }">
    <a-layout class="app-shell">
      <a-layout-sider :width="216" class="app-sider">
        <div class="brand">
          <div class="brand-mark">A</div>
          <div><strong>A股监控</strong><span>RESEARCH DESK</span></div>
        </div>
        <a-menu :selected-keys="selectedKeys" mode="inline" class="nav-menu" @click="({ key }: { key: string }) => router.push(key === 'pair-trends' ? { name: 'pair-trends-intraday' } : { name: key })">
          <a-menu-item key="pair-trends"><StockOutlined />对子顶底</a-menu-item>
          <a-menu-item key="operations"><ControlOutlined />运维中心</a-menu-item>
        </a-menu>
        <div class="sider-note">
          <span>研究监控系统</span>
          <small>不连接交易与下单</small>
        </div>
      </a-layout-sider>
      <a-layout>
        <a-layout-header class="topbar">
          <div><h1>{{ title }}</h1><p>{{ new Intl.DateTimeFormat('zh-CN', { dateStyle: 'full' }).format(new Date()) }}</p></div>
          <div class="connection-pill product-scope">
            <SafetyCertificateOutlined />
            <div><strong>研究数据系统</strong><span>不连接交易与下单</span></div>
          </div>
        </a-layout-header>
        <a-layout-content class="content"><router-view /></a-layout-content>
      </a-layout>
    </a-layout>
  </a-config-provider>
</template>
