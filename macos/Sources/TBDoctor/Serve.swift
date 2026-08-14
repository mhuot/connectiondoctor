import Foundation
import Network

/// Minimal read-only HTTP endpoint for the Connection Dashboard:
///   GET /contract  → current state as a Connection Contract v1 envelope
///   GET /events    → the v1 events JSONL written by the collector
///
/// Defaults to loopback. `--bind lan` exposes it on the local network — the
/// data is topology and power telemetry with no authentication, which is fine
/// for a home lab fleet and explicitly opt-in for anything else.
enum Serve {

    static func run(port: UInt16, lan: Bool) {
        let params = NWParameters.tcp
        if !lan {
            params.requiredLocalEndpoint = NWEndpoint.hostPort(
                host: .ipv4(.loopback), port: NWEndpoint.Port(rawValue: port)!)
        }

        let listener: NWListener
        do {
            listener = lan
                ? try NWListener(using: params, on: NWEndpoint.Port(rawValue: port)!)
                : try NWListener(using: params)
        } catch {
            FileHandle.standardError.write("TBDoctor: cannot listen on \(port): \(error)\n".data(using: .utf8)!)
            exit(1)
        }

        listener.newConnectionHandler = { connection in
            connection.start(queue: .global())
            receiveRequest(connection)
        }
        listener.stateUpdateHandler = { state in
            if case .ready = state {
                print("TBDoctor serving on \(lan ? "0.0.0.0" : "127.0.0.1"):\(port)  (GET /contract, GET /events)")
                if lan { print("note: LAN binding is unauthenticated read-only telemetry — opt-in by design") }
            }
        }
        listener.start(queue: .global())
        dispatchMain()
    }

    private static func receiveRequest(_ connection: NWConnection) {
        connection.receive(minimumIncompleteLength: 1, maximumLength: 8192) { data, _, _, error in
            guard error == nil, let data, let request = String(data: data, encoding: .utf8) else {
                connection.cancel()
                return
            }
            let firstLine = request.split(separator: "\r\n").first ?? ""
            let parts = firstLine.split(separator: " ")
            let method = parts.count > 0 ? String(parts[0]) : ""
            let path = parts.count > 1 ? String(parts[1]) : "/"

            let response: (status: String, type: String, body: Data)
            switch (method, path) {
            case ("GET", "/contract"):
                if let body = try? Contract.json(from: Probes.sample()) {
                    response = ("200 OK", "application/json", body)
                } else {
                    response = ("500 Internal Server Error", "text/plain", Data("contract failed".utf8))
                }
            case ("GET", "/events"):
                let body = (try? Data(contentsOf: ContractLog.path)) ?? Data()
                response = ("200 OK", "application/x-ndjson", body)
            case ("GET", "/"):
                response = ("200 OK", "text/plain",
                            Data("TBDoctor contract endpoint. GET /contract or GET /events\n".utf8))
            default:
                response = ("404 Not Found", "text/plain", Data("not found\n".utf8))
            }

            var head = "HTTP/1.1 \(response.status)\r\n"
            head += "Content-Type: \(response.type)\r\n"
            head += "Content-Length: \(response.body.count)\r\n"
            head += "Access-Control-Allow-Origin: *\r\n"  // the dashboard is a browser app
            head += "Connection: close\r\n\r\n"
            var payload = Data(head.utf8)
            payload.append(response.body)
            connection.send(content: payload, completion: .contentProcessed { _ in
                connection.cancel()
            })
        }
    }
}
