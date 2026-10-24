import { useEffect, useId, useRef, useState, type CSSProperties, type ReactNode } from 'react';
import { createPortal } from 'react-dom';
import { AlertTriangle, X } from 'lucide-react';
import { Button } from './ui';

const FOCUSABLE =
  'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])';

function useFocusTrap(open: boolean, onClose: () => void) {
  const ref = useRef<HTMLDivElement>(null);
  const closeRef = useRef(onClose);
  closeRef.current = onClose;

  useEffect(() => {
    if (!open) return;
    const previouslyFocused = document.activeElement as HTMLElement | null;
    const node = ref.current;
    const first = node?.querySelector<HTMLElement>('[data-autofocus]') ?? node?.querySelector<HTMLElement>(FOCUSABLE);
    first?.focus();

    function onKey(e: KeyboardEvent) {
      if (!node) return;
      if (e.key === 'Escape') {
        // Only the top-most dialog handles Escape.
        const dialogs = document.querySelectorAll('[data-dialog-root]');
        if (dialogs[dialogs.length - 1] === node) {
          e.stopPropagation();
          closeRef.current();
        }
        return;
      }
      if (e.key !== 'Tab') return;
      const items = Array.from(node.querySelectorAll<HTMLElement>(FOCUSABLE)).filter((el) => el.offsetParent !== null);
      if (items.length === 0) return;
      const firstEl = items[0];
      const lastEl = items[items.length - 1];
      if (e.shiftKey && document.activeElement === firstEl) { e.preventDefault(); lastEl.focus(); }
      else if (!e.shiftKey && document.activeElement === lastEl) { e.preventDefault(); firstEl.focus(); }
    }
    // Regresión (revisión exploratoria 022, F-03): el botón que se deshabilita mientras espera al servidor saca el foco al
    // body; si la acción falla, una persona con teclado quedaba fuera del diálogo. Al rehabilitarse, el foco regresa a él.
    let last: HTMLElement | null = null;
    const onFocusIn = (e: FocusEvent) => { if (node?.contains(e.target as Node)) last = e.target as HTMLElement; };
    const watcher = new MutationObserver(() => {
      const lostFocus = document.activeElement === null || document.activeElement === document.body;
      if (!lostFocus || !node) return;
      if (last?.isConnected && node.contains(last) && !last.matches(':disabled')) last.focus();
      else if (last && !last.isConnected) node.querySelector<HTMLElement>(FOCUSABLE)?.focus();
    });
    if (node) watcher.observe(node, { attributes: true, attributeFilter: ['disabled'], subtree: true, childList: true });
    document.addEventListener('focusin', onFocusIn);
    document.addEventListener('keydown', onKey);
    const prevOverflow = document.body.style.overflow;
    document.body.style.overflow = 'hidden';
    return () => {
      watcher.disconnect();
      document.removeEventListener('focusin', onFocusIn);
      document.removeEventListener('keydown', onKey);
      document.body.style.overflow = prevOverflow;
      previouslyFocused?.focus?.();
    };
  }, [open]);

  return ref;
}

/**
 * Cuerpo del diálogo. Regresión (revisión exploratoria 006, F-01): cuando el contenido no cabe y se desplaza,
 * debe poder enfocarse para recorrerlo con el teclado (WCAG 2.1.1, regla axe scrollable-region-focusable).
 */
function DialogBody({ children, style, label }: { children: ReactNode; style?: CSSProperties; label: string }) {
  const ref = useRef<HTMLDivElement>(null);
  const [scrolls, setScrolls] = useState(false);
  useEffect(() => {
    const el = ref.current;
    if (!el) return;
    const check = () => setScrolls(el.scrollHeight > el.clientHeight + 1);
    check();
    const ro = new ResizeObserver(check);
    ro.observe(el);
    const mo = new MutationObserver(check);
    mo.observe(el, { childList: true, subtree: true, characterData: true });
    return () => { ro.disconnect(); mo.disconnect(); };
  }, []);
  return (
    <div ref={ref} className="dialog-body" style={style}
      tabIndex={scrolls ? 0 : undefined} role={scrolls ? 'region' : undefined} aria-label={scrolls ? label : undefined}>
      {children}
    </div>
  );
}

export interface DialogProps {
  open: boolean;
  onClose: () => void;
  title: ReactNode;
  description?: ReactNode;
  size?: 'md' | 'lg' | 'xl';
  footer?: ReactNode;
  children?: ReactNode;
  dismissable?: boolean;
}

export function Dialog({ open, onClose, title, description, size = 'md', footer, children, dismissable = true }: DialogProps) {
  const titleId = useId();
  const descId = useId();
  const close = dismissable ? onClose : () => {};
  const ref = useFocusTrap(open, close);
  if (!open) return null;
  return createPortal(
    <div className="overlay" onMouseDown={(e) => { if (e.target === e.currentTarget) close(); }}>
      <div
        ref={ref}
        data-dialog-root
        className={`dialog${size !== 'md' ? ` dialog-${size}` : ''}`}
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        aria-describedby={description ? descId : undefined}
      >
        <div className="dialog-header">
          <div className="grow">
            <h2 id={titleId} className="dialog-title">{title}</h2>
            {description && <p id={descId} className="dialog-desc">{description}</p>}
          </div>
          {dismissable && <Button variant="ghost" size="sm" iconOnly icon={X} aria-label="Cerrar" onClick={onClose} />}
        </div>
        {children && <DialogBody label={typeof title === 'string' ? title : 'Contenido'}>{children}</DialogBody>}
        {footer && <div className="dialog-footer">{footer}</div>}
      </div>
    </div>,
    document.body,
  );
}

export interface DrawerProps {
  open: boolean;
  onClose: () => void;
  title: ReactNode;
  description?: ReactNode;
  footer?: ReactNode;
  children?: ReactNode;
}

export function Drawer({ open, onClose, title, description, footer, children }: DrawerProps) {
  const titleId = useId();
  const ref = useFocusTrap(open, onClose);
  if (!open) return null;
  return createPortal(
    <div className="overlay drawer-overlay" onMouseDown={(e) => { if (e.target === e.currentTarget) onClose(); }}>
      <div ref={ref} data-dialog-root className="drawer" role="dialog" aria-modal="true" aria-labelledby={titleId}>
        <div className="dialog-header" style={{ borderBottom: '1px solid var(--border)', paddingBottom: 'var(--space-4)' }}>
          <div className="grow">
            <h2 id={titleId} className="dialog-title">{title}</h2>
            {description && <p className="dialog-desc">{description}</p>}
          </div>
          <Button variant="ghost" size="sm" iconOnly icon={X} aria-label="Cerrar" onClick={onClose} />
        </div>
        <DialogBody style={{ flex: 1, paddingTop: 'var(--space-5)' }} label={typeof title === 'string' ? title : 'Contenido'}>{children}</DialogBody>
        {footer && <div className="dialog-footer">{footer}</div>}
      </div>
    </div>,
    document.body,
  );
}

export interface ConfirmDialogProps {
  open: boolean;
  title: string;
  message: ReactNode;
  confirmLabel?: string;
  cancelLabel?: string;
  danger?: boolean;
  loading?: boolean;
  onConfirm: () => void;
  onCancel: () => void;
}

export function ConfirmDialog({ open, title, message, confirmLabel = 'Confirmar', cancelLabel = 'Cancelar', danger, loading, onConfirm, onCancel }: ConfirmDialogProps) {
  return (
    <Dialog
      open={open}
      onClose={onCancel}
      title={<span className="row">{danger && <AlertTriangle size={18} color="var(--danger)" aria-hidden />}{title}</span>}
      footer={
        <>
          <Button onClick={onCancel} disabled={loading} data-autofocus>{cancelLabel}</Button>
          <Button variant={danger ? 'danger' : 'primary'} loading={loading} onClick={onConfirm}>{confirmLabel}</Button>
        </>
      }
    >
      <div className="muted">{message}</div>
    </Dialog>
  );
}
