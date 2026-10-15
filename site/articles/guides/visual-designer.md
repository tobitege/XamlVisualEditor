---
title: Visual Designer
description: Work with the XamlVisualEditor live designer, design surface, tree panels, adorners, and property editor.
---

# Visual Designer

The designer turns XAML AST nodes into a live Avalonia control tree, maps visuals
back to source nodes, and keeps the editing surface synchronized with document
state.

## Designer surfaces

- `XamlVisualEditor.Designer.Core` defines design items, selections, hit testing,
  and designer host abstractions.
- `XamlVisualEditor.Designer.Rendering` instantiates live Avalonia controls from
  AST nodes and maintains visual-to-node mapping.
- `XamlVisualEditor.Designer.Adorners` renders selection rectangles, resize
  handles, grids, rulers, snap lines, and margin/padding guides.
- `XamlVisualEditor.Designer.DragDrop` handles toolbox insertion, surface
  rearrangement, and tree reorder workflows.

## Grid, rulers and coordinate strip

The viewport toolbar has toggle buttons for the grid, the rulers and the
coordinate strip. The grid size in the viewport settings applies to the visible
grid and to snapping; at small zoom levels the grid draws every second, fourth,
... line so that lines stay at least four screen pixels apart. The rulers mark the
pixel under the pointer.

The coordinate strip below the design surface and below the external previewer
shows logical pixels relative to the top-left corner of the root element,
independent of the zoom level:

- the pixel under the pointer, also left of or above the root element while the
  pointer is inside the viewport,
- the type and name of the element under the pointer as `Type#Name`,
- position and size of the selection, and its change while it is moved or resized,
- a measured distance: with the measure button on, a drag on the surface measures
  the horizontal and vertical distance and the direct length,
- the zoom level in whole percentages.

A click on a value copies it in XAML notation (`x,y`, `x,y,width,height` or
`dx,dy`). Grid, snapping, grid size, rulers and coordinate strip are stored in
`editor-options.json` and apply to all designer documents. Exported preview frames
contain neither the grid nor the strip.

## Supporting panels

- Toolbox contributes insert commands.
- Property Editor shows categorized or searchable selected-node properties.
- Tree Inspector exposes visual and logical tree views.
- Animation Editor handles timeline/keyframe editing.

## Implementation notes

Designer logic belongs in services and ViewModels. Views render bound state and
route input through Avalonia bindings and behaviors. Keep shell-specific wiring in
host adapters and keep reusable designer rules in domain/services projects.

