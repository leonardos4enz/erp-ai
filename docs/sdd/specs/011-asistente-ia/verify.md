# Verificación 011 — Asistente IA (Claude)

**Fecha:** 2026-10-23 · **Resultado:** Aprobada

| Compuerta | Resultado |
|---|---|
| Unitarias | 278/278 (+19 en `AssistantTests`: el contexto lleva el modelo activo sin catálogos ni campos archivados, sin valores ni etiquetas de filtros de vistas —solo `$me`/`$today`— y con `<`/`>` escapados para no cerrar el bloque `<modelo>`; huella estable; con poco presupuesto resume catálogos sin perder ninguno; respuesta válida → plan que compila y se re-serializa (lo extra se descarta); sin plan = respuesta; ocho respuestas malformadas u hostiles —vacía, no JSON, arreglo, plan no texto, operación desconocida, sin `op`, sin operaciones, sin campos obligatorios—; límites de tamaño, operaciones y explicación; un plan hostil que se interpreta (SQL en el nombre, `EXEC` en una regla, rol inventado) lo rechaza el compilador en cada operación; advertencias; lo que quitan las operaciones set*; URL base https salvo loopback, modelo como identificador, topes; formato de la clave; mensaje de corrección acotado) |
| E2E | 161/161 en dos corridas limpias seguidas (+15 en `asistente.spec.ts`, con un proveedor simulado en 127.0.0.1:5091 que imita la API de mensajes y la consulta de modelos, y guarda cada petición). La suite ya rebasaba como administrador el límite global de 600 peticiones por minuto (429 en `h3-h4 › conflicto` y en cascada): el límite pasó a `Security:RequestsPerMinute` (600 por defecto) y la suite usa 5000, como ya hacía con el de inicio de sesión |
| Visual | Capturas de referencia sin cambios: «Asistente IA» va al final de «Configuración» y los catálogos de la suite viven en el grupo «Pruebas IA», que se archiva al terminar; no se aprobó ninguna referencia nueva |
| Base real | `ErpAi` sin clave: el administrador ve «Conecta el asistente con Anthropic». Flujo completo con una instancia temporal (puerto 5081) sobre `ErpAi`, clave ficticia por configuración y proveedor simulado: propuesta sobre *Productos*, refinamiento, corrección automática, descartar; no se guardó ninguna clave ni se aplicó ningún cambio al modelo de `ErpAi` |

## Cobertura
| Criterio | Prueba |
|---|---|
| H1 CA1 · CA2 — propuesta con explicación y la vista previa del modelador (pasos, SQL), compilada contra el entorno | `asistente › H1 CA1 · CA2 + H3 CA2`; unitaria `Una_respuesta_valida…` |
| H1 CA3 — corrige con los errores del compilador; tras los intentos queda «Con errores» y sin «Aplicar» | `asistente › H1 CA3` |
| H1 CA4 — lo que no es metadato se explica sin plan | `asistente › H1 CA4 + H3 CA3`; unitaria `Sin_plan_es_una_respuesta…` |
| H1 CA5 + H2 CA3 — refinar con la conversación completa reemplaza la propuesta anterior; descartar; recargar conserva la conversación | `asistente › H1 CA5 + H2 CA3` |
| H2 CA1 · CA2 — confirmación con resumen y advertencias, cancelar no aplica, aplica lo guardado una sola vez, `model.change` | `asistente › H2 CA1 · CA2 + H4 CA2`; unitarias de advertencias |
| H2 CA4 — si el modelo cambió, la vista previa lo muestra y aplicar responde 400 sin cambios | `asistente › H2 CA4` |
| H3 CA1 — solo modeladores (403, sin opción, URL directa «Sin acceso»); propuestas ajenas 404; un modelador no superusuario lo usa | `asistente › H3 CA1` |
| H3 CA2 — solo metadatos al proveedor (sin registros, etiquetas de filtros, tipos de cambio, usuarios, contraseña, empresa, entorno ni la clave en el cuerpo); texto hostil de los metadatos viaja como dato | `asistente › H1 CA1 · CA2 + H3 CA2`; unitaria `El_contexto_lleva…` |
| H3 CA3 — salida hostil (operación desconocida, HTML en la explicación) no es propuesta y se muestra como texto | `asistente › H1 CA4 + H3 CA3`; unitarias del parser |
| H3 CA4 — clave desde la app: solo superusuario, probada antes de guardarse, cifrada en la base, nunca devuelta ni auditada; reemplazar y quitar con confirmación; sin clave, 503 y avisos | `asistente › H3 CA4: sin clave…`, `H3 CA4: el administrador reemplaza o quita…` |
| H3 CA5 — longitud, solicitudes por hora por usuario (429), falla del proveedor (502 genérico, sin detalle ni clave), clave rechazada | `asistente › H3 CA5` |
| H4 CA1 · CA2 · CA3 — `assistant.propose` con estado, modelo, intentos, tokens y huella; `assistant.discard`; `assistant.key`; historial de conversaciones | `asistente › H4 …` |
| Móvil sin desbordes (320–760 px) y accesibilidad AA | `asistente › Asistente sin desbordes…`; `expectAccessible` en propuesta, confirmación, formulario y diálogo de la clave |

## Hallazgos
| ID | Sev. | Hallazgo | Estado |
|---|---|---|---|
| F-01 | Media | (revisión de diseño, antes de la exploratoria) `setRules`, `setViews`, `setReports`, `setAutomations` (sin `merge`) y `setWebhooks` reemplazan la lista completa. Un plan del asistente que «agrega una regla» a *Productos* quitaba en silencio `CostoNoNegativo` e `IvaRango`; la vista previa solo decía «Reglas de 'Producto'», y las advertencias no lo destacaban | Corregido (`PlanWarnings` dice qué quita cada operación set* y quitar un layout); regresión `Las_advertencias_dicen_que_quita…`, **verificada fallando con el código anterior** (restaurado desde copia) |
| F-03 | Media | (exploratoria, `ErpAi`) Una solicitud sin plan en medio de la conversación («mejor da de alta la empresa Norte») marcaba la propuesta válida anterior como «Reemplazada»: quien solo preguntaba algo perdía una propuesta aplicable | Corregido (solo una nueva propuesta con plan reemplaza a la anterior); regresión en `asistente › H1 CA5 + H2 CA3`, **verificada fallando con el código anterior** (restaurado desde copia) |
| F-04 | Media | (exploratoria, 375 px) El redactor fijo al pie ocupaba casi media pantalla en móvil y tapaba la propuesta mientras se leía | Corregido (en ≤ 900 px de ancho o ≤ 720 px de alto el redactor sigue a la conversación); regresión `asistente › En móvil el redactor no tapa…`, **verificada fallando con el código anterior** |
| F-02 | Media | (primera corrida completa) Los catálogos de prueba del asistente quedaban en el menú y empujaban «Auditoría» fuera de la pantalla en `compras › H7` (regresión 003); el resto falló en cascada | Corregido (la suite archiva sus catálogos al terminar); cubierto por `compras › H7`, que falló en esa corrida |

## Revisión exploratoria (navegador integrado, base `ErpAi`)
- `ErpAi` (servidor de desarrollo, sin clave): *Configuración › Asistente IA* muestra al administrador el formulario para
  conectar con Anthropic (campo de contraseña, «Guardar y probar», explicación de que se prueba y se guarda cifrada).
  No se capturó ninguna clave en `ErpAi`: la clave real la pondrá el dueño.
- Instancia temporal sobre `ErpAi` con clave ficticia del servidor y proveedor simulado (en ella no aparece «Clave de
  Anthropic»: la del servidor no se cambia desde la app):
  - Ejemplo «Peso (kg) en Productos»: propuesta lista con la advertencia de F-01 sobre datos reales («Quita reglas
    existentes de «Producto»: CostoNoNegativo, IvaRango»), pasos con SQL, JSON plegable, «Descartar» y «Aplicar…».
  - La petición al proveedor (55 KB, Compras + Inventario completos) no contiene registros (p. ej. el proveedor
    *Industrial Supply*), el tipo de cambio 18.25, la empresa ni la clave.
  - Refinar con «mejor da de alta la empresa Norte…»: respuesta sin plan, y la propuesta anterior quedó «Reemplazada»
    (F-03). Una solicitud que el simulado respondió primero con un tipo de campo inexistente se corrigió sola en el
    segundo intento; el texto con `<b>…</b>` y «ñandú» se mostró literal. Descartar funcionó.
  - Móvil (375 px): sin desbordes, pero el redactor fijo tapaba la propuesta (F-04). Tema claro y oscuro correctos.
    Consola sin errores ni violaciones de CSP.
- No se modificó al usuario `admin` ni el modelo de `ErpAi`; quedaron solo las solicitudes de la exploración en
  `meta.AssistantRequest` y su auditoría.

## Riesgo residual
- La estructura del modelo (nombres, etiquetas, fórmulas, reglas, roles) y el texto que escribe la persona salen hacia
  Anthropic en cada solicitud. Se advierte en pantalla; no salen registros ni secretos.
- La inyección indirecta (texto hostil en metadatos) se mitiga, no se elimina: el modelo podría proponer algo indebido;
  lo frenan el compilador, las advertencias y la confirmación humana. Una persona que confirma sin leer sigue siendo un riesgo.
- La clave guardada en la app depende de las llaves de Data Protection del servidor: si se pierden, hay que capturarla
  de nuevo; en producción deben persistirse y protegerse (ADR-0002).
- Las advertencias comparan contra el modelo antes del plan: un catálogo creado y luego modificado en el mismo plan no
  genera advertencias de reemplazo (no tenía nada que perder).
- Sin respuestas en flujo: una propuesta grande puede tardar decenas de segundos (se muestra «preparando la propuesta»).
- El SDK (v13) envía la clave como `Authorization: Bearer`. No hay respaldo automático si el proveedor rechaza por
  seguridad (`refusal`): la solicitud queda registrada y se pide reformularla.
