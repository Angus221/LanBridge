package app.lanbridge.phone.net

import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import app.lanbridge.protocol.Protocol
import java.security.KeyPairGenerator
import java.security.KeyStore
import java.security.PrivateKey
import java.security.spec.ECGenParameterSpec

class DeviceIdentity {
    private val alias = "lanbridge-p256"

    init {
        val store = keyStore()
        if (!store.containsAlias(alias)) {
            val generator = KeyPairGenerator.getInstance("EC", "AndroidKeyStore")
            val spec = KeyGenParameterSpec.Builder(alias, KeyProperties.PURPOSE_SIGN)
                .setAlgorithmParameterSpec(ECGenParameterSpec("secp256r1"))
                .setDigests(KeyProperties.DIGEST_SHA256)
                .build()
            generator.initialize(spec)
            generator.generateKeyPair()
        }
    }

    fun publicSpki(): ByteArray {
        val cert = keyStore().getCertificate(alias) ?: error("手机密钥没有生成成功")
        return cert.publicKey.encoded
    }

    fun sign(canonical: String): ByteArray {
        val key = keyStore().getKey(alias, null) as PrivateKey
        return Protocol.sign(key, canonical)
    }

    private fun keyStore(): KeyStore = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
}
