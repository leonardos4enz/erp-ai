# Fase VERIFY — cómo se da por terminada una spec

Una spec **no está terminada** hasta pasar las cuatro compuertas, en este orden.
Las dos primeras son automáticas y se repiten igual en cada cambio. La tercera es una lectura
adversarial del código por un agente que no construyó la spec (desde la 028). La cuarta es la
revisión de una persona o de un agente de IA usando la app como lo haría un usuario.
El `verify.md` sigue la [plantilla](plantillas/verify.md) y los riesgos que quedan van a [riesgos.md](riesgos.md).

```
 1. Pruebas unitarias        dotnet test            código de seguridad crítica
                                                    (030: integración con SQL real — concurrencia, fallas parciales)
 2. Suite E2E (Playwright)   npm --prefix web run e2e
      ├─ api-security.spec   API real: CSRF, inyección, permisos, bloqueo, concurrencia
      ├─ hN-*.spec           un escenario por criterio de aceptación de la spec
      ├─ accesibilidad       axe-core (WCAG 2.1 AA) en las pantallas clave
      ├─ guardia             falla si hay errores JS o violaciones de CSP en consola
      │                      (030: desbordes, alertas fuera de la vista, foco en body)
      └─ visual.spec         capturas claro/oscuro/móvil contra referencias aprobadas
 3. Revisión adversarial     agente sin el contexto de la sesión, leyendo el diff (sección 4)
 4. Revisión exploratoria    agente con navegador (o persona) + reporte en verify.md
```

## 1–2. Compuertas automáticas

```bash
dotnet test
```
```bash
npm --prefix web run e2e
```

- La suite crea una base **desechable**: nunca toca los datos de desarrollo. En la carpeta principal es `ErpAi_E2E`
  con la API en el puerto 5090; en un worktree, `ErpAi_<worktree>_<huella>_E2E` y su propio bloque de 10 puertos libre (spec 028).
  Así dos sesiones en carpetas distintas corren su suite a la vez; en la misma carpeta, la segunda se detiene con un aviso
  (candado por base en `%TEMP%`) en lugar de borrarle la base a la primera. `E2E_DB`
  (debe terminar en `_E2E`) y `E2E_PORT_BASE` las fijan a mano.
- La contraseña del admin de prueba se genera en cada corrida; no vive en el repositorio.
- Fallas: `npm --prefix web run e2e:report` abre el reporte con video, captura y *trace*
  paso a paso de cada prueba fallida.
- Cambio visual **intencional**: `npm --prefix web run e2e -- --update-snapshots`, y luego
  **mirar cada imagen nueva** antes de aceptarla. Una referencia aprobada sin revisar
  protege el bug en lugar de detectarlo (ver hallazgo F-06 de la spec 001).

### Reglas para escribir pruebas E2E
- Un archivo por grupo de historias: `h1-…spec.ts`, `h3-h4-…spec.ts`. El título de cada
  prueba empieza con la historia y el criterio (`H1 CA3: …`).
- Selectores por rol y etiqueta accesible (`getByRole`, `getByLabel`), nunca por clases CSS.
  Si no se puede seleccionar algo por su rol o etiqueta, probablemente tampoco es accesible.
- Los datos de apoyo se preparan por API (`applyPlan`, `apiAs`); lo que se está probando,
  por la UI.
- **Playwright es más hábil que una persona:** desplaza contenedores y hace clic en elementos
  recortados. Antes de interactuar con algo que aparece al vuelo (listas, menús, popovers), verifica
  que sea visible para una persona: `elementFromPoint` en su centro debe devolver el propio elemento
  (ver `pick()` en `compras.spec.ts`, hallazgo F-20).
- Una regresión nueva se valida **corriéndola contra el código anterior**: debe fallar. Con
  `npm --prefix web run e2e:regresion -- <commit> [e2e/archivo.spec.ts …]`: crea un worktree en el commit, copia las
  pruebas cambiadas, corre con `E2E_INDEPENDENT=1` en su base y sus puertos, resume qué falló y por qué
  (`web/e2e/.results/regresion-<sha>.json`) y limpia (primero la unión de `node_modules`, lección 013 F-03). Si la prueba
  depende de otro archivo (p. ej. de que `compras.spec.ts` instale Compras), se agrega a la lista.
- Cada archivo deja el entorno como lo encontró (archivar catálogos, restaurar versiones) **también si se corta a mitad**:
  la limpieza va en `afterAll` y no depende de que las pruebas anteriores pasen.
- Todo archivo serial admite `E2E_INDEPENDENT=1`: `mode: process.env.E2E_INDEPENDENT ? 'default' : 'serial'`
  (`run.mjs` rechaza un `mode: 'serial'` fijo). En modo independiente, tras una falla Playwright reinicia el proceso y
  vuelve a correr `beforeAll`: la preparación debe tolerar datos que ya existen.
- Ninguna prueba fija la base ni un puerto: `DB`, `BASE_URL` y `PORTS` salen de `helpers.ts`.
- Una pantalla nueva tiene captura de referencia o una línea en `verify.md` que diga por qué no.

### Cuántas veces correr la suite
- Al iterar: solo los archivos de la spec y los de las áreas que toca.
- Al cerrar: **una** corrida completa limpia y los archivos nuevos o modificados **tres veces** con
  `npm --prefix web run e2e -- e2e/a.spec.ts e2e/b.spec.ts --veces=3` (cada vuelta sobre una base nueva; agregar los
  archivos de los que dependen, p. ej. `compras.spec.ts`). `--repeat-each=3` solo sirve en un archivo idempotente: repite
  dentro de la misma base y las pruebas «preparar» que crean datos con nombre fijo fallan en la segunda vuelta (028).
- Tras corregir un hallazgo: su archivo con `--veces=3` y la corrida completa final.
- La segunda corrida completa es de CI cuando exista (spec 030); si CI falla, la spec se reabre. **Mientras no exista**,
  la sesión que abre la spec siguiente corre la suite completa antes de cambiar código: si falla, corrige primero eso.
- Registrar en `verify.md` cada corrida con su duración.

### Fragmentación (`--shard`): todavía no
La suite no se puede partir por archivo (revisado en la 028): 13 archivos declaran en su encabezado que dependen de que
otro corra antes (`compras` instala Compras para `entornos`, `explorador`, `integraciones`, `interfaz`, `inventario`,
`navegacion`, `seguridad`…; `inventario` para `multiempresa`, `pendientes` y `valuacion-contable`, que instala
Contabilidad para `valuacion-politicas`, `valuacion-rutas-avisos` y `visual-pdf`) y las capturas de
`visual*.spec.ts` muestran el menú, que cambia con los catálogos que crean los demás archivos. Para fragmentar hace falta
que cada archivo instale lo que necesita (o un *fixture* de proyecto que deje Compras, Inventario y Contabilidad listos
en cada fragmento) y que las capturas no dependan de lo que dejaron otros. Queda para la 030 (CI), donde paga.

### Instancia para la revisión exploratoria
`npm --prefix web run revision` levanta la API sobre una base desechable propia (`ErpAi_Revision_E2E`, o la del
worktree), en un puerto libre desde 5950, con aprobación de otro administrador, paquetes firmados y cambios directos
permitidos. La contraseña del administrador se genera y se guarda **fuera del repositorio**
(`%LOCALAPPDATA%\ErpAi\revision\<base>.json`); el script imprime la URL, el usuario y esa ruta. `-- --conservar` no
recrea la base; `-- --sin-compilar` no recompila la SPA. Detener antes el servidor de desarrollo de la misma carpeta.

## 3. Revisión exploratoria con agente

El agente (Claude con el navegador integrado o con Claude in Chrome) recorre la
funcionalidad como un usuario real, sin guion fijo, buscando lo que una aserción no ve.

### Lista de comprobación
**Flujo**
- [ ] El camino feliz de cada historia se completa sin ayuda ni conocimiento técnico
- [ ] Los mensajes de error dicen qué pasó y cómo resolverlo, en español claro
- [ ] Doble clic, Atrás del navegador, recargar a mitad de un formulario
- [ ] Datos límite: textos muy largos, acentos y ñ, valores vacíos, números negativos y decimales

**Diseño**
- [ ] Claro y oscuro, escritorio (1280 px) y móvil (390 px): nada tapado, cortado ni desbordado
- [ ] Solo iconos Lucide, sin emojis; jerarquía visual consistente con el resto de la app
- [ ] Estados vacíos, de carga y de error tienen diseño, no pantallas en blanco

**Accesibilidad**
- [ ] Todo se puede hacer solo con teclado; el foco es visible y sigue un orden lógico
- [ ] Los diálogos atrapan el foco y se cierran con Esc

**Seguridad**
- [ ] Un usuario sin permiso no ve la opción ni puede llegar por URL directa
- [ ] La consola no muestra errores ni violaciones de CSP
- [ ] Cada camino alterno de la tabla STRIDE probado con un usuario sin permiso (API directa, no solo la UI)
- [ ] Dos peticiones a la vez sobre la misma operación nueva (doble clic y dos pestañas)

**Cierre**
- [ ] Pasada solo con teclado por cada pantalla nueva y por el modelador si la spec agrega metadatos
- [ ] Hallazgos de un componente genérico: buscar sus hermanos (`grep`) y corregirlos o registrarlos
- [ ] Alcance congelado: lo que el dueño pidió durante VERIFY y no bloquea el uso quedó en el roadmap

### Regla de oro: cada hallazgo se vuelve prueba
1. Se registra en `specs/NNN-…/verify.md` con ID (`F-01`, `F-02`…), severidad y evidencia.
2. Se corrige.
3. Se agrega una prueba E2E (o unitaria) que habría fallado antes de la corrección,
   con un comentario `// Regresión (revisión exploratoria NNN): …`.
4. Solo entonces se marca como resuelto.

Así cada revisión deja la suite más fuerte y el mismo bug no puede volver.

### Severidad
| Nivel | Criterio |
|---|---|
| Alta | Pérdida o exposición de datos, bloqueo de un flujo principal, falla de seguridad |
| Media | El flujo funciona pero confunde, excluye a usuarios de teclado/lector o se ve roto |
| Baja | Detalle cosmético o de redacción |

## 4. Revisión adversarial de código
Antes de la revisión exploratoria, un **subagente nuevo** (sin el contexto de la sesión) recibe el diff de la spec
(`git diff` contra el commit anterior) y la tabla STRIDE del plan, **sin** las conclusiones del plan, con el encargo de
romper: rutas sin capacidad o sin `RecordFilter`/`FieldAccess`/empresa, caminos alternos al mismo dato o efecto,
delegación de privilegios, carreras y «revisar → aplicar» sin huella, fallas parciales entre dos pasos, límites. Lee
código; no usa la app. Lo que encuentre se registra como F-xx con la misma regla de oro. Si no encuentra nada,
`verify.md` lo dice y lista qué revisó. Desde la 029 recibe también el inventario de rutas.

## ¿Navegador integrado o Claude in Chrome?
- **Navegador integrado** (predeterminado): aislado de tus pestañas y sesiones; ideal para
  esta app en `localhost`.
- **Claude in Chrome**: tu Chrome real. Útil para ver la revisión en tu propio navegador o
  probar con extensiones y perfiles reales.

Pídelo explícitamente ("haz la revisión exploratoria con Chrome") si prefieres el segundo.
