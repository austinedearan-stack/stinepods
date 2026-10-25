package com.stinepods.core

import org.json.JSONArray
import org.json.JSONException
import org.json.JSONObject
import java.nio.ByteBuffer
import java.security.MessageDigest
import java.security.SecureRandom
import java.util.Base64

/** Stores long-term pairing keys by peer device id. */
interface KeyStore {
    fun get(peerId: String): ByteArray?
    fun put(peerId: String, key: ByteArray)
}

sealed interface LinkEvent {
    /** Show this 6-digit code; call [Session.confirmPairing] with the user's answer. */
    data class PairingCode(val code: Int, val peerName: String) : LinkEvent
    data class Ready(val peerId: String, val peerName: String) : LinkEvent
    data class Incoming(val m: Msg) : LinkEvent
    /** The link must be closed. */
    data class Failed(val reason: String) : LinkEvent
}

class Step(val out: List<ByteArray> = emptyList(), val events: List<LinkEvent> = emptyList())

/**
 * One connection's handshake, pairing and message authentication (protocol/PROTOCOL.md). Port of Session.cs.
 * Pure: hand it each received frame, write out the frames it returns. Not thread-safe.
 */
class Session(
    private val role: Role,
    private val myId: String,
    private val myName: String,
    private val keys: KeyStore,
    private val random: (Int) -> ByteArray = { n -> ByteArray(n).also(SecureRandom()::nextBytes) },
) {
    companion object {
        const val MAX_BODY = 4096
        private const val PLAIN: Byte = 0
        private const val AUTHED: Byte = 1

        fun msgJson(m: Msg): ByteArray = JSONObject().apply {
            put("t", m.t)
            m.h?.let { put("h", it) }
            m.id?.let { put("id", it) }
            m.why?.let { put("why", it) }
            m.here?.let { put("here", it) }
            m.text?.let { put("text", it) }
            m.headsets?.let { put("headsets", JSONArray(it)) }
            m.prefer?.let { put("prefer", Describe.name(it)) }
            m.returnOnStop?.let { put("returnOnStop", Describe.name(it)) }
        }.toString().toByteArray()

        fun parseMsg(j: JSONObject): Msg {
            fun str(k: String) = if (j.has(k) && j.get(k) is String) j.getString(k) else null
            return Msg(
                t = j.getString("t"),
                h = str("h"),
                id = if (j.has("id") && j.get("id") is Number) j.getInt("id") else null,
                why = str("why"),
                here = if (j.has("here") && j.get("here") is Boolean) j.getBoolean("here") else null,
                text = str("text"),
                headsets = j.optJSONArray("headsets")?.let { arr -> List(arr.length()) { arr.getString(it) } },
                prefer = str("prefer")?.let { p -> Prefer.entries.firstOrNull { it.name.equals(p, true) } },
                returnOnStop = str("returnOnStop")?.let { r -> ReturnOnStop.entries.firstOrNull { it.name.equals(r, true) } },
            )
        }

        /** Length-prefixed frame: u16 length, kind, payload. */
        private fun frame(kind: Byte, payload: ByteArray): ByteArray {
            check(payload.size + 1 <= MAX_BODY) { "frame too large" }
            return ByteBuffer.allocate(2 + 1 + payload.size).putShort((1 + payload.size).toShort()).put(kind).put(payload).array()
        }

        private fun json(vararg kv: Pair<String, Any>) = JSONObject().apply { kv.forEach { (k, v) -> put(k, v) } }.toString().toByteArray()
    }

    private val myDir = (if (role == Role.PHONE) 'P' else 'C').code.toByte()
    private val peerDir = (if (role == Role.PHONE) 'C' else 'P').code.toByte()

    private var myNonce: ByteArray? = null
    private var peerNonce: ByteArray? = null
    private var connKey: ByteArray? = null
    private var peerName: String? = null
    private var myPair: LinkCrypto.KeyPair? = null
    private var peerPub: ByteArray? = null
    private var pairing = false
    private var localOk = false
    private var peerOk = false
    private var sendSeq = 0L
    private var recvSeq = 0L

    var ready = false
        private set
    var peerId: String? = null
        private set
    var droppedFrames = 0
        private set

    /** Frames to send as soon as the transport is up. */
    fun start(): List<ByteArray> {
        val nonce = random(16).also { myNonce = it }
        return listOf(frame(PLAIN, json("t" to "hello", "v" to 1, "id" to myId, "name" to myName, "nonce" to b64(nonce))))
    }

    fun send(m: Msg): ByteArray {
        check(ready) { "link not ready" }
        return authedFrame(msgJson(m))
    }

    /** Keep-alive / wake-up frame; the peer answers with a pong (ignored here). */
    fun ping(): ByteArray {
        check(ready) { "link not ready" }
        return authedFrame(json("t" to "ping"))
    }

    fun confirmPairing(yes: Boolean): Step {
        if (!pairing || localOk) return Step()
        val out = mutableListOf(frame(PLAIN, json("t" to if (yes) "pair-ok" else "pair-no")))
        val events = mutableListOf<LinkEvent>()
        if (!yes) return Step(out, listOf(LinkEvent.Failed("pairing rejected here")))
        localOk = true
        finishPairingIfBoth(out, events)
        return Step(out, events)
    }

    /** Handle one frame (the bytes after the length prefix). */
    fun receive(frame: ByteArray): Step {
        val out = mutableListOf<ByteArray>()
        val events = mutableListOf<LinkEvent>()
        try {
            require(frame.isNotEmpty()) { "empty frame" }
            when (frame[0]) {
                PLAIN -> onPlain(frame.copyOfRange(1, frame.size), out, events)
                AUTHED -> onAuthed(frame.copyOfRange(1, frame.size), out, events)
                else -> droppedFrames++
            }
        } catch (e: Exception) {
            when (e) {
                is JSONException, is IllegalArgumentException, is IllegalStateException, is java.security.GeneralSecurityException -> droppedFrames++
                else -> throw e
            }
        }
        return Step(out, events)
    }

    private fun onPlain(body: ByteArray, out: MutableList<ByteArray>, events: MutableList<LinkEvent>) {
        val j = JSONObject(String(body))
        when (j.getString("t")) {
            "hello" -> {
                if (peerId != null) { droppedFrames++; return }
                peerId = j.getString("id")
                peerName = j.optString("name", "?")
                val nonce = unb64(j.getString("nonce"))
                require(nonce.size == 16) { "bad nonce" }
                peerNonce = nonce
                val longKey = keys.get(peerId!!)
                if (longKey != null) {
                    connKey = connKeyFrom(longKey)
                    if (role == Role.PHONE) out += authedFrame(json("t" to "ready"))
                } else {
                    beginPairing(out, events)
                }
            }
            "pair" -> {
                if (ready || peerId == null) { droppedFrames++; return }
                peerPub = unb64(j.getString("pub"))
                beginPairing(out, events)
            }
            "pair-ok" -> if (pairing) {
                peerOk = true
                finishPairingIfBoth(out, events)
            } else droppedFrames++
            "pair-no" -> if (pairing) events += LinkEvent.Failed("pairing rejected on the other device") else droppedFrames++
            else -> droppedFrames++
        }
    }

    private fun beginPairing(out: MutableList<ByteArray>, events: MutableList<LinkEvent>) {
        if (!pairing) {
            pairing = true
            connKey = null
            val kp = LinkCrypto.KeyPair.generate().also { myPair = it }
            out += frame(PLAIN, json("t" to "pair", "pub" to b64(kp.public)))
        }
        val kp = myPair
        val pp = peerPub
        if (kp != null && pp != null) {
            val (phonePub, pcPub) = if (role == Role.PHONE) kp.public to pp else pp to kp.public
            events += LinkEvent.PairingCode(LinkCrypto.pairCode(kp.agree(pp), phonePub, pcPub), peerName ?: "?")
        }
    }

    private fun finishPairingIfBoth(out: MutableList<ByteArray>, events: MutableList<LinkEvent>) {
        val kp = myPair ?: return
        val pp = peerPub ?: return
        val id = peerId ?: return
        if (!localOk || !peerOk) return
        val longKey = LinkCrypto.longKey(kp.agree(pp))
        keys.put(id, longKey)
        myPair = null
        pairing = false
        connKey = connKeyFrom(longKey)
        if (role == Role.PHONE) out += authedFrame(json("t" to "ready"))
    }

    private fun onAuthed(rest: ByteArray, out: MutableList<ByteArray>, events: MutableList<LinkEvent>) {
        require(rest.size >= 8 + LinkCrypto.TAG_LENGTH) { "short frame" }
        val seq = ByteBuffer.wrap(rest).long
        val tag = rest.copyOfRange(8, 8 + LinkCrypto.TAG_LENGTH)
        val body = rest.copyOfRange(8 + LinkCrypto.TAG_LENGTH, rest.size)
        val key = connKey
        if (key == null) {
            // We have no key for this peer (e.g. it was re-installed); pairing is the only way forward.
            droppedFrames++
            return
        }
        if (seq <= recvSeq || !MessageDigest.isEqual(tag, LinkCrypto.tag(key, peerDir, seq, body))) {
            droppedFrames++
            // During the handshake a bad tag means the stored keys don't match: re-pair (the user must confirm a code).
            if (!ready && !pairing) beginPairing(out, events)
            return
        }
        recvSeq = seq

        val j = JSONObject(String(body))
        val t = j.getString("t")
        if (!ready) {
            if (t != "ready") { droppedFrames++; return }
            ready = true
            if (role == Role.PC) out += authedFrame(json("t" to "ready"))
            events += LinkEvent.Ready(peerId!!, peerName ?: "?")
            return
        }
        when (t) {
            "ping" -> out += authedFrame(json("t" to "pong"))
            "pong", "ready" -> Unit
            else -> events += LinkEvent.Incoming(parseMsg(j))
        }
    }

    private fun connKeyFrom(longKey: ByteArray): ByteArray {
        val (phoneNonce, pcNonce) = if (role == Role.PHONE) myNonce!! to peerNonce!! else peerNonce!! to myNonce!!
        return LinkCrypto.connKey(longKey, phoneNonce, pcNonce)
    }

    private fun authedFrame(body: ByteArray): ByteArray {
        val seq = ++sendSeq
        val tag = LinkCrypto.tag(connKey!!, myDir, seq, body)
        return frame(AUTHED, ByteBuffer.allocate(8 + tag.size + body.size).putLong(seq).put(tag).put(body).array())
    }

    private fun b64(b: ByteArray) = Base64.getEncoder().encodeToString(b)
    private fun unb64(s: String) = Base64.getDecoder().decode(s)
}

/** Splits a byte stream into frames (the bytes after each u16 length prefix). */
class FrameReader {
    private val buf = ByteArray(2 + Session.MAX_BODY)
    private var len = 0

    /** Appends received bytes; returns complete frames. Throws on an oversized frame (close the link). */
    fun push(data: ByteArray, count: Int = data.size): List<ByteArray> {
        val frames = mutableListOf<ByteArray>()
        var off = 0
        while (off < count) {
            val n = minOf(count - off, buf.size - len)
            System.arraycopy(data, off, buf, len, n)
            len += n
            off += n
            while (len >= 2) {
                val size = ((buf[0].toInt() and 0xff) shl 8) or (buf[1].toInt() and 0xff)
                if (size == 0 || size > Session.MAX_BODY) throw java.io.IOException("bad frame length")
                if (len < 2 + size) break
                frames += buf.copyOfRange(2, 2 + size)
                System.arraycopy(buf, 2 + size, buf, 0, len - 2 - size)
                len -= 2 + size
            }
        }
        return frames
    }
}
