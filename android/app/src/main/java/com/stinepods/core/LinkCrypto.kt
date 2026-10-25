package com.stinepods.core

import java.math.BigInteger
import java.nio.ByteBuffer
import java.security.AlgorithmParameters
import java.security.KeyFactory
import java.security.KeyPairGenerator
import java.security.MessageDigest
import java.security.PrivateKey
import java.security.interfaces.ECPublicKey
import java.security.spec.ECGenParameterSpec
import java.security.spec.ECParameterSpec
import java.security.spec.ECPoint
import java.security.spec.ECPrivateKeySpec
import java.security.spec.ECPublicKeySpec
import javax.crypto.KeyAgreement
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec

/** Key derivation and message tags for the link (protocol/PROTOCOL.md). Port of LinkCrypto.cs. */
object LinkCrypto {
    const val TAG_LENGTH = 16

    /** 6-digit comparison code both devices show during pairing. */
    fun pairCode(shared: ByteArray, phonePub: ByteArray, pcPub: ByteArray): Int {
        val h = MessageDigest.getInstance("SHA-256").digest("stinepods-pair-v1".toByteArray() + shared + phonePub + pcPub)
        return ((ByteBuffer.wrap(h).int.toLong() and 0xffffffffL) % 1_000_000).toInt()
    }

    fun longKey(shared: ByteArray) = hmac(shared, "stinepods-key-v1".toByteArray())

    fun connKey(longKey: ByteArray, phoneNonce: ByteArray, pcNonce: ByteArray) =
        hmac(longKey, "stinepods-conn-v1".toByteArray() + phoneNonce + pcNonce)

    /** HMAC-SHA256(connKey, dir || seq || body) truncated; dir is 'P' (phone→PC) or 'C' (PC→phone). */
    fun tag(connKey: ByteArray, dir: Byte, seq: Long, body: ByteArray): ByteArray {
        val data = ByteBuffer.allocate(1 + 8 + body.size).put(dir).putLong(seq).put(body).array()
        return hmac(connKey, data).copyOf(TAG_LENGTH)
    }

    fun hmac(key: ByteArray, data: ByteArray): ByteArray =
        Mac.getInstance("HmacSHA256").run { init(SecretKeySpec(key, "HmacSHA256")); doFinal(data) }

    private val p256: ECParameterSpec by lazy {
        AlgorithmParameters.getInstance("EC").run {
            init(ECGenParameterSpec("secp256r1"))
            getParameterSpec(ECParameterSpec::class.java)
        }
    }

    /** A P-256 key pair whose public key is exported as an uncompressed point (0x04 || X || Y). */
    class KeyPair private constructor(private val priv: PrivateKey, val public: ByteArray) {
        companion object {
            fun generate(): KeyPair {
                val kp = KeyPairGenerator.getInstance("EC").run { initialize(ECGenParameterSpec("secp256r1")); generateKeyPair() }
                return KeyPair(kp.private, encode((kp.public as ECPublicKey).w))
            }

            /** Fixed key, for test vectors. */
            fun fixed(d: ByteArray, uncompressedPublic: ByteArray): KeyPair {
                val key = KeyFactory.getInstance("EC").generatePrivate(ECPrivateKeySpec(BigInteger(1, d), p256))
                return KeyPair(key, uncompressedPublic)
            }
        }

        /** Raw ECDH shared secret (the x coordinate, 32 bytes). */
        fun agree(peerUncompressedPublic: ByteArray): ByteArray {
            require(peerUncompressedPublic.size == 65 && peerUncompressedPublic[0] == 4.toByte()) { "bad public key" }
            val point = ECPoint(
                BigInteger(1, peerUncompressedPublic.copyOfRange(1, 33)),
                BigInteger(1, peerUncompressedPublic.copyOfRange(33, 65)),
            )
            val peer = KeyFactory.getInstance("EC").generatePublic(ECPublicKeySpec(point, p256))
            return KeyAgreement.getInstance("ECDH").run { init(priv); doPhase(peer, true); generateSecret() }
        }
    }

    private fun encode(w: ECPoint) = byteArrayOf(4) + fixed32(w.affineX) + fixed32(w.affineY)

    private fun fixed32(v: BigInteger): ByteArray {
        val b = v.toByteArray()   // may carry a sign byte or be short
        return when {
            b.size == 32 -> b
            b.size > 32 -> b.copyOfRange(b.size - 32, b.size)
            else -> ByteArray(32 - b.size) + b
        }
    }

    fun hex(b: ByteArray) = b.joinToString("") { "%02x".format(it) }
    fun unhex(s: String) = ByteArray(s.length / 2) { s.substring(it * 2, it * 2 + 2).toInt(16).toByte() }
}
