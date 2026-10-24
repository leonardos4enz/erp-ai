# Spec 011 — Asistente IA (Claude)

**Estado:** Implementado · **Depende de:** 001–010 · **Origen:** [roadmap](../../roadmap.md#011--asistente-ia)

## Problema
1. Todo el lenguaje de metadatos (catálogos, procesos, reglas, automatizaciones, seguridad, layouts, vistas, reportes,
   tableros, webhooks, catálogos por empresa, fórmulas con tipos de cambio) se escribe a mano: en el modelador, campo
   por campo, o como JSON de un Plan de Cambios. Quien conoce el negocio no siempre conoce el lenguaje.
2. La constitución (I.3, II.8) ya prevé un asistente: **humanos, UI e IA usan la misma vía** y la IA solo **propone**
   planes que un humano autorizado confirma. Falta construirlo sin abrir una puerta nueva al sistema.
3. Un LLM es un componente no confiable: su salida puede ser inválida, excesiva o manipulada (inyección de
   instrucciones en el texto del usuario o en los propios metadatos que se le envían). Además, cada petición sale del
   entorno hacia un tercero: no debe llevar datos de negocio ni secretos.

## Historias

**H1 — Pedir un cambio en lenguaje natural.** Como *modelador*, quiero describir en español lo que necesito y recibir
una propuesta de Plan de Cambios lista para revisar.
- CA1: En *Configuración › Asistente IA* escribo una solicitud (p. ej. «agrega un catálogo de Vehículos con placa
  única, marca y año, y un proceso de alta con autorización») y recibo una **propuesta**: explicación en lenguaje claro
  y el plan con la **misma vista previa** del modelador (pasos, SQL, errores), compilada contra este entorno.
- CA2: La propuesta puede usar cualquier metadato del motor: catálogos y campos, procesos, reglas, automatizaciones,
  seguridad por campo y registro, layouts, vistas, reportes, tableros, webhooks, `perCompany` y fórmulas (incluidas
  `RATE`, `CONVERT`, `BASECURRENCY()`).
- CA3: Si el plan no compila, el asistente recibe los errores del compilador y lo corrige solo (un número limitado de
  intentos); si aun así no compila, veo la propuesta con sus errores y no se puede aplicar.
- CA4: Si la solicitud no se puede expresar con metadatos (p. ej. «da de alta una empresa», «crea un usuario», «sube
  el tipo de cambio») el asistente lo explica y no propone plan.
- CA5: Puedo **refinar** la propuesta con otra solicitud en la misma conversación («mejor que la placa no sea
  obligatoria») y empezar una conversación nueva.

**H2 — Confirmar o descartar.** Como *modelador*, quiero decidir yo qué se aplica.
- CA1: Nada se aplica sin que yo pulse «Aplicar» y confirme en un diálogo que resume lo que va a cambiar. Se aplica
  **exactamente** el plan que vi (guardado en el servidor), por el mismo `PlanCompiler`/`PlanService` del modelador, en
  una transacción, con mis permisos y con la auditoría de siempre (`model.change`).
- CA2: La propuesta destaca lo que merece atención: archivar catálogos o campos, cambios de seguridad por campo o
  registro, roles en transiciones, webhooks, y pasar catálogos a por empresa (no se puede revertir).
- CA3: Puedo descartar una propuesta. Al refinar, la propuesta anterior queda reemplazada: solo la última de la
  conversación se puede aplicar, y una propuesta aplicada o descartada no se vuelve a aplicar.
- CA4: Si el modelo cambió desde que se generó la propuesta y el plan ya no es válido, aplicar se rechaza con los
  errores; nada queda a medias.

**H3 — Seguridad del asistente.** Como *responsable de seguridad*, quiero que el asistente no amplíe lo que una persona
ya puede hacer ni saque información del entorno.
- CA1: Solo quien puede modificar el modelo (modelador o superusuario) ve el asistente y llama a su API; el resto
  recibe 403 y no ve la opción. Una propuesta solo la ve y aplica quien la pidió.
- CA2: Al LLM solo se envían **metadatos** (catálogos, campos, procesos, reglas, seguridad, interfaz, nombres de roles
  y tableros) y la solicitud. **Nunca** registros, usuarios, empresas, tipos de cambio, claves de API, destinos o
  secretos de webhooks, valores de filtros de vistas, ni auditoría.
- CA3: La salida del LLM se trata como entrada no confiable: JSON con tamaño máximo, solo operaciones del Plan de
  Cambios (una operación desconocida invalida la propuesta), luego el compilador completo. El asistente no tiene
  herramientas: no ejecuta SQL, no llama endpoints ni consulta datos.
- CA4: Un **administrador** guarda la clave de Anthropic desde *Configuración › Asistente IA*: se prueba con el proveedor
  antes de guardarse (una clave rechazada no se guarda), se guarda **cifrada** en el servidor, nunca vuelve a mostrarse
  y se puede reemplazar o quitar (con confirmación). Nunca llega al navegador después de capturarla, ni a la auditoría,
  ni a los logs. Si el servidor trae una clave por configuración (user-secrets o variable de entorno), esa manda y no se
  cambia desde la app. Sin clave, el asistente se muestra «no configurado» (al administrador, con el formulario; al
  modelador, con a quién pedirla) y el resto del ERP funciona igual.
- CA5: Hay límites: longitud de la solicitud, solicitudes por usuario por hora, tamaño de la respuesta, tiempo de
  espera, intentos de corrección.

**H4 — Rastro.** Como *auditor*, quiero saber qué se le pidió al asistente, qué propuso y qué se aplicó.
- CA1: Cada solicitud queda registrada con quién, cuándo, el texto pedido, el modelo usado, el resultado (propuesta
  válida, inválida, sin plan, falla del proveedor), el plan propuesto, tokens usados y la huella del contexto enviado.
- CA2: La auditoría inmutable registra `assistant.propose`, `assistant.apply` (con el conjunto de cambios),
  `assistant.discard` y `assistant.key` (guardar o quitar la clave, sin la clave). El plan aplicado queda además en `model.change` como cualquier cambio de modelo.
- CA3: En el asistente veo el historial de mis conversaciones y el estado de cada propuesta.

## Decisiones (CLARIFY)
Sesión sin interlocutor para las preguntas de diseño; el dueño decidió el proveedor durante la sesión. Se registran
para revisión.

- **Proveedor: Anthropic (Claude), no MiniMax.** El dueño lo pidió al arrancar la spec (2026-10-23). Se usa el SDK
  oficial de Anthropic para .NET con el modelo `claude-sonnet-5-5` (configurable; el dueño eligió Sonnet por costo) y salida estructurada (JSON con esquema).
  La constitución nombraba a MiniMax solo como ejemplo de secreto; el cambio queda en [ADR-0003](../../../adr/0003-asistente-ia-anthropic.md).
- **La clave se configura en la app** (decisión del dueño, a mitad de la sesión): el superusuario la captura en
  *Configuración › Asistente IA*; se guarda cifrada con Data Protection en `meta.AssistantKey`, como los secretos de
  webhooks (008). Así no hace falta acceso al servidor para activarlo. Alternativa para servidores administrados por
  infraestructura: `Assistant:AnthropicApiKey` (user-secrets o `Assistant__AnthropicApiKey`), que tiene prioridad.
  Opcionales por configuración: `Assistant:Model`, límites y `Assistant:BaseUrl` (solo pruebas: proveedor simulado en
  `127.0.0.1`; fuera de loopback exige `https`).
- **Quién:** quien tiene `canManageModel` (o es superusuario): son las mismas personas que ya pueden aplicar un plan
  a mano, así el asistente no da a nadie una capacidad nueva. Las propuestas son de quien las pidió.
- **Qué contexto se envía:** el modelo de metadatos activo (catálogos no archivados con sus campos activos, procesos,
  reglas, automatizaciones, seguridad, layout, vistas sin valores de filtro, reportes, webhooks sin destino),
  nombres de roles, tableros, la referencia del lenguaje de expresiones y del Plan de Cambios, y las solicitudes y
  planes previos de la misma conversación. Si el modelo es muy grande, los catálogos que no caben van resumidos
  (nombre, etiqueta y campos). No se envían datos, usuarios, empresas, tipos de cambio, claves ni auditoría.
- **Qué puede proponer:** solo operaciones del Plan de Cambios. Roles, permisos por catálogo, usuarios, empresas,
  tipos de cambio, módulos y paquetes de entornos no son operaciones del plan, así que no puede proponerlos (puede
  citar roles existentes en seguridad y transiciones).
- **Límites por defecto:** solicitud ≤ 4 000 caracteres; 20 solicitudes por usuario por hora; plan ≤ 100 operaciones
  y ≤ 256 KB; respuesta ≤ 16 000 tokens; 120 s de espera; 2 intentos de corrección con los errores del compilador;
  conversación de hasta 10 turnos.
- **Auditoría:** tabla `meta.AssistantRequest` (texto pedido, explicación, plan, errores, tokens, huella del contexto,
  estado) más las acciones `assistant.*` en la bitácora inmutable. La UI advierte que la solicitud se envía a Anthropic
  y se guarda: no escribir datos personales ni secretos.
- **Sin configuración:** `GET /api/assistant` responde `configured: false`; el administrador ve el formulario de la
  clave y el modelador un aviso; `POST` responde 503. La falta de clave no impide arrancar el servidor.
- **Falla del proveedor:** 502 con mensaje genérico en español; la solicitud queda registrada como `failed` con un
  código de error (sin el detalle del proveedor ni la clave), y el detalle va al log del servidor.
- **Fuera de alcance:** respuestas en flujo (*streaming*), que el asistente consulte datos o ejecute acciones, editar
  a mano el plan propuesto antes de aplicarlo (se pide el ajuste o se lleva al modelador), aprobación de cuatro ojos
  para propuestas (los paquetes de *Entornos* ya la tienen para producción), otros proveedores de LLM.
