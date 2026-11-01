package app.lanbridge.protocol

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertTrue

class MdnsBrowseTest {
    @Test
    fun query_asks_for_lanbridge_ptr_with_unicast_bit() {
        val packet = MdnsBrowse.query()
        val text = packet.toString(Charsets.US_ASCII)
        assertTrue(text.contains("_lanbridge"))
        assertEquals(0x80, packet[packet.size - 2].toInt() and 0xff)
        assertEquals(0x01, packet[packet.size - 1].toInt() and 0xff)
        assertEquals(12, ((packet[packet.size - 4].toInt() and 0xff) shl 8) or (packet[packet.size - 3].toInt() and 0xff))
    }

    @Test
    fun announcement_round_trip() {
        val packet = announcement(
            instance = "StudyPC._lanbridge._tcp.local",
            host = "lanbridge-111111111111.local",
            address = byteArrayOf(192.toByte(), 168.toByte(), 1, 20),
            port = 47821,
            txt = mapOf(
                "name" to "书房电脑",
                "fp" to "1104288a2a9d514d9a0a9e770b4983179b4ccb705a76ff1c59c3afba364ae749",
                "id" to "11111111-1111-1111-1111-111111111111",
                "port" to "47821",
                "role" to "pc"
            )
        )
        val found = MdnsBrowse.collect(packet)
        assertNotNull(found)
        assertEquals("192.168.1.20", found.host)
        assertEquals(47821, found.port)
        assertEquals("书房电脑", found.txt["name"])
        assertEquals("1104288a2a9d514d9a0a9e770b4983179b4ccb705a76ff1c59c3afba364ae749", found.txt["fp"])
    }

    private fun announcement(
        instance: String,
        host: String,
        address: ByteArray,
        port: Int,
        txt: Map<String, String>
    ): ByteArray {
        val writer = java.io.ByteArrayOutputStream()
        fun u16(value: Int) {
            writer.write((value shr 8) and 0xff)
            writer.write(value and 0xff)
        }
        fun u32(value: Int) {
            u16((value ushr 16) and 0xffff)
            u16(value and 0xffff)
        }
        fun name(dotted: String) {
            for (label in dotted.split('.').filter { it.isNotEmpty() }) {
                val raw = label.toByteArray(Charsets.UTF_8)
                writer.write(raw.size)
                writer.write(raw)
            }
            writer.write(0)
        }
        fun record(recordName: String, type: Int, body: ByteArray) {
            name(recordName)
            u16(type)
            u16(1)
            u32(120)
            u16(body.size)
            writer.write(body)
        }
        u16(0)
        u16(0x8400)
        u16(0)
        u16(4)
        u16(0)
        u16(0)
        val ptr = java.io.ByteArrayOutputStream().also { nested ->
            for (label in instance.split('.')) {
                val raw = label.toByteArray()
                nested.write(raw.size)
                nested.write(raw)
            }
            nested.write(0)
        }.toByteArray()
        record("_lanbridge._tcp.local", MdnsBrowse.TYPE_PTR, ptr)
        val srv = java.io.ByteArrayOutputStream()
        srv.write(0); srv.write(0); srv.write(0); srv.write(0)
        srv.write((port shr 8) and 0xff); srv.write(port and 0xff)
        for (label in host.split('.')) {
            val raw = label.toByteArray()
            srv.write(raw.size)
            srv.write(raw)
        }
        srv.write(0)
        record(instance, MdnsBrowse.TYPE_SRV, srv.toByteArray())
        val txtBytes = java.io.ByteArrayOutputStream()
        for ((key, value) in txt) {
            val raw = "$key=$value".toByteArray(Charsets.UTF_8)
            txtBytes.write(raw.size)
            txtBytes.write(raw)
        }
        record(instance, MdnsBrowse.TYPE_TXT, txtBytes.toByteArray())
        record(host, MdnsBrowse.TYPE_A, address)
        return writer.toByteArray()
    }
}
