# Plan 023 — Diseño técnico

## 1. Listas que se fusionan (`PlanCompiler`, `PlanWarnings`, `ModelDiff`)
```ts
Operation 'setViews' | 'setReports' | 'setWebhooks' | 'setSchedules' += { merge?: boolean }
// como setRules (022) y setAutomations (020): agrega o reemplaza por nombre; las demás se conservan
```
- `PlanCompiler`: un solo ayudante `MergeByName(actual, plan, nombre)` — los elementos actuales que el plan nombra se
  reemplazan **en su lugar**, los nuevos van al final (el orden de las vistas es el del menú). Se usa en las seis listas.
  Las validaciones de forma (pasada final: `InterfaceRules`, `WebhookRules`, `ScheduleRules`) corren sobre el resultado,
  igual que hoy; los topes también. La descripción del paso dice «Agregar o actualizar» con `merge`.
- `PlanWarnings.For`: `Removed(...)` solo cuando la operación **no** es `merge` (las seis listas). Además, un aviso nuevo:
  «La regla X de «Y» pasa a aviso: ya no impide guardar ni ejecutar acciones» cuando una regla de error pasa a `warning`
  (relaja un control).
- `ModelDiff`: `Scope(p)` sigue siendo «lo que el paquete crea» (permisos, validación «no crea nada»); nuevo
  `Extended(p)` = catálogos con operaciones del paquete que el paquete no crea. `Desired` compila la cadena sobre el destino
  sin los creados (como hoy) y devuelve creados ∪ extendidos. La instantánea de la 017 (`ModelSnapshot.Take`) y la reversión
  usan creados ∪ extendidos: revertir devuelve el catálogo extendido a como estaba (lo que el paquete le agregó se archiva).
  `Compute` emite listas completas (estado deseado completo) para ambos.

## 2. Reglas de aviso (`BusinessRule`, `RecordService`)
```ts
BusinessRule       += { severity?: 'error' | 'warning' }          // omitido = 'error'
WorkflowTransition += { onWarning?: 'confirm' | 'block' | 'allow' } // omitido = 'confirm'
POST /api/data/{e}/{id}/transition  body += { acceptWarnings?: string[] }
// 428 { title: "La acción tiene avisos: revísalos y confirma para continuar.", errors: { clave: [mensaje] } }
GET/POST/PUT registro → row._warnings?: { clave: string[] }       // solo si hay; clave = campo, Partidas[i].Campo o "registro"
POST /api/data/{e}/compute → { values, layout?, warnings? }
POST /api/data/{e}/import  → ImportResult += { warnings: ImportError[] }
auditoría (create | update | workflow.* | automation.*) → after._warnings
```
- `RuleErrors(e, scope, env, prefix, errors, warnings?)`: la regla `warning` incumplida va a `warnings` (nula: no se evalúa).
  `Validate(...)` devuelve los avisos (sigue lanzando `ValidationFailed` con los errores). Mismo `when`/`assert`, misma
  lógica de tres valores (un `assert` desconocido no se viola).
- **Visibles** (`VisibleWarnings`): se descarta el aviso cuya regla (`when` o `assert`) lee un campo oculto para el
  usuario, con `ReadsHidden` (ahora por todos los saltos de la ruta, §3). Se usan para `_warnings`, `/compute` y la
  confirmación; la auditoría guarda todos.
- **Crear/editar** (`InsertDraftAsync`, `UpdateAsync`, `SystemUpdateAsync`): los avisos no detienen; van a la auditoría y,
  con un `WarningSink` en `AutoCtx`, a la importación (fila = la del archivo; claves como en los errores).
- **Transición** (`ApplyTransitionAsync`): tras calcular y validar el estado nuevo, si hay avisos:
  `block` → `ValidationFailed` con los avisos como errores; `allow` → sigue; `confirm` → si la ejecuta una persona
  (profundidad 0) y algún aviso visible no está en `acceptWarnings` (comparación exacta del mensaje), `AppException(428)`
  con los avisos visibles; una automatización sigue. En todos los casos que siguen, `_warnings` va a la auditoría.
- `LoadOutputAsync(..., warnings: true)` en GET, crear, editar y transición (no en webhooks): calcula el borrador guardado
  y agrega `_warnings` visibles. `/compute` evalúa solo las reglas `warning` sobre el borrador calculado.
- `PlanCompiler.SetRules`: `severity` ∈ {error, warning}. `SetWorkflow`: `onWarning` ∈ {confirm, block, allow}. Se
  serializan solo si no son nulos (la forma canónica de los paquetes de la 009 no cambia).
- **UI**: `EntityFormPage` muestra un `Alert tone="warning"` «Avisos» (`role="status"`) con los avisos de `/compute` o de
  `_warnings`; `needsCompute` también cuando el catálogo o sus partidas tienen reglas de aviso. `TransitionDialog`: ante 428
  muestra los avisos (anunciados con `role="alert"`) y el botón pasa a «Continuar con avisos», que reenvía
  `acceptWarnings`. Importación: los avisos se listan aparte de los errores. Modelador: la pista del JSON de reglas
  menciona `severity`; el panel de la transición del diseñador del proceso agrega «Si hay avisos» (pedir confirmación /
  bloquear / continuar sin preguntar). El asistente conoce `severity`, `onWarning`, `merge` y las rutas.

## 3. Rutas de tres niveles (`Expr`, `ExprAnalyzer`, `ExprEvaluator`, `Scopes`, `RecordFilter`, `ReadsHidden`)
```text
ruta := Ident ('.' Ident){0,3}        // máx. 4 segmentos (3 saltos): ExprParser.MaxPathSegments = 4
```
- AST: `PathRef(string Head, IReadOnlyList<string> Rest)` (`Rest` vacío = campo propio). Se quita `Tail` para que el
  compilador obligue a revisar cada consumidor; los que solo admiten un nivel lo dicen (`p.Single`) y rechazan más.
- **Analyzer**: recorre los saltos contra los metadatos. Intermedios: `reference`/`parent` (al catálogo referido) o
  `user` (solo como penúltimo: `…Usuario.Nombre`). Final: existe, no archivado, no partidas. Errores con la ruta completa.
  `SUM(Partidas.X)`/`COUNT(Partidas)`: un nivel, como hoy.
- **Evaluador**: `IExprScope.RelatedPath(head, rest)` (método por defecto: `rest.Count == 1 ? Related(head, rest[0]) :
  null`, para los ámbitos de prueba). `DocScope`: el padre en memoria si el primer salto es el campo padre de la partida;
  después `RelatedLoader.Walk(entity, id, rest)` lee cada registro por `Id` con la caché `Row` (nulo si falta o está
  borrado). Identificadores solo de los metadatos.
- **SQL** (`RecordFilter.Translator.Column`, modo acumulado): subconsultas anidadas
  `(SELECT aN.[final] FROM app.[En] aN WHERE aN.[Id] = (… (SELECT a0.[f1] FROM app.[E1] a0 WHERE a0.[Id] = t.[Head] AND
  a0.[IsDeleted] = 0) …) AND aN.[IsDeleted] = 0)`; intermedios solo `reference`/`parent`. Las reglas de acceso y los filtros
  de reporte (sin modelo) siguen rechazando rutas.
- **Seguridad**: `ReadsHidden`/`PathHidden` revisan `FieldAccess.CanSee` en **cada** salto (campo de referencia y campo
  final); con el usuario relacionado no hay seguridad por campo (como hoy).

## 4. Módulos
- **Inventario 1.7.0** (`upgrades`): `updateField RecepcionPartida.CostoUnitario` con la moneda
  `COALESCE(Recepcion.Moneda, Recepcion.OrdenCompra.Moneda.Clave)` (3 saltos) en lugar de `Recepcion.Moneda`; el resto como
  1.6.1 (vacío sin tipo de cambio ni respaldo).
- **Costeo 1.1.0**: `setViews Recepcion` pasa a `merge` (CLARIFY). Sin versión nueva.

## 5. Pruebas
- Unitarias: `merge` en las cuatro listas (conserva, reemplaza en su lugar, tope sobre el resultado); `PlanWarnings` con
  `merge` (regresión 1b) y regla que pasa a aviso; `ModelDiff` con paquete que extiende un catálogo (regresión 1c) y su
  reversión; paquetes de `modules/` sin listas ajenas reemplazadas (regresión 1d); `severity`/`onWarning` inválidos;
  parser (tope, mensajes), analyzer (saltos válidos e inválidos), evaluador (3 saltos, nulos), traducción SQL (forma,
  identificadores citados), `ReadsHidden` en el segundo y tercer salto; Inventario 1.7.0 (regresión H3 CA4 con 1.6.1).
- E2E: `valuacion-rutas-avisos.spec.ts` (después de las de Costeo) — H1 (merge de vistas por API y vista previa), H2 (aviso en el formulario en vivo, al
  guardar y al abrir; transición `confirm` con 428 y diálogo; `block`; `allow`; importación con avisos; auditoría; caso 7
  con `PolizaConCuentas` en `warning`), H3 (fórmula de 3 saltos en el formulario; `rollup` con filtro de 3 saltos;
  recepción sin moneda con Inventario 1.7.0), accesibilidad del diálogo con avisos y del formulario, y visual del aviso.

## 6. STRIDE
| Amenaza | Vector | Mitigación |
|---|---|---|
| **S**uplantación | — | Sin cambios de autenticación |
| **T**ampering | Un cliente envía `acceptWarnings` para saltarse una regla | Solo acepta avisos que el servidor calculó; los errores y `block` no se aceptan; comparación exacta del mensaje |
| **T**ampering | `merge` usado para colar cambios sin que se noten | Misma vía (plan, vista previa, permiso de modelo, `baseHash` de la 021); la vista previa lista lo agregado y lo quitado |
| **R**epudio | «Nadie vio el aviso» | Todo guardado o acción con avisos los deja en la auditoría (`_warnings`), con quién y cuándo |
| **I**nformación | Ruta que lee un campo oculto en el 2.º o 3.º salto | `ReadsHidden` revisa cada salto (layouts, reportes, avisos) |
| **I**nformación | Un aviso revela un dato oculto | Avisos que leen campos ocultos no se muestran ni se piden; el 428 solo lista visibles |
| **I**nformación / inyección | Segmentos de la ruta en SQL | Segmentos del tokenizador (letras, dígitos, `_`), resueltos contra metadatos y citados con `Quote`; valores como parámetros |
| **D**enegación | Rutas largas por partida | Tope fijo de 4 segmentos; caché por operación; tope de partidas (500) y de longitud de expresión (1000) existentes |
| **E**levación | Paquete de Entornos que extiende un catálogo ajeno | Mismo flujo de aprobación del diff; los permisos de roles siguen limitados a lo que el paquete crea; la reversión cubre lo extendido |
