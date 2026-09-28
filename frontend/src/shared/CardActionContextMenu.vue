<template>
  <div
    v-if="menu && menu.open"
    class="search-matchblock-menu"
    :style="menuStyle"
    @click.stop
  >
    <button
      v-if="showRecognize"
      type="button"
      class="search-matchblock-menu__action"
      :disabled="recognizeDisabled"
      @click="emit('recognize')"
    >识别</button>
    <button
      v-if="showMatchBlock"
      type="button"
      class="search-matchblock-menu__action"
      :class="{ 'search-matchblock-menu__action--danger': matchBlockDanger }"
      :disabled="matchBlockDisabled"
      @click="emit('toggle-match-block')"
    >{{ matchBlockLabel }}</button>
    <button
      v-if="showDelete"
      type="button"
      class="search-matchblock-menu__action"
      :class="{ 'search-matchblock-menu__action--danger': deleteDanger }"
      :disabled="deleteDisabled"
      @click="emit('delete')"
    >
      <svg
        xmlns="http://www.w3.org/2000/svg"
        width="18"
        height="18"
        viewBox="0 0 24 24"
        fill="none"
        stroke="currentColor"
        stroke-width="2"
        stroke-linecap="round"
        stroke-linejoin="round"
        aria-hidden="true"
      >
        <path d="M3 6h18"></path>
        <path d="M8 6V4h8v2"></path>
        <path d="M19 6l-1 14H6L5 6"></path>
        <path d="M10 11v6"></path>
        <path d="M14 11v6"></path>
      </svg>
      <span>{{ deleteLabel }}</span>
    </button>
  </div>
</template>

<script setup>
import { computed } from 'vue';
import { buildContextMenuStyle } from './contextMenuState';

const props = defineProps({
  menu: { type: Object, default: () => ({}) },
  showRecognize: { type: Boolean, default: true },
  showMatchBlock: { type: Boolean, default: true },
  showDelete: { type: Boolean, default: false },
  matchBlockLabel: { type: String, default: '加入匹配禁用' },
  matchBlockDanger: { type: Boolean, default: true },
  matchBlockDisabled: { type: Boolean, default: false },
  recognizeDisabled: { type: Boolean, default: false },
  deleteLabel: { type: String, default: '删除' },
  deleteDanger: { type: Boolean, default: true },
  deleteDisabled: { type: Boolean, default: false },
});

const emit = defineEmits(['recognize', 'toggle-match-block', 'delete']);

const menuStyle = computed(() => buildContextMenuStyle(props.menu || {}));
</script>
