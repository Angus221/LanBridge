package app.lanbridge.protocol

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

class PairingUiTest {
    @Test
    fun connect_requires_a_target_and_six_digits() {
        assertFalse(PairingUi.canConnect(false, "123456"))
        assertTrue(PairingUi.blockReason(false, "123456")!!.contains("目标电脑"))
        assertFalse(PairingUi.canConnect(true, "12345"))
        assertTrue(PairingUi.blockReason(true, "12345")!!.contains("6 位"))
        assertTrue(PairingUi.canConnect(true, "123456"))
        assertNull(PairingUi.blockReason(true, "123456"))
        assertEquals("123456", PairingUi.pinDigits("12 34-56"))
    }

    @Test
    fun manual_endpoint_accepts_ip_with_optional_port() {
        assertEquals(ManualEndpoint("192.168.1.20", 47821), PairingUi.parseManualEndpoint("192.168.1.20"))
        assertEquals(ManualEndpoint("192.168.1.20", 47821), PairingUi.parseManualEndpoint(" 192.168.1.20:47821 "))
        assertEquals(ManualEndpoint("10.0.0.8", 9), PairingUi.parseManualEndpoint("10.0.0.8：9"))
        assertNull(PairingUi.parseManualEndpoint("电脑.local"))
        assertNull(PairingUi.parseManualEndpoint("192.168.1.20:0"))
        assertNull(PairingUi.parseManualEndpoint("192.168.01.2"))
        assertNull(PairingUi.parseManualEndpoint(""))
    }

    @Test
    fun info_json_needs_a_fingerprint_and_port() {
        val parsed = PairingUi.parseInfo("""{"name":"书房","deviceId":"abc","fingerprint":"1104288a2a9d514d9a0a9e770b4983179b4ccb705a76ff1c59c3afba364ae749","port":47821}""")
        assertNotNull(parsed)
        assertEquals("书房", parsed.name)
        assertEquals(47821, parsed.port)
        assertNull(PairingUi.parseInfo("""{"fingerprint":"zz","port":1}"""))
    }

    @Test
    fun luma_respects_row_stride() {
        val width = 2
        val height = 2
        val buffer = byteArrayOf(1, 2, 9, 3, 4, 9)
        val luma = extractLuma(buffer, width, height, rowStride = 3, pixelStride = 1)
        assertNotNull(luma)
        assertEquals(listOf<Byte>(1, 2, 3, 4), luma.toList())
        assertNull(extractLuma(buffer, 4, 4, 3, 1))
    }
}
