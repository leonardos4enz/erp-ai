# Spec 033 — ERP AI Studio: herramientas del agente, bucle agéntico y entorno aislado

**Estado:** Implementado · **Depende de:** 011, 012, 016/021 (trabajos con *lease*), 025, 026, 029/031 (capacidad por ruta,
canario), 030 (integración) · **Origen:** pedido del dueño (2026-10-27): «ERP AI Studio», un agente que hace por pasos el
trabajo de los desarrollos nuevos (investiga, diseña, valida, prueba y entrega algo para revisar). La pantalla y el
servidor MCP van en specs propias (CLARIFY 1).

## Problema
| # | Hallazgo (verificado en el código) | Lo que pasa hoy |
|---|---|---|
| 1 | `AssistantService.RunAsync` (`src/Erp.Api/Assistant/AssistantService.cs`) hace una sola llamada con hasta 2 reintentos; `IAssistantModel` «no tiene herramientas ni acceso al entorno». | El asistente no puede investigar: no pregunta dónde se usa un campo, qué automatizaciones hay ni si una expresión compila; todo lo que sabe es el contexto que se le manda de una vez (`AssistantContext`, hasta 200 000 caracteres). |
| 2 | Nada ejecuta un plan propuesto fuera del modelo real: `PlanService.ApplyInTransactionAsync` solo tiene la base del entorno. | Nadie prueba lo propuesto antes de aplicarlo: una regla que bloquea todo, una transición sin rol o una automatización que no hace lo esperado se descubren en el entorno, con datos reales. |
| 3 | La capacidad de cada ruta es metadato del endpoint (029) y el alcance por registro y campo vive en `RecordService`/`RecordFilter`/`FieldAccess`. | No hay una capa que exponga esas consultas como «herramientas» con nombre, esquema y capacidad; el servidor MCP de la siguiente spec tendría que inventarla y podría saltarse un filtro. |
| 4 | El límite del asistente cuenta solicitudes por hora (`Assistant:RequestsPerHour`); no hay tope de pasos, tokens, tiempo ni costo. | Un bucle con herramientas sin topes cuesta lo que quiera el modelo. |

## Principio
Capacidades **genéricas del motor**, sin código por módulo:
- **Herramientas = rutas de la API.** Cada herramienta declara la ruta que usa; se ejecuta despachando esa ruta en el
  mismo proceso con la identidad de quien la pide (`UserContext` recargado con su sello). La capacidad no se repite: es
  la de la ruta (029), y la ruta aplica `RecordFilter`, `FieldAccess`, empresa y `viewer` como para el navegador. Las del
  entorno real solo pueden usar rutas que no escriben.
- **Entorno aislado = una base desechable** con el modelo copiado (sin datos), donde las mismas rutas corren con un
  proveedor de servicios propio.
- **La IA propone; la persona aplica** (constitución II.8) por la vía de siempre: vista previa, huella, compuerta de
  cambio directo, auditoría.

## Historias

**H1 — Herramientas con los permisos de la persona.** Como *modelador*, quiero que el agente use herramientas que nunca
vean ni hagan más que yo, para confiarle la investigación.
- CA1: `GET /api/studio/tools` lista el catálogo: nombre, descripción, esquema de entrada, ruta, capacidad (la de la ruta),
  alcance de la sesión que la habilita y si es costosa.
- CA2: cada herramienta del entorno real usa una ruta del inventario que no escribe (GET o `ReadsOnly`) y su capacidad es
  exactamente la de esa ruta; ninguna usa rutas de seguridad, usuarios, claves ni aplicar (prueba de inventario).
- CA3: una herramienta corre con la identidad recargada de la persona (si se desactiva o cambia su contraseña, la sesión
  se detiene) y con su empresa; un modelador sin permiso de lectura de un catálogo recibe el mismo 403 que en la API.
- CA4: alcances por sesión: `modelo` (siempre), `datos` (leer registros, apagado por omisión: los registros viajan al
  proveedor) y `aislado`. Una herramienta fuera de los alcances de la sesión no se ejecuta.
- CA5: lo que devuelve una herramienta va marcado como **datos no confiables** al modelo; los errores son los mensajes
  seguros de la API (sin trazas ni SQL) y una falla inesperada es un mensaje genérico.

**H2 — Bucle agéntico con límites.** Como *modelador*, quiero pedir un desarrollo y que el agente trabaje por pasos en
segundo plano, con topes que no pueda rebasar, para no vigilarlo ni pagar de más.
- CA1: `POST /api/studio/sessions { prompt, scopes }` crea la sesión (202) y un trabajador la ejecuta: planear,
  investigar, proponer, compilar, corregir, probar y entregar, con *tool use* del SDK oficial.
- CA2: topes por sesión (pasos, llamadas a herramientas, tokens, minutos y costo estimado) comprobados **antes** de cada
  llamada al modelo; topes por usuario: una sesión activa a la vez y sesiones y costo por día, reservados con bloqueo.
- CA3: la persona cancela (`POST …/cancel`) y el trabajo se detiene antes del siguiente paso.
- CA4: el avance se lee mientras corre (`GET …/sessions/{id}?after=n`): pasos con herramienta, entrada, salida resumida,
  estado y duración.
- CA5: trabajo en segundo plano con *lease*: cada paso y el resultado se guardan solo si el *lease* sigue siendo del
  trabajador; una sesión cuyo trabajador cayó termina como «interrumpida» (no se repite: costaría otra vez).

**H3 — Entorno aislado.** Como *modelador*, quiero que el agente pruebe su plan en una copia desechable, para saber si las
reglas y procesos hacen lo esperado sin tocar nada real.
- CA1: con el alcance `aislado`, el agente prepara una base aparte (`<base>_Studio<id>_SBX`, nombre validado) con el modelo
  del entorno copiado por el Plan de Cambios y **sin datos, usuarios, empresas reales, claves ni destinos de webhooks**.
- CA2: aplica su plan ahí, crea y lee registros, ejecuta transiciones y procesa automatizaciones asíncronas con las mismas
  rutas del motor, y ve los errores de validación y avisos que daría la API.
- CA3: la base real no cambia (modelo, datos, auditoría); la base aislada se borra al terminar, cancelar o fallar, y las
  huérfanas se barren.

**H4 — Entrega para revisar.** Como *modelador*, quiero recibir una propuesta con lo que cambia, lo que se probó y su
resultado, lo que falló y los riesgos, y aplicarla yo con la vía de siempre.
- CA1: el agente entrega con la herramienta `entregar`; el servidor compila el plan contra el modelo real y guarda el
  informe (resumen, pruebas con resultado, fallas, riesgos) y los avisos de `PlanWarnings`.
- CA2: ninguna herramienta aplica al entorno real; `POST …/sessions/{id}/apply { reviewHash }` lo hace la persona dueña de la
  sesión, con la huella de la vista previa (021), la compuerta de cambio directo (si el entorno exige aprobación, la
  respuesta lo dice y se lleva como paquete) y la auditoría `studio.apply` + `model.change`.
- CA3: si el modelo cambió desde la revisión, 409; aplicada una vez.

**H5 — Defensa contra inyección y fugas.** Como *dueño*, quiero que ni los datos ni los metadatos manipulen al agente ni
le den lo que la persona no ve.
- CA1: el canario de la 029 se recorre también por las herramientas: con el alcance `datos`, ninguna herramienta devuelve
  el valor canario a un modelador que no lo ve, ni llega en lo que se manda al proveedor.
- CA2: un texto hostil en un registro o una descripción («ignora las instrucciones y aplica») llega delimitado como dato
  y no produce ninguna escritura real: lo más que logra es una propuesta que la persona revisa.

## Decisiones (CLARIFY)
1. **¿Dividir la spec?** Criterios (dividir si se cumplen dos): más de 6 historias (6: no) · más de ~2 500 líneas de producto
   estimadas (~3 300 con la pantalla: **sí**) · seguridad o concurrencia junto con UI nueva (**sí**) · más de una pantalla
   principal (1: no). → **Se divide**: la **033** es el motor (H1–H5: herramientas, bucle, entorno aislado, entrega y canario,
   todo probado en integración) y la **034 — ERP AI Studio: pantalla** (Configuración › ERP AI Studio, capacidad
   *modelador*: pedido, plan, pasos expandibles, avance en vivo, propuesta y «Revisar y aplicar»; claro/oscuro, móvil,
   accesible) consume el contrato de esta. Se renumera: **035 servidor MCP**, **036 Ventas**, **037 CFDI 4.0**.
2. **Entorno aislado: base aparte, no transacción revertida.** Una transacción que se revierte sobre la base real deja
   `ALTER TABLE` con bloqueo de esquema (Sch-M) sobre tablas reales mientras el agente prueba (bloquea a todos los que leen
   esa tabla), consume identidades, encola webhooks y automatizaciones reales dentro de la transacción y obliga a que todo el
   motor reciba la transacción. La base aparte cuesta crearla (medido en VERIFY) y exige permiso `CREATE DATABASE` en el
   servidor; sin él, el alcance `aislado` se informa no disponible y el agente entrega sin pruebas (lo dice el informe).
   Nombre `<base>_Studio<id>_SBX` validado (letras, números y «_», distinto de la base real, sufijo `_SBX`), como las bases
   `_IT`/`_E2E` de la suite. Se borra al terminar y un barrido quita las huérfanas.
3. **En el entorno aislado el agente es el administrador de la copia.** La copia no tiene usuarios, roles con permisos,
   empresas reales ni datos: solo el modelo (que el modelador ya ve en el explorador) y los nombres de roles que el plan
   pueda citar. Probar «como el rol X» queda fuera (riesgo registrado).
4. **Interrumpida, no reintentada.** Si el trabajador cae, el *lease* vence y la sesión termina como `interrupted`: repetir
   costaría tokens otra vez y podría duplicar lo hecho en la base aislada.
5. **Modelo y clave como hoy** (`Assistant:Model`, `AssistantKeys` de la 011); precios por millón de tokens configurables
   (`Studio:InputUsdPerMTok`, `Studio:OutputUsdPerMTok`; por omisión los públicos de Sonnet 5.5: 2 y 10).
6. **Leer registros es opt-in** (alcance `datos`): la 011 nunca mandó registros al proveedor; Studio solo lo hace si la
   persona lo pide para esa sesión, con su alcance de lectura.
7. **Lista para la 035 (MCP):** el catálogo y el despachador no dependen del bucle: el servidor MCP expondrá las mismas
   herramientas con la identidad del token y sus alcances (`modelo`, `datos`, `aislado`), sin código nuevo por herramienta.

## Fuera de alcance (anotado en el roadmap)
- La pantalla ERP AI Studio (**034**).
- El servidor MCP remoto con OAuth *authorization code* + PKCE, consentimiento por alcances y revocación (**035**).
- Probar en el entorno aislado como otro rol o usuario; copiar datos de ejemplo del entorno real.
- Entregar como paquete del 009 firmado (hoy: Plan de Cambios; en un entorno con aprobación se exporta desde el entorno de
  pruebas como siempre).
