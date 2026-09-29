# AvaBuild Inspector

VRChat avatar performance preview and cause inspector for Unity 2022.3.

## Open

Open `Tools > AvaBuild Inspector > Open Inspector` and drag an avatar with a
`VRCAvatarDescriptor` into the Avatar field. Quick Scan starts automatically.

## Implemented in 0.1.0

- SDK-backed PC / Android performance values and ranks
- automatic Quick Scan when the Avatar field changes
- two primary tabs: Scan Results and Breakdown
- Overall rank and main issues
- Full metric list with Exact / Estimated / Unavailable accuracy
- compact texture-memory breakdown with thumbnails and asset navigation
- queued multi-texture compression editing for Default / PC / Android settings
- one-click batch Apply followed by an automatic rescan
- manual Rescan button
- skinned-mesh breakdown with triangle, vertex, blend-shape, and bone information
- material-slot breakdown per renderer
- PhysBone component breakdown with transform, collider, and collision-check information
- unused PhysBone candidates detected by comparing affected transforms with SkinnedMeshRenderer bone references
- non-destructive source-avatar analysis

Texture memory and PhysBone collision-check breakdowns are diagnostic estimates.
The aggregate value and rank shown in the metric header come from the VRChat SDK.

The window intentionally omits build, history, report, comparison, and export
features to keep the workflow focused on immediate Quick Scan results.
