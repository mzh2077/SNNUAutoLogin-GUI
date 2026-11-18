import AppKit
import CoreWLAN
import CoreLocation
import Security
import ServiceManagement

private let service = "org.snnuautologin.mac"
private final class LocationPermission: NSObject, CLLocationManagerDelegate {
    let manager = CLLocationManager()
    override init() { super.init(); manager.delegate = self }
}
private var location: LocationPermission?

@_cdecl("snnu_request_location")
public func requestLocation() {
    precondition(Thread.isMainThread)
    if location == nil { location = LocationPermission() }
    location?.manager.requestWhenInUseAuthorization()
}

@_cdecl("snnu_wifi_kind")
public func wifiKind(_ name: UnsafePointer<CChar>) -> Int32 {
    return CWWiFiClient.interfaceNames()?.contains(String(cString: name)) == true ? 1 : 0
}

@_cdecl("snnu_ssid")
public func ssid(_ name: UnsafePointer<CChar>) -> UnsafeMutablePointer<CChar>? {
    let value = CWWiFiClient.shared().interface(withName: String(cString: name))?.ssid() ?? ""
    return strdup(value)
}

@_cdecl("snnu_password_read")
public func passwordRead(_ key: UnsafePointer<CChar>, _ code: UnsafeMutablePointer<Int32>) -> UnsafeMutablePointer<CChar>? {
    let query: [String: Any] = [
        kSecClass as String: kSecClassGenericPassword,
        kSecAttrService as String: service,
        kSecAttrAccount as String: String(cString: key),
        kSecReturnData as String: true,
        kSecMatchLimit as String: kSecMatchLimitOne
    ]
    var result: CFTypeRef?
    code.pointee = SecItemCopyMatching(query as CFDictionary, &result)
    guard code.pointee == errSecSuccess, let data = result as? Data,
          let value = String(data: data, encoding: .utf8) else { return nil }
    return strdup(value)
}

@_cdecl("snnu_password_write")
public func passwordWrite(_ key: UnsafePointer<CChar>, _ secret: UnsafePointer<CChar>) -> Int32 {
    let query: [String: Any] = [
        kSecClass as String: kSecClassGenericPassword,
        kSecAttrService as String: service,
        kSecAttrAccount as String: String(cString: key)
    ]
    let attributes: [String: Any] = [kSecValueData as String: Data(String(cString: secret).utf8)]
    let status = SecItemUpdate(query as CFDictionary, attributes as CFDictionary)
    if status != errSecItemNotFound { return status }
    var item = query
    item[kSecValueData as String] = attributes[kSecValueData as String]
    item[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
    return SecItemAdd(item as CFDictionary, nil)
}

@_cdecl("snnu_password_delete")
public func passwordDelete(_ key: UnsafePointer<CChar>) -> Int32 {
    return SecItemDelete([
        kSecClass as String: kSecClassGenericPassword,
        kSecAttrService as String: service,
        kSecAttrAccount as String: String(cString: key)
    ] as CFDictionary)
}

@_cdecl("snnu_login_item")
public func loginItem(_ enabled: Int32) -> Int32 {
    do {
        if enabled != 0 { try SMAppService.mainApp.register() }
        else { try SMAppService.mainApp.unregister() }
        return 0
    } catch { return 1 }
}

@_cdecl("snnu_free")
public func freeString(_ value: UnsafeMutableRawPointer?) { free(value) }
