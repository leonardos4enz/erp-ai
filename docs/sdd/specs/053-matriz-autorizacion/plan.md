# Plan 053 — Diseño técnico

## 1. Modelo de datos

**Metadatos** (`Models.cs`, en el proceso; viaja en `setWorkflow`, paquetes y `ModelDiff` como parte de `Workflow`):
```jsonc
"workflow": {
  "initial": "Borrador",
  "states": [ …, { "name": "PorAutorizar", … }, { "name": "Autorizada", … }, { "name": "Rechazada", … } ],
  "transitions": [
    { "name": "enviar", "from": ["Borrador"], "to": "PorAutorizar" },
    { "name": "autorizada", "from": ["PorAutorizar"], "to": "Autorizada", "system": true },
    { "name": "rechazada", "from": ["PorAutorizar"], "to": "Rechazada", "system": true },
    { "name": "devolver", "from": ["PorAutorizar"], "to": "Borrador", "roles": ["…"] }      // salir por otra: cancela
  ],
  "approval": {
    "state": "PorAutorizar", "approve": "autorizada", "reject": "rechazada",
    "amount": "TotalBase",                      // campo numérico propio (puede ser una fórmula en moneda base); opcional
    "conditions": ["Departamento", "Tipo"],     // hasta 5 campos propios: referencia, opción, sí/no o persona
    "approvers": "Autorizadores"                // multiuser, editable: false; lo mantiene el motor
  }
}
```
`WorkflowApproval(State, Approve, Reject, Amount?, Conditions?, Approvers)`; se omite en nulo (forma canónica de los paquetes).
`ApprovalRules.Validate` (nuevo, `Metadata/`) corre en la pasada global del plan (como `AutomationRules`): todo lo de H1 CA2. Un
`updateField`/`archiveField` que rompa la declaración (archivar `approvers`, volverlo editable) cae en la misma pasada.

**Configuración** (tablas nuevas en `Bootstrap`, por empresa):
```
meta.ApprovalProfile (Id int PK, CompanyId int FK, Name nvarchar(100), MinAmount/MaxAmount decimal(19,4) NULL, IsActive bit,
                      CreatedAt/By, UpdatedAt/By, RowVer rowversion, UNIQUE (CompanyId, Name))
meta.ApprovalProfileMember (ProfileId FK, UserId FK, AddedAt, AddedBy, PK (ProfileId, UserId))
meta.ApprovalRule (Id int PK, CompanyId FK, EntityName nvarchar(64), Name nvarchar(100), Priority int, IsActive bit,
                   ValidFrom/ValidTo date NULL, MinAmount/MaxAmount decimal(19,4) NULL, ConditionsJson, LevelsJson (ids de perfil en
                   orden), CreatedAt/By, UpdatedAt/By, RowVer, UNIQUE (CompanyId, EntityName, Name))
meta.ApprovalRun (Id bigint PK, EntityName, RecordId bigint, CompanyId int, RuleId int, RuleJson (congelada: nombre y niveles
                  {perfil, nombre, rango, saltado}), Amount decimal NULL, ConditionsHash varbinary(32), CurrentLevel int NULL,
                  Status ('pending'|'approved'|'rejected'|'cancelled'|'reset'), StartedAt/By, EndedAt, RowVer rowversion;
                  índice único filtrado (EntityName, RecordId) WHERE Status = 'pending')
meta.ApprovalDecision (Id bigint PK, RunId FK, Level int, ProfileId int, UserId int NULL, Decision ('approve'|'reject'|'skip'),
                       Comment nvarchar(1000) NULL, Fingerprint varbinary(32) NULL, Superseded bit, At datetime2)
```
Topes: 200 perfiles y 200 reglas por empresa, 100 personas por perfil, 10 niveles, 5 condiciones con 50 valores, nombres de 100.

**Motor** (`Data/RecordService.Approval.cs`, nuevo):
- `ApprovalEnterAsync` (en `ApplyTransitionAsync`, tras escribir el encabezado y antes de las automatizaciones): si `t.To` es
  `state`, elige la regla (H4 CA1) con los datos del documento, congela los niveles, salta los de rango, crea la corrida, avanza al
  primer nivel con personas y escribe `approvers`; sin regla o sin niveles → `AppException(400)` (la transacción se revierte). Si
  `from` era `state` y `t` no es `approve`/`reject`, cierra la corrida como `cancelled` y vacía `approvers`.
- `ApprovalCheckAsync` (en `UpdateAsync`, `SystemUpdateAsync` y transiciones de `state` a `state`): si hay corrida pendiente y la
  huella de condiciones (monto y campos de condición, SHA-256 de su JSON) cambió → `reset` (decisiones `Superseded`) y nueva
  corrida con la matriz vigente.
- `DecideAsync` (ruta nueva): bloquea el documento (`UPDLOCK, HOLDLOCK`) y la corrida, comprueba versiones y turno, guarda la
  decisión con la huella y avanza; en el último nivel ejecuta `approve` por `ApplyTransitionAsync` con la persona como actor; al
  rechazar, `reject` con el motivo.
- **Quién puede ahora** (`Approvers`): personas activas del perfil del nivel en curso (al momento), no de servicio ni equipo, con
  acceso a la empresa, menos el creador y menos quien ya decidió en esta corrida. La ruta exige además ver el registro.
- **Exclusión:** todo cambio a corridas y a `approvers` toma `sp_getapplock 'erp:approval'` (exclusivo, de la transacción) después de
  los bloqueos del documento; `SaveMultiAsync` no escribe el campo `approvers` (solo el motor, con el candado), así una edición que
  leyó la lista antes no la pisa.
- Los cambios de personas o perfiles recalculan `approvers` de las corridas pendientes cuyo nivel en curso usa ese perfil, en la
  misma transacción y con el mismo candado.

**Configuración** (`Approvals/ApprovalConfig.cs`, nuevo): leer y guardar perfiles y reglas de la empresa actual, con la compuerta de
seguridad (`SecurityApproval`, tipos `approvalprofile.create|update` y `approvalrule.create|update`, todos «amplían»; nadie cambia su
propia pertenencia; quien revisa no es una persona que el cambio agrega o quita).

## 2. Contrato de la API
| Método y ruta | Capacidad | Entrada | Respuesta |
|---|---|---|---|
| `GET /api/data/{e}/{id}` (cambia) | `Records` | — | además `_approval: { status, rule, level, version, levels: [{ level, profile, status: 'approved'\|'current'\|'pending'\|'skipped', reason?, decisions: [{ user, decision, comment, at }] }], approvers: [nombres], canDecide, waitingReason? }` (si el catálogo declara `approval` y hay o hubo corrida) |
| `POST /api/data/{e}/{id}/approval` (nueva) | `Records`, `Writes(Data)`, `AppliesReviewed("rowVersion + approvalVersion")` | `{ decision: 'approve'\|'reject', comment?: string(≤1000), rowVersion, approvalVersion }` | 200 el registro. 400 sin motivo al rechazar, decisión inválida, catálogo sin autorización. 403 «No te toca autorizar este documento…» (con la razón: lo creaste, ya decidiste el nivel N, no estás en el perfil). 404 no existe o no lo ve. 409 no está pendiente, o cambió (versión) |
| `GET /api/admin/approvals` (nueva) | `UserAdmin` | — | `{ entities: [{ name, label, conditions: [{ field, label, type, options? }], amount? }], profiles: [{ id, name, minAmount, maxAmount, isActive, members: [{ id, label }], version }], rules: [{ id, entity, name, priority, isActive, validFrom, validTo, minAmount, maxAmount, conditions: { campo: [valores] }, levels: [ids], version }] }` de la empresa actual |
| `POST /api/admin/approvals/profiles`, `PUT …/profiles/{id}` (nuevas) | `UserAdmin`, `Writes(Security, SecurityApproval)` | `{ name, minAmount?, maxAmount?, isActive, memberIds: [], version? }` | 200/201 el perfil, o 202 `{ change }` si espera aprobación. 400 validación (incluida la propia pertenencia: «pídeselo a otro administrador»). 404 no es de la empresa. 409 versión |
| `POST /api/admin/approvals/rules`, `PUT …/rules/{id}` (nuevas) | `UserAdmin`, `Writes(Security, SecurityApproval)` | `{ entity, name, priority, isActive, validFrom?, validTo?, minAmount?, maxAmount?, conditions: { campo: [valores] }, levels: [profileId], version? }` | igual |
| `POST /api/data/{e}/{id}/transition` | igual | — | `approve`/`reject` son `system`: 400 «…la ejecuta el sistema automáticamente» (también al superusuario). Entrar a `state` sin regla: 400 |
| `POST /api/model/plans/*`, paquetes, Studio, asistente | igual | `workflow.approval` | 400 `errors[]` con «{E} › autorización: …» |

## 3. SPA
- `types.ts`: `WorkflowApproval`, `ApprovalPanel`; `api.ts`: `dataApi.decide`, `approvalsApi` (config, perfiles, reglas).
- **Panel** (`components/ApprovalPanel.tsx`, nuevo) en `EntityFormPage` cuando el registro trae `_approval`: lista de niveles
  (`<ol>` con estado por icono Lucide y texto, no solo color), decisiones, quiénes pueden autorizar y botones «Autorizar» /
  «Rechazar». Rechazar abre un diálogo con motivo obligatorio (foco atrapado, Esc); autorizar, un diálogo con comentario opcional.
  Errores con `role="alert"`; 409 dice «recarga».
- **Pantalla** `/admin/approvals` (`pages/ApprovalsPage.tsx`, nueva; menú de administración para quien administra usuarios):
  pestañas «Perfiles» y «Reglas»; tabla de cada uno y un diálogo de edición (nombre, rango, activo, personas con el buscador de
  usuarios; regla: catálogo, prioridad, vigencia, rango, valores por condición —opciones o registros con `ReferenceSelect`— y niveles
  como lista ordenada con subir, bajar y quitar). Si el cambio queda como solicitud, lo dice.
- Modelador: la declaración se muestra en el resumen del proceso (texto; editarla es por paquete o asistente, como el filtro antes
  de la 052) y la vista previa del plan describe el cambio («autorización por matriz en «Por autorizar»»).

## 4. Pruebas
- **Unitarias** (`tests/Erp.Tests/ApprovalTests.cs`, nueva): validación del plan (cada error de H1 CA2, la declaración válida, la
  forma JSON omitida en nulo); elegir regla (prioridad, especificidad, vigencia, rango, condiciones vacías = cualquiera);
  niveles saltados por rango; huella de condiciones estable; quién puede (creador, ya decidió, inactivo).
- **Integración** (`tests/Erp.IntegrationTests/ApprovalTests.cs`, nueva, SQL real): flujo de tres niveles con saltos; falla cerrado
  (sin regla, todos saltados); creador y dos niveles; superusuario fuera del perfil; nivel que espera (solo el creador); persona
  viva (baja quita de la lista y del permiso, alta agrega); reglas congeladas; reinicio por cambio de monto (automatización) y por
  edición; salir por «devolver» cancela; transiciones `approve`/`reject` por la ruta de transiciones → 400; plan rechaza
  automatización que las ejecuta y asignaciones a `approvers`; formulario/API/importación no escriben `approvers`; visibilidad por
  la 051 (lista, registro, buscador, exportación, OData, impresión); huella y versiones (409); **concurrencia**: diez personas del
  mismo nivel a la vez → una decisión; autorizar mientras se quita a la persona; la compuerta de seguridad (solicitud y aprobación
  por otro; nadie se agrega a sí mismo; el revisor no es un miembro agregado); falla parcial: la automatización de `approve` falla →
  nada escrito (ni la decisión).
- **E2E** (`web/e2e/autorizaciones.spec.ts`, nuevo): H2 CA1 (el administrador da de alta perfil y regla en la pantalla), H3 CA1–CA3
  (el panel muestra el avance; la persona del nivel autoriza con teclado; rechazar pide motivo), H4 CA4 (quien no le toca no lo ve
  en la lista), accesibilidad (axe) del panel y de la pantalla, capturas claro/oscuro/móvil de la pantalla.
- Con `--veces=3`: `autorizaciones.spec.ts` y los que cambien (canario, `visual*` si el menú cambia).

## 5. STRIDE
| Fila | Pregunta | Respuesta | Prueba o riesgo |
|---|---|---|---|
| Entradas | Rutas, operaciones de plan, acciones, OData, módulo/paquete/asistente | `POST /api/data/{e}/{id}/approval` (`Records`, `Data`, huella). `GET/POST/PUT /api/admin/approvals/*` (`UserAdmin`, `Security` con `SecurityApproval`). `setWorkflow` acepta `approval`, validado por `ApprovalRules` para todos los caminos del plan (UI, paquete, Studio, asistente). SQL solo con nombres de los metadatos | unit › `Una_declaracion_invalida_es_error_del_plan`; IT › `Sin_administrar_usuarios_la_matriz_responde_403` |
| Caminos alternos | UI, API, importación, exportación, OData, webhooks, automatización, tarea, paquete, reversión, impresión, asistente, módulo | Salir autorizado o rechazado: solo `DecideAsync` (las transiciones son `system`; el plan prohíbe que una automatización o tarea las ejecute). Escribir `approvers`: solo el motor (`editable: false`, el plan prohíbe asignarlo, `SaveMultiAsync` lo salta). Entrar a `state` por persona o automatización pasa por `ApplyTransitionAsync`. Leer: lista, registro, buscador, reportes, exportación, OData, MCP, Acompañante e impresión aplican `RecordFilter` con la pertenencia de la 051 | IT › `Nadie_ejecuta_approve_ni_reject_por_la_ruta_de_transiciones`; IT › `El_plan_rechaza_atajos_a_la_matriz`; IT › `F03_F04_el_plan_rechaza_otra_entrada_al_estado_autorizado_y_condiciones_en_la_salida`; IT › `Formulario_api_e_importacion_no_escriben_autorizadores`; IT › `Solo_quien_le_toca_ve_el_pendiente_en_todos_los_caminos` |
| Lectura de datos | ¿`RecordFilter`, `FieldAccess` y empresa? | `DecideAsync` exige leer el catálogo y ver el registro (`EnsureAccessAsync`), 404 si no. `_approval` sale con el registro (ya filtrado); nombres de personas solo del panel del registro que ve. La configuración es de la empresa actual del administrador; un id de otra empresa → 404 | IT › `Una_persona_del_perfil_que_no_ve_el_registro_no_autoriza`; IT › `La_matriz_de_otra_empresa_no_se_ve_ni_se_cambia` |
| Delegación | ¿Otorgar, aprobar o heredar lo que no tiene? ¿El beneficiario se aprueba? | Nadie autoriza lo que creó ni dos niveles; el superusuario no es autorizador si no está en el perfil; nadie cambia su propia pertenencia; con aprobación de seguridad, el revisor no es quien pidió ni una persona agregada o quitada; un nivel con personas que no pueden no se salta (espera) | IT › `El_creador_no_autoriza_y_nadie_autoriza_dos_niveles`; IT › `F06_quien_lo_manda_a_autorizar_no_lo_autoriza`; IT › `F01_desactivar_a_la_unica_persona_del_perfil_no_salta_el_nivel`; IT › `El_superusuario_fuera_del_perfil_no_autoriza`; IT › `Nadie_cambia_su_propia_pertenencia`; IT › `Con_aprobacion_de_seguridad_la_matriz_espera_a_otro_administrador`; IT › `Un_nivel_que_solo_tiene_al_creador_espera` |
| Concurrencia | Invariante; 10 a la vez; «abrir → guardar» | Invariante: una decisión por nivel y corrida; a lo más una corrida pendiente por registro (índice único filtrado). Decidir bloquea el documento y la corrida, y exige `rowVersion` y `approvalVersion` (huella de lo visto). Candado `erp:approval` para corridas y `approvers`; los cambios de personas recalculan bajo el mismo candado | IT › `Diez_personas_del_mismo_nivel_a_la_vez_cuenta_una`; IT › `Autorizar_mientras_se_quita_a_la_persona_da_un_orden_serial`; IT › `Una_decision_con_la_version_vieja_es_409_y_rechazar_pide_motivo`; R-053-1 |
| Fallas parciales | ¿Cae entre dos pasos? | Decisión, avance, transición `approve`/`reject`, sus automatizaciones, bitácora e historial van en una transacción: si algo falla no queda la decisión ni el avance | IT › `Si_falla_la_automatizacion_de_approve_no_queda_la_decision` |
| Límites | Tamaño, paginación, memoria, tasa | Topes de perfiles, reglas, personas, niveles, condiciones y valores (sección 1); comentario 1 000; un cambio de personas recalcula a lo más las corridas pendientes de ese perfil en la empresa (una consulta por corrida) | R-053-2 (topes validados en `ApprovalConfig`; sin prueba propia) |
| Semántica | Nulos, vacíos, monedas, fechas, redondeo | Monto vacío: una regla o un perfil **con** rango no aplica (desconocido); sin rango, sí. Condición vacía en la regla = cualquiera; un campo de condición vacío en el documento solo cumple «cualquiera». Monto en moneda base lo da el campo (decimal, sin redondeo extra). Vigencia por fecha del servidor (007), inclusiva | unit › `Vigencia_inclusiva_y_monto_vacio_no_entra_en_un_rango`; unit › `Un_nivel_se_salta_al_empezar_si_el_monto_no_cae_en_el_rango_del_perfil` (sin monto no se salta: F-10) |

Amenazas propias de la spec (S, T, R, I, D, E):

| Amenaza | Riesgo | Mitigación | Prueba o riesgo |
|---|---|---|---|
| **S** suplantación | Media | Quien decide es la sesión; el motor ejecuta `approve` con la persona como actor (bitácora e historial con su nombre) | IT › `La_bitacora_nombra_regla_nivel_persona_y_huella` |
| **T** autorizar algo distinto de lo que se vio | Alta | `rowVersion` + `approvalVersion` en la decisión; huella SHA-256 guardada; cambio de monto o condición reinicia | IT › `Una_decision_con_la_version_vieja_es_409_y_rechazar_pide_motivo`; IT › `Cambiar_el_monto_reinicia_la_autorizacion_y_la_edicion_directa_no_se_permite` (el estado no es editable: F-02) |
| **R** repudio | Media | `meta.ApprovalDecision` + `meta.AuditLog` (`approval.*`) con regla, nivel, perfil, persona, comentario y huella; la configuración con antes y después | IT › `La_bitacora_nombra_regla_nivel_persona_y_huella` |
| **I** ver documentos que no le tocan | Alta | Visibilidad por `approvers` con la 051 en todos los caminos; la lista se recalcula al cambiar personas | IT › `Solo_quien_le_toca_ve_el_pendiente_en_todos_los_caminos`; IT › `Dar_de_baja_quita_de_la_lista_al_momento_y_dar_de_alta_agrega` |
| **D** recálculos masivos | Baja | Recalcular solo las corridas pendientes del perfil; topes | R-053-2 |
| **E** saltarse la matriz | Alta | Transiciones `system`, plan sin atajos, `approvers` solo del motor, falla cerrado, nivel que espera en vez de saltarse, superusuario sin atajo | IT (Caminos alternos y Delegación) |
