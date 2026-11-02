# Tasks 049 — Endurecimiento 8

- [x] T1 · spec.md, plan.md (contrato y STRIDE de 8 filas), tasks.md; pregunta de O-02 al dueño
- [x] T2 · H1 (D-01): 503 al confirmar y descartar apagado; el trabajador no reclama y cierra `queued`/`waiting`; barrido y cierre sin reanudar; `LoopAsync` con `disabled`
- [x] T3 · H2 (D-02): `DiscardPendingAsync` en `FinishAsync` (todo cierre que no espera) y `ExpireAsync`; resultados de las acciones después de `Normalize`; texto del resultado «detenida» con su motivo
- [x] T4 · H3 (D-03): `MaxInputTokens` y `EstimateInput` en Studio y el Acompañante; `too_long` en el proveedor y sus mensajes
- [x] T5 · H4 (O-01): `ResumeAsync` con el sello de quien decide
- [x] T6 · H5 (O-03): `filledBy` y `buscar_en_el_sistema` con la visibilidad del campo de partidas
- [x] T7 · H6 (O-04): empresa al detener y borrar
- [x] T8 · Pruebas: `CompanionApagadoTests`, `CompanionTests.Hardening049.cs`, `McpTests › H049_O05…`, `Hardening8Tests`; contra `a1341b4`
- [x] T9 · H8 (O-02, opción (a) del dueño): `DerivedAccess.NamesHidden` y `ForUser` sin expresiones que nombran lo oculto
- [x] T9b · Revisión adversarial F-01 a F-05: automatizaciones y definición de partidas ocultas en la ruta, empresa antes de la caducidad, propuestas de turnos cerrados, conversación sin empresa
- [x] T10 · VERIFY: revisión adversarial, corrida completa, lo nuevo tres veces, verify.md, riesgos, roadmap, push
