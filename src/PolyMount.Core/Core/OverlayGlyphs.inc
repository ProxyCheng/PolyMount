// The Explorer badge's vocabulary, in slot order. Each name is one of the tray's line icons
// (TrayIcons.cs) and each slot is drawn in the colour at the same index in OriginPalette.inc, so
// the two files must stay the same length -- LayerColorsTests says so.
//
// The order is the built-in layer types in the order their colours are assigned, and the last
// entry is what a layer type without a glyph of its own gets. A plugin that names one of the
// tray's icons in its descriptor is filed under that glyph: the smb source declares "folder" and
// therefore wears the same badge as the dir source, which is the honest answer -- both are a
// folder somewhere else.
PM_GLYPH("edit-layer")
PM_GLYPH("disk-cache")
PM_GLYPH("repository")
PM_GLYPH("folder")
PM_GLYPH("predict-path")
PM_GLYPH("memory-chip")
PM_GLYPH("warm-listings")
PM_GLYPH("layers")
