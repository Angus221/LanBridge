package app.lanbridge.protocol

import org.json.JSONObject
import java.security.KeyFactory
import java.security.spec.X509EncodedKeySpec
import java.util.Base64
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

class VectorTest {
    private val json = JSONObject(java.io.File(System.getProperty("lanbridge.vectors")).readText())

    @Test
    fun sha256_matches_shared_vectors() {
        val cases = json.getJSONArray("sha256")
        for (i in 0 until cases.length()) {
            val item = cases.getJSONObject(i)
            assertEquals(item.getString("hex"), Protocol.sha256Hex(item.getString("input")), item.getString("input"))
        }
    }

    @Test
    fun normalize_matches_shared_vectors() {
        val cases = json.getJSONArray("normalize")
        for (i in 0 until cases.length()) {
            val item = cases.getJSONObject(i)
            assertEquals(item.getString("expected"), Protocol.normalize(item.getString("input")))
        }
    }

    @Test
    fun sanitize_matches_shared_vectors() {
        val cases = json.getJSONArray("sanitize")
        for (i in 0 until cases.length()) {
            val item = cases.getJSONObject(i)
            assertEquals(item.getString("expected"), Protocol.sanitize(item.getString("input")), item.getString("input"))
        }
    }

    @Test
    fun sensitive_matches_shared_vectors() {
        val cases = json.getJSONArray("sensitive")
        for (i in 0 until cases.length()) {
            val item = cases.getJSONObject(i)
            assertEquals(item.getBoolean("sensitive"), Protocol.isSensitive(item.getString("input")), item.getString("input"))
        }
    }

    @Test
    fun receive_policy_matches_shared_vectors() {
        val cases = json.getJSONArray("receive")
        for (i in 0 until cases.length()) {
            val item = cases.getJSONObject(i)
            val decision = Protocol.evaluateReceive(
                item.getString("name"),
                item.getLong("size"),
                item.getLong("free"),
                item.getLong("max"),
                item.getBoolean("auto"),
                item.getInt("active")
            )
            assertEquals(item.getString("action"), decision.action, item.toString())
            val reason = if (item.isNull("reason")) null else item.getString("reason")
            assertEquals(reason, decision.reason, item.toString())
            assertEquals(item.getString("safe"), decision.safeName, item.toString())
        }
    }

    @Test
    fun ecdsa_vector_verifies_and_canonical_matches() {
        val ecdsa = json.getJSONObject("ecdsa")
        val canonical = Protocol.canonical(
            ecdsa.getLong("timestampMs"),
            ecdsa.getString("nonce"),
            ecdsa.getString("method"),
            ecdsa.getString("path"),
            ecdsa.getString("contentSha256")
        )
        assertEquals(ecdsa.getString("canonical"), canonical)
        val key = KeyFactory.getInstance("EC").generatePublic(
            X509EncodedKeySpec(Base64.getDecoder().decode(ecdsa.getString("publicKeySpkiBase64")))
        )
        assertTrue(Protocol.verify(key, canonical, Base64.getDecoder().decode(ecdsa.getString("signatureDerBase64"))))
    }

    @Test
    fun pairing_link_round_trip() {
        val link = "lanbridge://pair?v=1&host=192.168.1.20&port=47821&id=11111111-1111-1111-1111-111111111111&fp=1104288a2a9d514d9a0a9e770b4983179b4ccb705a76ff1c59c3afba364ae749&pin=042813&exp=1800000000"
        val parsed = Protocol.parsePairLink(link)!!
        assertEquals("192.168.1.20", parsed.host)
        assertEquals(47821, parsed.port)
        assertEquals("042813", parsed.pin)
        assertEquals(1800000000L, parsed.expiresAtUnixSeconds)
    }
}
