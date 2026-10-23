# Fase VERIFY — cómo se da por terminada una spec

Una spec **no está terminada** hasta pasar las tres compuertas, en este orden.
Las dos primeras son automáticas y se repiten igual en cada cambio. La tercera es la
revisión de una persona o de un agente de IA usando la app como lo haría un usuario.

```
 1. Pruebas unitarias        dotnet test            código de seguridad crítica
 2. Suite E2E (Playwright)   npm --prefix web run e2e
      ├─ api-security.spec   API real: CSRF, inyección, permisos, bloqueo, concurrencia
      ├─ hN-*.spec           un escenario por criterio de aceptación de la spec
      ├─ accesibilidad       axe-core (WCAG 2.1 AA) en las pantallas clave
      ├─ guardia             falla si hay errores JS o violaciones de CSP en consola
      └─ visual.spec         capturas claro/oscuro/móvil contra referencias aprobadas
 3. Revisión exploratoria    agente con navegador (o persona) + reporte en verify.md
```

## 1–2. Compuertas automáticas

```bash
dotnet test
```
```bash
npm --prefix web run e2e
```

- La suite crea una base **desechable** `ErpAi_E2E`: nunca toca los datos de desarrollo.
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

## ¿Navegador integrado o Claude in Chrome?
- **Navegador integrado** (predeterminado): aislado de tus pestañas y sesiones; ideal para
  esta app en `localhost`.
- **Claude in Chrome**: tu Chrome real. Útil para ver la revisión en tu propio navegador o
  probar con extensiones y perfiles reales.

Pídelo explícitamente ("haz la revisión exploratoria con Chrome") si prefieres el segundo.
