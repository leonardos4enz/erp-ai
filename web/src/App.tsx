import type { ReactNode } from 'react';
import { Navigate, Route, Routes, useLocation } from 'react-router-dom';
import { useAuth } from './auth/AuthContext';
import { LoadingBlock } from './components/ui';
import { Layout } from './components/Layout';
import { LoginPage } from './pages/LoginPage';
import { HomePage } from './pages/HomePage';
import { EntityListPage } from './pages/EntityListPage';
import { EntityFormPage } from './pages/EntityFormPage';
import { ModelerPage } from './pages/ModelerPage';
import { SecurityPage } from './pages/SecurityPage';
import { AuditPage } from './pages/AuditPage';
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
        <Route path="c/:entity" element={<EntityListPage />} />
        <Route path="c/:entity/new" element={<EntityFormPage />} />
        <Route path="c/:entity/:id" element={<EntityFormPage />} />
        <Route path="admin/model" element={<Guard allow={!!(me?.canManageModel || me?.isSuperuser)}><ModelerPage /></Guard>} />
        <Route path="admin/security" element={<Guard allow={!!(me?.canManageUsers || me?.isSuperuser)}><SecurityPage /></Guard>} />
        <Route path="admin/audit" element={<Guard allow={!!(me?.canViewAudit || me?.isSuperuser)}><AuditPage /></Guard>} />
        <Route path="*" element={<NotFoundPage />} />
      </Route>
    </Routes>
  );
}
