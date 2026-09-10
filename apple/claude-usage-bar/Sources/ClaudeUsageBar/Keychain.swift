import Foundation
import Security

/// The credential blob Claude Code stores in the login keychain under
/// service "Claude Code-credentials", account = the local macOS short username.
struct OAuthCredentials: Codable {
    struct Bundle: Codable {
        var accessToken: String
        var refreshToken: String?
        var expiresAt: Double?          // milliseconds since epoch
        var scopes: [String]?
        var subscriptionType: String?
    }
    var claudeAiOauth: Bundle
}

enum KeychainError: LocalizedError {
    case notFound
    case unreadable(OSStatus)
    case malformed(String)
    case writeFailed(OSStatus)

    var errorDescription: String? {
        switch self {
        case .notFound:
            return "No Claude Code credentials in the login keychain. Run `claude` once to sign in."
        case .unreadable(let status):
            let msg = SecCopyErrorMessageString(status, nil) as String? ?? "status \(status)"
            return "Keychain read denied: \(msg)"
        case .malformed(let detail):
            return "Credential blob not in the expected shape: \(detail)"
        case .writeFailed(let status):
            let msg = SecCopyErrorMessageString(status, nil) as String? ?? "status \(status)"
            return "Could not write refreshed token back to the keychain: \(msg)"
        }
    }
}

struct Keychain {
    static let service = "Claude Code-credentials"

    /// Claude Code keys the item by the macOS short username.
    static var account: String { NSUserName() }

    static func read() throws -> OAuthCredentials {
        var query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecReturnData as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne,
        ]
        query[kSecAttrAccount as String] = account

        var item: CFTypeRef?
        var status = SecItemCopyMatching(query as CFDictionary, &item)

        // Fall back to a service-only match in case the account attribute differs.
        if status == errSecItemNotFound {
            query.removeValue(forKey: kSecAttrAccount as String)
            status = SecItemCopyMatching(query as CFDictionary, &item)
        }

        guard status != errSecItemNotFound else { throw KeychainError.notFound }
        guard status == errSecSuccess, let data = item as? Data else {
            throw KeychainError.unreadable(status)
        }

        do {
            return try JSONDecoder().decode(OAuthCredentials.self, from: data)
        } catch {
            throw KeychainError.malformed(String(describing: error))
        }
    }

    /// Persist a rotated token pair, preserving every other field in the blob.
    static func update(accessToken: String, refreshToken: String?, expiresAt: Double?) throws {
        var creds = try read()
        creds.claudeAiOauth.accessToken = accessToken
        if let refreshToken { creds.claudeAiOauth.refreshToken = refreshToken }
        if let expiresAt { creds.claudeAiOauth.expiresAt = expiresAt }

        let body = try JSONEncoder().encode(creds)
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
        ]
        let status = SecItemUpdate(query as CFDictionary, [kSecValueData as String: body] as CFDictionary)
        guard status == errSecSuccess else { throw KeychainError.writeFailed(status) }
    }
}
