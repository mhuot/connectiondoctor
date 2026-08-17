## ADDED Requirements

### Requirement: Mode switch is visible without scrolling
The topology view SHALL label its modes **Physical** and **All device nodes**, and SHALL show, next to the control, how many nodes are folded (and into how many containers) or surfaced, so that switching modes produces visible feedback independent of scroll position while preserving the accessible radio state and persisted choice.

#### Scenario: First surfaced node below the fold
- **WHEN** the user switches from Physical to All device nodes on a topology whose first surfaced node is below the viewport
- **THEN** the chip changes from "49 internal folded into 13 containers" to "49 surfaced" without any scrolling

### Requirement: Built-in devices are a view choice, off by default
The topology view SHALL offer an **Include built-in devices** control, default off, that hides nodes with `builtIn: true` (and their built-in-only descendants) while keeping every external branch, SHALL say how many are hidden, and SHALL persist the choice.

#### Scenario: Surface Laptop 7 with a dock
- **WHEN** the control is off
- **THEN** the calibrated panel, touch screen, touchpad and internal keyboards are hidden and the LG UltraWide and Surface Thunderbolt 4 Dock branches remain; **WHEN** on, everything is shown and the chip reads "0 built-in hidden"
