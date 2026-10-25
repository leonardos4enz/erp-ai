import {
  forwardRef, useId, type ButtonHTMLAttributes, type InputHTMLAttributes, type ReactNode,
  type SelectHTMLAttributes, type TextareaHTMLAttributes,
} from 'react';
import { AlertCircle, ChevronDown, Loader2, type LucideIcon } from 'lucide-react';

/* ---------------- Button ---------------- */
type ButtonVariant = 'primary' | 'secondary' | 'ghost' | 'danger';
type ButtonSize = 'sm' | 'md' | 'lg';

export interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: ButtonVariant;
  size?: ButtonSize;
  loading?: boolean;
  icon?: LucideIcon;
  iconOnly?: boolean;
  block?: boolean;
}

export const Button = forwardRef<HTMLButtonElement, ButtonProps>(function Button(
  { variant = 'secondary', size = 'md', loading, icon: Icon, iconOnly, block, className, children, disabled, type, ...rest },
  ref,
) {
  const cls = [
    'btn', `btn-${variant}`, size !== 'md' && `btn-${size}`, iconOnly && 'btn-icon', block && 'btn-block', className,
  ].filter(Boolean).join(' ');
  const iconSize = size === 'sm' ? 15 : 16;
  return (
    <button ref={ref} type={type ?? 'button'} className={cls} disabled={disabled || loading} aria-busy={loading || undefined} {...rest}>
      {loading ? <Loader2 size={iconSize} className="spin" aria-hidden /> : Icon ? <Icon size={iconSize} aria-hidden /> : null}
      {children}
    </button>
  );
});

/* ---------------- Field wrapper ---------------- */
export interface FieldProps {
  label?: ReactNode;
  required?: boolean;
  hint?: ReactNode;
  error?: string;
  className?: string;
  htmlFor?: string;
  children: ReactNode;
}

export function Field({ label, required, hint, error, className, htmlFor, children }: FieldProps) {
  return (
    <div className={['field', className].filter(Boolean).join(' ')}>
      {label && (
        <label className="field-label" htmlFor={htmlFor}>
          {label}
          {required && <span className="field-required" aria-hidden>*</span>}
          {required && <span className="sr-only">(obligatorio)</span>}
        </label>
      )}
      {children}
      {error ? (
        <span className="field-error" role="alert" id={htmlFor ? `${htmlFor}-err` : undefined}>
          <AlertCircle size={13} aria-hidden /> {error}
        </span>
      ) : hint ? (
        <span className="field-hint">{hint}</span>
      ) : null}
    </div>
  );
}

/* ---------------- Inputs ---------------- */
export interface InputProps extends InputHTMLAttributes<HTMLInputElement> {
  label?: ReactNode;
  hint?: ReactNode;
  error?: string;
  icon?: LucideIcon;
  wrapClassName?: string;
  inputSize?: 'sm' | 'md';
}

export const Input = forwardRef<HTMLInputElement, InputProps>(function Input(
  { label, hint, error, icon: Icon, wrapClassName, className, id, required, inputSize, ...rest },
  ref,
) {
  const autoId = useId();
  const inputId = id ?? autoId;
  const input = (
    <input
      ref={ref}
      id={inputId}
      className={['input', inputSize === 'sm' && 'input-sm', className].filter(Boolean).join(' ')}
      aria-invalid={error ? true : undefined}
      aria-describedby={error ? `${inputId}-err` : undefined}
      required={required}
      {...rest}
    />
  );
  const control = Icon ? (
    <div className="input-wrap"><Icon size={16} aria-hidden />{input}</div>
  ) : input;
  if (!label && !error && !hint) return control;
  return (
    <Field label={label} required={required} hint={hint} error={error} htmlFor={inputId} className={wrapClassName}>
      {control}
    </Field>
  );
});

export interface TextareaProps extends TextareaHTMLAttributes<HTMLTextAreaElement> {
  label?: ReactNode;
  hint?: ReactNode;
  error?: string;
  wrapClassName?: string;
}

export const Textarea = forwardRef<HTMLTextAreaElement, TextareaProps>(function Textarea(
  { label, hint, error, wrapClassName, className, id, required, ...rest },
  ref,
) {
  const autoId = useId();
  const tid = id ?? autoId;
  return (
    <Field label={label} required={required} hint={hint} error={error} htmlFor={tid} className={wrapClassName}>
      <textarea
        ref={ref}
        id={tid}
        className={['textarea', className].filter(Boolean).join(' ')}
        aria-invalid={error ? true : undefined}
        aria-describedby={error ? `${tid}-err` : undefined}
        required={required}
        {...rest}
      />
    </Field>
  );
});

export interface SelectProps extends SelectHTMLAttributes<HTMLSelectElement> {
  label?: ReactNode;
  hint?: ReactNode;
  error?: string;
  wrapClassName?: string;
  selectSize?: 'sm' | 'md';
}

export function Select({ label, hint, error, wrapClassName, className, id, required, children, selectSize, ...rest }: SelectProps) {
  const autoId = useId();
  const sid = id ?? autoId;
  const control = (
    <div className="select-wrap">
      <select
        id={sid}
        className={['select', selectSize === 'sm' && 'select-sm', className].filter(Boolean).join(' ')}
        aria-invalid={error ? true : undefined}
        aria-describedby={error ? `${sid}-err` : undefined}
        required={required}
        {...rest}
      >
        {children}
      </select>
      <ChevronDown size={15} aria-hidden />
    </div>
  );
  if (!label && !error && !hint) return control;
  return (
    <Field label={label} required={required} hint={hint} error={error} htmlFor={sid} className={wrapClassName}>
      {control}
    </Field>
  );
}

export interface ToggleProps {
  checked: boolean;
  onChange: (checked: boolean) => void;
  label?: ReactNode;
  disabled?: boolean;
  id?: string;
  'aria-label'?: string;
}

export function Switch({ checked, onChange, label, disabled, id, ...aria }: ToggleProps) {
  return (
    <label className="switch">
      <input
        id={id}
        type="checkbox"
        role="switch"
        checked={checked}
        disabled={disabled}
        aria-checked={checked}
        aria-label={aria['aria-label']}
        onChange={(e) => onChange(e.target.checked)}
      />
      <span className="switch-track" aria-hidden />
      {label && <span>{label}</span>}
    </label>
  );
}

export function Checkbox({ checked, onChange, label, disabled, id, ...aria }: ToggleProps) {
  return (
    <label className="checkbox">
      <input
        id={id}
        type="checkbox"
        checked={checked}
        disabled={disabled}
        aria-label={aria['aria-label']}
        onChange={(e) => onChange(e.target.checked)}
      />
      {label && <span>{label}</span>}
    </label>
  );
}

/* ---------------- Badge ---------------- */
export function Badge({ tone = 'neutral', children, icon: Icon }: { tone?: 'neutral' | 'accent' | 'success' | 'danger' | 'warning'; children: ReactNode; icon?: LucideIcon }) {
  return (
    <span className={`badge${tone !== 'neutral' ? ` badge-${tone}` : ''}`}>
      {Icon && <Icon size={12} aria-hidden />}
      {children}
    </span>
  );
}

/* ---------------- Spinner ---------------- */
export function Spinner({ size = 20, label = 'Cargando' }: { size?: number; label?: string }) {
  return (
    <span role="status" className="row" aria-live="polite">
      <Loader2 size={size} className="spin" aria-hidden />
      <span className="sr-only">{label}</span>
    </span>
  );
}

export function LoadingBlock() {
  return <div className="center-fill"><Spinner size={22} /></div>;
}

/* ---------------- Empty state ---------------- */
export function EmptyState({ icon: Icon, title, description, action }: { icon: LucideIcon; title: string; description?: ReactNode; action?: ReactNode }) {
  return (
    <div className="empty">
      <div className="empty-icon"><Icon size={22} aria-hidden /></div>
      <div className="empty-title">{title}</div>
      {description && <div className="empty-desc">{description}</div>}
      {action}
    </div>
  );
}

/* ---------------- Page header ---------------- */
export function PageHeader({ icon: Icon, title, subtitle, actions }: { icon?: LucideIcon; title: ReactNode; subtitle?: ReactNode; actions?: ReactNode }) {
  return (
    <header className="page-header">
      {Icon && <div className="page-header-icon"><Icon size={20} aria-hidden /></div>}
      <div className="grow">
        <h1 className="page-title">{title}</h1>
        {subtitle && <p className="page-subtitle">{subtitle}</p>}
      </div>
      {actions && <div className="page-actions">{actions}</div>}
    </header>
  );
}

/* ---------------- Alert ---------------- */
export function Alert({ tone = 'info', icon: Icon = AlertCircle, title, children, action }: { tone?: 'info' | 'danger' | 'warning'; icon?: LucideIcon; title?: ReactNode; children?: ReactNode; action?: ReactNode }) {
  return (
    <div className={`alert alert-${tone}`} role={tone === 'danger' ? 'alert' : 'status'}>
      <Icon size={16} aria-hidden />
      <div className="grow">
        {title && <div style={{ fontWeight: 500 }}>{title}</div>}
        {children && <div>{children}</div>}
      </div>
      {action}
    </div>
  );
}

/* ---------------- Tabs ---------------- */
export interface TabItem<T extends string> { id: T; label: string; icon?: LucideIcon }

export function Tabs<T extends string>({ items, value, onChange, label }: { items: TabItem<T>[]; value: T; onChange: (v: T) => void; label: string }) {
  return (
    <div className="tabs" role="tablist" aria-label={label}>
      {items.map((t, i) => (
        <button
          key={t.id}
          role="tab"
          type="button"
          className="tab"
          aria-selected={value === t.id}
          tabIndex={value === t.id ? 0 : -1}
          onClick={() => onChange(t.id)}
          onKeyDown={(e) => {
            if (e.key === 'ArrowRight' || e.key === 'ArrowLeft') {
              const j = (i + (e.key === 'ArrowRight' ? 1 : items.length - 1)) % items.length;
              onChange(items[j].id);
              // Regresión (revisión exploratoria 026, F-01): con la pestaña en la URL el cambio llega después del cuadro
              // siguiente, y buscar la seleccionada dejaba el foco en la anterior. Se enfoca por posición.
              (e.currentTarget.parentElement?.children[j] as HTMLElement | undefined)?.focus();
            }
          }}
        >
          {t.icon && <t.icon size={15} aria-hidden />}
          {t.label}
        </button>
      ))}
    </div>
  );
}
