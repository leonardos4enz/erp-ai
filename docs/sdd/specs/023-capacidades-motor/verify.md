# Verificación 023 — Capacidades del motor: merge, reglas de aviso y rutas de tres niveles

**Fecha:** 2026-10-24 · **Resultado:** Aprobada

| Compuerta | Resultado |
|---|---|
| Unitarias | 522/522 en tres corridas (+37: `EngineCapabilitiesTests.cs`, `EngineCapabilities2Tests.cs`; ajustadas a Inventario 1.7.0 `AccountingTests` y `CostingPolicyTests`; `ExpressionTests` ahora rechaza una ruta de 5 segmentos, no `a.b.c`, que es válida por diseño) |
| E2E | 314/314 en dos corridas completas seguidas (+14 en `valuacion-rutas-avisos.spec.ts`; `valuacion-politicas` H8 espera Inventario 1.7.0). La primera corrida falló solo por F-01 (abajo) |
| Contra el código anterior | Las regresiones se corrieron contra `HEAD` (8673286) en un *worktree* aparte: fallan por la razón esperada (abajo) |
| Visual | 4 referencias nuevas (aviso del formulario y diálogo que pide confirmarlo, claro y oscuro), revisadas una por una; las de `visual.spec.ts` no cambiaron |
| Accesibilidad | axe sin violaciones serias en el formulario con avisos, en el diálogo con avisos y en el diseñador del proceso; pasada solo con teclado (abajo) |

## Cobertura
| Criterio | Prueba |
|---|---|
| H1 CA1 `merge` en vistas, reportes, webhooks, tareas | `H1_CA1_merge_reemplaza_en_su_lugar…`, `H1_CA1_setViews_reports_webhooks_y_schedules_con_merge…`; E2E `H1 CA1 · CA4` (vista agregada a Recepción, «Pólizas pendientes» se conserva, vista previa «Agregar o actualizar») |
| H1 CA2 vista previa | `H1_CA2_merge_de_reglas_no_se_reporta_como_quitar`, mismo para vistas en `H1_CA1_setViews…` |
| H1 CA3 Entornos | `H1_CA3_paquete_que_extiende_un_catalogo_ajeno_llega_al_destino`, `H1_CA3_revertir_un_paquete_que_extiende…` |
| H1 CA4 módulos | `H1_CA4_ningun_modulo_reemplaza_listas_de_catalogos_ajenos`; CLARIFY: `H1_inventario_no_se_instala_sobre_compras_anterior` |
| Forma canónica de paquetes | `H1_opciones_nuevas_omitidas_no_cambian_la_forma_canonica…` |
| H2 CA1 gravedad | `H2_CA1_gravedad_y_politica_invalidas_se_rechazan`; E2E `H2 CA1 · CA2 · CA4` (un error sigue bloqueando) |
| H2 CA2 dónde se ven | E2E `H2 CA1 · CA2 · CA4` (`_warnings` al crear y abrir, `/compute`), `H2 CA2` importación, `H2 CA2 · CA3 (UI)` en vivo y al guardar |
| H2 CA3 `onWarning` | E2E `H2 CA3` (428 con los avisos, aceptar otro mensaje no basta, `block` 400, `allow` sigue, sin avisos no pregunta) y `H2 CA2 · CA3 (UI)` (diálogo, «Continuar con avisos», solo teclado) |
| H2 CA4 auditoría | E2E `H2 CA1 · CA2 · CA4` (`create`), `H2 CA3` (`workflow.confirmar`, `workflow.confirmarDirecto`), `H2 CA6` |
| H2 CA5 avisos ocultos | `H2_CA5_un_aviso_que_lee_un_campo_oculto_no_se_muestra` |
| H2 CA6 caso 7 «avisar» | E2E `H2 CA6` (`PolizaConCuentas` en `warning`: 428, aceptar, Confirmada con *Póliza pendiente*, aviso en `_warnings` y en la auditoría; se restaura a error) |
| Modelador | E2E `H2 (modelador)` («Si hay avisos» con sus tres opciones; pista de `severity` en el JSON de reglas); `H2_regla_que_pasa_a_aviso…` (vista previa) |
| H3 CA1 rutas | `H3_CA1_el_parser…`, `…el_evaluador…`, `…rutas_validas…`, `…rutas_invalidas…`, `…SUM_sigue…`; E2E `H3 CA1` (fórmula de 3 saltos desde la partida con el documento en memoria, en vivo; el modelador rechaza ruta inexistente y de 5 segmentos) |
| H3 CA2 SQL | `H3_CA2_el_filtro_en_sql_anida…`; E2E `H3 CA2` (acumulado con filtro de 3 saltos: solo lo confirmado a US) |
| H3 CA3 seguridad | `H3_CA3_un_campo_oculto_en_el_segundo_o_tercer_salto…` |
| H3 CA4 Inventario 1.7.0 | `H3_CA4_recepcion_sin_moneda_usa_la_moneda_de_la_orden`; E2E `H4` |
| H4 actualización | `Inventario_1_5_llega…`, `Inventario_1_6_y_Costeo_1_1…` (cadena hasta 1.7.0); E2E `H4` y `valuacion-politicas` H8 |

## Contra el código anterior
| Prueba | Falla con `HEAD` |
|---|---|
| `H1_CA2_merge_de_reglas_no_se_reporta_como_quitar` | la vista previa decía «Quita reglas existentes» con `merge` |
| `H1_CA3_paquete_que_extiende…` | el diff no traía el `addField` de Proveedor (se descartaba) |
| `H1_CA3_revertir…` | sin el campo en el destino, no hay nada que revertir |
| `H1_CA4_ningun_modulo_reemplaza…` | `costeo 1.1.0: SetViewsOp Recepcion (de compras)` |
| `H3_CA4_recepcion_sin_moneda…` | *Expected 9, Actual null* con la fórmula de Inventario 1.6.1 |
| E2E `H2 CA2 · CA3 (UI)` | F-01: contraste del botón «Eliminar» (axe) |

## Hallazgos
| ID | Severidad | Hallazgo | Resolución |
|---|---|---|---|
| F-01 | Media | El botón «Eliminar» del pie del formulario (`--danger` #dc2626 sobre `--surface-2`) no llega a contraste AA (4.3:1). Previo a esta spec: ninguna prueba de accesibilidad abría un formulario editable con permiso de eliminar | `--danger-text` (AA en claro y oscuro). Regresión: el axe de `H2 CA2 · CA3 (UI)` |
| F-02 | Baja (dato) | El riesgo residual de la 022 sobre recepciones sin `Moneda` era más chico de lo descrito: al guardar o confirmar desde la UI o la API, el valor por defecto (`OrdenCompra.MonedaClave`) llena `Moneda` antes de calcular. Solo un recálculo del motor (sin valores por defecto) usaba el respaldo | Inventario 1.7.0 cubre también ese camino; la regresión es unitaria (en E2E el valor por defecto lo enmascara) |

## Revisión exploratoria
Servidor aparte (:5096) sobre la base desechable `ErpAi_E2E` después de la corrida verde (nunca `ErpAi`), con el superusuario
de prueba de la suite.
- **Solo teclado, oscuro, 800 px:** alta de un pedido de ruta: cliente y artículo con el buscador (escribir, flecha, Enter),
  «Agregar partida» deja el foco en el artículo, Tab a cantidad y precio. Con 12 piezas a 100 aparecen en vivo los dos avisos
  («Total: …» y «Partida 1 · Cantidad: …») y «País de destino: US» (tres saltos). Enter guarda; el registro abre con los avisos.
- «Confirmar» → Tab → Enter: el diálogo se queda abierto, anuncia «Confirma los avisos para continuar» con los dos avisos y el
  foco sigue en el botón, que ahora dice «Continuar con avisos»; Enter confirma. «Cerrar» (`block`) muestra los avisos como
  error anunciado; Esc cierra y devuelve el foco al botón.
- **Modelador:** pestaña Proceso, transición «Cerrar» con teclado: «Si hay avisos» = «No ejecutarla», con su pista; cabe en
  una columna.
- **Móvil 375 px, claro:** el aviso del registro confirmado se lee completo, sin desplazamiento horizontal.
- Consola: solo los 4xx esperados (sesión antes de entrar, 428 y 400 del bloqueo).

## Riesgos residuales
- Quitar un elemento con `merge` no existe: se hace con el reemplazo completo, que la vista previa del asistente lista.
- El dueño de un catálogo que otros extienden debe usar `merge` en sus actualizaciones (convención; la prueba de paquetes solo
  revisa listas de catálogos ajenos).
- Un aviso cuya regla lee un campo oculto no se muestra ni se pide confirmar a ese usuario (se audita igual).
- `block` muestra los avisos como errores sin decir que son avisos; es consistente con el resto de los rechazos.
- Los saltos 2 y 3 de una ruta leen el registro al que apunta el anterior sin revisar su alcance por registro (como la
  etiqueta de una referencia); la seguridad por campo sí se revisa en cada salto.
