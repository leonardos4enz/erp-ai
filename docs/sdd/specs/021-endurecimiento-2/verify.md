# Verificación 021 — Endurecimiento 2

**Fecha:** 2026-10-24 · **Resultado:** Aprobada

| Compuerta | Resultado |
|---|---|
| Unitarias | 467/467 (+9 en `Hardening2Tests.cs`: capacidades, versión y huella base, huella del asistente, huella de una tarea, avisos de seguridad, confirmación de seguridad y tope de `$skip`) |
| E2E | 289/289 en dos corridas completas limpias seguidas tras la última corrección (+18 en `endurecimiento-2.spec.ts` con una segunda instancia en :5094 con aprobación de seguridad, +1 en `oauth-odata.spec.ts`, aserción nueva en `valuacion-contable.spec.ts`; antes, 288/288 dos veces sin F-01). Ajustes: `helpers.applyPlan` revisa y aplica con la huella; `editores.spec.ts` confirma el descarte y el aviso nuevo del diagrama; `oauth-odata.spec.ts` filtra la auditoría por acción (los tokens ahora también se auditan) |
| Contra el código anterior | Las 15 pruebas de `endurecimiento-2.spec.ts`, la de OData/tokens y la de la póliza automática se corrieron contra `HEAD` (worktree aparte con las pruebas nuevas, `E2E_INDEPENDENT=1` para que una falla no salte las siguientes) y fallaron todas por la razón esperada; ver abajo |
| Visual | Sin cambios en las referencias (las corridas no reescribieron ninguna captura): los avisos nuevos solo aparecen con cambios pendientes, reversiones con seguridad o errores |
| Accesibilidad | axe sin violaciones serias en el diálogo de usuario con el error de H1 (F-01); el diálogo de descarte enfoca «Seguir editando» y se cierra con Esc |

## Cobertura
| Criterio | Prueba |
|---|---|
| H1 CA1 superusuario solo por superusuario | `H1 CA1` (asignarse, dar de alta y quitar superusuario: 403; lo que sí tiene, 200) y `Solo_se_otorga_o_quita_lo_que_se_tiene` |
| H1 CA2 modelo y auditoría | `H1 CA2` (dar y quitar: 403; el superusuario sí) |
| H1 CA3 roles | `H1 CA3` (ganar capacidades, crear con auditoría, editar el rol con modelo o el superusuario: 403; describir uno sin capacidades: 200) |
| H1 CA4 aprobador | `H1 CA4` (instancia con aprobación de seguridad: `canApprove = false`, 403 y la solicitud sigue pendiente; otro superusuario la aprueba) |
| H1 CA5 beneficiario | `H1 CA5` (el propio usuario no aprueba; otro administrador sí) |
| H2 CA1 versión al abrir | `H2 CA1 · CA2` (API: vista previa inválida con «cambió desde que lo abriste») y `H2 CA1 (UI)` (editor abierto, cambio ajeno, «Aplicar cambios» deshabilitado) |
| H2 CA2 `baseHash` | `H2 CA1 · CA2` (400 sin huella, 409 con huella vieja, el layout ajeno se conserva); `helpers.applyPlan` ahora revisa y aplica como el modelador; unitaria `La_huella_base_cubre_solo_lo_que_el_plan_toca` |
| H2 CA3 asistente | `La_huella_del_asistente_cambia_si_cambia_la_base…`; `asistente.spec.ts` sigue aplicando propuestas con la huella nueva |
| H3 CA1 éxito en la transacción | `H3 CA1` (marcar el éxito falla: el trabajo queda fallido y sin efectos) |
| H3 CA2 dueño del lease | Todas las escrituras de la bandeja filtran por `LeaseToken` (revisión de código); `H3 CA4` y `programadas.spec.ts` |
| H3 CA3 renovación | Revisión de código (`WithLeaseAsync`, `RenewInAsync` por registro); `programadas.spec.ts` |
| H3 CA4 tope de intentos | `H3 CA4` (lease vencido con 3 de 3 intentos: `failed`, «se interrumpió», sin segunda ejecución) |
| H4 CA1 · CA2 pausa por contenido | `H4 CA1 · CA2` (la etiqueta no pausa; la consulta sí, con auditoría; quitar y volver a declarar llega en pausa) y `La_huella_de_una_tarea_cubre_lo_que_hace…` |
| H4 CA3 ocurrencia encolada | `H4 CA3` (huella distinta: `canceled`, «cambió») |
| H4 CA4 tareas anteriores | Revisión de código (`PauseChangedAsync` adopta la huella del modelo anterior) |
| H5 CA1 · CA2 reversión | `H5 CA1 · CA2` (`securityChanges`, 409 sin `confirmSecurity` al aprobar y al aplicar, casilla en *Entornos*) y `Aprobar_o_aplicar_una_reversion_que_cambia_seguridad…`, `Los_avisos_de_seguridad_de_un_plan…` |
| H6 CA1 póliza automática | `valuacion-contable.spec.ts` `H3 · H5 CA1 · CA3` (cancelar la automática aplicada: 400 con el mensaje; saldos intactos); `H4 CA3` sigue cancelando una manual |
| H7 CA1 · CA2 OData | `oauth-odata.spec.ts` `Spec 021 H7 …` (`$skip` 100 001 → 400, 100 000 → 200; 11 partidas con tope de 10 → 400) y `Skip_tiene_tope` |
| H8 CA1 `applying` atorado | `H8 CA1` |
| H8 CA2 token auditado | `oauth-odata.spec.ts` `Spec 021 … H8 CA2` |
| H8 CA3 estado con registros | `H8 CA3` (vista previa inválida con el conteo); `editores.spec.ts` actualiza el aviso del diagrama |
| H8 CA4 cambios sin aplicar | `H8 CA4` (`beforeunload` cancelado, «Seguir editando» conserva el borrador, «Descartar cambios» sale) |

## Contra el código anterior
| Prueba | Falla con `HEAD` |
|---|---|
| H1 CA1 / CA2 / CA3 | *Expected: 403, Received: 200* |
| H1 CA4 / CA5 | *Expected: false, Received: true* (`canApprove`) |
| H8 CA1 | la solicitud en `applying` no aparece como pendiente |
| H2 CA1 · CA2 | `version` indefinida |
| H2 CA1 (UI) | «El plan es válido» en lugar del aviso |
| H8 CA4 | `beforeunload` no se cancela |
| H8 CA3 | *Expected: false, Received: true* (`valid`) |
| H3 CA4 | *Expected: "failed", Received: "succeeded"* (se ejecutó por cuarta vez) |
| H3 CA1 | *Expected: 0, Received: 1* (fallido pero con efectos) |
| H4 CA1 · CA2 | *Expected: false, Received: true* (sigue activa) |
| H4 CA3 | no existe `ScheduleState.ContentHash` |
| H5 CA1 · CA2 | `securityChanges` indefinido |
| OData / token | `total` de `integration.oauthtoken` = 0 |
| Póliza automática | *Expected: 400, Received: 200* |
| F-01 (con la `SecurityPage.tsx` anterior) | *toBeInViewport() failed* |

## Hallazgos
| ID | Sev. | Hallazgo | Estado |
|---|---|---|---|
| F-01 | Media | Como administrador de usuarios, marcar «Administrador» y pulsar *Guardar* no parecía hacer nada: el 403 llegaba con su mensaje, pero el aviso quedaba arriba del diálogo, fuera de la vista (el diálogo estaba desplazado hasta el pie) | Corregido en `SecurityPage` (genérico para los diálogos de usuario, rol, equipo y unidad): el error se trae a la vista. Regresión `H1 CA1 (UI)` |

Durante la implementación: la primera simulación de H3 CA1 usaba un *trigger* en `meta.AutomationRun`, que rompe el reclamo
de la bandeja (`UPDATE … OUTPUT` sin `INTO` no admite triggers): se cambió por una restricción `CHECK`. Queda como lección
para pruebas futuras de la bandeja.

## Revisión exploratoria (navegador integrado)
- **Instancia temporal** en `localhost:5098` sobre `ErpAi_E2E`, preparada por la segunda corrida completa con una contraseña
  de administrador generada para la sesión (guardada fuera del repositorio), con `RequireApproval` y `AllowDirectChanges`.
- *Administrador de usuarios* (e2e-e21-usuarios), tema oscuro, 1280 px: editar a otro usuario, marcar «Administrador» y
  guardar → 403 con «Solo un administrador del sistema puede otorgar o quitar el rol «Administrador».» (detectado F-01:
  el aviso no se veía). El usuario conserva sus roles.
- *Administrador*, dos pestañas sobre *Visitas*: la segunda cambia la descripción (con ñ) y aplica; la primera, en el
  diseñador del formulario, renombra una sección y revisa → «El plan tiene errores» y «‹Visita› cambió desde que lo
  abriste…»; *Aplicar cambios* deshabilitado; Esc cierra. *Cancelar* con cambios abre «¿Descartar los cambios?» con el
  foco en «Seguir editando»; Esc lo cierra y el borrador sigue.
- 375 px: el diálogo de la reversión aplicada de *Revisiones 021* cabe sin desbordar (0 px de scroll horizontal).
- Consola: solo los 401 previos al inicio de sesión y el 403 esperado; sin errores de JavaScript ni de CSP.

## Riesgo residual
- Si el catálogo cambió mientras se editaba, el borrador no se fusiona: hay que volver a abrirlo y repetir el cambio (el
  aviso lo dice). Fusionar layouts y procesos es otra spec.
- La comprobación de capacidades al aprobar mira los roles agregados o quitados; un rol que gana capacidades **después** de
  solicitarse un cambio de usuario se compara al aprobar contra su estado de ese momento (la operación vuelve a correr la
  regla con el aprobador, así que no hay escalamiento, solo un mensaje distinto del de la lista).
- Un trabajo asíncrono que tarde más de 5 minutos sin poder renovar su lease (base caída) pierde su transacción y se
  reintenta: sus efectos no se duplican, pero se repite el trabajo.
- Cambiar el calendario de una tarea activa ahora la pausa (antes la reagendaba): el superusuario debe volver a activarla.
- Pendientes de la spec: costeo 2, `ForwardedHeaders` para el límite de `/oauth/token` detrás de un proxy, aviso al navegar
  dentro de la SPA, confirmar seguridad también en paquetes normales.
