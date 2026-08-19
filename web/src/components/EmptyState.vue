<script setup lang="ts">
defineProps<{ title?: string; description?: string; actionText?: string }>()
defineEmits<{ action: [] }>()
</script>

<template>
  <div class="empty-state">
    <div class="empty-radar" />
    <strong>{{ title || '暂无数据' }}</strong>
    <span>{{ description || '新的业务事件会自动出现在这里' }}</span>
    <a-button v-if="actionText" class="empty-action" @click="$emit('action')">{{ actionText }}</a-button>
  </div>
</template>
