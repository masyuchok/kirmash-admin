import { useEffect, useRef, useState } from 'react';

type MenuPosition = { top: number; left: number };

type Options = {
  menuWidth?: number;
  estimatedMenuHeight?: number;
};

/** Fixed-position dropdown anchored to a trigger; renders outside overflow containers via portal. */
export function usePortalMenu(options: Options = {}) {
  const menuWidth = options.menuWidth ?? 256;
  const estimatedMenuHeight = options.estimatedMenuHeight ?? 280;

  const [open, setOpen] = useState(false);
  const [mounted, setMounted] = useState(false);
  const [position, setPosition] = useState<MenuPosition>({ top: 0, left: 0 });
  const triggerRef = useRef<HTMLButtonElement | null>(null);
  const menuRef = useRef<HTMLDivElement | null>(null);

  const updatePosition = () => {
    if (!triggerRef.current) return;
    const rect = triggerRef.current.getBoundingClientRect();
    const viewportPadding = 8;
    const maxLeft = window.innerWidth - menuWidth - viewportPadding;
    const left = Math.max(viewportPadding, Math.min(rect.left, maxLeft));
    let top = rect.bottom + 8;
    if (top + estimatedMenuHeight > window.innerHeight - viewportPadding) {
      top = Math.max(viewportPadding, rect.top - estimatedMenuHeight - 8);
    }
    setPosition({ top, left });
  };

  useEffect(() => {
    setMounted(true);
  }, []);

  useEffect(() => {
    if (!open) return;
    updatePosition();

    const onDocClick = (event: MouseEvent) => {
      const target = event.target as Node;
      if (
        menuRef.current?.contains(target) ||
        triggerRef.current?.contains(target)
      )
        return;
      setOpen(false);
    };
    const onViewportChange = () => updatePosition();

    document.addEventListener('mousedown', onDocClick);
    window.addEventListener('resize', onViewportChange);
    window.addEventListener('scroll', onViewportChange, true);
    return () => {
      document.removeEventListener('mousedown', onDocClick);
      window.removeEventListener('resize', onViewportChange);
      window.removeEventListener('scroll', onViewportChange, true);
    };
  }, [open, menuWidth, estimatedMenuHeight]);

  const toggle = () => {
    setOpen((prev) => {
      const next = !prev;
      if (next) updatePosition();
      return next;
    });
  };

  return {
    open,
    setOpen,
    toggle,
    mounted,
    position,
    triggerRef,
    menuRef,
    menuWidth,
  };
}
