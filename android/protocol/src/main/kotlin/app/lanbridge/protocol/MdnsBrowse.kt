package app.lanbridge.protocol

data class LanAnnouncement(
    val instance: String,
    val hostName: String,
    val host: String,
    val port: Int,
    val txt: Map<String, String>
)

object MdnsBrowse {
    const val TYPE_A = 1
    const val TYPE_PTR = 12
    const val TYPE_TXT = 16
    const val TYPE_SRV = 33
    const val SERVICE = "_lanbridge._tcp.local"

    fun query(service: String = SERVICE): ByteArray {
        val writer = PacketWriter()
        writer.u16(0)
        writer.u16(0)
        writer.u16(1)
        writer.u16(0)
        writer.u16(0)
        writer.u16(0)
        writer.name(service)
        writer.u16(TYPE_PTR)
        writer.u16(0x8001)
        return writer.bytes()
    }

    fun collect(packet: ByteArray): LanAnnouncement? {
        if (packet.size < 12) return null
        var offset = 2
        val flags = u16(packet, offset)
        offset += 2
        if (flags and 0x8000 == 0) return null
        val questions = u16(packet, offset)
        offset += 2
        val answers = u16(packet, offset)
        offset += 2
        val authority = u16(packet, offset)
        offset += 2
        val additional = u16(packet, offset)
        offset += 2
        repeat(questions) {
            offset = skipName(packet, offset)
            if (offset < 0 || offset + 4 > packet.size) return null
            offset += 4
        }
        val records = ArrayList<Record>(answers + authority + additional)
        repeat(answers + authority + additional) {
            if (offset < 0 || offset >= packet.size) return@repeat
            val named = readName(packet, offset, packet) ?: return null
            offset = named.second
            if (offset + 10 > packet.size) return null
            val type = u16(packet, offset)
            offset += 2
            offset += 2
            offset += 4
            val length = u16(packet, offset)
            offset += 2
            if (offset + length > packet.size) return null
            records.add(Record(named.first, type, packet.copyOfRange(offset, offset + length)))
            offset += length
        }
        val ptr = records.firstOrNull { record ->
            record.type == TYPE_PTR && record.name.equals(SERVICE, ignoreCase = true)
        } ?: return null
        val instance = readName(ptr.data, 0, packet)?.first ?: return null
        val srv = records.firstOrNull { it.type == TYPE_SRV && it.name.equals(instance, ignoreCase = true) }
        val txt = records.firstOrNull { it.type == TYPE_TXT && it.name.equals(instance, ignoreCase = true) }
        val hostName = if (srv != null && srv.data.size >= 6) readName(srv.data, 6, packet)?.first.orEmpty() else ""
        val address = records.firstOrNull { record ->
            record.type == TYPE_A && record.data.size == 4 &&
                (hostName.isEmpty() || record.name.equals(hostName, ignoreCase = true))
        }?.data
        if (address == null || address.size != 4) return null
        val portFromSrv = if (srv != null && srv.data.size >= 6) u16(srv.data, 4) else 0
        val fields = if (txt != null) decodeTxt(txt.data) else emptyMap()
        val port = portFromSrv.takeIf { it in 1..65535 } ?: fields["port"]?.toIntOrNull() ?: return null
        val host = address.joinToString(".") { (it.toInt() and 0xff).toString() }
        return LanAnnouncement(instance, hostName, host, port, fields)
    }

    private fun decodeTxt(data: ByteArray): Map<String, String> {
        val map = LinkedHashMap<String, String>()
        var offset = 0
        while (offset < data.size) {
            val length = data[offset].toInt() and 0xff
            offset++
            if (length == 0 || offset + length > data.size) break
            val text = data.copyOfRange(offset, offset + length).toString(Charsets.UTF_8)
            offset += length
            val eq = text.indexOf('=')
            if (eq <= 0) continue
            map[text.substring(0, eq)] = text.substring(eq + 1)
        }
        return map
    }

    private data class Record(val name: String, val type: Int, val data: ByteArray)

    private fun u16(packet: ByteArray, offset: Int): Int =
        ((packet[offset].toInt() and 0xff) shl 8) or (packet[offset + 1].toInt() and 0xff)

    private fun skipName(packet: ByteArray, offset: Int): Int {
        val named = readName(packet, offset, packet) ?: return -1
        return named.second
    }

    private fun readName(packet: ByteArray, offset: Int, pointerSpace: ByteArray): Pair<String, Int>? {
        val labels = ArrayList<String>()
        var cursor = offset
        var jumped = false
        var end = offset
        var guard = 0
        var buffer = packet
        while (guard++ < 128 && cursor < buffer.size) {
            val length = buffer[cursor].toInt() and 0xff
            if (length == 0) {
                if (!jumped) end = cursor + 1
                return labels.joinToString(".") to end
            }
            if (length and 0xC0 == 0xC0) {
                if (cursor + 1 >= buffer.size) return null
                val pointer = ((length and 0x3F) shl 8) or (buffer[cursor + 1].toInt() and 0xff)
                if (!jumped) end = cursor + 2
                jumped = true
                cursor = pointer
                buffer = pointerSpace
                continue
            }
            cursor++
            if (cursor + length > buffer.size) return null
            labels.add(buffer.copyOfRange(cursor, cursor + length).toString(Charsets.UTF_8))
            cursor += length
            if (!jumped) end = cursor
        }
        return null
    }

    private class PacketWriter {
        private val out = java.io.ByteArrayOutputStream()
        fun u16(value: Int) {
            out.write((value shr 8) and 0xff)
            out.write(value and 0xff)
        }
        fun name(dotted: String) {
            for (label in dotted.split('.').filter { it.isNotEmpty() }) {
                val raw = label.toByteArray(Charsets.UTF_8)
                val size = raw.size.coerceAtMost(63)
                out.write(size)
                out.write(raw, 0, size)
            }
            out.write(0)
        }
        fun bytes(): ByteArray = out.toByteArray()
    }
}
