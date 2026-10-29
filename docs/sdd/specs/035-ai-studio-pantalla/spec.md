# Spec 035 — ERP AI Studio: pantalla

**Estado:** Implementado · **Depende de:** 011 (clave y vista previa del asistente), 021 (huella), 026 (vista previa del
Modelador), 030 (integración), 033 (motor de Studio), 034 (catálogo endurecido) · **Origen:** pedido del dueño
(2026-10-27, «ERP AI Studio»); la 033 se dividió en CLARIFY 1 (motor y pantalla) y la 034 la recorrió a la 035. Cierra o
atiende R-033-4, R-033-10 y R-033-11.

## Problema
| # | Hallazgo (verificado en el código) | Lo que pasa hoy |
|---|---|---|
| 1 | `/api/studio/*` (`src/Erp.Api/Endpoints/StudioEndpoints.cs`) existe desde la 033; `web/src` no la llama (`grep -rn studio web/src` no da nada). | Un modelador no puede usar Studio sin escribir peticiones a mano: no ve los pasos, el consumo ni la propuesta, y no tiene cómo aplicarla. |
| 2 | `StudioService.GetAsync` recompila el plan de una propuesta vigente en **cada** lectura, también con `after` (R-033-10). | Una pantalla que sondea cada segundo recompilaría el plan contra el modelo en cada vuelta. |
| 3 | `meta.StudioStep.OutputJson` guarda lo que devolvieron `leer_registros`/`leer_registro` sin caducidad (R-033-4). | Con el alcance `datos`, los valores de registros que la persona vio quedan para siempre en su sesión, aunque después pierda el permiso. |
| 4 | El informe (`tested`, `ok`) lo declara el propio modelo (R-033-11). | Una pantalla que lo muestre como hecho daría por probado lo que el agente solo afirma. |
| 5 | El texto del modelo y las salidas de herramientas son datos no confiables (constitución II.8). | Una pantalla que interprete Markdown o HTML daría enlaces activos o marcado de un texto que pudo escribir un registro hostil. |

## Principio
Pantalla genérica sobre el contrato de la 033, sin código por módulo. Reutiliza la vista previa del Modelador
(`PlanPreviewView`) y la vía de aplicar de siempre (huella de lo revisado, compuerta de cambio directo). Lo que la persona
ve del agente es texto plano: nada se interpreta como marcado ni enlace.

## Historias

**H1 — Pedir un desarrollo.** Como *modelador*, quiero abrir ERP AI Studio en Configuración y pedir un desarrollo con los
alcances que decida, para que el agente trabaje por mí sin ver más de lo que yo veo.
- CA1: Configuración › ERP AI Studio aparece solo con la capacidad de modelador; sin ella, la opción no está y la URL directa
  dice que no hay acceso.
- CA2: el pedido se escribe en un área de texto con su límite; `modelo` va siempre, `datos` viene **apagado** y al
  encenderlo un aviso dice que los registros que el agente lea viajan a Anthropic; `aislado` se muestra como no disponible
  (con su motivo) si el servidor no lo permite.
- CA3: sin clave de Anthropic la pantalla lo explica y no ofrece pedir; con una sesión en curso, abre esa y no deja pedir
  otra; un rechazo del servidor (409, 429, 400) se muestra con su motivo junto al formulario.

**H2 — Avance en vivo.** Como *modelador*, quiero ver qué hace el agente mientras trabaja y cuánto lleva gastado, para
decidir si lo dejo seguir.
- CA1: la sesión en curso se actualiza sola pidiendo solo los pasos nuevos (`?after=n`); cada paso muestra herramienta,
  estado y duración, y se expande para ver entrada y salida.
- CA2: el consumo se muestra contra los topes: pasos, herramientas, tokens, minutos y costo.
- CA3: «Cancelar» detiene la sesión en curso y la pantalla lo refleja.
- CA4: el texto del modelo y las salidas de herramientas se muestran como texto: sin HTML interpretado ni enlaces activos.

**H3 — Revisar y aplicar la propuesta.** Como *modelador*, quiero revisar lo que el agente propone, lo que dice haber
probado y lo que falló, y aplicarlo yo con la vía de siempre.
- CA1: la propuesta muestra resumen, pruebas con su resultado (como **afirmación del agente**, contrastada con las
  herramientas que de verdad corrió en la copia aislada), fallas, riesgos, avisos de `PlanWarnings` y la vista previa del
  Plan de Cambios con el componente del Modelador.
- CA2: «Revisar y aplicar» pide confirmación y aplica con la huella (`reviewHash`); si el modelo cambió (409), la pantalla
  lo dice, recarga la propuesta y pide revisarla de nuevo.
- CA3: en un entorno con aprobación la pantalla dice por qué no se aplica aquí, no ofrece aplicar y deja descargar el plan
  para llevarlo como paquete.
- CA4: «Descartar» cierra la propuesta.

**H4 — Historial.** Como *modelador*, quiero ver mis sesiones anteriores y abrir cualquiera, para retomar una propuesta o
revisar qué hizo el agente.
- CA1: la lista muestra mis sesiones con pedido, estado y fecha; al abrir una se ve completa (pasos, consumo, propuesta).

**H5 — Lo leído no se queda; sondear no recompila.** Como *dueño*, quiero que los registros que el agente leyó no queden
guardados después de la sesión y que la pantalla no recompile el plan en cada sondeo.
- CA1: al terminar una sesión (propuesta, respuesta, falla, cancelación o interrupción) se borran las salidas de las
  herramientas del alcance `datos`; el paso conserva herramienta, entrada, estado, duración y resumen (R-033-4).
- CA2: `GET …/sessions/{id}?after=n` no compila el plan: devuelve el estado, los conteos y los pasos nuevos, sin vista previa
  ni huella; la lectura completa (sin `after`) sí (R-033-10).

## Decisiones (CLARIFY)
1. **¿Dividir la spec?** Criterios (dividir si se cumplen dos): más de 6 historias (5: no) · más de ~2 500 líneas de producto
   estimadas (~1 300: no) · seguridad o concurrencia junto con UI nueva (sí: la purga de R-033-4) · más de una pantalla
   principal (1: no). → No se divide.
2. **Caducidad de lo leído (R-033-4): al cerrar, no por días.** La persona lo vio mientras el agente trabajaba (con su
   permiso, recargado en cada paso); al cerrar ya no hace falta. Purgar por días dejaría una ventana configurable y un
   barrido más; borrar en la misma transacción del cierre (y de la interrupción) es una sola regla. El texto del propio
   modelo puede citar valores leídos y se conserva: queda como riesgo (R-035-1).
3. **Sondear con `after` es ligero (R-033-10).** Con `after` no se compila ni se devuelve la huella: la pantalla pide la
   lectura completa una vez al abrir la sesión y otra al cambiar de estado. Así no hace falta una caché por versión del
   modelo.
4. **El informe es una afirmación del agente (R-033-11).** Se titula así y, si dice haber probado algo y no corrió ninguna
   herramienta de la copia aislada, la pantalla lo advierte.
5. **Entorno con aprobación:** no se aplica (ya lo niega el servidor); la pantalla deja descargar el plan JSON para llevarlo
   a un entorno de desarrollo y exportarlo como paquete (009). Sin ruta nueva.
6. **Ubicación:** grupo «Modelo» de Configuración, después del Explorador (la usa quien diseña el modelo), icono `Bot`.

## Fuera de alcance (anotado en el roadmap)
- El servidor MCP por persona (038; antes 036).
- La pantalla como chat de varios turnos: la pidió el dueño al revisar esta (2026-10-29); motor en la 036, pantalla en la 037.
- Retomar una sesión interrumpida o pedir ajustes sobre una propuesta (hoy se pide una sesión nueva).
- Que cancelar interrumpa la creación de la copia aislada (R-033-11, segunda mitad).
