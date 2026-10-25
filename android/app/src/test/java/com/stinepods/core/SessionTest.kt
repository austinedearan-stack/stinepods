package com.stinepods.core

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class SessionTest {
    private class MemKeys : KeyStore {
        val keys = mutableMapOf<String, ByteArray>()
        override fun get(peerId: String) = keys[peerId]
        override fun put(peerId: String, key: ByteArray) { keys[peerId] = key }
    }

    /** A phone and a PC session wired back to back, delivering frames until both go quiet. */
    private class Pair(phoneKeys: KeyStore, pcKeys: KeyStore) {
        val phone = Session(Role.PHONE, "phone-1", "Stine", phoneKeys)
        val pc = Session(Role.PC, "pc-1", "AUSTINE", pcKeys)
        val phoneEvents = mutableListOf<LinkEvent>()
        val pcEvents = mutableListOf<LinkEvent>()
        private val toPhone = FrameReader()
        private val toPc = FrameReader()

        init { deliver(phone.start(), pc.start()) }

        fun deliver(fromPhone0: List<ByteArray>, fromPc0: List<ByteArray>) {
            var fromPhone = fromPhone0
            var fromPc = fromPc0
            while (fromPhone.isNotEmpty() || fromPc.isNotEmpty()) {
                val nextPhone = mutableListOf<ByteArray>()
                val nextPc = mutableListOf<ByteArray>()
                for (f in fromPhone) for (frame in toPc.push(f)) pc.receive(frame).let { nextPc += it.out; pcEvents += it.events }
                for (f in fromPc) for (frame in toPhone.push(f)) phone.receive(frame).let { nextPhone += it.out; phoneEvents += it.events }
                fromPhone = nextPhone
                fromPc = nextPc
            }
        }

        fun confirmBoth() {
            val a = phone.confirmPairing(true).also { phoneEvents += it.events }
            val b = pc.confirmPairing(true).also { pcEvents += it.events }
            deliver(a.out, b.out)
        }
    }

    @Test
    fun firstConnectionPairsWithMatchingCodesThenAuthenticates() {
        val p = Pair(MemKeys(), MemKeys())
        val phoneCode = p.phoneEvents.filterIsInstance<LinkEvent.PairingCode>().single()
        val pcCode = p.pcEvents.filterIsInstance<LinkEvent.PairingCode>().single()
        assertEquals(phoneCode.code, pcCode.code)
        assertEquals("AUSTINE", phoneCode.peerName)
        assertFalse(p.phone.ready)

        p.confirmBoth()
        assertTrue(p.phone.ready && p.pc.ready)
        p.deliver(listOf(p.phone.send(Msg("claim", "AA:BB:CC:DD:EE:FF", 3))), emptyList())
        assertEquals(Msg("claim", "AA:BB:CC:DD:EE:FF", 3), p.pcEvents.filterIsInstance<LinkEvent.Incoming>().single().m)
    }

    @Test
    fun reconnectUsesStoredKeysWithoutPairing() {
        val phoneKeys = MemKeys()
        val pcKeys = MemKeys()
        Pair(phoneKeys, pcKeys).confirmBoth()
        assertArrayEquals(phoneKeys.keys["pc-1"], pcKeys.keys["phone-1"])

        val p = Pair(phoneKeys, pcKeys)
        assertTrue(p.phoneEvents.none { it is LinkEvent.PairingCode })
        assertTrue(p.phone.ready && p.pc.ready)
    }

    @Test
    fun tamperedAndReplayedFramesAreDropped() {
        val p = Pair(MemKeys(), MemKeys())
        p.confirmBoth()
        val frame = p.phone.send(Msg("claim", "AA", 1))
        val tampered = frame.clone().also { it[it.size - 2] = (it[it.size - 2].toInt() xor 1).toByte() }
        p.deliver(listOf(tampered), emptyList())
        assertTrue(p.pcEvents.none { it is LinkEvent.Incoming })
        p.deliver(listOf(frame), emptyList())
        p.deliver(listOf(frame), emptyList())   // replay
        assertEquals(1, p.pcEvents.count { it is LinkEvent.Incoming })
        assertEquals(2, p.pc.droppedFrames)
    }

    @Test
    fun mismatchedKeysTriggerRepairing() {
        val phoneKeys = MemKeys()
        val pcKeys = MemKeys()
        Pair(phoneKeys, pcKeys).confirmBoth()
        pcKeys.keys["phone-1"] = ByteArray(32)

        val p = Pair(phoneKeys, pcKeys)
        assertFalse(p.pc.ready)
        assertEquals(1, p.phoneEvents.count { it is LinkEvent.PairingCode })
        p.confirmBoth()
        assertTrue(p.phone.ready && p.pc.ready)
    }

    @Test
    fun messagesRoundTripThroughJson() {
        val m = Msg("config", headsets = listOf("AA", "BB"), prefer = Prefer.PHONE, returnOnStop = ReturnOnStop.RETURN)
        assertEquals(m, Session.parseMsg(org.json.JSONObject(String(Session.msgJson(m)))))
    }
}
