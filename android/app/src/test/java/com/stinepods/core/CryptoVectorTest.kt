package com.stinepods.core

import com.stinepods.core.LinkCrypto.hex
import com.stinepods.core.LinkCrypto.unhex
import org.junit.Assert.assertEquals
import org.junit.Test

/** protocol/vectors/crypto.txt: the C# implementation checks the same values. */
class CryptoVectorTest {
    private val v = Vectors.file("crypto.txt").readLines()
        .filter { it.isNotEmpty() && !it.startsWith("#") }
        .associate { it.substringBefore(' ') to it.substringAfter(' ') }

    @Test
    fun derivedValuesMatch() {
        val phone = LinkCrypto.KeyPair.fixed(unhex(v.getValue("phone_d")), unhex(v.getValue("phone_pub")))
        val pc = LinkCrypto.KeyPair.fixed(unhex(v.getValue("pc_d")), unhex(v.getValue("pc_pub")))
        val shared = phone.agree(pc.public)
        assertEquals(v["shared"], hex(shared))
        assertEquals(v["shared"], hex(pc.agree(phone.public)))
        assertEquals(v.getValue("code").toInt(), LinkCrypto.pairCode(shared, phone.public, pc.public))

        val longKey = LinkCrypto.longKey(shared)
        assertEquals(v["long_key"], hex(longKey))
        val connKey = LinkCrypto.connKey(longKey, unhex(v.getValue("phone_nonce")), unhex(v.getValue("pc_nonce")))
        assertEquals(v["conn_key"], hex(connKey))

        val body = unhex(v.getValue("body"))
        assertEquals(v["tag_P_7"], hex(LinkCrypto.tag(connKey, 'P'.code.toByte(), 7, body)))
        assertEquals(v["tag_C_7"], hex(LinkCrypto.tag(connKey, 'C'.code.toByte(), 7, body)))
    }

    @Test
    fun generatedKeysAgree() {
        val a = LinkCrypto.KeyPair.generate()
        val b = LinkCrypto.KeyPair.generate()
        assertEquals(65, a.public.size)
        assertEquals(hex(a.agree(b.public)), hex(b.agree(a.public)))
    }
}
