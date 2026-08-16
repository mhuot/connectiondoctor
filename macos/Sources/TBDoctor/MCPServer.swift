import Foundation

/// Model Context Protocol server over stdio, so coding agents (Claude Code,
/// Claude Desktop, Copilot in VS Code, Cursor) can query hardware state and
/// diagnosis directly instead of being handed pasted terminal output.
///
/// Transport is newline-delimited JSON-RPC 2.0. stdout carries protocol traffic
/// and nothing else — every diagnostic goes to stderr, because one stray
/// `print` corrupts the stream and the failure looks baffling from the client side.
enum MCPServer {

    static let version = "1.0.0"
    static let defaultProtocolVersion = "2024-11-05"

    // MARK: - Loop

    static func serve() {
        while let line = readLine(strippingNewline: true) {
            guard !line.trimmingCharacters(in: .whitespaces).isEmpty,
                  let data = line.data(using: .utf8),
                  let request = try? JSONSerialization.jsonObject(with: data) as? [String: Any]
            else { continue }

            let method = request["method"] as? String ?? ""
            let id = request["id"]

            // Notifications carry no id and must not be answered.
            guard id != nil else { continue }

            switch method {
            case "initialize":
                let params = request["params"] as? [String: Any]
                // Echo the client's protocol version when it offers one, rather
                // than forcing ours and risking a version mismatch rejection.
                let negotiated = (params?["protocolVersion"] as? String) ?? defaultProtocolVersion
                respond(id: id, result: [
                    "protocolVersion": negotiated,
                    "capabilities": ["tools": [:] as [String: Any]],
                    "serverInfo": ["name": "tbdoctor", "version": version]
                ])

            case "tools/list":
                respond(id: id, result: ["tools": toolDefinitions])

            case "tools/call":
                let params = request["params"] as? [String: Any] ?? [:]
                let name = params["name"] as? String ?? ""
                let arguments = params["arguments"] as? [String: Any] ?? [:]
                dispatch(id: id, tool: name, arguments: arguments)

            case "ping":
                respond(id: id, result: [:])

            default:
                respondError(id: id, code: -32601, message: "Unknown method: \(method)")
            }
        }
    }

    // MARK: - Tools

    private static var toolDefinitions: [[String: Any]] {
        [
            [
                "name": "tb_probe",
                "description": """
                Current Thunderbolt, USB and power state of this Mac: attached Thunderbolt devices \
                and negotiated link speed, the USB device tree with per-device negotiated speeds, \
                the active power adapter's identity and wattage, and instantaneous battery current. \
                Use this to answer "what is plugged in right now" and "is the machine actually \
                charging or quietly draining".
                """,
                "inputSchema": ["type": "object", "properties": [:] as [String: Any]]
            ],
            [
                "name": "tb_diagnose",
                "description": """
                Root-cause analysis of dock, peripheral and power faults from recorded history. \
                Detects a power supply that cannot cover demand (battery discharging while on AC), \
                Thunderbolt link drops and whether they correlate with power deficits, groups of USB \
                devices lost together behind a shared hub, and repeated power-source arbitration. \
                Returns ranked findings, each with the evidence behind it and a recommendation. \
                Use this for "my dock keeps disconnecting" or "devices randomly drop".
                """,
                "inputSchema": [
                    "type": "object",
                    "properties": [
                        "hours": ["type": "number", "description": "How far back to analyse. Default 6."]
                    ]
                ]
            ],
            [
                "name": "tb_diagram",
                "description": """
                The current connection topology as an Excalidraw document (JSON). Boxes for the power \
                source, the Mac, any Thunderbolt device and the whole USB hub tree, joined by \
                orthogonal connectors, with the power path drawn distinctly. Use this when someone \
                wants to see or share how their devices are wired together, or to reason about which \
                devices sit behind which hub. Write the returned JSON to a .excalidraw file.
                """,
                "inputSchema": [
                    "type": "object",
                    "properties": [
                        "style": [
                            "type": "string",
                            "enum": ["cascade", "topDown", "flow"],
                            "description": "Layout. cascade steps down-right (default); topDown fans children below; flow reads left to right."
                        ]
                    ]
                ]
            ],
            [
                "name": "tb_contract",
                "description": """
                The current state as a Connection Contract v1 envelope (JSON) — the shared schema \
                also emitted by ConnectionDoctor on Windows and consumed by the Connection Dashboard. \
                Flat node list with parentId, VID:PID identity, power source incl. dock/mains, honest \
                tunneling flags. Use this when another tool or dashboard needs this machine's topology.
                """,
                "inputSchema": ["type": "object", "properties": [:] as [String: Any]]
            ],
            [
                "name": "tb_incidents",
                "description": """
                Discrete fault incidents reconstructed from kernel events and power samples, newest \
                first. Each has a start time, duration, whether a Thunderbolt link drop was the root \
                event, peak battery discharge, the power adapter in use at the time, and which \
                devices disappeared. Use this to establish when and how often a fault occurred.
                """,
                "inputSchema": [
                    "type": "object",
                    "properties": [
                        "hours": ["type": "number", "description": "How far back to look. Default 24."],
                        "limit": ["type": "number", "description": "Max incidents. Default 20."]
                    ]
                ]
            ]
        ]
    }

    private static func dispatch(id: Any?, tool: String, arguments: [String: Any]) {
        switch tool {
        case "tb_probe":
            emit(id: id, payload: encode(Probes.sample()))

        case "tb_diagnose":
            let hours = arguments["hours"] as? Double ?? 6
            let (samples, events) = history(hours: hours)
            let findings = Diagnosis.analyze(samples: samples, events: events)
            emit(id: id, payload: [
                "windowHours": hours,
                "sampleCount": samples.count,
                "eventCount": events.count,
                "findings": findings.map(encode),
                // Say so explicitly rather than returning an empty list that
                // reads as "healthy" when it really means "no data recorded".
                "note": samples.count < 2
                    ? "Little or no history recorded — run the collector (menu bar app) for coverage over time."
                    : "Analysis based on recorded history."
            ])

        case "tb_diagram":
            let style = DiagramStyle(rawValue: arguments["style"] as? String ?? "") ?? .cascade
            let sample = Probes.sample()
            let layout = Diagram.layout(root: Topology.build(from: sample), style: style)
            if let data = ExcalidrawExport.document(
                layout: layout, caption: "TBDoctor — connections as of \(Diagnosis.stamp(sample.t))"),
               let json = String(data: data, encoding: .utf8) {
                respond(id: id, result: ["content": [["type": "text", "text": json]]])
            } else {
                respondError(id: id, code: -32603, message: "Could not build diagram")
            }

        case "tb_contract":
            if let data = try? Contract.json(from: Probes.sample()),
               let json = String(data: data, encoding: .utf8) {
                respond(id: id, result: ["content": [["type": "text", "text": json]]])
            } else {
                respondError(id: id, code: -32603, message: "Could not build contract")
            }

        case "tb_incidents":
            let hours = arguments["hours"] as? Double ?? 24
            let limit = Int(arguments["limit"] as? Double ?? 20)
            let (samples, events) = history(hours: hours)
            let incidents = Collector.deriveIncidents(samples: samples, events: events)
            emit(id: id, payload: [
                "windowHours": hours,
                "count": incidents.count,
                "incidents": incidents.prefix(limit).map(encode)
            ])

        default:
            respondError(id: id, code: -32602, message: "Unknown tool: \(tool)")
        }
    }

    // MARK: - Data

    private static func history(hours: Double) -> ([Sample], [KernelEvent]) {
        let cutoff = Date().addingTimeInterval(-hours * 3600)
        var samples = Store(filename: "samples.jsonl").load(Sample.self, since: cutoff) { $0.t }
        let events = Store(filename: "events.jsonl").load(KernelEvent.self, since: cutoff) { $0.t }
        samples.append(Probes.sample())
        return (samples, events)
    }

    private static func encode<T: Encodable>(_ value: T) -> Any {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        guard let data = try? encoder.encode(value),
              let object = try? JSONSerialization.jsonObject(with: data) else { return [:] }
        return object
    }

    // MARK: - JSON-RPC

    /// MCP tool results are content blocks. JSON goes in a text block, which is
    /// what every current client parses most reliably.
    private static func emit(id: Any?, payload: Any) {
        let text: String
        if let data = try? JSONSerialization.data(withJSONObject: payload, options: [.prettyPrinted, .sortedKeys]),
           let string = String(data: data, encoding: .utf8) {
            text = string
        } else {
            text = "{}"
        }
        respond(id: id, result: ["content": [["type": "text", "text": text]]])
    }

    private static func respond(id: Any?, result: [String: Any]) {
        var message: [String: Any] = ["jsonrpc": "2.0", "result": result]
        if let id { message["id"] = id }
        write(message)
    }

    private static func respondError(id: Any?, code: Int, message text: String) {
        var message: [String: Any] = ["jsonrpc": "2.0", "error": ["code": code, "message": text]]
        if let id { message["id"] = id }
        write(message)
    }

    private static func write(_ message: [String: Any]) {
        guard let data = try? JSONSerialization.data(withJSONObject: message),
              let line = String(data: data, encoding: .utf8) else { return }
        print(line)
        fflush(stdout)
    }
}
