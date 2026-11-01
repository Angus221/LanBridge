package app.lanbridge.protocol

import java.security.MessageDigest
import java.security.PrivateKey
import java.security.PublicKey
import java.security.SecureRandom
import java.security.Signature
import java.util.Locale

object Protocol {
    const val VERSION = 1
    const val SERVICE_TYPE = "_lanbridge._tcp"
    const val DEFAULT_PORT = 47821
    const val CHUNK_SIZE = 1_048_576
    const val MAX_TEXT_BYTES = 256 * 1024
    const val DISK_MARGIN_BYTES = 64L * 1024 * 1024
    const val MAX_CONCURRENT = 2
    const val FALLBACK_NAME = "received.bin"

    fun canonical(timestampMs: Long, nonce: String, method: String, path: String, contentSha256: String): String =
        listOf(timestampMs.toString(), nonce, method.uppercase(Locale.US), path, contentSha256).joinToString("\n")

    fun sha256Hex(data: ByteArray): String = hex(MessageDigest.getInstance("SHA-256").digest(data))

    fun sha256Hex(text: String): String = sha256Hex(text.toByteArray(Charsets.UTF_8))

    fun sha256Hex(stream: java.io.InputStream): String {
        val digest = MessageDigest.getInstance("SHA-256")
        val buffer = ByteArray(81920)
        while (true) {
            val read = stream.read(buffer)
            if (read < 0) break
            digest.update(buffer, 0, read)
        }
        return hex(digest.digest())
    }

    fun randomNonce(): String {
        val bytes = ByteArray(16)
        SecureRandom().nextBytes(bytes)
        return hex(bytes)
    }

    fun sign(key: PrivateKey, canonical: String): ByteArray {
        val signature = Signature.getInstance("SHA256withECDSA")
        signature.initSign(key)
        signature.update(canonical.toByteArray(Charsets.UTF_8))
        return signature.sign()
    }

    fun verify(key: PublicKey, canonical: String, signatureBytes: ByteArray): Boolean = try {
        val signature = Signature.getInstance("SHA256withECDSA")
        signature.initVerify(key)
        signature.update(canonical.toByteArray(Charsets.UTF_8))
        signature.verify(signatureBytes)
    } catch (_: Exception) {
        false
    }

    fun normalize(text: String?): String {
        if (text.isNullOrEmpty()) return ""
        return text.replace("\r\n", "\n").replace('\r', '\n').replace("\u0000", "")
    }

    fun exceedsLimit(text: String): Boolean = text.toByteArray(Charsets.UTF_8).size > MAX_TEXT_BYTES

    fun sanitize(input: String?): String {
        if (input.isNullOrEmpty()) return FALLBACK_NAME
        val parts = input.replace('\\', '/').split('/')
        var name = ""
        for (i in parts.indices.reversed()) {
            if (parts[i].isNotEmpty()) {
                name = parts[i]
                break
            }
        }
        if (name.isEmpty()) return FALLBACK_NAME
        val kept = StringBuilder(name.length)
        for (ch in name) {
            if (ch.code < 32) continue
            if (ch in "<>:\"/\\|?*") continue
            if (ch in '\u202A'..'\u202E') continue
            if (ch in '\u2066'..'\u2069') continue
            kept.append(ch)
        }
        name = kept.toString().trim(' ', '.')
        if (name.isEmpty()) return FALLBACK_NAME
        if (isReserved(name)) name = "_$name"
        if (name.length > 120) name = truncate(name, 120)
        name = name.trim(' ', '.')
        if (name.isEmpty()) return FALLBACK_NAME
        return name
    }

    fun isSensitive(text: String?): Boolean {
        if (text.isNullOrEmpty()) return false
        return SENSITIVE.any { it.containsMatchIn(text) }
    }

    fun evaluateReceive(
        originalName: String?,
        size: Long,
        freeBytes: Long,
        maxBytes: Long,
        autoReceive: Boolean,
        activeTransfers: Int,
        maxConcurrent: Int = MAX_CONCURRENT
    ): ReceiveDecision {
        val safe = sanitize(originalName)
        if (size < 0) return ReceiveDecision("reject", "invalid_size", safe)
        if (maxBytes > 0 && size > maxBytes) return ReceiveDecision("reject", "too_large", safe)
        if (activeTransfers >= maxConcurrent) return ReceiveDecision("reject", "busy", safe)
        if (freeBytes < size + DISK_MARGIN_BYTES) return ReceiveDecision("reject", "no_space", safe)
        if (!autoReceive) return ReceiveDecision("confirm", "needs_confirm", safe)
        return ReceiveDecision("accept", null, safe)
    }

    fun parsePairLink(raw: String?): PairLink? {
        if (raw.isNullOrBlank()) return null
        val uri = try {
            java.net.URI(raw.trim())
        } catch (_: Exception) {
            return null
        }
        if (!uri.scheme.equals("lanbridge", true) || !uri.host.equals("pair", true)) return null
        val query = uri.rawQuery ?: return null
        val map = query.split('&').mapNotNull { part ->
            val eq = part.indexOf('=')
            if (eq <= 0) null else part.substring(0, eq) to java.net.URLDecoder.decode(part.substring(eq + 1), Charsets.UTF_8)
        }.toMap()
        val host = map["host"] ?: return null
        val port = map["port"]?.toIntOrNull() ?: return null
        val id = map["id"] ?: return null
        val fp = map["fp"] ?: return null
        val pin = map["pin"] ?: return null
        val exp = map["exp"]?.toLongOrNull() ?: return null
        if (port !in 1..65535 || pin.length != 6 || fp.length != 64) return null
        return PairLink(host, port, id, fp.lowercase(Locale.US), pin, exp)
    }

    private fun isReserved(name: String): Boolean {
        val dot = name.lastIndexOf('.')
        var base = if (dot > 0) name.substring(0, dot) else name
        base = base.uppercase(Locale.US)
        if (base == "CON" || base == "PRN" || base == "AUX" || base == "NUL") return true
        if (base.length == 4 && (base.startsWith("COM") || base.startsWith("LPT"))) {
            val digit = base[3]
            if (digit in '1'..'9') return true
        }
        return false
    }

    private fun truncate(name: String, max: Int): String {
        val dot = name.lastIndexOf('.')
        if (dot > 0 && name.length - dot <= 16) {
            val ext = name.substring(dot)
            val keep = max - ext.length
            if (keep < 1) return name.substring(0, max)
            return name.substring(0, keep) + ext
        }
        return name.substring(0, max)
    }

    private fun hex(bytes: ByteArray): String = bytes.joinToString("") { "%02x".format(it) }

    private val SENSITIVE = listOf(
        """-----BEGIN (?:RSA |OPENSSH |EC |DSA )?PRIVATE KEY-----""".toRegex(),
        """\bAKIA[0-9A-Z]{16}\b""".toRegex(),
        """\bsk-[A-Za-z0-9]{16,}""".toRegex(),
        """\bghp_[A-Za-z0-9]{20,}""".toRegex(),
        """\bxox[baprs]-[A-Za-z0-9-]{10,}""".toRegex(),
        """(?i)(?:password|passwd|pwd|secret|api[_-]?key|access[_-]?key|token|密码|口令)\s*[:=：]\s*\S{4,}""".toRegex()
    )
}

data class ReceiveDecision(val action: String, val reason: String?, val safeName: String)

data class PairLink(
    val host: String,
    val port: Int,
    val deviceId: String,
    val fingerprint: String,
    val pin: String,
    val expiresAtUnixSeconds: Long
)
