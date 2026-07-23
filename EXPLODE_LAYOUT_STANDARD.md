# SOP Explosion Layout Standard

This standard applies to every assembly processed by the SOP generator. It is geometry-led and does not require a particular file-name convention.

## 1. Reference component

- The largest structural component is the anchor and remains stationary.
- All other components are placed relative to that anchor.
- If two structural components have equal volume, the one nearest the assembly center is the anchor.

## 2. Structural versus fastener classification

The following order is used:

1. A component whose longest dimension is at least 35 percent of the assembly diagonal is structural.
2. A thin planar component is structural when its thickness-to-width ratio is at most 0.45 and its width-to-length ratio is at least 0.45.
3. A fastener must be small relative to the assembly and either have a nearly symmetric cross-section (at least 0.65) plus a length-to-cross-section ratio of at least 3, or fit inside the configured small-fastener size.
4. A component not matching a fastener rule is structural.

Names are not part of the default decision. A deployment may opt in to name-based hints for a controlled library, but the result must remain usable when names are arbitrary.

## 3. Explosion directions

- Structural components move on the dominant signed X, Y, or Z axis from the anchor, never on an arbitrary diagonal.
- Components in a common coaxial group move on the same axis, outward from the anchor, ordered from inside to outside.
- Isolated screws, pins, and rods move along their own axis, outward from the anchor.
- True radial SolidWorks explode steps are opt-in. They are reserved for assemblies whose design intent explicitly calls for radial separation.

## 4. Distances and drawing result

- Distances are proportional to assembly size and clamped by configured minimum and maximum limits.
- Coaxial components use regular stack spacing to preserve assembly order.
- The target drawing contains both exploded and collapsed isometric views; balloons and BOM attach to the exploded view.

## 5. Validation requirements

- The planner must produce no motion for the anchor.
- Every placement must have a cardinal or component-axis direction and a finite positive distance.
- The generated SolidWorks steps must be checked for successful creation before the drawing workflow continues.
