import {
  Blocks, Building2, CalendarClock, GitCompareArrows, History, Image, Network, ShieldCheck, Sparkles, Webhook, Workflow, type LucideIcon,
} from 'lucide-react';
import type { Me } from '../api/types';

/** Spec 024 (H3): una página del área de configuración, visible solo con su capacidad (las rutas siguen protegidas). */
export interface AdminItem { to: string; label: string; description: string; icon: LucideIcon; allow: (me: Me) => boolean }
export interface AdminGroup { label: string; items: AdminItem[] }

const model = (me: Me) => me.canManageModel || me.isSuperuser;
const users = (me: Me) => me.canManageUsers || me.isSuperuser;
const audit = (me: Me) => me.canViewAudit || me.isSuperuser;
const superuser = (me: Me) => me.isSuperuser;

export const ADMIN_GROUPS: AdminGroup[] = [
  {
    label: 'Modelo',
    items: [
      { to: '/admin/model', label: 'Modelador', description: 'Catálogos, campos, procesos y módulos del menú.', icon: Workflow, allow: model },
      { to: '/admin/explorer', label: 'Explorador', description: 'Base de datos, relaciones, usos, automatizaciones y seguridad (solo lectura).', icon: Network, allow: model },
      { to: '/admin/images', label: 'Imágenes', description: 'Sellos, firmas e imágenes de las plantillas de impresión.', icon: Image, allow: model },
      { to: '/admin/modules', label: 'Módulos', description: 'Instalar y actualizar paquetes de módulos.', icon: Blocks, allow: superuser },
      { to: '/admin/environments', label: 'Entornos', description: 'Exportar, revisar y aplicar paquetes entre entornos.', icon: GitCompareArrows, allow: superuser },
    ],
  },
  {
    label: 'Seguridad',
    items: [
      { to: '/admin/security', label: 'Usuarios y roles', description: 'Usuarios, roles, equipos y unidades de negocio.', icon: ShieldCheck, allow: users },
      { to: '/admin/companies', label: 'Empresas', description: 'Empresas, monedas base y tipos de cambio.', icon: Building2, allow: superuser },
    ],
  },
  {
    label: 'Automatización e integraciones',
    items: [
      { to: '/admin/automations', label: 'Automatizaciones', description: 'Tareas programadas y bitácora de ejecuciones.', icon: CalendarClock, allow: superuser },
      { to: '/admin/integrations', label: 'Integraciones', description: 'Webhooks, claves de API y clientes OAuth.', icon: Webhook, allow: superuser },
      { to: '/admin/assistant', label: 'Asistente IA', description: 'Construir el modelo conversando con Claude.', icon: Sparkles, allow: model },
    ],
  },
  {
    label: 'Auditoría',
    items: [
      { to: '/admin/audit', label: 'Auditoría', description: 'Quién cambió qué y cuándo, en datos y modelo.', icon: History, allow: audit },
    ],
  },
];

/** Grupos con solo las páginas que el usuario puede abrir (sin grupos vacíos). */
export function adminGroupsFor(me: Me | null): AdminGroup[] {
  if (!me) return [];
  return ADMIN_GROUPS.map((g) => ({ ...g, items: g.items.filter((i) => i.allow(me)) })).filter((g) => g.items.length > 0);
}

export const canAdmin = (me: Me | null) => adminGroupsFor(me).length > 0;
