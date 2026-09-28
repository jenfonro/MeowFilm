<template>
  <div
    :class="cardClass"
    role="link"
    tabindex="0"
    @click.capture="onActivate"
    @contextmenu.prevent="onContextMenu"
    @keydown.enter.prevent="onActivate"
    @keydown.space.prevent="onActivate"
  >
    <div
      class="media-card__poster"
      :class="{ 'media-card__poster--placeholder': !resolvedPoster }"
    >
      <div class="media-card__hoverGradient"></div>
      <div class="media-card__hoverPlay">
        <div class="media-card__hoverPlayIcon" aria-hidden="true">
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="0.8" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="10"></circle><polygon points="10 8 16 12 10 16 10 8"></polygon></svg>
        </div>
      </div>
      <a
        v-if="showLinkBadge && item.detailUrl"
        class="media-card__linkBadge"
        :href="item.detailUrl"
        target="_blank"
        rel="noopener noreferrer"
        :aria-label="linkAriaLabel"
        @click.stop
      >
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
          <path d="M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71"></path>
          <path d="M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71"></path>
        </svg>
      </a>
      <img
        v-if="resolvedPoster"
        :src="resolvedPoster"
        :alt="item.title || titleFallback"
        loading="lazy"
      >
      <span v-if="showScoreBadge && item.scoreBadge" class="media-card__scoreBadge">{{ item.scoreBadge }}</span>
      <span v-else-if="showTextBadge && item.textBadge" class="media-card__badge">{{ item.textBadge }}</span>
    </div>
    <div class="media-card__title">{{ item.title }}</div>
    <div v-if="item.siteLabel" class="media-card__site">
      <div class="media-card__siteLabel">{{ item.siteLabel }}</div>
    </div>
  </div>
</template>

<script setup>
import { computed } from 'vue';

const props = defineProps({
  item: { type: Object, default: () => ({}) },
  posterSrc: { type: String, default: '' },
  cardClass: { type: String, default: 'media-card' },
  titleFallback: { type: String, default: '' },
  showLinkBadge: { type: Boolean, default: true },
  linkAriaLabel: { type: String, default: '打开详情页' },
  showScoreBadge: { type: Boolean, default: true },
  showTextBadge: { type: Boolean, default: true },
});

const emit = defineEmits(['activate', 'contextmenu']);

const onActivate = (event) => {
  emit('activate', event);
};

const onContextMenu = (event) => {
  emit('contextmenu', event);
};

const resolvedPoster = computed(() => {
  const direct = typeof props.posterSrc === 'string' ? props.posterSrc : '';
  if (direct) return direct;
  return props.item && typeof props.item.poster === 'string' ? props.item.poster : '';
});
</script>
