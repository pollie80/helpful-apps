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

    var errorDescription: String? {
        switch self {
        case .notFound:
            return "No Claude Code credentials in the login keychain. Run `claude` once to sign in."
        case .unreadable(let status):
            let msg = SecCopyErrorMessageString(status, nil) as String? ?? "status \(status)"
            return "Keychain read denied: \(msg)"
        case .malformed(let detail):
            return "Credential blob not in the expected shape: \(detail)"
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

    // Deliberately read-only. Do not add a write path here.
    //
    // This item belongs to Claude Code, and touching it causes two distinct
    // kinds of damage:
    //
    // 1. Writing to a keychain item from a process that isn't on its trusted
    //    list makes macOS reset that item's ACL, which locks Claude Code out of
    //    its own credentials and produces an unstoppable stream of keychain
    //    password prompts.
    //
    // 2. Refresh tokens rotate. Redeeming the stored refresh token invalidates
    //    the copy Claude Code holds, so refreshing here - even if the result
    //    were written back perfectly - races Claude Code and can sign the user
    //    out of it.
    //
    // So this app only ever reads. When the stored token has expired it waits
    // for Claude Code to refresh it in the course of normal use, and shows a
    // short "waiting" state until then.
}
