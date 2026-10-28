import type { ReactNode } from 'react';
import { Navigate, Route, Routes, useLocation } from 'react-router-dom';
import { useAuth } from './auth/AuthContext';
import { LoadingBlock } from './components/ui';
import { Layout } from './components/Layout';
import { LoginPage } from './pages/LoginPage';
import { HomePage } from './pages/HomePage';
import { AppHomePage } from './pages/AppHomePage';
import { AdminHomePage } from './pages/AdminHomePage';
import { EntityListPage } from './pages/EntityListPage';
import { EntityFormPage } from './pages/EntityFormPage';
import { ModelerPage } from './pages/ModelerPage';
import { SecurityPage } from './pages/SecurityPage';
import { AuditPage } from './pages/AuditPage';
import { ModulesPage } from './pages/ModulesPage';
import { IntegrationsPage } from './pages/IntegrationsPage';
import { AutomationsPage } from './pages/AutomationsPage';
import { EnvironmentsPage } from './pages/EnvironmentsPage';
import { CompaniesPage } from './pages/CompaniesPage';
import { AssistantPage } from './pages/AssistantPage';
import { StudioPage } from './pages/StudioPage';
import { ExplorerPage } from './pages/ExplorerPage';
import { ImagesPage } from './pages/ImagesPage';
import { ReportPage, ReportsPage } from './pages/ReportsPage';
import { DashboardPage, DashboardsPage } from './pages/DashboardsPage';
import { NotFoundPage, ForbiddenPage } from './pages/StatusPages';

function RequireAuth({ children }: { children: ReactNode }) {
  const { me, loading } = useAuth();
  const location = useLocation();
  if (loading) return <LoadingBlock />;
  if (!me) return <Navigate to="/login" replace state={{ from: location.pathname + location.search }} />;
  return <>{children}</>;
}

function Guard({ allow, children }: { allow: boolean; children: ReactNode }) {
  return allow ? <>{children}</> : <ForbiddenPage />;
}

export function App() {
  const { me } = useAuth();
  return (
    <Routes>
      <Route path="/login" element={<LoginPage />} />
      <Route
        element={
          <RequireAuth>
            <Layout />
          </RequireAuth>
        }
      >
        <Route index element={<HomePage />} />
        {/* Spec 024: página de un módulo y entrada al área de configuración. */}
        <Route path="m/:app" element={<AppHomePage />} />
        <Route path="admin" element={<AdminHomePage />} />
        <Route path="c/:entity" element={<EntityListPage />} />
        <Route path="c/:entity/new" element={<EntityFormPage />} />
        <Route path="c/:entity/:id" element={<EntityFormPage />} />
        <Route path="reports" element={<ReportsPage />} />
        <Route path="reports/:entity/:name" element={<ReportPage />} />
        <Route path="dashboards" element={<DashboardsPage />} />
        <Route path="dashboards/:name" element={<DashboardPage />} />
        <Route path="admin/model" element={<Guard allow={!!(me?.canManageModel || me?.isSuperuser)}><ModelerPage /></Guard>} />
        {/* Spec 025: explorador del modelo y de la base de datos (solo lectura). */}
        <Route path="admin/explorer" element={<Guard allow={!!(me?.canManageModel || me?.isSuperuser)}><ExplorerPage /></Guard>} />
        <Route path="admin/images" element={<Guard allow={!!(me?.canManageModel || me?.isSuperuser)}><ImagesPage /></Guard>} />
        <Route path="admin/security" element={<Guard allow={!!(me?.canManageUsers || me?.isSuperuser)}><SecurityPage /></Guard>} />
        <Route path="admin/modules" element={<Guard allow={!!me?.isSuperuser}><ModulesPage /></Guard>} />
        <Route path="admin/integrations" element={<Guard allow={!!me?.isSuperuser}><IntegrationsPage /></Guard>} />
        <Route path="admin/automations" element={<Guard allow={!!me?.isSuperuser}><AutomationsPage /></Guard>} />
        <Route path="admin/environments" element={<Guard allow={!!me?.isSuperuser}><EnvironmentsPage /></Guard>} />
        <Route path="admin/companies" element={<Guard allow={!!me?.isSuperuser}><CompaniesPage /></Guard>} />
        {/* Spec 035: ERP AI Studio (motor de la 033). */}
        <Route path="admin/studio" element={<Guard allow={!!(me?.canManageModel || me?.isSuperuser)}><StudioPage /></Guard>} />
        <Route path="admin/assistant" element={<Guard allow={!!(me?.canManageModel || me?.isSuperuser)}><AssistantPage /></Guard>} />
        <Route path="admin/audit" element={<Guard allow={!!(me?.canViewAudit || me?.isSuperuser)}><AuditPage /></Guard>} />
        <Route path="*" element={<NotFoundPage />} />
      </Route>
    </Routes>
  );
}
