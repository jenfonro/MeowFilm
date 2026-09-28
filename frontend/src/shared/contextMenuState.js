export const buildContextMenuClosedState = (extra = {}) => ({
  open: false,
  x: 0,
  y: 0,
  busy: false,
  item: null,
  ...(extra && typeof extra === 'object' ? extra : {}),
});

export const buildContextMenuOpenState = ({
  event,
  item = null,
  extra = {},
} = {}) => ({
  open: true,
  x: event && typeof event.clientX === 'number' ? event.clientX : 0,
  y: event && typeof event.clientY === 'number' ? event.clientY : 0,
  busy: false,
  item,
  ...(extra && typeof extra === 'object' ? extra : {}),
});

export const buildContextMenuStyle = (menu) => ({
  left: `${Math.max(8, Number(menu && menu.x) || 0)}px`,
  top: `${Math.max(8, Number(menu && menu.y) || 0)}px`,
});
