# Spec 053 — Matriz de autorización: núcleo

**Estado:** Implementado · **Depende de:** 002, 006, 010, 012, 015, 021, 029, 034, 050, 051 · **Origen:** roadmap, «Specs planeadas
(después de la 049): piezas para procesos complejos», entrada 053 (revisión del dueño, 2026-11-02, de un proceso de compras real
que **no** se documenta). Cuarta pieza genérica: «quién autoriza lo configura el administrador», «niveles en orden; un nivel vacío
se salta», «solo ves el documento cuando te toca autorizarlo». Los ejemplos son inventados.

## Problema
Verificado en el código de `9f98e6b`.

| # | Hallazgo (verificado en el código) | Lo que pasa hoy |
|---|---|---|
| 1 | Quién ejecuta una transición son los `roles` fijos de `WorkflowTransition` (`RecordService.CanTransition`); el superusuario pasa siempre | Cambiar quién autoriza exige un Plan de Cambios (modelador); no hay «Gerente de compras solo hasta 50 000» ni «Director solo desde 100 000» |
| 2 | Los estados de `Workflow` son fijos: una autorización en tres niveles son tres estados y tres transiciones | No hay niveles que dependan del monto, del departamento o del tipo; un nivel sin personas no se salta |
| 3 | La segregación se escribe a mano en cada `when` (`NOT ISCREATOR() OR HASROLE('Director')` en Compras 1.12.0) | Nada impide que la misma persona autorice dos niveles, y el superusuario se la salta |
| 4 | La visibilidad por pertenencia (051) necesita una lista en el registro; nadie mantiene «quién puede autorizar ahora» | No hay forma de que el documento solo lo vea quien le toca |
| 5 | `meta.WorkflowHistory` guarda transiciones, no qué regla ni qué nivel ni qué vio la persona | No hay bitácora de autorización ni huella de lo autorizado |

## Principio
Una capacidad **genérica del motor** (constitución I.2, en SAP *estrategia de liberación*): el proceso **declara** que un estado se
autoriza por matriz (qué estado, a qué transición sale al completar, a cuál al rechazar, qué campos sirven de condición, cuál es el
monto y qué campo de varios valores lleva a quienes pueden autorizar ahora). **Quién** autoriza deja de ser metadato y pasa a ser
**configuración por empresa** que llena un administrador (perfiles con personas y rango de montos; reglas con condiciones,
prioridad, vigencia y niveles). El motor elige la regla al entrar al estado, la **congela**, calcula los niveles, mantiene la lista
de autorizadores (y con ella la visibilidad, por la pertenencia de la 051, en todos los caminos que aplican `RecordFilter`) y
ejecuta la transición de salida cuando se completa. Autorizar es una **ruta propia** con huella; las transiciones de salida son del
sistema. Se ejerce con metadatos en las pruebas (un catálogo de solicitudes de gasto inventado); el paquete de ejemplo completo es
la 057.

## Historias

**H1 — Declarar la autorización en el proceso.** Como *administrador del modelo*, quiero marcar un estado como «se autoriza por
matriz» para que quién autoriza lo decida la configuración de cada empresa y no el proceso.
- CA1: `workflow.approval: { state, approve, reject, amount?, conditions?, approvers }`. `state` es un estado no editable que no es el inicial;
  `approve` y `reject` son transiciones `system: true` que salen de `state` hacia otro estado; `amount` es una expresión numérica
  del documento (en moneda base: la escribe el modelador, p. ej. con `CONVERT`); `conditions`, hasta 5 campos propios de tipo
  referencia, opción, sí/no o persona; `approvers`, un campo `multiuser` no editable (`editable: false`).
- CA2: el plan rechaza con un mensaje para el modelador: catálogo que no es por empresa o es una partida; estado o transición
  inexistente; `approve`/`reject` sin `system` o que no salen de `state`; `amount` inválida o no numérica; condición que no es un
  campo propio de un tipo admitido; `approvers` que no es `multiuser` no editable; una asignación (`set` de cualquier transición,
  `update`/`create` de cualquier automatización) sobre `approvers`; una automatización o tarea (de cualquier catálogo) que ejecuta
  `approve` o `reject`; `state` editable; `when` o campos obligatorios en `approve`/`reject`; otra transición que llega al estado
  de `approve` (revisión adversarial F-02–F-04).
- CA3: el modelador muestra la declaración en las opciones del proceso (estado, transiciones, monto, condiciones, autorizadores) y
  la vista previa del plan la describe.

**H2 — Configurar la matriz.** Como *administrador de usuarios*, quiero dar de alta perfiles con sus personas y reglas con sus
niveles, en la empresa en la que trabajo, para cambiar quién autoriza sin tocar el proceso.
- CA1: pantalla «Autorizaciones» (`/admin/approvals`): **perfiles** (nombre, rango de montos opcional, activo, personas) y
  **reglas** por catálogo (nombre, prioridad, activa, vigencia opcional, rango de montos opcional, valores admitidos por cada
  campo de condición —vacío = cualquiera— y niveles en orden, cada uno un perfil).
- CA2: una persona de un perfil es un usuario activo, no de servicio ni equipo, con acceso a la empresa. Nadie (tampoco el
  superusuario) se agrega ni se quita a sí mismo de un perfil: «pídeselo a otro administrador».
- CA3: con aprobación de seguridad (spec 015), todo cambio a la matriz queda como solicitud que aprueba otro administrador (y no
  una persona que el cambio agrega o quita); sin ella, se aplica y queda en la bitácora con antes y después.
- CA4: guardar con la versión que se abrió (`rowVersion`): si otro la cambió, 409. Los perfiles y las reglas se desactivan, no se
  borran.

**H3 — Autorizar o rechazar.** Como *persona de un perfil*, quiero ver en el registro el avance por nivel y autorizar o rechazar
cuando me toca para que el documento siga su proceso.
- CA1: el registro muestra un panel «Autorización»: regla, niveles (perfil, estado: autorizado, en curso, pendiente o saltado y
  por qué), quién decidió, cuándo y su comentario, y quiénes pueden autorizar el nivel en curso.
- CA2: «Autorizar» (comentario opcional) y «Rechazar» (motivo obligatorio) solo aparecen a quien le toca. La decisión viaja con la
  versión del documento y de la autorización que vio la persona; si cambió, 409 «recarga».
- CA3: autorizar el último nivel ejecuta la transición `approve` (con su bitácora y automatizaciones); rechazar ejecuta `reject`
  con el motivo. Cada decisión guarda la **huella** (SHA-256) de lo que vio la persona.

**H4 — Solo quien le toca.** Como *administrador*, quiero que el motor calcule quién autoriza ahora y que solo esa persona vea el
documento pendiente para que nadie autorice fuera de turno.
- CA1: al entrar a `state` (por una persona o por una automatización) el motor elige la regla: activas del catálogo y de la
  empresa del documento, vigentes hoy, con el monto en su rango y cada condición cumplida; gana la de menor número de prioridad,
  luego la que fija más condiciones, luego la más antigua.
- CA2: un nivel cuyo perfil no incluye el monto en su rango se salta al empezar; uno cuyo perfil está inactivo o sin personas se salta **al llegar
  a él** (desactivar a una persona fuera de la matriz no vacía el nivel: espera, F-01). Si no aplica ninguna regla o todos los niveles quedan saltados, la transición que lo mandó a `state` se rechaza (400, nada
  escrito).
- CA3: puede autorizar el nivel en curso una persona activa del perfil (leída al momento) que tenga acceso a la empresa y vea el
  registro, que **no lo creó ni lo mandó a autorizar** (F-06) y que **no decidió otro nivel** de esta autorización. Si las personas del perfil existen pero
  ninguna puede (p. ej. solo el creador), el documento **espera**: el panel lo dice y nunca se salta.
- CA4: el campo `approvers` lleva, en todo momento, a quienes pueden autorizar ahora; un permiso por registro
  `CURRENTUSER() IN Autorizadores` (051) da la visibilidad en todos los caminos (lista, registro, buscador, reportes, exportación,
  OData, MCP, Acompañante, impresión) y una vista con el filtro «Autorizadores contiene yo» es la bandeja del catálogo. Dar de
  baja a una persona de un perfil la quita de las listas de los documentos en curso en la misma transacción.

**H5 — Congelar y reiniciar.** Como *administrador*, quiero que cambiar la matriz no altere los documentos en curso y que un
cambio en el documento obligue a volver a autorizar para que lo autorizado sea lo que se vio.
- CA1: la regla y sus niveles (perfil y rango) se congelan al empezar; editar o desactivar la regla no cambia los documentos en
  curso. Las personas de cada perfil se leen al momento (decisión 3).
- CA2: si mientras espera cambia el monto o un campo de condición (por cualquier camino: edición, transición, automatización), la
  autorización **se reinicia**: las decisiones anteriores quedan como «anuladas por cambio» y el motor vuelve a elegir regla con la
  matriz vigente (si ya no aplica ninguna, el cambio se rechaza).
- CA3: salir de `state` por otra transición (p. ej. «Devolver») cierra la autorización como cancelada y vacía `approvers`.

**H6 — Segregación y todos los caminos.** Como *auditor*, quiero que la matriz no tenga atajos para que una autorización
signifique lo mismo por cualquier camino.
- CA1: nadie ejecuta `approve` ni `reject` por la ruta de transiciones (ni el superusuario); ninguna automatización puede; la
  importación, el formulario y la API no escriben `approvers` ni el estado.
- CA2: dos personas del mismo nivel que deciden a la vez: cuenta **una** decisión y la otra recibe 409; diez a la vez, igual.
- CA3: cada evento queda en la bitácora (`approval.start`, `approval.approve`, `approval.reject`, `approval.reset`,
  `approval.cancel`, con regla, nivel, perfil, persona, comentario y huella) y en el historial del registro.

## Decisiones (CLARIFY)
1. **¿Dividir la spec?** Criterios: más de 6 historias (la entrada del roadmap, ~8: sí) · más de ~2 500 líneas de producto (sí) ·
   seguridad o concurrencia junto con UI nueva (sí) · más de una pantalla principal (matriz y bandeja: sí).
   → **Se divide** (dueño, 2026-11-02): la **053** es el motor, la declaración, la pantalla de la matriz y el panel del registro
   (6 historias, ~2 100 líneas). La bandeja de cada catálogo sale de la pertenencia de la 051 (una vista «Autorizadores contiene
   yo»). Pasan a la **055**: bandeja global entre catálogos, exportar la bitácora de autorizaciones, el Acompañante proponiendo
   autorizar y condiciones con una expresión libre.
2. **Superusuario** (dueño): solo autoriza quien está en el perfil, también el superusuario; si quiere autorizar, otro
   administrador lo agrega.
3. **Personas vivas** (dueño): se congelan la regla y sus niveles; las personas de cada perfil se leen al momento. Una baja quita
   la autorización de inmediato; una alta la da en los documentos que esperan ese nivel. Una baja nunca hace avanzar un documento:
   «nivel vacío se salta» se decide al llegar a él.
4. **Falla cerrado** (dueño): sin regla aplicable o con todos los niveles saltados, la transición que lo manda a autorizar se
   rechaza; el documento no avanza solo.
5. **Rango del perfil:** un nivel cuyo perfil tiene rango y el monto no cae en él se salta (así «Director solo desde 100 000»).
   Sin `amount` en la declaración, los rangos no aplican.
6. **Quién administra:** quien administra usuarios (o el superusuario), en su empresa actual; todo cambio a la matriz pasa por la
   compuerta de seguridad (015) porque casi cualquier cambio puede aflojar el control (quitar personas, angostar un rango o
   desactivar un perfil hacen que un nivel se salte).
7. **El catálogo es por empresa** (`perCompany`): la matriz es por empresa y el documento ya sabe la suya.
8. **Sin cambio a Compras:** cambiar la requisición u orden de Compras rompe el flujo que usan muchos archivos E2E; lo ejercen
   un catálogo inventado en integración y E2E, y la 057 arma el paquete de ejemplo con la matriz.

## Fuera de alcance (anotado en el roadmap)
- **055** (junto con lo que ya tenía): bandeja global entre catálogos, exportar la bitácora de autorizaciones, el Acompañante
  proponiendo autorizar (hoy no tiene ruta para hacerlo) y condiciones con una expresión libre.
- Todos, N de M, en paralelo, autorizador por relación, delegación con fechas, regresar para corrección (055); avisos, plazos,
  límites acumulados, rechazo parcial, tolerancia y simulador (056).
