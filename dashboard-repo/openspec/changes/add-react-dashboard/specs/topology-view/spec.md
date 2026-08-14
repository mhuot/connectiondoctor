## ADDED Requirements

### Requirement: Render the connection diagram
The system SHALL render nodes as text-sized boxes joined by orthogonal connectors in three user-selectable layouts (cascade, top-down, flow), with the choice persisted.

#### Scenario: Long device names
- **WHEN** a node title exceeds the minimum box width
- **THEN** the box widens to fit rather than truncating the title

### Requirement: Colour links by protocol
The system SHALL colour each link by its protocol (power, thunderbolt, displayPort, usb3, usb2, usbLow) and SHALL render dashed strokes only for links whose contract `tunneled` flag is true.

#### Scenario: USB 2.0 behind a dock
- **WHEN** a USB 2.0 device sits behind a Thunderbolt dock
- **THEN** its link renders solid (USB4 carries USB 2.0 natively), while a USB3 or DisplayPort link behind the same dock renders dashed

### Requirement: Physical and logical modes
The system SHALL offer a physical mode that collapses same-enclosure and controller-silicon nodes into their box with a "+N internal" badge, and a logical mode showing every node.

#### Scenario: Toggling modes
- **WHEN** the user switches modes
- **THEN** no device disappears silently — folded nodes are accounted for in their enclosure's badge

### Requirement: Node inspector
The system SHALL show, on node selection, every field the contract carries for that node, each row copyable, with a VID:PID lookup action for USB nodes.

#### Scenario: Anonymous hub
- **WHEN** a hub named "USB2.0 Hub" is selected
- **THEN** its vidPid is displayed and a lookup action is offered
