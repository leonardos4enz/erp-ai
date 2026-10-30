import { useEffect, useRef, useState } from 'react';
import { Link, useLocation } from 'react-router-dom';
import { Database, Globe, Plug, ShieldAlert, User, Workflow } from 'lucide-react';
import { ApiError } from '../api/client';
import { mcpApi } from '../api/endpoints';
import { BrandLogo } from '../components/Brand';
import { Alert, Badge, Button, Checkbox, LoadingBlock } from '../components/ui';
import { useAsync } from '../lib/hooks';

/**
 * Spec 038 (H2): la persona autoriza a un cliente MCP (su Claude) a usar el ERP con su cuenta. El servidor llega aquí desde
 * `/oauth/authorize` con la consulta OAuth; esta pantalla la valida con la sesión de la persona, muestra quién pide, a qué
 * sitio volverá el acceso y qué alcances, y manda la decisión. El navegador vuelve al cliente con lo que responda el
 * servidor (código o error), nunca a otra dirección.
 */
export function ConnectPage() {
  const query = useLocation().search.replace(/^\?/, '');
  const info = useAsync((signal) => mcpApi.check(query, signal), [query]);
  const [datos, setDatos] = useState(false);
  const [busy, setBusy] = useState<'approve' | 'deny' | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [leaving, setLeaving] = useState<string | null>(null);
  const titleRef = useRef<HTMLHeadingElement>(null);

  const data = info.data;
  // Revisión adversarial F-03: un error que va al cliente (alcance desconocido, sin PKCE…) no redirige solo: cualquiera puede
  // registrar un cliente, y el ERP sería un redirector abierto. Se explica y la persona decide volver.
  const clientError = data?.error ? data : null;
  useEffect(() => {
    if (data?.client || data?.error || info.error) titleRef.current?.focus();
  }, [data?.client, data?.error, info.error]);

  async function decide(approve: boolean) {
    setBusy(approve ? 'approve' : 'deny');
    setError(null);
    try {
      const res = await mcpApi.decide(query, approve, datos ? ['modelo', 'datos'] : ['modelo']);
      setLeaving(res.redirect);
      window.location.assign(res.redirect);
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'No se pudo enviar tu decisión. Inténtalo de nuevo.');
      setBusy(null);
    }
  }

  const requestedDatos = !!data?.scopes?.find((s) => s.name === 'datos')?.requested;

  return (
    <div className="consent-shell">
      <main className="consent-panel" aria-labelledby="consent-title">
        <BrandLogo size={32} />
        {info.loading && !data && <LoadingBlock />}
        {info.error != null && (
          <>
            <h1 id="consent-title" ref={titleRef} tabIndex={-1}>No se puede conectar</h1>
            <Alert tone="danger" title="La petición de conexión no es válida">
              {info.error instanceof ApiError ? info.error.message : 'No se pudo revisar la petición.'} No se envió nada al cliente.
            </Alert>
            <Link className="btn" to="/">Ir al inicio</Link>
          </>
        )}
        {clientError && !leaving && (
          <>
            <h1 id="consent-title" ref={titleRef} tabIndex={-1}>No se puede conectar</h1>
            <Alert tone="danger" title="La aplicación pidió algo que este ERP no admite">
              {clientError.errorDescription ?? clientError.error} No se concedió ningún acceso.
            </Alert>
            <div className="consent-actions">
              <Link className="btn" to="/">Ir al inicio</Link>
              <Button variant="primary" onClick={() => { setLeaving(clientError.redirect!); window.location.assign(clientError.redirect!); }}>
                Volver a {clientError.redirectHost}
              </Button>
            </div>
          </>
        )}
        {leaving && (
          <p role="status" className="muted">Volviendo a la aplicación que pidió el acceso…</p>
        )}
        {data?.client && !leaving && (
          <>
            <header className="consent-header">
              <h1 id="consent-title" ref={titleRef} tabIndex={-1}>Conectar {data.client.name} con ERP AI</h1>
              <p className="muted">Una aplicación con MCP (como Claude) pide usar el ERP con tu cuenta.</p>
            </header>
            <dl className="consent-facts">
              <div>
                <dt><Plug size={15} aria-hidden /> Quién lo pide</dt>
                <dd>{data.client.name} <Badge>nombre sin verificar</Badge></dd>
              </div>
              <div>
                <dt><Globe size={15} aria-hidden /> A dónde volverá el acceso</dt>
                <dd><strong className="consent-host">{data.client.redirectHost}</strong></dd>
              </div>
              <div>
                <dt><User size={15} aria-hidden /> Entrarás como</dt>
                <dd>{data.user?.displayName} ({data.user?.username})</dd>
              </div>
            </dl>
            {!data.client.trusted && (
              <Alert tone="warning" icon={ShieldAlert} title="Sitio desconocido">
                El acceso irá a <strong>{data.client.redirectHost}</strong>, que no es un sitio conocido de Claude. Continúa solo si tú
                empezaste esta conexión desde ese sitio.
              </Alert>
            )}
            <fieldset className="consent-scopes">
              <legend>Qué podrá hacer, con tus permisos</legend>
              <div className="consent-scope">
                <Checkbox checked disabled onChange={() => undefined}
                  label={<span><Workflow size={14} aria-hidden /> <strong>Modelo</strong>: leer catálogos, relaciones, seguridad, rutas y riesgos; validar expresiones y compilar planes sin aplicarlos.</span>} />
              </div>
              <div className="consent-scope">
                <Checkbox checked={datos} disabled={!requestedDatos} onChange={setDatos}
                  label={<span><Database size={14} aria-hidden /> <strong>Datos</strong>: leer los registros que tú puedes ver.{!requestedDatos && ' (No lo pidió.)'}</span>} />
              </div>
              {datos && (
                <Alert tone="warning" title="Los registros saldrán del ERP">
                  Lo que lea viajará a {data.client.redirectHost} y al proveedor de IA de esa aplicación.
                </Alert>
              )}
            </fieldset>
            <p className="subtle consent-note">
              Nada se escribe en el ERP por esta conexión. Puedes desconectarla cuando quieras en <em>Conexiones de Claude</em> (menú de tu
              usuario).
            </p>
            {error && <Alert tone="danger" title="No se pudo completar">{error}</Alert>}
            <div className="consent-actions">
              <Button onClick={() => decide(false)} loading={busy === 'deny'} disabled={busy !== null}>Cancelar</Button>
              <Button variant="primary" onClick={() => decide(true)} loading={busy === 'approve'} disabled={busy !== null}>Permitir</Button>
            </div>
          </>
        )}
      </main>
    </div>
  );
}
