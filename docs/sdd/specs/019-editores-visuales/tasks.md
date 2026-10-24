# Tareas 019

## Lógica pura
- [x] `lib/layoutEdit.ts`: mover campos y secciones (ratón y pasos de teclado), pestañas, secciones, opciones de campo
- [x] `lib/workflowEdit.ts`: capas, posiciones y aristas del diagrama; agregar y eliminar estados y transiciones
- [x] `canonical()` sin depender del orden de las propiedades

## UI
- [x] Editor del catálogo con pestañas Catálogo / Formulario / Proceso (en la URL, borrador compartido, ancho completo)
- [x] `DesignOrJson`: conmutador Diseñador / JSON con error de JSON
- [x] `LayoutDesigner`: sin colocar, lienzo con pestañas y secciones, arrastrar y soltar, teclado con región en vivo, diálogo de opciones, propiedades de sección, campos inexistentes
- [x] `LayoutPreview`: vista previa en vivo con `formSections` (pestañas, columnas, otros datos, totales, condicional)
- [x] `WorkflowDesigner`: diagrama SVG accesible, conectar arrastrando, panel de estado y transición, eliminar con confirmación, crear proceso
- [x] Partidas: aviso en las pestañas
- [x] Estilos con tokens (claro/oscuro, 390 px)

## Verificación
- [x] E2E `editores.spec.ts` por criterio (ratón y teclado) y ajuste de pruebas afectadas
- [x] Referencias visuales del modelador revisadas una por una
- [x] `dotnet test` y E2E en verde dos veces
- [x] Revisión exploratoria en el navegador integrado; hallazgos → pruebas que fallan con el código anterior
- [x] `verify.md`, roadmap e índice; revisión de secretos y artefactos; commit
