package app.lanbridge.protocol

data class ManualEndpoint(val host: String, val port: Int)

data class PcInfo(val name: String, val deviceId: String, val fingerprint: String, val port: Int)

object PairingUi {
    fun pinDigits(pin: String): String = pin.filter { it.isDigit() }

    fun canConnect(hasTarget: Boolean, pin: String): Boolean = blockReason(hasTarget, pin) == null

    fun blockReason(hasTarget: Boolean, pin: String): String? {
        if (!hasTarget) return "还没有目标电脑。请搜索局域网、填写 IP 和端口，或扫描二维码。"
        val digits = pinDigits(pin)
        if (digits.length < 6 || pin.any { !it.isDigit() }) return "配对码需要 6 位数字。"
        return null
    }

    fun parseManualEndpoint(raw: String?, defaultPort: Int = Protocol.DEFAULT_PORT): ManualEndpoint? {
        if (raw.isNullOrBlank()) return null
        val text = raw.trim().replace('：', ':').replace(" ", "")
        if (text.isEmpty()) return null
        val hostPart: String
        val port: Int
        val colon = text.lastIndexOf(':')
        if (colon > 0 && text.indexOf(':') == colon) {
            hostPart = text.substring(0, colon)
            port = text.substring(colon + 1).toIntOrNull() ?: return null
        } else if (colon < 0) {
            hostPart = text
            port = defaultPort
        } else {
            return null
        }
        if (port !in 1..65535) return null
        if (!isIpv4(hostPart)) return null
        return ManualEndpoint(hostPart, port)
    }

    fun parseInfo(json: String?): PcInfo? {
        if (json.isNullOrBlank()) return null
        val fp = jsonField(json, "fingerprint")?.lowercase() ?: return null
        val port = jsonField(json, "port")?.toIntOrNull() ?: return null
        if (fp.length != 64 || port !in 1..65535) return null
        if (fp.any { it !in '0'..'9' && it !in 'a'..'f' }) return null
        return PcInfo(
            name = jsonField(json, "name").orEmpty().ifBlank { "电脑" },
            deviceId = jsonField(json, "deviceId").orEmpty(),
            fingerprint = fp,
            port = port
        )
    }

    private fun jsonField(json: String, name: String): String? {
        val key = "\"$name\""
        val start = json.indexOf(key)
        if (start < 0) return null
        var index = start + key.length
        while (index < json.length && json[index].isWhitespace()) index++
        if (index >= json.length || json[index] != ':') return null
        index++
        while (index < json.length && json[index].isWhitespace()) index++
        if (index >= json.length) return null
        if (json[index] == '"') {
            val end = json.indexOf('"', index + 1)
            if (end < 0) return null
            return json.substring(index + 1, end)
        }
        val end = (index until json.length).firstOrNull { json[it] == ',' || json[it] == '}' } ?: json.length
        return json.substring(index, end).trim()
    }

    private fun isIpv4(host: String): Boolean {
        val parts = host.split('.')
        if (parts.size != 4) return false
        return parts.all { part ->
            if (part.isEmpty() || part.length > 3 || (part.length > 1 && part.startsWith('0'))) return false
            val value = part.toIntOrNull() ?: return false
            value in 0..255
        }
    }
}

fun extractLuma(buffer: ByteArray, width: Int, height: Int, rowStride: Int, pixelStride: Int): ByteArray? {
    if (width <= 0 || height <= 0 || rowStride <= 0 || pixelStride <= 0) return null
    val out = ByteArray(width * height)
    var offset = 0
    for (y in 0 until height) {
        val row = y * rowStride
        for (x in 0 until width) {
            val index = row + x * pixelStride
            if (index >= buffer.size) return null
            out[offset++] = buffer[index]
        }
    }
    return out
}
