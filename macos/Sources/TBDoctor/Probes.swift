import Foundation
import IOKit
import IOKit.ps

/// Direct IOKit reads. Everything here is on the order of a millisecond, which
/// is what makes a short sampling interval affordable — shelling out to
/// `system_profiler` costs ~230ms per call and cannot be polled.
enum Probes {

    // MARK: - Registry plumbing

    private static func properties(of service: io_service_t) -> [String: Any] {
        var unmanaged: Unmanaged<CFMutableDictionary>?
        guard IORegistryEntryCreateCFProperties(service, &unmanaged, kCFAllocatorDefault, 0) == KERN_SUCCESS,
              let dict = unmanaged?.takeRetainedValue() as? [String: Any]
        else { return [:] }
        return dict
    }

    /// Runs `body` over every service of a class, releasing each as it goes.
    private static func forEachService(_ className: String, _ body: ([String: Any]) -> Void) {
        guard let matching = IOServiceMatching(className) else { return }
        var iterator: io_iterator_t = 0
        guard IOServiceGetMatchingServices(kIOMainPortDefault, matching, &iterator) == KERN_SUCCESS else { return }
        defer { IOObjectRelease(iterator) }
        while true {
            let service = IOIteratorNext(iterator)
            if service == 0 { break }
            body(properties(of: service))
            IOObjectRelease(service)
        }
    }

    // MARK: - Thunderbolt

    /// Apple Silicon uses IOThunderboltSwitchType7; Intel Macs use the Intel
    /// JHL controller class. Matching both keeps this portable.
    /// Thunderbolt switches do NOT share a single class name. The host
    /// controllers on this Mac are `IOThunderboltSwitchType7`, while an attached
    /// CalDigit dock appears as `IOThunderboltSwitchIntelJHL8440` — the class is
    /// named after whichever controller silicon the *device* uses, so it varies
    /// by dock and by generation. Matching an enumerated list of class names
    /// silently misses hardware; matching the substring does not.
    private static func isSwitchClass(_ name: String) -> Bool {
        name.contains("ThunderboltSwitch")
    }

    static func thunderbolt() -> [TBDevice] {
        var devices: [TBDevice] = []

        var iterator: io_iterator_t = 0
        if IORegistryCreateIterator(kIOMainPortDefault, kIOServicePlane,
                                    IOOptionBits(kIORegistryIterateRecursively), &iterator) == KERN_SUCCESS {
            defer { IOObjectRelease(iterator) }
            while true {
                let entry = IOIteratorNext(iterator)
                if entry == 0 { break }
                defer { IOObjectRelease(entry) }

                var buffer = [CChar](repeating: 0, count: 256)
                guard IOObjectGetClass(entry, &buffer) == KERN_SUCCESS,
                      isSwitchClass(String(cString: buffer)) else { continue }

                let props = properties(of: entry)
                // Depth 0 is the host controller itself, not an attached device.
                let depth = (props["Depth"] as? NSNumber)?.intValue ?? 0
                guard depth > 0 else { continue }

                devices.append(TBDevice(
                    vendor: props["Device Vendor Name"] as? String ?? "Unknown",
                    model: props["Device Model Name"] as? String ?? "Unknown",
                    depth: depth,
                    route: (props["Route String"] as? NSNumber)?.intValue ?? 0,
                    // Read unsigned: Thunderbolt UIDs routinely exceed Int64.max,
                    // and `stringValue` would render them as negative numbers
                    // that no longer match what ioreg reports.
                    uid: (props["UID"] as? NSNumber).map { String($0.uint64Value) } ?? "?",
                    linkGbps: nil,
                    vendorID: (props["Vendor ID"] as? NSNumber)?.intValue))
            }
        }

        // "Link Bandwidth" is in units of 0.1 Gb/s: 400 = 40 Gb/s, 1200 = 120 Gb/s.
        // We take the fastest active Thunderbolt port as the negotiated link
        // speed. With a single dock this is exact; with a daisy chain it
        // reports the fastest hop rather than per-device speeds.
        var fastest = 0.0
        forEachService("IOThunderboltPort") { props in
            guard (props["Description"] as? String) == "Thunderbolt Port" else { return }
            let bandwidth = (props["Link Bandwidth"] as? NSNumber)?.doubleValue ?? 0
            if bandwidth > 0 { fastest = max(fastest, bandwidth / 10.0) }
        }
        if fastest > 0 {
            for index in devices.indices { devices[index].linkGbps = fastest }
        }

        return devices.sorted { $0.route < $1.route }
    }

    // MARK: - Power

    static func adapter() -> AdapterInfo {
        guard let details = IOPSCopyExternalPowerAdapterDetails()?.takeRetainedValue() as? [String: Any] else {
            return AdapterInfo()
        }
        return AdapterInfo(
            watts: (details["Watts"] as? NSNumber)?.intValue,
            id: (details["AdapterID"] as? NSNumber)?.intValue,
            name: details["Name"] as? String,
            serial: details["SerialString"] as? String ?? details["SerialNumber"] as? String,
            manufacturer: details["Manufacturer"] as? String)
    }

    struct BatteryState {
        var externalConnected = false
        var amperageMilliAmps = 0
        var voltage = 0.0
        var percent = 0
    }

    static func battery() -> BatteryState {
        var state = BatteryState()
        var found = false

        forEachService("AppleSmartBattery") { props in
            guard !found else { return }
            found = true
            state.externalConnected = (props["ExternalConnected"] as? Bool) ?? false
            // Read through int64Value: IOKit stores this signed, and it is only
            // `ioreg`'s text output that renders it as an unsigned 64-bit wrap.
            state.amperageMilliAmps = Int((props["InstantAmperage"] as? NSNumber)?.int64Value ?? 0)
            state.voltage = ((props["Voltage"] as? NSNumber)?.doubleValue ?? 0) / 1000.0

            let current = (props["CurrentCapacity"] as? NSNumber)?.doubleValue ?? 0
            let maximum = (props["MaxCapacity"] as? NSNumber)?.doubleValue ?? 0
            let ratio = maximum > 0 ? (current / maximum) * 100 : current
            state.percent = min(100, max(0, Int(ratio.rounded())))
        }

        return state
    }

    // MARK: - USB

    static func usb() -> [USBDevice] {
        var devices: [USBDevice] = []
        forEachService("IOUSBHostDevice") { props in
            guard let name = props["USB Product Name"] as? String else { return }
            func int(_ key: String) -> Int? { (props[key] as? NSNumber)?.intValue }

            devices.append(USBDevice(
                name: name,
                speed: int("Device Speed") ?? -1,
                locationID: (props["locationID"] as? NSNumber)?.uint32Value ?? 0,
                vendorID: int("idVendor"),
                vendorName: props["USB Vendor Name"] as? String,
                productID: int("idProduct"),
                serial: props["USB Serial Number"] as? String,
                deviceClass: int("bDeviceClass"),
                deviceSubClass: int("bDeviceSubClass"),
                deviceProtocol: int("bDeviceProtocol"),
                releaseBCD: int("bcdDevice"),
                usbVersionBCD: int("bcdUSB"),
                linkSpeedBitsPerSecond: int("UsbLinkSpeed"),
                usbAddress: int("USB Address")))
        }
        return devices.sorted { $0.locationID < $1.locationID }
    }

    // MARK: - Composite

    static func sample() -> Sample {
        let power = battery()
        return Sample(
            t: Date(),
            tb: thunderbolt(),
            adapter: adapter(),
            externalConnected: power.externalConnected,
            amperageMilliAmps: power.amperageMilliAmps,
            voltage: power.voltage,
            percent: power.percent,
            usb: usb())
    }
}
