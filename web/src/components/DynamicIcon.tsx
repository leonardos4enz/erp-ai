import { icons, Database, type LucideProps } from 'lucide-react';

/** "shopping-cart" → "ShoppingCart" */
export function kebabToPascal(name: string): string {
  return name
    .split(/[-_\s]+/)
    .filter(Boolean)
    .map((p) => p.charAt(0).toUpperCase() + p.slice(1))
    .join('');
}

export function resolveIcon(name?: string | null) {
  if (!name) return Database;
  const key = (/[-_\s]/.test(name) || name[0] === name[0].toLowerCase() ? kebabToPascal(name) : name) as keyof typeof icons;
  return icons[key] ?? Database;
}

export function DynamicIcon({ name, ...props }: { name?: string | null } & LucideProps) {
  const Icon = resolveIcon(name);
  return <Icon aria-hidden {...props} />;
}

/** Curated set offered in the icon picker (kebab-case, Lucide names). */
export const CURATED_ICONS = [
  'package', 'package-2', 'box', 'boxes', 'archive', 'container', 'truck', 'warehouse',
  'factory', 'store', 'shopping-cart', 'shopping-bag', 'tag', 'tags', 'barcode', 'qr-code',
  'users', 'user', 'user-round', 'contact', 'building', 'building-2', 'briefcase', 'handshake',
  'file-text', 'files', 'folder', 'clipboard-list', 'clipboard-check', 'receipt', 'invoice' /* filtered if missing */,
  'wallet', 'credit-card', 'banknote', 'coins', 'piggy-bank', 'landmark', 'calculator', 'percent',
  'chart-bar', 'chart-line', 'chart-pie', 'trending-up', 'calendar', 'clock', 'timer', 'alarm-clock',
  'map-pin', 'map', 'globe', 'route', 'plane', 'ship', 'car', 'fuel',
  'wrench', 'hammer', 'settings', 'cog', 'cpu', 'hard-drive', 'database', 'server',
  'layers', 'layout-grid', 'list', 'list-checks', 'bookmark', 'star', 'flag', 'award',
  'shield', 'shield-check', 'key-round', 'lock', 'bell', 'mail', 'phone', 'message-square',
  'heart-pulse', 'stethoscope', 'pill', 'flask-conical', 'leaf', 'sprout', 'apple', 'utensils',
  'shirt', 'scissors', 'ruler', 'scale', 'droplet', 'flame', 'zap', 'sun',
].filter((n) => kebabToPascal(n) in icons);
